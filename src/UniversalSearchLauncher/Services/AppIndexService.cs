using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Discovers Start Menu shortcuts and UWP/AppsFolder entries. It launches a
/// background refresh, caches the result and watches the Start Menu for changes.
/// </summary>
public sealed class AppIndexService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _sync = new();
    private System.Threading.Timer? _debounceTimer;
    private AppEntry[] _entries = Array.Empty<AppEntry>();
    private DateTime _updatedUtc;
    private bool _disposed;

    public event EventHandler? IndexChanged;

    public IReadOnlyList<AppEntry> Entries => Volatile.Read(ref _entries);
    public DateTime UpdatedUtc => _updatedUtc;
    public bool IsRefreshing { get; private set; }
    public string? LastError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadCache();
        StartWatchers();
        _debounceTimer = new System.Threading.Timer(
            _ => _ = RefreshSafelyAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        await RefreshSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IsRefreshing = true;
            var started = Stopwatch.StartNew();
            var entries = await Task.Run(() => BuildIndex(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Count > 0)
            {
                entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                Volatile.Write(ref _entries, entries.ToArray());
                _updatedUtc = DateTime.UtcNow;
                LastError = null;
                SaveCache(entries);
                IndexChanged?.Invoke(this, EventArgs.Empty);
                AppLog.Info($"Индекс приложений обновлён: {entries.Count} шт. за {started.ElapsedMilliseconds} мс.");
            }
            else
            {
                LastError = "Не удалось найти приложения в меню «Пуск».";
                AppLog.Warn(LastError);
            }
        }
        catch (OperationCanceledException)
        {
            // A new refresh or shutdown superseded this one.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("Не удалось обновить индекс приложений.", ex);
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Фоновое обновление приложений завершилось ошибкой.", ex);
        }
    }

    private static List<AppEntry> BuildIndex(CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var shortcut in EnumerateShortcuts(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = CreateShortcutEntry(shortcut);
            if (entry is null)
            {
                continue;
            }

            var key = NormalizeName(entry.Name);
            if (key.Length == 0 || entries.ContainsKey(key))
            {
                continue;
            }

            entries[key] = entry;
        }

        foreach (var (name, appId) in GetStartApps(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizeName(name);
            if (key.Length == 0)
            {
                continue;
            }

            if (entries.TryGetValue(key, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.ExecutablePath) &&
                    string.IsNullOrWhiteSpace(existing.Aumid))
                {
                    if (TryResolveAppIdPath(appId, out var resolvedPath) &&
                        IsLaunchableTarget(resolvedPath!))
                    {
                        existing.ExecutablePath = resolvedPath;
                        existing.IconPath = resolvedPath;
                        existing.Aumid = null;
                    }
                    else if (!appId.StartsWith('{'))
                    {
                        existing.Aumid = appId;
                    }
                }

                continue;
            }

            var newEntry = new AppEntry
            {
                Name = name.Trim(),
                Source = "AppsFolder"
            };

            if (TryResolveAppIdPath(appId, out var path))
            {
                if (!IsLaunchableTarget(path!))
                {
                    continue;
                }

                var sameTarget = entries.Values.FirstOrDefault(existingEntry =>
                    existingEntry.Source == "Start Menu" &&
                    !string.IsNullOrWhiteSpace(existingEntry.ExecutablePath) &&
                    string.Equals(existingEntry.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
                if (sameTarget is not null)
                {
                    if (ContainsCyrillic(name) && !ContainsCyrillic(sameTarget.Name))
                    {
                        sameTarget.Name = name.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(sameTarget.IconPath))
                    {
                        sameTarget.IconPath = path;
                    }

                    continue;
                }

                newEntry.ExecutablePath = path;
                newEntry.IconPath = path;
            }
            else if (!appId.StartsWith('{'))
            {
                newEntry.Aumid = appId;
            }
            else
            {
                continue;
            }

            entries[key] = newEntry;
        }

        return entries.Values.Where(IsLaunchableEntry).ToList();
    }

    private static bool ContainsCyrillic(string value)
    {
        foreach (var ch in value)
        {
            if (ch is >= '\u0400' and <= '\u04FF')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLaunchableEntry(AppEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath) && File.Exists(entry.ExecutablePath))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.Aumid))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.ShortcutPath))
        {
            var extension = Path.GetExtension(entry.ShortcutPath);
            return extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static AppEntry? CreateShortcutEntry(string path)
    {
        var extension = Path.GetExtension(path);
        var name = CleanShortcutName(Path.GetFileNameWithoutExtension(path));
        if (name.Length == 0 || IsUnwantedName(name))
        {
            return null;
        }

        var entry = new AppEntry
        {
            Name = name,
            ShortcutPath = path,
            Source = "Start Menu"
        };

        object? comLink = null;
        try
        {
            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                var url = ReadUrlShortcut(path);
                if (string.IsNullOrWhiteSpace(url) ||
                    url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return entry;
            }

            if (extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                comLink = new ShellLink();
                var shellLink = (IShellLinkW)comLink;
                var persist = (IPersistFile)comLink;
                var hr = persist.Load(path, 0);
                if (hr == 0)
                {
                    var target = new StringBuilder(1024);
                    if (shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0) == 0)
                    {
                        var resolved = Environment.ExpandEnvironmentVariables(target.ToString());
                        if (resolved.Length > 0 && File.Exists(resolved))
                        {
                            if (!IsLaunchableTarget(resolved))
                            {
                                return null;
                            }

                            entry.ExecutablePath = resolved;
                            entry.IconPath = resolved;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(entry.IconPath) &&
                !string.IsNullOrWhiteSpace(entry.ExecutablePath))
            {
                entry.IconPath = entry.ExecutablePath;
            }

            return entry;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Не удалось разобрать ярлык {path}: {ex.Message}");
            return entry;
        }
        finally
        {
            if (comLink is not null && Marshal.IsComObject(comLink))
            {
                Marshal.ReleaseComObject(comLink);
            }
        }
    }

    private static IEnumerable<string> EnumerateShortcuts(CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", options);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось прочитать {root}: {ex.Message}");
                continue;
            }

            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".url", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static string? ReadUrlShortcut(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[4..].Trim();
                }
            }
        }
        catch
        {
            // Treat unreadable URL shortcuts as not launchable.
        }

        return null;
    }

    private static bool IsLaunchableTarget(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".msc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cpl", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveAppIdPath(string appId, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(appId) || appId.Contains('!') || !appId.StartsWith('{'))
        {
            return false;
        }

        var closingBrace = appId.IndexOf('}');
        if (closingBrace <= 1 || closingBrace + 2 >= appId.Length || appId[closingBrace + 1] != '\\')
        {
            return false;
        }

        if (!Guid.TryParse(appId[..(closingBrace + 1)], out var folderId))
        {
            return false;
        }

        var relative = appId[(closingBrace + 2)..].Replace('/', '\\');
        if (relative.Length == 0)
        {
            return false;
        }

        IntPtr folderPointer = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out folderPointer) != 0 ||
                folderPointer == IntPtr.Zero)
            {
                return false;
            }

            var folder = Marshal.PtrToStringUni(folderPointer);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(folder, relative));
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }
        catch
        {
            // Fall through: the app can still be launched through its AUMID.
        }
        finally
        {
            if (folderPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(folderPointer);
            }
        }

        return false;
    }

    private static List<(string Name, string AppId)> GetStartApps(CancellationToken cancellationToken)    {
        var result = new List<(string, string)>();
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");

        if (!File.Exists(powershell))
        {
            return result;
        }

        try
        {
            var startInfo = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
                "Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return result;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            if (!process.WaitForExit(15_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore.
                }

                return result;
            }

            var output = outputTask.GetAwaiter().GetResult().Trim();
            if (output.Length == 0)
            {
                return result;
            }

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    AddStartApp(item, result);
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddStartApp(document.RootElement, result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn("Get-StartApps недоступен: " + ex.Message);
        }

        return result;
    }

    private static void AddStartApp(JsonElement element, List<(string Name, string AppId)> result)
    {
        string? name = null;
        string? appId = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                name = property.Value.GetString();
            }
            else if (property.Name.Equals("AppID", StringComparison.OrdinalIgnoreCase))
            {
                appId = property.Value.GetString();
            }
        }

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId) && !IsUnwantedName(name))
        {
            result.Add((name.Trim(), appId.Trim()));
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.ApplicationCacheFile))
            {
                return;
            }

            var json = File.ReadAllText(AppPaths.ApplicationCacheFile);
            var cache = JsonSerializer.Deserialize<AppCache>(json, JsonOptions);
            if (cache?.Entries is null || cache.Entries.Count == 0)
            {
                return;
            }

            var valid = cache.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .ToArray();
            if (valid.Length == 0)
            {
                return;
            }

            Volatile.Write(ref _entries, valid);
            _updatedUtc = cache.UpdatedUtc;
            AppLog.Info($"Загружен кэш приложений: {valid.Length} шт.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось загрузить кэш приложений: " + ex.Message);
        }
    }

    private static void SaveCache(List<AppEntry> entries)
    {
        try
        {
            AppPaths.EnsureCreated();
            var cache = new AppCache
            {
                UpdatedUtc = DateTime.UtcNow,
                Entries = entries
            };
            var json = JsonSerializer.Serialize(cache, JsonOptions);
            var temp = AppPaths.ApplicationCacheFile + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, AppPaths.ApplicationCacheFile, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось сохранить кэш приложений: " + ex.Message);
        }
    }

    private void StartWatchers()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 32 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, _) => ScheduleRefresh();
                watcher.Deleted += (_, _) => ScheduleRefresh();
                watcher.Renamed += (_, _) => ScheduleRefresh();
                watcher.Changed += (_, _) => ScheduleRefresh();
                watcher.Error += (_, _) => ScheduleRefresh();
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось следить за меню «Пуск» {root}: {ex.Message}");
            }
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _debounceTimer?.Change(2500, Timeout.Infinite);
        }
    }

    private static string CleanShortcutName(string value)
    {
        var name = value.Trim();
        foreach (var suffix in new[] { " - Shortcut", " - Ярлык", " (1)" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].Trim();
            }
        }

        return name;
    }

    private static bool IsUnwantedName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("uninstall", StringComparison.Ordinal) ||
               lower.Contains("удалить", StringComparison.Ordinal) ||
               lower.Contains("readme", StringComparison.Ordinal) ||
               lower.Contains("release notes", StringComparison.Ordinal) ||
               lower.Contains("documentation", StringComparison.Ordinal);
    }

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer?.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private sealed class AppCache
    {
        public DateTime UpdatedUtc { get; set; }
        public List<AppEntry> Entries { get; set; } = new();
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
