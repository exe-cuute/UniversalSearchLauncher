using System.Globalization;
using System.Text.Json;
using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Services;

public sealed class StoredFileIndex
{
    public List<FileEntry> Entries { get; } = new();
    public List<string> Roots { get; } = new();
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// SQLite persistence for the file index, launch statistics, pins, history and
/// small UI state. All access is serialised; index writes run on worker threads.
/// </summary>
public sealed class StorageService : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _database;
    private bool _disposed;

    public StorageService()
    {
        try
        {
            AppPaths.EnsureCreated();
            Initialize();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("SQLite is unavailable; the application will run without persistence.", ex);
            IsAvailable = false;
            CloseDatabase();
        }
    }

    public bool IsAvailable { get; private set; }

    private void Initialize()
    {
        var pathBytes = SqliteNative.Utf8(AppPaths.DatabaseFile);
        var code = SqliteNative.sqlite3_open_v2(
            pathBytes,
            out _database,
            SqliteNative.OpenReadWrite | SqliteNative.OpenCreate | SqliteNative.OpenFullMutex,
            IntPtr.Zero);

        if (code != SqliteNative.Ok || _database == IntPtr.Zero)
        {
            throw new InvalidOperationException("Не удалось открыть базу данных: " + SqliteNative.Error(_database));
        }

        SqliteNative.sqlite3_busy_timeout(_database, 5000);
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Exec("""
             CREATE TABLE IF NOT EXISTS history (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 query TEXT NOT NULL,
                 result_title TEXT,
                 created_utc TEXT NOT NULL
             );
             CREATE INDEX IF NOT EXISTS idx_history_id ON history(id DESC);
             CREATE TABLE IF NOT EXISTS usage (
                 key TEXT PRIMARY KEY,
                 title TEXT,
                 category INTEGER NOT NULL DEFAULT 0,
                 count INTEGER NOT NULL DEFAULT 0,
                 last_used_utc TEXT NOT NULL
             );
             CREATE INDEX IF NOT EXISTS idx_usage_last ON usage(last_used_utc DESC);
             CREATE TABLE IF NOT EXISTS pins (
                 key TEXT PRIMARY KEY,
                 title TEXT,
                 category INTEGER NOT NULL DEFAULT 0,
                 created_utc TEXT NOT NULL
             );
             CREATE TABLE IF NOT EXISTS file_index (
                 path TEXT PRIMARY KEY,
                 name TEXT NOT NULL,
                 directory TEXT NOT NULL,
                 parent_name TEXT,
                 extension TEXT,
                 is_directory INTEGER NOT NULL DEFAULT 0,
                 size INTEGER NOT NULL DEFAULT 0,
                 modified_utc TEXT NOT NULL
             );
             CREATE TABLE IF NOT EXISTS meta (
                 key TEXT PRIMARY KEY,
                 value TEXT
             );
             """);
    }

    public Dictionary<string, UsageInfo> LoadUsage()
    {
        var result = new Dictionary<string, UsageInfo>(StringComparer.OrdinalIgnoreCase);
        if (!IsAvailable)
        {
            return result;
        }

        lock (_sync)
        {
            try
            {
                using var statement = SqliteNative.Statement.Prepare(
                    _database,
                    "SELECT key, title, category, count, last_used_utc FROM usage;");
                while (statement.Read())
                {
                    var key = statement.GetString(0) ?? string.Empty;
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    result[key] = new UsageInfo
                    {
                        Key = key,
                        Title = statement.GetString(1) ?? key,
                        Category = (SearchCategory)(int)statement.GetInt64(2),
                        Count = (int)statement.GetInt64(3),
                        LastUsedUtc = ParseDate(statement.GetString(4))
                    };
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось загрузить статистику запусков.", ex);
            }
        }

        return result;
    }

    public HashSet<string> LoadPins()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsAvailable)
        {
            return result;
        }

