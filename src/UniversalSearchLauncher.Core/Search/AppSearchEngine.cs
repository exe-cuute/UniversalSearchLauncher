using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Core.Search;

public readonly record struct AppSearchHit(AppEntry Entry, double Score);

public static class AppSearchEngine
{
    public static IReadOnlyList<AppSearchHit> Search(
        IReadOnlyList<AppEntry> entries,
        PreparedQuery query,
        IReadOnlyDictionary<string, UsageInfo> usage,
        ISet<string> pinned,
        int limit)
    {
        if (entries.Count == 0 || query.IsEmpty || limit <= 0)
        {
            return Array.Empty<AppSearchHit>();
        }

        var candidates = new List<AppSearchHit>(Math.Min(entries.Count, 128));
        var now = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            var score = FuzzyMatcher.ScoreNormalized(entry.SearchText, query);
            if (score < FuzzyMatcher.MinimumUsefulScore)
            {
                continue;
            }

            score += GetUsageBoost(entry.Identity, usage, pinned, now);
            candidates.Add(new AppSearchHit(entry, score));
        }

        candidates.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0
                ? byScore
                : string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
        });

        if (candidates.Count > limit)
        {
            candidates.RemoveRange(limit, candidates.Count - limit);
        }

        return candidates;
    }

    public static IReadOnlyList<AppSearchHit> GetRecent(
        IReadOnlyList<AppEntry> entries,
        IReadOnlyDictionary<string, UsageInfo> usage,
        ISet<string> pinned,
        int limit)
    {
        if (entries.Count == 0 || limit <= 0)
        {
            return Array.Empty<AppSearchHit>();
        }

        var byIdentity = entries.ToDictionary(entry => entry.Identity, StringComparer.OrdinalIgnoreCase);
        var result = new List<AppSearchHit>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pinnedKey in pinned)
        {
            if (byIdentity.TryGetValue(pinnedKey, out var entry) && seen.Add(entry.Identity))
            {
                result.Add(new AppSearchHit(entry, 1200 + (usage.TryGetValue(pinnedKey, out var stat) ? stat.Count : 0)));
                if (result.Count >= limit)
                {
                    return result;
                }
            }
        }

        foreach (var stat in usage.Values
                     .Where(item => item.Count > 0)
                     .OrderByDescending(item => item.LastUsedUtc)
                     .ThenByDescending(item => item.Count))
        {
            if (stat.Category != SearchCategory.Application || !byIdentity.TryGetValue(stat.Key, out var entry))
            {
                continue;
            }

            if (!seen.Add(entry.Identity))
            {
                continue;
            }

            result.Add(new AppSearchHit(entry, 900 + stat.Count));
            if (result.Count >= limit)
            {
                return result;
            }
        }

        // First run: offer a few useful Start Menu applications rather than an empty panel.
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!seen.Add(entry.Identity))
            {
                continue;
            }

            result.Add(new AppSearchHit(entry, 100));
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    public static double GetUsageBoost(
        string identity,
        IReadOnlyDictionary<string, UsageInfo> usage,
        ISet<string> pinned,
        DateTime now)
    {
        var boost = pinned.Contains(identity) ? 200d : 0d;
        if (!usage.TryGetValue(identity, out var stat))
        {
            return boost;
        }

        boost += Math.Min(130, 24 * Math.Log2(1 + Math.Max(0, stat.Count)));
        var days = Math.Max(0, (now - stat.LastUsedUtc).TotalDays);
        boost += Math.Max(0, 50 - days * 3.5);
        return boost;
    }
}
