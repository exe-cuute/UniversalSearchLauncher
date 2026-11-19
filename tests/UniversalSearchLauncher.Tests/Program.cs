using System.Globalization;
using UniversalSearchLauncher.Core.Indexing;
using UniversalSearchLauncher.Core.Models;
using UniversalSearchLauncher.Core.Search;

namespace UniversalSearchLauncher.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length >= 3 && args[0].Equals("score", StringComparison.OrdinalIgnoreCase))
        {
            var prepared = PreparedQuery.Create(args[1]);
            var normalized = FuzzyMatcher.NormalizeForSearch(args[2]);
            Console.WriteLine("normalized: " + normalized);
            Console.WriteLine("score: " + FuzzyMatcher.Score(args[2], prepared).ToString("F2"));
            return 0;
        }

        if (args.Length >= 3 && args[0].Equals("score-apps", StringComparison.OrdinalIgnoreCase))
        {
            var json = File.ReadAllText(args[1]);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<AppEntry>>(
                document.RootElement.GetProperty("Entries").GetRawText()) ?? new List<AppEntry>();
            var prepared = PreparedQuery.Create(args[2]);
            foreach (var entry in entries)
            {
                var score = FuzzyMatcher.ScoreNormalized(entry.SearchText, prepared);
                if (score > 0)
                {
                    Console.WriteLine($"{score:F1}  {entry.Name}  [{entry.SearchText}]");
                }
            }

            return 0;
        }

        TestFuzzyMatcher();
        TestRanking();
        TestQueryIntent();
        TestExclusions();
        TestFileScanner();
        TestFileSearch();
        TestAppSearch();
        TestSettings();
        TestPerformance();

        Console.WriteLine();
        Console.WriteLine($"PASSED: {_passed}");
        Console.WriteLine($"FAILED: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestFuzzyMatcher()
    {
        Section("Fuzzy matching");
        AssertScore("chr", "Google Chrome", 850);
        AssertScore("tele", "Telegram Desktop", 880);
        AssertScore("vis", "Visual Studio Code", 880);
        AssertScore("down", "Downloads", 880);
        AssertScore("pho", "Adobe Photoshop 2025", 860);
        AssertScore("doc", "Project document", 850);
        AssertScore("vsc", "Visual Studio Code", 600);
        AssertScore("chorme", "Google Chrome", 280);
        AssertScore("code studio", "Visual Studio Code", 600);
        AssertTrue(Score("chrome", "Telegram Desktop") == 0, "unrelated query must not match");
        AssertTrue(Score("x", "Google Chrome") == 0, "single characters only match word prefixes");
        AssertTrue(Score("goo", "Google Chrome") > Score("gle", "Google Chrome"), "prefix outranks substring");
        AssertTrue(Score("chrome", "Google Chrome") > Score("chr", "Google Chrome"), "exact word outranks prefix");
    }

    private static void TestRanking()
    {
        Section("Ranking/usage");
        var entries = new List<AppEntry>
        {
            new() { Name = "Google Chrome", ExecutablePath = @"C:\Apps\chrome.exe" },
            new() { Name = "Chromium", ExecutablePath = @"C:\Apps\chromium.exe" },
            new() { Name = "Visual Studio Code", ExecutablePath = @"C:\Apps\Code.exe" }
        };

        var query = PreparedQuery.Create("chr");
        var usage = new Dictionary<string, UsageInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [entries[1].Identity] = new UsageInfo
            {
                Key = entries[1].Identity,
                Count = 30,
                LastUsedUtc = DateTime.UtcNow,
                Category = SearchCategory.Application
            }
        };
        var pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = AppSearchEngine.Search(entries, query, usage, pins, 10);
        AssertTrue(results.Count >= 2, "both Chrome and Chromium should be found");
        AssertTrue(results[0].Entry.Name == "Chromium", "usage boost should put Chromium first");

        pins.Add(entries[0].Identity);
        results = AppSearchEngine.Search(entries, query, usage, pins, 10);
        AssertTrue(results[0].Entry.Name == "Google Chrome", "pinned app should outrank recent app");
    }

    private static void TestQueryIntent()
    {
        Section("Query intent");
        var empty = QueryIntent.Parse("   ");
        AssertTrue(empty.Kind == QueryKind.Empty, "blank query is empty");

        var text = QueryIntent.Parse("погода завтра");
        AssertTrue(text.Kind == QueryKind.Text, "natural language is text");
        AssertTrue(
            text.GetSearchUri("Google").AbsoluteUri.Contains("google.com/search?q=", StringComparison.Ordinal),
            "Google search URL");
        AssertTrue(
            text.GetSearchUri("Bing").AbsoluteUri.Contains("bing.com/search?q=", StringComparison.Ordinal),
            "Bing search URL");
        AssertTrue(
            text.GetSearchUri("DuckDuckGo").AbsoluteUri.StartsWith("https://duckduckgo.com/?q=", StringComparison.Ordinal),
            "DuckDuckGo search URL");

        AssertTrue(QueryIntent.Parse("https://example.com/a?b=1").IsUrl, "absolute URL");
        AssertTrue(QueryIntent.Parse("example.com").IsUrl, "bare domain");
        AssertTrue(QueryIntent.Parse("github.com/foo").IsUrl, "domain with path");
        AssertTrue(QueryIntent.Parse("localhost:3000").IsUrl, "localhost");
        AssertTrue(!QueryIntent.Parse("report.pdf").IsUrl, "file name is not a domain");
        AssertTrue(!QueryIntent.Parse("readme.md").IsUrl, "markdown file is not a domain");
        AssertTrue(!QueryIntent.Parse("hello world").IsUrl, "space-containing text is not a URL");
        AssertTrue(!QueryIntent.Parse(@"C:\Users\test").IsUrl, "Windows path is not a URL");
    }

    private static void TestExclusions()
    {
        Section("Exclusions");
        var exclusions = ExclusionSet.Create(new[]
        {
            "node_modules",
            "*.tmp",
            "~$*",
            @".\secret\*",
            "bin"
        });

        AssertTrue(exclusions.IsExcluded(@"C:\Work\node_modules\pkg\index.js"), "node_modules segment");
        AssertTrue(exclusions.IsExcluded(@"C:\Work\file.tmp"), "extension wildcard");
        AssertTrue(exclusions.IsExcluded(@"C:\Work\~$report.docx"), "name wildcard");
        AssertTrue(exclusions.IsExcluded(@"C:\Work\secret\passwords.txt"), "path wildcard");
        AssertTrue(exclusions.IsExcluded(@"C:\Work\BIN\app.dll"), "case-insensitive segment");
        AssertTrue(!exclusions.IsExcluded(@"C:\Work\src\index.js"), "ordinary path stays");
        AssertTrue(!exclusions.IsExcluded(@"C:\Work\binary.txt"), "segment must match exactly");
    }

    private static void TestFileScanner()
    {
        Section("File scanner");
        var root = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".testdata",
            "scanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Documents"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(root, "Documents", "Quarterly Report.pdf"), "x");
        File.WriteAllText(Path.Combine(root, "Documents", "notes.tmp"), "x");
        File.WriteAllText(Path.Combine(root, "node_modules", "hidden.js"), "x");
        File.WriteAllText(Path.Combine(root, "Readme.md"), "x");

        try
        {
            var result = FileIndexScanner.Scan(
                new[] { root },
                new[] { "node_modules", "*.tmp" },
                10_000);

            AssertTrue(result.Entries.Any(entry => entry.Name == "Quarterly Report.pdf"), "index finds document");
            AssertTrue(result.Entries.Any(entry => entry.Name == "Documents" && entry.IsDirectory), "index includes folders");
            AssertTrue(result.Entries.Any(entry => entry.FullPath == root && entry.IsDirectory), "index includes root folder");
            AssertTrue(result.Entries.All(entry => !entry.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase)), "excluded folder");
            AssertTrue(result.Entries.All(entry => !entry.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)), "excluded extension");

            var query = PreparedQuery.Create("quart");
            var hits = FileSearchEngine.Search(result.Entries, query, 10, DateTime.UtcNow);
            AssertTrue(hits.Count > 0 && hits[0].Entry.Name == "Quarterly Report.pdf", "scan result is searchable");

            var limitResult = FileIndexScanner.Scan(new[] { root }, Array.Empty<string>(), 2);
            AssertTrue(limitResult.LimitReached && limitResult.Entries.Count <= 2, "entry limit is respected");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup must not mask assertions.
            }
        }
    }

    private static void TestFileSearch()
    {
        Section("File search");
        var now = DateTime.UtcNow;
        var entries = new List<FileEntry>
        {
            new()
            {
                FullPath = @"C:\Users\me\Documents\Annual Report 2025.pdf",
                Name = "Annual Report 2025.pdf",
                DirectoryPath = @"C:\Users\me\Documents",
                ParentName = "Documents",
                Extension = ".pdf",
                ModifiedUtc = now
            },
            new()
            {
                FullPath = @"C:\Users\me\Downloads",
                Name = "Downloads",
                DirectoryPath = @"C:\Users\me",
                ParentName = "me",
                IsDirectory = true,
                ModifiedUtc = now
            },
            new()
            {
                FullPath = @"C:\Users\me\Documents\photo.jpg",
                Name = "photo.jpg",
                DirectoryPath = @"C:\Users\me\Documents",
                ParentName = "Documents",
                Extension = ".jpg",
                ModifiedUtc = now
            }
        };

        var report = FileSearchEngine.Search(entries, PreparedQuery.Create("report"), 10, now);
        AssertTrue(report.Count > 0 && report[0].Entry.Name == "Annual Report 2025.pdf", "document by name");

        var down = FileSearchEngine.Search(entries, PreparedQuery.Create("down"), 10, now);
        AssertTrue(down.Count > 0 && down[0].Entry.Name == "Downloads", "folder by prefix");

        var pdf = FileSearchEngine.Search(entries, PreparedQuery.Create("pdf"), 10, now);
        AssertTrue(pdf.Count > 0 && pdf[0].Entry.Extension == ".pdf", "extension search");

        var noMatch = FileSearchEngine.Search(entries, PreparedQuery.Create("zzzz"), 10, now);
        AssertTrue(noMatch.Count == 0, "no false positives");
    }

    private static void TestAppSearch()
    {
        Section("App search");
        var entries = new List<AppEntry>
        {
            new() { Name = "Google Chrome", ShortcutPath = @"C:\Start\Chrome.lnk" },
            new() { Name = "Calculator", ExecutablePath = @"C:\Windows\System32\calc.exe" }
        };

        var results = AppSearchEngine.Search(
            entries,
            PreparedQuery.Create("calc"),
            new Dictionary<string, UsageInfo>(),
            new HashSet<string>(),
            10);
        AssertTrue(results.Count == 1 && results[0].Entry.Name == "Calculator", "application prefix");
        AssertTrue(results[0].Entry.Mode == LaunchMode.Executable, "plain executable launch mode");
    }

    private static void TestSettings()
    {
        Section("Settings");
        var settings = Settings.CreateDefault();
        AssertTrue(settings.IndexedFolders.Count > 0, "default folders are populated");
        AssertTrue(settings.Exclusions.Contains("node_modules"), "default exclusions");
        AssertTrue(!settings.ShowOverFullscreenApps, "fullscreen overlay is disabled by default");
        settings.Theme = "nonsense";
        settings.SearchEngine = "AskJeeves";
        settings.MaxResults = 99;
        settings.PriorityFiles = -4;
        settings.Hotkey = " ";
        settings.ShowOverFullscreenApps = false;
        settings.Normalize();
        AssertTrue(settings.Theme == "System", "invalid theme normalized");
        AssertTrue(settings.SearchEngine == "Google", "invalid engine normalized");
        AssertTrue(settings.MaxResults == 20, "max results clamped");
        AssertTrue(settings.PriorityFiles == 1, "priority clamped");
        AssertTrue(settings.Hotkey == "Alt+Space", "empty hotkey normalized");

        settings.ShowOverFullscreenApps = true;
        var clone = settings.Clone();
        clone.IndexedFolders.Add(@"C:\Extra");
        AssertTrue(settings.IndexedFolders.Count != clone.IndexedFolders.Count, "clone is independent");
        AssertTrue(clone.ShowOverFullscreenApps, "fullscreen option is cloned");
    }

    private static void TestPerformance()
    {
        Section("Performance");
        var now = DateTime.UtcNow;
        var entries = new List<FileEntry>(100_000);
        for (var i = 0; i < 100_000; i++)
        {
            var extension = i % 4 == 0 ? ".pdf" : ".txt";
            var entry = new FileEntry
            {
                FullPath = @"C:\Users\me\Documents\Generated\Document-" + i + extension,
                Name = "Document-" + i + extension,
                DirectoryPath = @"C:\Users\me\Documents\Generated",
                ParentName = "Generated",
                Extension = extension,
                ModifiedUtc = now
            };
            _ = entry.SearchText;
            entries.Add(entry);
        }

        PreparedQuery query = PreparedQuery.Create("document 500");
        _ = FileSearchEngine.Search(entries, PreparedQuery.Create("warmup"), 12, now);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var hits = FileSearchEngine.Search(entries, query, 12, now);
        stopwatch.Stop();
        Console.WriteLine($"  100k-file query: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        AssertTrue(hits.Count > 0, "performance search returns matches");
        AssertTrue(stopwatch.ElapsedMilliseconds < 500, "100k-file query under 500 ms");
    }

    private static double Score(string query, string title)
    {
        return FuzzyMatcher.Score(title, PreparedQuery.Create(query));
    }

    private static void AssertScore(string query, string title, double minimum)
    {
        var score = Score(query, title);
        AssertTrue(
            score >= minimum,
            $"'{query}' should match '{title}' with >= {minimum}, got {score:F0}");
    }

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name + " ==");
    }

    private static void AssertTrue(bool condition, string description)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  [ok] " + description);
        }
        else
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + description);
        }
    }
}