        lock (_sync)
        {
            try
            {
                using var statement = SqliteNative.Statement.Prepare(_database, "SELECT key FROM pins;");
                while (statement.Read())
                {
                    var key = statement.GetString(0);
                    if (!string.IsNullOrEmpty(key))
                    {
                        result.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось загрузить закреплённые элементы.", ex);
            }
        }

        return result;
    }

    public void RecordLaunch(string key, string title, SearchCategory category)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                using var statement = SqliteNative.Statement.Prepare(
                    _database,
                    """
                    INSERT INTO usage(key, title, category, count, last_used_utc)
                    VALUES ($key, $title, $category, 1, $last)
                    ON CONFLICT(key) DO UPDATE SET
                        title = excluded.title,
                        category = excluded.category,
                        count = usage.count + 1,
                        last_used_utc = excluded.last_used_utc;
                    """);
                statement.Bind(1, key);
                statement.Bind(2, title);
                statement.Bind(3, (long)category);
                statement.Bind(4, ToDate(DateTime.UtcNow));
                statement.Execute();
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить факт запуска.", ex);
            }
        }
    }

    public void SetPinned(string key, string title, SearchCategory category, bool pinned)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                using var statement = pinned
                    ? SqliteNative.Statement.Prepare(
                        _database,
                        "INSERT OR REPLACE INTO pins(key, title, category, created_utc) VALUES ($key, $title, $category, $created);")
                    : SqliteNative.Statement.Prepare(_database, "DELETE FROM pins WHERE key = $key;");

                statement.Bind(1, key);
                if (pinned)
                {
                    statement.Bind(2, title);
                    statement.Bind(3, (long)category);
                    statement.Bind(4, ToDate(DateTime.UtcNow));
                }

                statement.Execute();
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось изменить закрепление.", ex);
            }
        }
    }

    public void AddHistory(string query, string? resultTitle)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                using var statement = SqliteNative.Statement.Prepare(
                    _database,
                    "INSERT INTO history(query, result_title, created_utc) VALUES ($query, $title, $created);");
                statement.Bind(1, query.Trim());
                statement.Bind(2, resultTitle);
                statement.Bind(3, ToDate(DateTime.UtcNow));
                statement.Execute();

                using var cleanup = SqliteNative.Statement.Prepare(
                    _database,
                    "DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT 100);");
                cleanup.Execute();
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить историю поиска.", ex);
            }
        }
    }

    public List<HistoryEntry> LoadHistory(int limit = 12)
    {
        var result = new List<HistoryEntry>();
        if (!IsAvailable)
        {
            return result;
        }

        lock (_sync)
        {
            try
            {
                using var statement = SqliteNative.Statement.Prepare(
                    _database,
                    """
                    SELECT query, result_title, created_utc
                    FROM history
                    WHERE id IN (
                        SELECT MAX(id) FROM history GROUP BY query
                    )
                    ORDER BY id DESC
                    LIMIT $limit;
                    """);
                statement.Bind(1, (long)Math.Clamp(limit, 1, 100));
                while (statement.Read())
                {
                    result.Add(new HistoryEntry
                    {
                        Query = statement.GetString(0) ?? string.Empty,
                        ResultTitle = statement.GetString(1),
                        CreatedUtc = ParseDate(statement.GetString(2))
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось загрузить историю.", ex);
            }
        }

        return result;
    }

    public void ClearHistory()
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Exec("DELETE FROM history;");
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось очистить историю.", ex);
            }
        }
    }

    public void SaveFileIndex(IReadOnlyList<FileEntry> entries, IReadOnlyList<string> roots)
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_sync)
        {
            var transactionStarted = false;
            try
            {
                Exec("BEGIN IMMEDIATE;");
                transactionStarted = true;
                Exec("DELETE FROM file_index;");

                using var statement = SqliteNative.Statement.Prepare(
                    _database,
                    """
                    INSERT INTO file_index(path, name, directory, parent_name, extension, is_directory, size, modified_utc)
                    VALUES ($path, $name, $directory, $parent, $extension, $isDirectory, $size, $modified);
                    """);

                foreach (var entry in entries)
                {
                    statement.Bind(1, entry.FullPath);
                    statement.Bind(2, entry.Name);
                    statement.Bind(3, entry.DirectoryPath);
                    statement.Bind(4, entry.ParentName);
                    statement.Bind(5, entry.Extension);
                    statement.Bind(6, entry.IsDirectory ? 1 : 0);
                    statement.Bind(7, entry.Size);
                    statement.Bind(8, ToDate(entry.ModifiedUtc));
                    statement.Execute();
                    statement.Reset();
                }

                SetMetaUnsafe("file_index_roots", JsonSerializer.Serialize(roots));
                SetMetaUnsafe("file_index_updated", ToDate(DateTime.UtcNow));
                SetMetaUnsafe("file_index_count", entries.Count.ToString(CultureInfo.InvariantCulture));
                Exec("COMMIT;");
                transactionStarted = false;
            }
            catch (Exception ex)
            {
                if (transactionStarted)
                {
                    try
                    {
                        Exec("ROLLBACK;");
                    }
                    catch
                    {
                        // Ignore rollback failure.
                    }
                }

                AppLog.Error("Не удалось сохранить индекс файлов.", ex);
            }
        }
    }

    public StoredFileIndex? LoadFileIndex()
    {
        if (!IsAvailable)
        {
            return null;
        }

        lock (_sync)
        {
            try
            {
                var result = new StoredFileIndex();
                var rootsJson = GetMetaUnsafe("file_index_roots");
                if (!string.IsNullOrWhiteSpace(rootsJson))
                {
                    var roots = JsonSerializer.Deserialize<List<string>>(rootsJson);
                    if (roots is not null)
                    {
                        result.Roots.AddRange(roots);
                    }
                }

                result.UpdatedUtc = ParseDate(GetMetaUnsafe("file_index_updated"));

                using var statement = SqliteNative.Statement.Prepare(
                    _database,
                    "SELECT path, name, directory, parent_name, extension, is_directory, size, modified_utc FROM file_index;");
                while (statement.Read())
                {
                    var entry = new FileEntry
                    {
                        FullPath = statement.GetString(0) ?? string.Empty,
                        Name = statement.GetString(1) ?? string.Empty,
                        DirectoryPath = statement.GetString(2) ?? string.Empty,
                        ParentName = statement.GetString(3) ?? string.Empty,
                        Extension = statement.GetString(4) ?? string.Empty,
                        IsDirectory = statement.GetInt64(5) != 0,
                        Size = statement.GetInt64(6),
                        ModifiedUtc = ParseDate(statement.GetString(7))
                    };

                    _ = entry.SearchText;
                    result.Entries.Add(entry);
                }

                return result.Entries.Count == 0 ? null : result;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось загрузить индекс файлов.", ex);
                return null;
            }
        }
    }

    public string? GetMeta(string key)
    {
        if (!IsAvailable)
        {
            return null;
        }

        lock (_sync)
        {
            try
            {
                return GetMetaUnsafe(key);
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetMeta(string key, string? value)
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                SetMetaUnsafe(key, value);
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить состояние.", ex);
            }
        }
    }

    public UiState LoadUiState()
    {
        return new UiState
        {
            LastSelectedKey = GetMeta("last_selected_key"),
            LastSelectedTitle = GetMeta("last_selected_title")
        };
    }

    public void SaveUiState(UiState state)
    {
        SetMeta("last_selected_key", state.LastSelectedKey);
        SetMeta("last_selected_title", state.LastSelectedTitle);
    }

    private string? GetMetaUnsafe(string key)
    {
        using var statement = SqliteNative.Statement.Prepare(
            _database,
            "SELECT value FROM meta WHERE key = $key;");
        statement.Bind(1, key);
        return statement.Read() ? statement.GetString(0) : null;
    }

    private void SetMetaUnsafe(string key, string? value)
    {
        using var statement = SqliteNative.Statement.Prepare(
            _database,
            "INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;");
        statement.Bind(1, key);
        statement.Bind(2, value);
        statement.Execute();
    }

    private void Exec(string sql)
    {
        var bytes = SqliteNative.Utf8(sql);
        var code = SqliteNative.sqlite3_exec(_database, bytes, IntPtr.Zero, IntPtr.Zero, out var error);
        if (code != SqliteNative.Ok)
        {
            var message = error == IntPtr.Zero
                ? SqliteNative.Error(_database)
                : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(error) ?? "SQLite error";
            throw new InvalidOperationException(message);
        }
    }

    private static string ToDate(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseDate(string? value)
    {
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;
    }

    private void CloseDatabase()
    {
        if (_database != IntPtr.Zero)
        {
            SqliteNative.sqlite3_close_v2(_database);
            _database = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsAvailable = false;
        lock (_sync)
        {
            CloseDatabase();
        }
    }
}
