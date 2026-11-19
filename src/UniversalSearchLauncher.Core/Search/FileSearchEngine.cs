using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Core.Search;

public readonly record struct FileSearchHit(FileEntry Entry, double Score);

/// <summary>
/// Parallel in-memory scoring with a bounded top-N collector. The index is never
/// rescanned for a query, and all work can be cancelled when the next key arrives.
/// </summary>
public static class FileSearchEngine
{
    public static IReadOnlyList<FileSearchHit> Search(
        IReadOnlyList<FileEntry> entries,
        PreparedQuery query,
        int limit,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0 || query.IsEmpty || limit <= 0)
        {
            return Array.Empty<FileSearchHit>();
        }

        var result = new List<FileSearchHit>(limit);
        if (entries.Count < 4_000)
        {
            var top = new TopN(limit);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var score = ScoreEntry(entry, query, utcNow);
                if (score > 0)
                {
                    top.Add(entry, score);
                }
            }

            result.AddRange(top.ToSorted());
            return result;
        }

        var partitionCount = Math.Min(64, Math.Max(1, entries.Count / 2_048));
        var partitionSize = (entries.Count + partitionCount - 1) / partitionCount;
        var collectors = new TopN[partitionCount];
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
        };

        Parallel.For(0, partitionCount, options, partitionIndex =>
        {
            var collector = new TopN(limit);
            var start = partitionIndex * partitionSize;
            var end = Math.Min(entries.Count, start + partitionSize);

            for (var i = start; i < end; i++)
            {
                if ((i & 511) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var entry = entries[i];
                var score = ScoreEntry(entry, query, utcNow);
                if (score > 0)
                {
                    collector.Add(entry, score);
                }
            }

            collectors[partitionIndex] = collector;
        });

        var merged = new TopN(limit);
        foreach (var collector in collectors)
        {
            if (collector is null)
            {
                continue;
            }

            foreach (var hit in collector.ToSorted())
            {
                merged.Add(hit.Entry, hit.Score);
            }
        }

        result.AddRange(merged.ToSorted());
        return result;
    }

    private static double ScoreEntry(FileEntry entry, PreparedQuery query, DateTime utcNow)
    {
        var score = FuzzyMatcher.ScoreNormalized(entry.SearchText, query);
        if (score < FuzzyMatcher.MinimumUsefulScore)
        {
            return 0;
        }

        if (entry.IsDirectory)
        {
            score += 14;
        }

        var age = utcNow - entry.ModifiedUtc;
        if (age.TotalDays is >= 0 and < 120)
        {
            score += (120 - age.TotalDays) * 0.12;
        }

        // Exact extension queries such as "pdf" should surface representative files.
        if (entry.Extension.Length > 1 &&
            query.Tokens.Count == 1 &&
            entry.Extension.AsSpan(1).Equals(query.Tokens[0], StringComparison.OrdinalIgnoreCase))
        {
            score += 45;
        }

        return score;
    }

    private sealed class TopN
    {
        private readonly int _capacity;
        private readonly List<FileSearchHit> _items;

        public TopN(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _items = new List<FileSearchHit>(_capacity + 1);
        }

        public void Add(FileEntry entry, double score)
        {
            if (_items.Count < _capacity)
            {
                _items.Add(new FileSearchHit(entry, score));
                if (_items.Count == _capacity)
                {
                    Sort();
                }

                return;
            }

            if (score <= _items[^1].Score)
            {
                return;
            }

            _items[^1] = new FileSearchHit(entry, score);
            Sort();
        }

        public IEnumerable<FileSearchHit> ToSorted()
        {
            Sort();
            return _items;
        }

        private void Sort()
        {
            _items.Sort(static (a, b) =>
            {
                var byScore = b.Score.CompareTo(a.Score);
                return byScore != 0
                    ? byScore
                    : string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
    }
}
