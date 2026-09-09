using System.Text.RegularExpressions;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>
/// Lightweight name comparison used to gauge how strongly an Atlas location name matches a wiki page
/// title. It complements the coordinate gate: an exact normalized title plus coordinate agreement is a
/// strong match, whereas a mere token overlap is weak and routes to manual review.
/// </summary>
public static partial class NameSimilarity
{
    [GeneratedRegex(@"[^a-z0-9 ]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>Normalizes a name to lowercase alphanumerics and single spaces for comparison.</summary>
    /// <param name="value">The raw name or title.</param>
    /// <returns>The normalized form, or an empty string.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var lowered = value.ToLowerInvariant().Replace('&', ' ');
        return Whitespace().Replace(NonAlphanumeric().Replace(lowered, " "), " ").Trim();
    }

    /// <summary>Scores two names from 0 (unrelated) to 1 (identical after normalization).</summary>
    /// <param name="a">The first name.</param>
    /// <param name="b">The second name.</param>
    /// <returns>A similarity score in the range 0..1.</returns>
    public static double Score(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
            return 0;
        if (left == right)
            return 1.0;
        if (left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal))
            return 0.8;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
            return 0.6;

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0;
        var shared = leftTokens.Count(rightTokens.Contains);
        return shared == 0 ? 0 : Math.Min(0.5, shared / (double)Math.Max(leftTokens.Count, rightTokens.Count));
    }
}
