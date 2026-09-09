using System.Text.RegularExpressions;

namespace Atlas;

/// <summary>
/// Repairs presentation-only line-break artifacts from legacy imports without mutating stored source data.
/// </summary>
public static class DisplayText
{
    /// <summary>Converts legacy escaped and stripped newline markers into displayable paragraph breaks.</summary>
    public static string NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value
            .Replace("\\r\\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\r", "\n", StringComparison.OrdinalIgnoreCase);

        // Some early wiki imports stripped the backslashes from CRLF twice, leaving literal "rnrn".
        normalized = Regex.Replace(normalized, "(?:rn){2,}", "\n\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"[ \t]*\r?\n[ \t]*", "\n", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        return normalized.Trim();
    }

    /// <summary>Returns normalized description text collapsed to one line for cards and metadata.</summary>
    public static string CompactDescription(string? value) =>
        Regex.Replace(NormalizeDescription(value), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
}
