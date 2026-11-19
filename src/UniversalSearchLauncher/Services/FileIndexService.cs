using System.IO;
using System.Threading;
using UniversalSearchLauncher.Core.Indexing;
using UniversalSearchLauncher.Core.Models;
using UniversalSearchLauncher.Core.Search;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Owns the persistent file index. Search reads an immutable snapshot while a
/// worker thread scans in the background; the UI thread is never blocked.
/// </summary>
public sealed class FileIndexService : IDisposable
{
    private readonly StorageService _storage;
    private readonly object _sync = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private FileEntry[] _entries = Array.Empty<FileEntry>();
    private string[] _indexedRoots = Array.Empty<string>();
    private Settings _settings = Settings.CreateDefault();
    private System.Threading.Timer? _debounceTimer;
    private System.Threading.Timer? _periodicTimer;
    private CancellationTokenSource? _refreshCts;
    private int _refreshing;
    private bool _disposed;
    private long _lastProgressNotification;
    private int _scannedDuringRefresh;

    public FileIndexService(StorageService storage)
    {
        _storage = storage;
    }

    public event EventHandler? StateChanged;
    public event EventHandler? IndexChanged;

    public IReadOnlyList<FileEntry> Entries => Volatile.Read(ref _entries);
    public int EntryCount => Entries.Count;
    public bool IsIndexing => Volatile.Read(ref _refreshing) != 0;
    public int ScannedDuringRefresh => Volatile.Read(ref _scannedDuringRefresh);
    public DateTime LastIndexedUtc { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<string> LastWarnings { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> IndexedRoots => Volatile.Read(ref _indexedRoots);

    public async Task InitializeAsync(Settings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings.Clone();
        _debounceTimer = new System.Threading.Timer(
            _ => _ = RefreshFromWatcherAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        StoredFileIndex? cached = null;
        try
        {
            cached = await Task.Run(_storage.LoadFileIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось прочитать кэш индекса файлов.", ex);
        }

        if (cached is not null)
        {
            Volatile.Write(ref _entries, cached.Entries.ToArray());
            Volatile.Write(ref _indexedRoots, cached.Roots.ToArray());
            LastIndexedUtc = cached.UpdatedUtc;
            AppLog.Info($"Загружен индекс файлов: {cached.Entries.Count} шт.");
            RaiseStateChanged();
            IndexChanged?.Invoke(this, EventArgs.Empty);
        }

        ConfigureWatchers(_settings);
        ConfigurePeriodicRefresh(_settings);

        var rootsChanged = !SameRoots(cached?.Roots, settings.IndexedFolders);
        var stale = LastIndexedUtc == default ||
                    DateTime.UtcNow - LastIndexedUtc > TimeSpan.FromMinutes(settings.IndexRefreshMinutes);
        if (cached is null || rootsChanged || stale)
        {
            _ = RefreshAsync(settings, cancellationToken);
        }
    }

    public async Task ApplySettingsAsync(Settings settings, bool forceReindex)
    {
        var previousRoots = _indexedRoots.ToArray();
        var previousExclusions = _settings.Exclusions.ToArray();
        _settings = settings.Clone();

        ConfigureWatchers(settings);
        ConfigurePeriodicRefresh(settings);

        var rootsChanged = !SameRoots(previousRoots, settings.IndexedFolders);
        var exclusionsChanged = !previousExclusions.SequenceEqual(settings.Exclusions, StringComparer.OrdinalIgnoreCase);

        if (forceReindex || rootsChanged || exclusionsChanged)
        {
            await RefreshAsync(settings).ConfigureAwait(false);
        }
    }

    public Task RefreshAsync(Settings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings.Clone();
        return RefreshCoreAsync(cancellationToken);
    }

    public IReadOnlyList<FileSearchHit> Search(PreparedQuery query, int limit, CancellationToken cancellationToken)
    {
        return FileSearchEngine.Search(Entries, query, limit, DateTime.UtcNow, cancellationToken);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Interlocked.Exchange(ref _refreshing, 1);
        CancellationTokenSource? localCts = null;
        try
        {
            localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _refreshCts = localCts;
            LastError = null;
            Volatile.Write(ref _scannedDuringRefresh, 0);
            RaiseStateChanged();

            var settings = _settings.Clone();
            var result = await Task.Run(() =>
            {
                var originalPriority = Thread.CurrentThread.Priority;
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                }
                catch
                {
                    // Not all hosts allow changing thread priority.
                }

                try
                {
                    return FileIndexScanner.Scan(
                        settings.IndexedFolders,
                        settings.Exclusions,
                        settings.FileIndexMaxEntries,
                        count =>
                        {
                            Volatile.Write(ref _scannedDuringRefresh, count);
                            var now = Environment.TickCount64;
                            if (now - Interlocked.Read(ref _lastProgressNotification) >= 500)
                            {
                                Interlocked.Exchange(ref _lastProgressNotification, now);
                                RaiseStateChanged();
                            }
                        },
                        localCts.Token);
                }
                finally
                {
                    try
                    {
                        Thread.CurrentThread.Priority = originalPriority;
                    }
                    catch
                    {
                    }
                }
            }, localCts.Token).ConfigureAwait(false);

            localCts.Token.ThrowIfCancellationRequested();

            var snapshot = result.Entries.ToArray();
            foreach (var entry in snapshot)
            {
                _ = entry.SearchText;
            }

            Volatile.Write(ref _entries, snapshot);
            Volatile.Write(ref _indexedRoots, settings.IndexedFolders.ToArray());
            LastIndexedUtc = DateTime.UtcNow;
            LastWarnings = result.Warnings.ToArray();
            if (result.Warnings.Count > 0)
            {
                LastError = result.Warnings[0];
            }

            AppLog.Info(
                $"Индекс файлов обновлён: {snapshot.Length} элементов, " +
                $"{result.ScannedDirectories} папок, {result.Duration.TotalMilliseconds:F0} мс" +
                (result.LimitReached ? " (достигнут лимит)" : string.Empty));
            RaiseStateChanged();
            IndexChanged?.Invoke(this, EventArgs.Empty);

            var roots = settings.IndexedFolders.ToArray();
            await Task.Run(() => _storage.SaveFileIndex(snapshot, roots), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Индексирование файлов отменено.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("Не удалось обновить индекс файлов.", ex);
            RaiseStateChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
            _refreshCts = null;
            localCts?.Dispose();
            _refreshGate.Release();
            RaiseStateChanged();
        }
    }

    private async Task RefreshFromWatcherAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Фоновое обновление файлов завершилось ошибкой.", ex);
        }
    }

    private void ConfigureWatchers(Settings settings)
    {
        lock (_sync)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
            if (_disposed)
            {
                return;
            }

            foreach (var root in settings.IndexedFolders)
            {
                if (!Directory.Exists(root) || !IsLocalFixedPath(root) || _watchers.Count >= 8)
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 64 * 1024,
                        EnableRaisingEvents = true
                    };
                    watcher.Created += (_, _) => ScheduleRefresh();
                    watcher.Deleted += (_, _) => ScheduleRefresh();
                    watcher.Renamed += (_, _) => ScheduleRefresh();
                    watcher.Error += (_, _) => ScheduleRefresh();
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Не удалось следить за папкой {root}: {ex.Message}");
                }
            }
        }
    }

    private void ConfigurePeriodicRefresh(Settings settings)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(settings.IndexRefreshMinutes, 1, 1440));
        _periodicTimer?.Dispose();
        _periodicTimer = new System.Threading.Timer(
            _ => _ = RefreshFromWatcherAsync(),
            null,
            interval,
            interval);
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _debounceTimer?.Change(6000, Timeout.Infinite);
        }
    }

    private static bool IsLocalFixedPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameRoots(IReadOnlyList<string>? left, IReadOnlyList<string> right)
    {
        if (left is null)
        {
            return false;
        }

        var leftSet = new HashSet<string>(
            left.Select(NormalizeRoot).Where(value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var rightSet = new HashSet<string>(
            right.Select(NormalizeRoot).Where(value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        return leftSet.SetEquals(rightSet);
    }

    private static string NormalizeRoot(string value)
    {
        try
        {
            return Path.GetFullPath(value).TrimEnd('\\');
        }
        catch
        {
            return value.Trim();
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Обработчик состояния индекса завершился ошибкой: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCts?.Cancel();
        _debounceTimer?.Dispose();
        _periodicTimer?.Dispose();

        lock (_sync)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        // Let an in-flight SQLite save finish before the database is disposed.
        try
        {
            if (_refreshGate.Wait(TimeSpan.FromSeconds(5)))
            {
                _refreshGate.Release();
            }
        }
        catch
        {
            // Best effort during shutdown.
        }
    }
}
