using System.Globalization;
using System.Text;

namespace UniversalSearchLauncher.Core.Search;

/// <summary>
/// Immutable, pre-processed query shared by every item scored for one keystroke.
/// </summary>
public sealed class PreparedQuery
{
    private PreparedQuery(string raw, string normalized, string[] tokens)
    {
        Raw = raw;
        Normalized = normalized;
        Tokens = tokens;
    }

    public string Raw { get; }
    public string Normalized { get; }
    public IReadOnlyList<string> Tokens { get; }
    public bool IsEmpty => Tokens.Count == 0;
    public bool HasMultipleTokens => Tokens.Count > 1;

    public static PreparedQuery Create(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        var normalized = FuzzyMatcher.NormalizeForSearch(value);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new PreparedQuery(value, normalized, tokens);
    }
}

/// <summary>
/// Ranker combining exact, prefix, word-prefix, substring, acronym, subsequence
/// and typo-tolerant matching. It is deliberately allocation-light because it is
/// called for every indexed item on every keystroke.
/// </summary>
public static class FuzzyMatcher
{
    public const double MinimumUsefulScore = 280;

    public static string NormalizeForSearch(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var source = value.Replace('ё', 'е').Replace('Ё', 'Е');
        var withBoundaries = new StringBuilder(source.Length + 8);
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            if (i > 0 && char.IsUpper(ch))
            {
                var previous = source[i - 1];
                var nextIsLower = i + 1 < source.Length && char.IsLower(source[i + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                {
                    withBoundaries.Append(' ');
                }
            }

            withBoundaries.Append(ch);
        }

        var lowered = withBoundaries.ToString().ToLowerInvariant();

        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length + 8);
        var lastWasSpace = true;

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    public static double Score(string? rawText, PreparedQuery query)
    {
        if (query.IsEmpty || string.IsNullOrWhiteSpace(rawText))
        {
            return 0;
        }

        return ScoreNormalized(NormalizeForSearch(rawText), query);
    }

    public static double ScoreNormalized(string normalizedText, PreparedQuery query)
    {
        if (query.IsEmpty || string.IsNullOrEmpty(normalizedText))
        {
            return 0;
        }

        var text = normalizedText.Trim();
        if (text.Length == 0)
        {
            return 0;
        }

        if (query.Tokens.Count == 1)
        {
            return ScoreToken(text, query.Tokens[0]);
        }

        var total = 0d;
        var allPrefixes = true;
        foreach (var token in query.Tokens)
        {
            var tokenScore = ScoreToken(text, token);
            if (tokenScore <= 0)
            {
                return 0;
            }

            total += tokenScore;
            if (!IsWordPrefix(text, token) && !string.Equals(text, token, StringComparison.Ordinal))
            {
                allPrefixes = false;
            }
        }

        var average = total / query.Tokens.Count;
        var bonus = 0d;

        if (text.Contains(query.Normalized, StringComparison.Ordinal))
        {
            bonus += 55;
        }

        if (allPrefixes)
        {
            bonus += 35;
        }

        // Reward queries whose words occur in the same order as the title.
        if (TokensAppearInOrder(text, query.Tokens))
        {
            bonus += 20;
        }

        return Math.Min(1000, average + bonus);
    }

    private static double ScoreToken(string text, string token)
    {
        if (token.Length == 0)
        {
            return 0;
        }

        if (string.Equals(text, token, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (token.Length == 1)
        {
            return IsWordPrefix(text, token) || text.StartsWith(token, StringComparison.Ordinal) ? 860 : 0;
        }

        var exactWord = FindWord(text, token);
        if (exactWord >= 0)
        {
            return 990 - Math.Min(25, exactWord * 0.2);
        }

        var prefixScore = BestWordPrefixScore(text, token);
        if (prefixScore > 0)
        {
            return prefixScore;
        }

        if (text.StartsWith(token, StringComparison.Ordinal))
        {
            return 900 - Math.Min(45, (text.Length - token.Length) * 0.35);
        }

        var index = text.IndexOf(token, StringComparison.Ordinal);
        if (index >= 0)
        {
            var boundary = index == 0 || text[index - 1] == ' ';
            return (boundary ? 820 : 760)
                   - Math.Min(90, index * 1.8)
                   - Math.Min(30, Math.Max(0, text.Length - token.Length) * 0.08);
        }

        if (token.Length >= 2)
        {
            var acronym = AcronymScore(text, token);
            if (acronym > 0)
            {
                return acronym;
            }
        }

        if (token.Length >= 4)
        {
            var subsequence = BestSubsequenceScore(text, token);
            if (subsequence > 0)
            {
                return subsequence;
            }
        }

        if (token.Length >= 4)
        {
            var typo = TypoScore(text, token);
            if (typo > 0)
            {
                return typo;
            }
        }

        return 0;
    }

    private static int FindWord(string text, string token)
    {
        var start = 0;
        var index = 0;
        while (start <= text.Length)
        {
            var end = text.IndexOf(' ', start);
            if (end < 0)
            {
                end = text.Length;
            }

            if (end - start == token.Length && string.CompareOrdinal(text, start, token, 0, token.Length) == 0)
            {
                return index;
            }

            if (end >= text.Length)
            {
                break;
            }

            start = end + 1;
            index++;
        }

        return -1;
    }

    private static bool IsWordPrefix(string text, string token)
    {
        return BestWordPrefixScore(text, token) > 0;
    }

    private static double BestWordPrefixScore(string text, string token)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf(' ', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var length = end - start;
            if (length >= token.Length &&
                string.CompareOrdinal(text, start, token, 0, token.Length) == 0)
            {
                if (length == token.Length)
                {
                    return 990 - Math.Min(20, start * 0.1);
                }

                // Exact word prefixes are the strongest signal after exact equal names.
                return 920 - Math.Min(70, (length - token.Length) * 0.8) - Math.Min(20, start * 0.08);
            }

            if (end >= text.Length)
            {
                break;
            }

            start = end + 1;
        }

        return 0;
    }

    private static double AcronymScore(string text, string token)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return 0;
        }

        var initials = new StringBuilder(words.Length);
        foreach (var word in words)
        {
            initials.Append(word[0]);
        }

        var value = initials.ToString();

        if (value.StartsWith(token, StringComparison.Ordinal))
        {
            return 800 - Math.Min(70, (value.Length - token.Length) * 8);
        }

        if (value.EndsWith(token, StringComparison.Ordinal))
        {
            return 740 - Math.Min(60, (value.Length - token.Length) * 6);
        }

        return 0;
    }

