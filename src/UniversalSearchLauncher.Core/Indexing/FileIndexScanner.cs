using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Core.Indexing;

public sealed class FileScanResult
{
    public List<FileEntry> Entries { get; } = new();
    public int ScannedDirectories { get; set; }
    public bool LimitReached { get; set; }
    public List<string> Warnings { get; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Breadth-first file-system walker. It never runs on the UI thread in the app,
/// skips reparse points/system/temp files, and applies user exclusions before
/// descending into a folder.
/// </summary>
public static class FileIndexScanner
{
    public static FileScanResult Scan(
        IEnumerable<string> roots,
        IReadOnlyList<string> exclusionPatterns,
        int maxEntries,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var result = new FileScanResult();
        var exclusions = ExclusionSet.Create(exclusionPatterns);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRoots = new List<string>();
        var stack = new Stack<string>();

        foreach (var root in NormalizeRoots(roots))
        {
            if (seenRoots.Any(existing =>
                    root.Equals(existing, StringComparison.OrdinalIgnoreCase) ||
                    root.StartsWith(existing + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            seenRoots.Add(root);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new DirectoryInfo(root);
                if (!info.Exists)
                {
                    result.Warnings.Add("Папка не найдена: " + root);
                    continue;
                }

                AddEntry(result, seenPaths, info, isDirectory: true, maxEntries);
                stack.Push(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                result.Warnings.Add($"Не удалось открыть {root}: {ex.Message}");
            }
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Temporary,
            MatchType = MatchType.Simple
        };

        var scanned = 0;
        var progressCounter = 0;

        while (stack.Count > 0 && !result.LimitReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            scanned++;

            try
            {
                var directoryInfo = new DirectoryInfo(directory);
                foreach (var info in directoryInfo.EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (result.Entries.Count >= maxEntries)
                    {
                        result.LimitReached = true;
                        break;
                    }

                    if (exclusions.IsExcluded(info.FullName))
                    {
                        continue;
                    }

                    var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                    if (isDirectory)
                    {
                        if (!AddEntry(result, seenPaths, info, true, maxEntries))
                        {
                            continue;
                        }

                        stack.Push(info.FullName);
                    }
                    else
                    {
                        AddEntry(result, seenPaths, info, false, maxEntries);
                    }

                    progressCounter++;
                    if (progress is not null && progressCounter % 500 == 0)
                    {
                        progress(result.Entries.Count);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (result.Warnings.Count < 8)
                {
                    result.Warnings.Add($"Пропущена папка {directory}: {ex.Message}");
                }
            }

            if (progress is not null && scanned % 100 == 0)
            {
                progress(result.Entries.Count);
            }
        }

        result.ScannedDirectories = scanned;
        result.Duration = DateTime.UtcNow - started;
        progress?.Invoke(result.Entries.Count);
        return result;
    }

    private static bool AddEntry(
        FileScanResult result,
        HashSet<string> seenPaths,
        FileSystemInfo info,
        bool isDirectory,
        int maxEntries)
    {
        if (result.Entries.Count >= maxEntries)
        {
            result.LimitReached = true;
            return false;
        }

        var fullPath = info.FullName;
        if (!seenPaths.Add(fullPath))
        {
            return false;
        }

        try
        {
            FileEntry entry;
            if (isDirectory)
            {
                var parentDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                entry = new FileEntry
                {
                    FullPath = fullPath,
                    Name = info.Name,
                    DirectoryPath = parentDirectory,
                    ParentName = Path.GetFileName(parentDirectory),
                    IsDirectory = true,
                    ModifiedUtc = info.LastWriteTimeUtc
                };
            }
            else
            {
                var file = (FileInfo)info;
                entry = new FileEntry
                {
                    FullPath = fullPath,
                    Name = file.Name,
                    DirectoryPath = file.DirectoryName ?? string.Empty,
                    ParentName = file.Directory?.Name ?? string.Empty,
                    Extension = file.Extension,
                    IsDirectory = false,
                    Size = file.Length,
                    ModifiedUtc = file.LastWriteTimeUtc
                };
            }

            result.Entries.Add(entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (result.Warnings.Count < 8)
            {
                result.Warnings.Add($"Пропущен {fullPath}: {ex.Message}");
            }

            return false;
        }
    }

    private static IEnumerable<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')))
                    .TrimEnd('\\');
            }
            catch
            {
                continue;
            }

            if (normalized.Length > 0)
            {
                result.Add(normalized);
            }
        }

        return result.OrderBy(path => path.Length);
    }
}
