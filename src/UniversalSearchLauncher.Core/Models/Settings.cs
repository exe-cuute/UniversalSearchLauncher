namespace UniversalSearchLauncher.Core.Models;

public sealed class Settings
{
    public const string DefaultHotkey = "Alt+Space";
    public const string DefaultSearchEngine = "Google";
    public const string DefaultTheme = "System";

    public string Hotkey { get; set; } = DefaultHotkey;
    public bool RunAtStartup { get; set; } = true;
    public string SearchEngine { get; set; } = DefaultSearchEngine;
    public string Theme { get; set; } = DefaultTheme;
    public int MaxResults { get; set; } = 8;
    public List<string> IndexedFolders { get; set; } = new();
    public List<string> Exclusions { get; set; } = new();
    public int PriorityApplications { get; set; } = 3;
    public int PriorityFiles { get; set; } = 3;
    public int PriorityWeb { get; set; } = 2;
    public bool HideOnFocusLost { get; set; } = true;
    public bool ShowOverFullscreenApps { get; set; }
    public bool EnableAnimations { get; set; } = true;
    public int IndexRefreshMinutes { get; set; } = 15;
    public int FileIndexMaxEntries { get; set; } = 250_000;
    public bool FirstRunCompleted { get; set; }
    public string UpdateFeedUrl { get; set; } = string.Empty;

    public static Settings CreateDefault()
    {
        var settings = new Settings();
        settings.IndexedFolders = GetDefaultFolders();
        settings.Exclusions = GetDefaultExclusions();
        return settings;
    }

    public void Normalize()
    {
        Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? DefaultHotkey : Hotkey.Trim();
        SearchEngine = SearchEngine is "Google" or "Bing" or "DuckDuckGo"
            ? SearchEngine
            : DefaultSearchEngine;
        Theme = Theme is "Dark" or "Light" or "System" ? Theme : DefaultTheme;
        MaxResults = Math.Clamp(MaxResults, 4, 20);
        PriorityApplications = Math.Clamp(PriorityApplications, 1, 5);
        PriorityFiles = Math.Clamp(PriorityFiles, 1, 5);
        PriorityWeb = Math.Clamp(PriorityWeb, 1, 5);
        IndexRefreshMinutes = Math.Clamp(IndexRefreshMinutes, 1, 1440);
        FileIndexMaxEntries = Math.Clamp(FileIndexMaxEntries, 1_000, 2_000_000);
        IndexedFolders ??= new List<string>();
        Exclusions ??= new List<string>();
        IndexedFolders = IndexedFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Environment.ExpandEnvironmentVariables(f.Trim().Trim('"')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Exclusions = Exclusions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        UpdateFeedUrl = (UpdateFeedUrl ?? string.Empty).Trim();
    }

    public Settings Clone()
    {
        return new Settings
        {
            Hotkey = Hotkey,
            RunAtStartup = RunAtStartup,
            SearchEngine = SearchEngine,
            Theme = Theme,
            MaxResults = MaxResults,
            IndexedFolders = new List<string>(IndexedFolders),
            Exclusions = new List<string>(Exclusions),
            PriorityApplications = PriorityApplications,
            PriorityFiles = PriorityFiles,
            PriorityWeb = PriorityWeb,
            HideOnFocusLost = HideOnFocusLost,
            ShowOverFullscreenApps = ShowOverFullscreenApps,
            EnableAnimations = EnableAnimations,
            IndexRefreshMinutes = IndexRefreshMinutes,
            FileIndexMaxEntries = FileIndexMaxEntries,
            FirstRunCompleted = FirstRunCompleted,
            UpdateFeedUrl = UpdateFeedUrl
        };
    }

    public static List<string> GetDefaultFolders()
    {
        var folders = new List<string>();
        AddIfExists(folders, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddIfExists(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddIfExists(folders, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddIfExists(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddIfExists(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddIfExists(folders, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        return folders;
    }

    public static List<string> GetDefaultExclusions()
    {
        return new List<string>
        {
            "node_modules",
            ".git",
            ".svn",
            ".vs",
            "bin",
            "obj",
            "packages",
            "AppData",
            "Temp",
            "tmp",
            "$Recycle.Bin",
            "System Volume Information",
            "Windows",
            "ProgramData",
            "*.tmp",
            "*.temp",
            "*.log",
            "~$*",
            "Thumbs.db",
            ".DS_Store"
        };
    }

    private static void AddIfExists(List<string> folders, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            folders.Add(path);
        }
    }
}