    private static double BestSubsequenceScore(string text, string token)
    {
        var best = 0d;
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf(' ', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var word = text.AsSpan(start, end - start);
            if (word.Length >= token.Length)
            {
                var score = SubsequenceScore(word, token);
                if (score > best)
                {
                    best = score;
                }
            }

            if (end >= text.Length)
            {
                break;
            }

            start = end + 1;
        }

        return best;
    }

    private static double SubsequenceScore(ReadOnlySpan<char> word, string token)
    {
        var tokenIndex = 0;
        var start = -1;
        var last = -1;
        var gaps = 0;

        for (var i = 0; i < word.Length && tokenIndex < token.Length; i++)
        {
            if (word[i] != token[tokenIndex])
            {
                continue;
            }

            if (start < 0)
            {
                start = i;
            }
            else
            {
                gaps += i - last - 1;
            }

            last = i;
            tokenIndex++;
        }

        if (tokenIndex != token.Length)
        {
            return 0;
        }

        var spanLength = last - start + 1;
        if (spanLength <= 0)
        {
            return 0;
        }

        var density = (double)token.Length / spanLength;
        if (density < 0.5 && token.Length <= 4)
        {
            return 0;
        }

        if (density < 0.38)
        {
            return 0;
        }

        var score = 360 + (density * 240);
        if (start == 0)
        {
            score += 45;
        }

        if (gaps == 0)
        {
            score += 40;
        }

        score -= Math.Min(55, gaps * 4.0);
        return Math.Clamp(score, 300, 690);
    }

    private static double TypoScore(string text, string token)
    {
        var maxDistance = token.Length >= 6 ? 2 : 1;
        var best = 0d;
        var start = 0;

        while (start < text.Length)
        {
            var end = text.IndexOf(' ', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var length = end - start;
            if (Math.Abs(length - token.Length) <= maxDistance)
            {
                var distance = LevenshteinDistance(
                    text.AsSpan(start, length),
                    token,
                    maxDistance);

                if (distance == 1)
                {
                    var score = 650 - Math.Min(80, Math.Abs(length - token.Length) * 25);
                    if (score > best)
                    {
                        best = score;
                    }
                }
                else if (distance == 2 && token.Length >= 6)
                {
                    var score = 500 - Math.Min(50, Math.Abs(length - token.Length) * 20);
                    if (score > best)
                    {
                        best = score;
                    }
                }
            }

            if (end >= text.Length)
            {
                break;
            }

            start = end + 1;
        }

        return best;
    }

    private static int LevenshteinDistance(ReadOnlySpan<char> source, string target, int maxDistance)
    {
        if (Math.Abs(source.Length - target.Length) > maxDistance)
        {
            return maxDistance + 1;
        }

        Span<int> previous = stackalloc int[target.Length + 1];
        Span<int> current = stackalloc int[target.Length + 1];

        for (var j = 0; j <= target.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];

            for (var j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                current[j] = value;
                if (value < rowMinimum)
                {
                    rowMinimum = value;
                }
            }

            if (rowMinimum > maxDistance)
            {
                return maxDistance + 1;
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[target.Length];
    }

    private static bool TokensAppearInOrder(string text, IReadOnlyList<string> tokens)
    {
        var position = 0;
        foreach (var token in tokens)
        {
            var index = text.IndexOf(token, position, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            position = index + token.Length;
        }

        return true;
    }
}
