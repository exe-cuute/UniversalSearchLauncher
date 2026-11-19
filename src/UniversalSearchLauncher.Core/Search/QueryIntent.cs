using System.Text.RegularExpressions;

namespace UniversalSearchLauncher.Core.Search;

public enum QueryKind
{
    Empty,
    Text,
    Url
}

/// <summary>
/// Parses a query into a web action / direct URL while preserving the text used
/// by local providers. A bare file name such as "report.pdf" is not a URL.
/// </summary>
public sealed partial class QueryIntent
{
    private static readonly HashSet<string> FileLikeTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv",
        "json", "xml", "yaml", "yml", "ini", "cfg", "log", "rtf", "odt", "ods",
        "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "ico", "tif", "tiff",
        "mp3", "wav", "flac", "aac", "ogg", "m4a", "mp4", "mkv", "avi", "mov",
        "wmv", "webm", "zip", "rar", "7z", "tar", "gz", "exe", "msi", "dll",
        "ps1", "bat", "cmd", "cs", "js", "ts", "py", "java", "cpp", "h", "css",
        "html", "htm", "db", "sqlite", "iso", "apk", "appx", "msix", "lnk"
    };

    private static readonly HashSet<string> KnownTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "edu", "gov", "mil", "int", "io", "dev", "app",
        "ai", "co", "me", "info", "biz", "xyz", "online", "site", "store",
        "tech", "cloud", "page", "blog", "shop", "pro", "name", "tv", "cc",
        "ru", "su", "by", "kz", "ua", "uk", "de", "fr", "it", "es", "nl",
        "pl", "cz", "se", "no", "fi", "dk", "ch", "at", "be", "pt", "gr",
        "tr", "jp", "cn", "kr", "in", "br", "ca", "au", "nz", "za", "mx",
        "ar", "cl", "us", "eu", "live", "news", "world", "today", "email",
        "design", "studio", "agency", "digital", "software", "tools", "games",
        "wiki", "zone", "space", "website", "network", "systems", "solutions"
    };

    private QueryIntent(string raw, string text, QueryKind kind, Uri? directUri)
    {
        Raw = raw;
        Text = text;
        Kind = kind;
        DirectUri = directUri;
    }

    public string Raw { get; }
    public string Text { get; }
    public QueryKind Kind { get; }
    public Uri? DirectUri { get; }
    public bool IsEmpty => Kind == QueryKind.Empty;
    public bool IsUrl => Kind == QueryKind.Url;

    public static QueryIntent Parse(string? query)
    {
        var raw = (query ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return new QueryIntent(raw, string.Empty, QueryKind.Empty, null);
        }

        if (TryParseDirectUrl(raw, out var uri))
        {
            return new QueryIntent(raw, raw, QueryKind.Url, uri);
        }

        return new QueryIntent(raw, raw, QueryKind.Text, null);
    }

    public static bool TryParseDirectUrl(string? input, out Uri? uri)
    {
        uri = null;
        var value = (input ?? string.Empty).Trim();
        if (value.Length < 3 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrWhiteSpace(absolute.Host))
        {
            uri = absolute;
            return true;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
            if (Uri.TryCreate(value, UriKind.Absolute, out absolute))
            {
                uri = absolute;
                return true;
            }

            return false;
        }

        var authority = value;
        var slash = authority.IndexOfAny(new[] { '/', '?', '#' });
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        if (authority.Length == 0 || authority.Contains('@'))
        {
            return false;
        }

        var host = authority;
        var colon = authority.LastIndexOf(':');
        if (colon > 0)
        {
            var port = authority[(colon + 1)..];
            if (port.Length is > 0 and <= 5 && port.All(char.IsDigit))
            {
                host = authority[..colon];
            }
        }

        var isLocalhost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var isIp = System.Net.IPAddress.TryParse(host, out _);

        if (!isLocalhost && !isIp)
        {
            if (!host.Contains('.', StringComparison.Ordinal) ||
                host.StartsWith(".", StringComparison.Ordinal) ||
                host.EndsWith(".", StringComparison.Ordinal) ||
                host.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }

            var labels = host.Split('.');
            if (labels.Any(label => label.Length == 0 || label.Length > 63 ||
                                    label.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-')))
            {
                return false;
            }

            var tld = labels[^1];
            if (tld.Length is < 2 or > 24 || !tld.All(char.IsLetter))
            {
                return false;
            }

            if (FileLikeTlds.Contains(tld))
            {
                return false;
            }

            // Unknown TLDs are accepted, but a known TLD needs no guessing.
            if (!KnownTlds.Contains(tld) && !tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
            {
                // Still accept it when the host has an explicit www prefix.
                if (!host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        var candidate = "https://" + value;
        return Uri.TryCreate(candidate, UriKind.Absolute, out uri) &&
               !string.IsNullOrWhiteSpace(uri.Host);
    }

    public Uri GetSearchUri(string? engine)
    {
        var encoded = Uri.EscapeDataString(Text);
        return engine switch
        {
            "Bing" => new Uri("https://www.bing.com/search?q=" + encoded),
            "DuckDuckGo" => new Uri("https://duckduckgo.com/?q=" + encoded),
            _ => new Uri("https://www.google.com/search?q=" + encoded)
        };
    }

    public static string GetSearchEngineDisplayName(string? engine)
    {
        return engine switch
        {
            "Bing" => "Bing",
            "DuckDuckGo" => "DuckDuckGo",
            _ => "Google"
        };
    }
}
