using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// In-memory launch statistics and pins with write-through SQLite persistence.
/// </summary>
public sealed class UsageService
{
    private readonly StorageService _storage;
    private readonly object _sync = new();
    private readonly Dictionary<string, UsageInfo> _usage;
    private readonly HashSet<string> _pins;

    public UsageService(StorageService storage)
    {
        _storage = storage;
        _usage = storage.LoadUsage();
        _pins = storage.LoadPins();

        foreach (var key in _pins)
        {
            if (_usage.TryGetValue(key, out var info))
            {
                info.Pinned = true;
            }
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyDictionary<string, UsageInfo> Snapshot()
    {
        lock (_sync)
        {
            return _usage.ToDictionary(
                pair => pair.Key,
                pair => new UsageInfo
                {
                    Key = pair.Value.Key,
                    Title = pair.Value.Title,
                    Category = pair.Value.Category,
                    Count = pair.Value.Count,
                    LastUsedUtc = pair.Value.LastUsedUtc,
                    Pinned = _pins.Contains(pair.Key)
                },
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public HashSet<string> PinnedSnapshot()
    {
        lock (_sync)
        {
            return new HashSet<string>(_pins, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsPinned(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_sync)
        {
            return _pins.Contains(key);
        }
    }

    public void RecordLaunch(SearchResult result)
    {
        var key = !string.IsNullOrWhiteSpace(result.UsageKey) ? result.UsageKey : result.Id;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            if (_usage.TryGetValue(key, out var info))
            {
                info.Count++;
                info.LastUsedUtc = DateTime.UtcNow;
                info.Title = result.Title;
                info.Category = result.Category;
            }
            else
            {
                _usage[key] = new UsageInfo
                {
                    Key = key,
                    Title = result.Title,
                    Category = result.Category,
                    Count = 1,
                    LastUsedUtc = DateTime.UtcNow
                };
            }
        }

        string title = result.Title;
        SearchCategory category = result.Category;
        Task.Run(() => _storage.RecordLaunch(key, title, category));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TogglePin(SearchResult result)
    {
        var key = !string.IsNullOrWhiteSpace(result.UsageKey) ? result.UsageKey : result.Id;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        bool pinned;
        lock (_sync)
        {
            if (_pins.Remove(key))
            {
                pinned = false;
            }
            else
            {
                _pins.Add(key);
                pinned = true;
            }

            if (_usage.TryGetValue(key, out var info))
            {
                info.Pinned = pinned;
            }
        }

        string title = result.Title;
        SearchCategory category = result.Category;
        Task.Run(() => _storage.SetPinned(key, title, category, pinned));
        result.IsPinned = pinned;
        Changed?.Invoke(this, EventArgs.Empty);
        return pinned;
    }
}
