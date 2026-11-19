using System.Globalization;
using UniversalSearchLauncher.Core.Models;
using UniversalSearchLauncher.Core.Search;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Merges application, file and web providers into one ranked, grouped snapshot.
/// Application/web results are delivered first while the file index is scoring;
/// the final snapshot follows as soon as it is ready.
/// </summary>
public sealed class SearchService
{
    private readonly AppIndexService _apps;
    private readonly FileIndexService _files;
    private readonly UsageService _usage;

    public SearchService(AppIndexService apps, FileIndexService files, UsageService usage)
    {
        _apps = apps;
        _files = files;
        _usage = usage;
    }

    public async Task<SearchSnapshot> SearchAsync(
        string rawQuery,
        Settings settings,
        Action<SearchSnapshot>? partial,
        CancellationToken cancellationToken)
    {
        var intent = QueryIntent.Parse(rawQuery);
        var usage = _usage.Snapshot();
        var pins = _usage.PinnedSnapshot();

        if (intent.IsEmpty)
        {
            var recent = AppSearchEngine.GetRecent(
                _apps.Entries,
                usage,
                pins,
                settings.MaxResults);
            var recentResults = recent
                .Select(hit => ToSearchResult(
                    hit.Entry,
                    hit.Score,
                    pins,
                    isRecent: usage.ContainsKey(hit.Entry.Identity) ||
                              pins.Contains(hit.Entry.Identity)))
                .ToList();
            return new SearchSnapshot(string.Empty, recentResults);
        }

        if (intent.DirectUri is not null)
        {
            var direct = CreateDirectUrlResult(intent, settings);
            return new SearchSnapshot(rawQuery, new[] { direct });
        }

        var query = PreparedQuery.Create(intent.Text);
        cancellationToken.ThrowIfCancellationRequested();

        var fileTask = Task.Run(
            () => _files.Search(query, Math.Max(settings.MaxResults * 3, 20), cancellationToken),
            cancellationToken);

        var appHits = AppSearchEngine.Search(
            _apps.Entries,
            query,
            usage,
            pins,
            Math.Max(settings.MaxResults * 2, 12));

        var appResults = appHits.Select(hit => ToSearchResult(hit.Entry, hit.Score, pins, isRecent: false)).ToList();
        var webResults = CreateWebResults(intent, settings);

        if (partial is not null)
        {
            var partialResults = BuildSnapshot(
                rawQuery,
                settings,
                appResults,
                Array.Empty<SearchResult>(),
                webResults,
                usage,
                pins,
                isPartial: true);
            partial(partialResults);
        }

        IReadOnlyList<FileSearchHit> fileHits;
        try
        {
            fileHits = await fileTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var fileResults = fileHits
            .Select(hit => ToFileResult(hit, pins, usage))
            .ToList();

        return BuildSnapshot(
            rawQuery,
            settings,
            appResults,
            fileResults,
            webResults,
            usage,
            pins,
            isPartial: false);
    }

    private static SearchSnapshot BuildSnapshot(
        string rawQuery,
        Settings settings,
        List<SearchResult> apps,
        IReadOnlyList<SearchResult> files,
        List<SearchResult> web,
        IReadOnlyDictionary<string, UsageInfo> usage,
        ISet<string> pins,
        bool isPartial)
    {
        var appPriorityBoost = (settings.PriorityApplications - 3) * 15 + 38;
        var filePriorityBoost = (settings.PriorityFiles - 3) * 15;
        foreach (var result in apps)
        {
            result.Score += appPriorityBoost;
        }

        foreach (var result in files)
        {
            result.Score += filePriorityBoost;
        }

        var local = new List<SearchResult>(apps.Count + files.Count);
        local.AddRange(apps);
        local.AddRange(files);
        local.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        var webCount = web.Count;
        var localBudget = Math.Max(0, settings.MaxResults - webCount);
        if (local.Count > localBudget)
        {
            local.RemoveRange(localBudget, local.Count - localBudget);
        }

        var groups = new List<SearchGroup>();
        var appGroup = local.Where(result => result.Category == SearchCategory.Application).ToList();
        var fileGroup = local.Where(result => result.Category == SearchCategory.File).ToList();
        if (appGroup.Count > 0)
        {
            groups.Add(new SearchGroup(SearchCategory.Application, appGroup));
        }

        if (fileGroup.Count > 0)
        {
            groups.Add(new SearchGroup(SearchCategory.File, fileGroup));
        }

        if (web.Count > 0)
        {
            groups.Add(new SearchGroup(SearchCategory.Web, web));
        }

        groups.Sort((a, b) =>
        {
            var priorityA = GetCategoryPriority(a.Category, settings);
            var priorityB = GetCategoryPriority(b.Category, settings);
            var byPriority = priorityB.CompareTo(priorityA);
            return byPriority != 0 ? byPriority : a.Category.CompareTo(b.Category);
        });

        var flat = new List<SearchResult>(settings.MaxResults + 2);
        foreach (var group in groups)
        {
            foreach (var result in group.Results)
            {
                if (flat.Count >= settings.MaxResults)
                {
                    break;
                }

                flat.Add(result);
            }

            if (flat.Count >= settings.MaxResults)
            {
                break;
            }
        }

        return new SearchSnapshot(rawQuery, flat, isPartial);
    }

    private static SearchResult ToSearchResult(
        AppEntry entry,
        double score,
        ISet<string> pins,
        bool isRecent)
    {
        var launchMode = entry.Mode;
        var subtitle = launchMode switch
        {
            LaunchMode.Aumid => "Приложение Windows",
            LaunchMode.Shortcut when !string.IsNullOrWhiteSpace(entry.ExecutablePath) =>
                Path.GetFileName(entry.ExecutablePath!),
            LaunchMode.Shortcut => "Ярлык приложения",
            _ => Path.GetFileName(entry.ExecutablePath ?? entry.Name)
        };

        return new SearchResult
        {
            Id = "app:" + entry.Identity,
            Title = entry.Name,
            Subtitle = subtitle,
            Meta = pins.Contains(entry.Identity)
                ? "Избранное"
                : isRecent
                    ? "Недавнее"
                    : "Приложение",
            Category = SearchCategory.Application,
            Action = SearchActionType.LaunchApplication,
            LaunchMode = launchMode,
            Target = entry.LaunchTarget,
            Arguments = entry.Arguments,
            WorkingDirectory = entry.WorkingDirectory,
            Aumid = entry.Aumid,
            ShortcutPath = entry.ShortcutPath,
            IconPath = entry.BestIconPath,
            UsageKey = entry.Identity,
            IsPinned = pins.Contains(entry.Identity),
            IsRecent = isRecent,
            Score = score
        };
    }

    private static SearchResult ToFileResult(
        FileSearchHit hit,
        ISet<string> pins,
        IReadOnlyDictionary<string, UsageInfo> usage)
    {
        var entry = hit.Entry;
        var id = "file:" + entry.FullPath.ToLowerInvariant();
        var score = hit.Score;

        if (pins.Contains(id))
        {
            score += 90;
        }

        if (usage.TryGetValue(id, out var info))
        {
            score += Math.Min(80, 15 * Math.Log2(1 + info.Count));
            score += Math.Max(0, 35 - (DateTime.UtcNow - info.LastUsedUtc).TotalDays * 2.5);
        }

        return new SearchResult
        {
            Id = id,
            Title = entry.Name,
            Subtitle = ShortenPath(entry.DirectoryPath),
            Meta = BuildFileMeta(entry),
            Category = SearchCategory.File,
            Action = entry.IsDirectory ? SearchActionType.OpenFolder : SearchActionType.OpenFile,
            Target = entry.FullPath,
            IconPath = entry.FullPath,
            IconIsDirectory = entry.IsDirectory,
            Extension = entry.Extension,
            UsageKey = id,
            IsPinned = pins.Contains(id),
            Score = score
        };
    }

    private static List<SearchResult> CreateWebResults(QueryIntent intent, Settings settings)
    {
        var engineName = QueryIntent.GetSearchEngineDisplayName(settings.SearchEngine);
        var uri = intent.GetSearchUri(settings.SearchEngine);
        var priorityBoost = (settings.PriorityWeb - 3) * 10;

        return new List<SearchResult>
        {
            new()
            {
                Id = "web:search:" + settings.SearchEngine,
                Title = $"Искать «{ShrinkQuery(intent.Text, 58)}» в {engineName}",
                Subtitle = "Открыть результаты в браузере",
                Meta = engineName,
                Category = SearchCategory.Web,
                Action = SearchActionType.SearchWeb,
                LaunchMode = LaunchMode.Url,
                Target = uri.AbsoluteUri,
                SearchQuery = intent.Text,
                UsageKey = "web:" + settings.SearchEngine,
                Score = 100 + priorityBoost
            }
        };
    }

    private static int GetCategoryPriority(SearchCategory category, Settings settings)
    {
        return category switch
        {
            SearchCategory.Application => settings.PriorityApplications,
            SearchCategory.File => settings.PriorityFiles,
            _ => settings.PriorityWeb
        };
    }

    private static SearchResult CreateDirectUrlResult(QueryIntent intent, Settings settings)    {
        var uri = intent.DirectUri!;
        return new SearchResult
        {
            Id = "web:url:" + uri.AbsoluteUri.ToLowerInvariant(),
            Title = "Открыть " + uri.Host,
            Subtitle = uri.AbsoluteUri,
            Meta = "Сайт",
            Category = SearchCategory.Web,
            Action = SearchActionType.OpenUrl,
            LaunchMode = LaunchMode.Url,
            Target = uri.AbsoluteUri,
            SearchQuery = intent.Raw,
            UsageKey = "web:url",
            Score = 2200 + (settings.PriorityWeb - 3) * 10
        };
    }

    private static string BuildFileMeta(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            return "Папка";
        }

        var extension = entry.Extension.TrimStart('.').ToUpperInvariant();
        if (entry.Size <= 0)
        {
            return extension.Length > 0 ? extension : "Файл";
        }

        var size = FormatSize(entry.Size);
        return extension.Length > 0 ? $"{extension} · {size}" : size;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 || unit == 0 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) &&
            path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
        {
            return "~" + path[profile.Length..];
        }

        return path;
    }

    private static string ShrinkQuery(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
    }
}
