using System.Globalization;
using System.Text.RegularExpressions;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>A candidate coordinate parsed from wiki text. Y is ignored; only the X/Z plane is compared.</summary>
/// <param name="X">The parsed X block coordinate.</param>
/// <param name="Z">The parsed Z block coordinate.</param>
public readonly record struct WikiCoordinate(long X, long Z);

/// <summary>
/// Extracts candidate <see cref="WikiCoordinate"/> pairs from MediaWiki article text. 2b2t pages express
/// coordinates in several informal ways (labeled <c>X:/Z:</c>, an infobox <c>coordinates =</c> field, a
/// <c>{{Coord}}</c> template, or a parenthesized triple), so this scans for the common, low-false-positive
/// shapes. Missed coordinates are safe: they only lower match confidence, never fabricate agreement.
/// </summary>
public static partial class WikiCoordinateExtractor
{
    // X <sep> [Y <sep>] Z, each optionally labeled and comma-grouped; e.g. "X: 1,234 Y 64 Z -5678".
    [GeneratedRegex(@"[Xx]\s*[:=]?\s*(-?\d[\d,]{0,12})[^0-9\-]{1,14}?(?:[Yy]\s*[:=]?\s*-?\d[\d,]{0,12}[^0-9\-]{1,14}?)?[Zz]\s*[:=]?\s*(-?\d[\d,]{0,12})",
        RegexOptions.CultureInvariant)]
    private static partial Regex LabeledPattern();

    // Infobox field: "coordinates = 1234, [64,] -5678" or "coords = ...".
    [GeneratedRegex(@"coord(?:inate)?s?\s*=\s*(-?\d[\d,]{0,12})\s*,\s*(?:-?\d[\d,]{0,12}\s*,\s*)?(-?\d[\d,]{0,12})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InfoboxPattern();

    // {{Coord|1234|-5678}} template (optional middle Y argument).
    [GeneratedRegex(@"\{\{\s*[Cc]oord\s*\|\s*(-?\d[\d,]{0,12})\s*\|\s*(?:-?\d[\d,]{0,12}\s*\|\s*)?(-?\d[\d,]{0,12})",
        RegexOptions.CultureInvariant)]
    private static partial Regex CoordTemplatePattern();

    /// <summary>Extracts distinct candidate coordinates from the given wiki text.</summary>
    /// <param name="wikitext">Raw article wikitext or plaintext; may be <see langword="null"/>.</param>
    /// <returns>Distinct parsed coordinates; empty when none are found.</returns>
    public static IReadOnlyList<WikiCoordinate> Extract(string? wikitext)
    {
        if (string.IsNullOrWhiteSpace(wikitext))
            return [];

        var found = new HashSet<WikiCoordinate>();
        foreach (var pattern in new[] { InfoboxPattern(), CoordTemplatePattern(), LabeledPattern() })
        {
            foreach (Match match in pattern.Matches(wikitext))
            {
                if (TryParse(match.Groups[1].Value, out var x) && TryParse(match.Groups[2].Value, out var z))
                    found.Add(new WikiCoordinate(x, z));
            }
        }
        return found.ToArray();
    }

    /// <summary>Parses a possibly comma-grouped signed integer within the plausible 2b2t coordinate range.</summary>
    private static bool TryParse(string value, out long result)
    {
        result = 0;
        var cleaned = value.Replace(",", string.Empty);
        if (!long.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
            return false;
        // Reject coordinates beyond the ~30M world border (with margin) to drop stray numbers.
        return result is >= -35_000_000 and <= 35_000_000;
    }
}
