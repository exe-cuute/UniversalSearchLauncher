using System.Text;
using System.Text.RegularExpressions;

namespace UniversalSearchLauncher.Core.Indexing;

/// <summary>
/// User-editable exclusion rules. Supports plain path segments (node_modules),
/// extension patterns (*.tmp), file-name wildcards (~$*) and full-path wildcards.
/// </summary>
public sealed class ExclusionSet
{
    private readonly List<Rule> _rules;
    private readonly string[] _emptySegments = Array.Empty<string>();

    private ExclusionSet(List<Rule> rules)
    {
        _rules = rules;
    }

    public int Count => _rules.Count;

    public static ExclusionSet Create(IEnumerable<string>? patterns)
    {
        var rules = new List<Rule>();
        if (patterns is not null)
        {
            foreach (var raw in patterns)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var pattern = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'))
                    .Replace('/', '\\');
                if (pattern.Length == 0)
                {
                    continue;
                }

                if (pattern.StartsWith(".\\", StringComparison.Ordinal))
                {
                    pattern = pattern[2..];
                }

                if (pattern.StartsWith("*.", StringComparison.Ordinal) &&
                    !pattern.Contains('\\') &&
                    !pattern.Contains('/') &&
                    !pattern[2..].Contains('*') &&
                    !pattern[2..].Contains('?'))
                {
                    rules.Add(new Rule(RuleKind.Extension, pattern[1..], null));
                }
                else if (pattern.Contains('*') || pattern.Contains('?'))
                {
                    var isPath = pattern.Contains('\\') || pattern.Contains('/');
                    var regex = isPath ? BuildPathWildcardRegex(pattern) : BuildWildcardRegex(pattern);
                    rules.Add(new Rule(isPath ? RuleKind.PathWildcard : RuleKind.SegmentWildcard, pattern, regex));
                }
                else if (pattern.Contains('\\') || pattern.Contains('/'))
                {
                    rules.Add(new Rule(RuleKind.PathContains, NormalizePath(pattern), null));
                }
                else if (pattern.StartsWith(".", StringComparison.Ordinal) && pattern.Length > 1)
                {
                    rules.Add(new Rule(RuleKind.Extension, pattern, null));
                }
                else
                {
                    rules.Add(new Rule(RuleKind.Segment, pattern, null));
                }
            }
        }

        return new ExclusionSet(rules);
    }

    public bool IsExcluded(string fullPath)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        var normalized = NormalizePath(fullPath);
        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(normalized);
        string[]? segments = null;

        foreach (var rule in _rules)
        {
            switch (rule.Kind)
            {
                case RuleKind.Extension:
                    if (extension.Equals(rule.Value, StringComparison.OrdinalIgnoreCase) ||
                        (rule.Value.StartsWith(".", StringComparison.Ordinal) &&
                         extension.Equals(rule.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    break;

                case RuleKind.Segment:
                    segments ??= SplitSegments(normalized);
                    foreach (var segment in segments)
                    {
                        if (segment.Equals(rule.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    break;

                case RuleKind.SegmentWildcard:
                    segments ??= SplitSegments(normalized);
                    if (rule.Regex!.IsMatch(fileName))
                    {
                        return true;
                    }

                    foreach (var segment in segments)
                    {
                        if (rule.Regex.IsMatch(segment))
                        {
                            return true;
                        }
                    }

                    break;

                case RuleKind.PathContains:
                    var normalizedRule = rule.Value.TrimEnd('\\');
                    if (normalized.Length == normalizedRule.Length
                        ? normalized.Equals(normalizedRule, StringComparison.OrdinalIgnoreCase)
                        : normalized.StartsWith(normalizedRule + "\\", StringComparison.OrdinalIgnoreCase) ||
                          normalized.Contains("\\" + normalizedRule + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    break;

                case RuleKind.PathWildcard:
                    if (rule.Regex!.IsMatch(normalized))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static string[] SplitSegments(string path)
    {
        return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }

    private static Regex BuildWildcardRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        foreach (var ch in pattern.Replace('/', '\\'))
        {
            switch (ch)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                default:
                    builder.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        builder.Append('$');
        return CreateRegex(builder.ToString());
    }

    private static Regex BuildPathWildcardRegex(string pattern)
    {
        var builder = new StringBuilder("^.*");
        foreach (var ch in pattern.Replace('/', '\\').TrimStart('\\'))
        {
            switch (ch)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                default:
                    builder.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        builder.Append('$');
        return CreateRegex(builder.ToString());
    }

    private static Regex CreateRegex(string pattern)
    {
        return new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(50));
    }

    private enum RuleKind
    {
        Extension,
        Segment,
        SegmentWildcard,
        PathContains,
        PathWildcard
    }

    private sealed record Rule(RuleKind Kind, string Value, Regex? Regex);
}
