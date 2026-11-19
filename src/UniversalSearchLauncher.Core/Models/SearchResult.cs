namespace UniversalSearchLauncher.Core.Models;

/// <summary>
/// A launchable item returned by one of the search providers.
/// </summary>
public sealed class SearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Meta { get; set; } = string.Empty;
    public SearchCategory Category { get; set; }
    public SearchActionType Action { get; set; }
    public LaunchMode LaunchMode { get; set; } = LaunchMode.Executable;
    public string? Target { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? IconPath { get; set; }
    public bool IconIsDirectory { get; set; }
    public string Extension { get; set; } = string.Empty;
    public string? Aumid { get; set; }
    public string? ShortcutPath { get; set; }
    public string? UsageKey { get; set; }
    public bool IsPinned { get; set; }
    public bool IsRecent { get; set; }
    public double Score { get; set; }
    public string? SearchQuery { get; set; }
}

public sealed class SearchGroup
{
    public SearchGroup(SearchCategory category, IReadOnlyList<SearchResult> results)
    {
        Category = category;
        Results = results;
    }

    public SearchCategory Category { get; }
    public IReadOnlyList<SearchResult> Results { get; }
    public double BestScore => Results.Count == 0 ? 0 : Results.Max(r => r.Score);
}

public sealed class SearchSnapshot
{
    public SearchSnapshot(string query, IReadOnlyList<SearchResult> results, bool isPartial = false)
    {
        Query = query;
        Results = results;
        IsPartial = isPartial;
    }

    public string Query { get; }
    public IReadOnlyList<SearchResult> Results { get; }
    public bool IsPartial { get; }
}
