namespace UniversalSearchLauncher.Core.Models;

public sealed class UsageInfo
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public SearchCategory Category { get; set; }
    public int Count { get; set; }
    public DateTime LastUsedUtc { get; set; }
    public bool Pinned { get; set; }
}

public sealed class HistoryEntry
{
    public string Query { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string? ResultTitle { get; set; }
}

public sealed class UiState
{
    public string? LastSelectedKey { get; set; }
    public string? LastSelectedTitle { get; set; }
}
