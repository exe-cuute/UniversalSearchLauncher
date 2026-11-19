using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Shell icon extraction with a bounded cache. Bitmaps are frozen so they can be
/// produced on a worker thread and used by the UI without cross-thread access errors.
/// </summary>
public sealed class IconService
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const int MaxCacheEntries = 800;

    private readonly object _sync = new();
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BitmapSource? GetIcon(string? path, bool isDirectory, string? extension = null)
    {
        var key = BuildKey(path, isDirectory, extension);
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            var bitmap = LoadIcon(path, isDirectory, extension);
            _cache[key] = bitmap;
            return bitmap;
        }
    }

    private static string BuildKey(string? path, bool isDirectory, string? extension)
    {
        if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
        {
            return path.ToLowerInvariant();
        }

        if (isDirectory)
        {
            return "::directory";
        }

        return "::extension:" + (extension ?? string.Empty).ToLowerInvariant();
    }

    private static BitmapSource? LoadIcon(string? path, bool isDirectory, string? extension)
    {
        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiLargeIcon;
        var target = path ?? string.Empty;
        uint attributes = 0;

        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path)))
            {
                attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
            }
            else
            {
                flags |= ShgfiUseFileAttributes;
                attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
                target = isDirectory ? "folder" : "file" + (extension ?? string.Empty);
            }

            var result = SHGetFileInfo(target, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            {
                return null;
            }

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (info.IconHandle != IntPtr.Zero)
            {
                DestroyIcon(info.IconHandle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
