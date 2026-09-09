using System.Globalization;
using System.Text.RegularExpressions;

namespace Atlas;

/// <summary>The single Archive teleport command inferred for one immutable WDL.</summary>
/// <param name="Name">Command argument entered after <c>/warp</c>.</param>
/// <param name="Source">Reviewable evidence source used to infer the name.</param>
/// <param name="Confidence">Confidence in the name itself, independent of its location match.</param>
/// <param name="IsTrusted">Whether authenticated operator input and/or recognized Archive metadata supports automatic use.</param>
public sealed record ArchiveWarpCandidate(string Name, string Source, double Confidence, bool IsTrusted);

/// <summary>Resolves at most one Archive warp from bounded downloader metadata and operator attribution.</summary>
public static partial class ArchiveWarpResolver
{
    /// <summary>The highest block layer retained by Sky Masons cutaway renders.</summary>
    public const int SkyMasonsCutawayTopY = 254;

    private static readonly IReadOnlyDictionary<string, string> IterationAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["i"] = "1", ["one"] = "1", ["first"] = "1",
            ["ii"] = "2", ["two"] = "2", ["second"] = "2",
            ["iii"] = "3", ["three"] = "3", ["third"] = "3",
            ["iv"] = "4", ["four"] = "4", ["fourth"] = "4",
            ["v"] = "5", ["five"] = "5", ["fifth"] = "5",
            ["vi"] = "6", ["six"] = "6", ["sixth"] = "6",
            ["vii"] = "7", ["seven"] = "7", ["seventh"] = "7",
            ["viii"] = "8", ["eight"] = "8", ["eighth"] = "8",
            ["ix"] = "9", ["nine"] = "9", ["ninth"] = "9",
            ["x"] = "10", ["ten"] = "10", ["tenth"] = "10",
        };

    /// <summary>Infers the one Archive warp associated with an uploaded WDL.</summary>
    public static ArchiveWarpCandidate? Resolve(
        ArchiveWdlEvidence? evidence,
        string? originalFileName,
        string? declaredSource,
        string? operatorWarpName = null)
    {
        if (TryClean(operatorWarpName, out var explicitName))
            return new ArchiveWarpCandidate(explicitName, "operator", 1, true);

        if (evidence?.IsArchiveSource == true && TryClean(evidence.DownloadName, out var reportName))
            return new ArchiveWarpCandidate(reportName, "archive-download-report", 0.99, true);

        var archiveAttributed = evidence?.IsArchiveSource == true || IsArchiveAttribution(declaredSource);
        if (archiveAttributed && TryClean(Path.GetFileNameWithoutExtension(originalFileName), out var fileName))
        {
            var withoutCaptureTimestamp = WorldToolsEpochSuffix().Replace(fileName, string.Empty).Trim(' ', '-', '_');
            if (TryClean(withoutCaptureTimestamp, out var cleaned))
                return new ArchiveWarpCandidate(cleaned, "archive-filename", evidence?.IsArchiveSource == true ? 0.94 : 0.9, true);
        }

        return null;
    }

    /// <summary>Normalizes a warp for uniqueness and identity comparisons without erasing meaningful dates.</summary>
    public static string Normalize(string? value)
    {
        if (!TryClean(value, out var cleaned)) return string.Empty;
        return Whitespace().Replace(NonAlphanumeric().Replace(cleaned.ToLowerInvariant(), " "), " ").Trim();
    }

    /// <summary>
    /// Returns whether Archive provenance explicitly identifies a capture as a single-player concept build.
    /// Concept captures are useful preservation artifacts, but must never be presented as live-server snapshots.
    /// </summary>
    public static bool IsSinglePlayerConcept(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token is "concept" or "concepts");
    }

    /// <summary>
    /// Normalizes the stable location portion of a warp by removing common capture dates and Archive ordering prefixes.
    /// This allows two dated WDLs of the same place to support a match without treating them as the same WDL.
    /// </summary>
    public static string Identity(string? value)
    {
        if (!TryClean(value, out var cleaned)) return string.Empty;
        cleaned = ArchiveProvenanceSuffix().Replace(cleaned, string.Empty);
        cleaned = ExhibitCollectionPrefix().Replace(cleaned, string.Empty);
        var normalized = Normalize(DimensionQualifier().Replace(cleaned, string.Empty));
        if (normalized.Length == 0) return string.Empty;
        normalized = ArchiveOrderingPrefix().Replace(normalized, string.Empty);
        normalized = IsoDate().Replace(normalized, " ");
        normalized = QuarterDate().Replace(normalized, " ");
        normalized = YearMonthSuffix().Replace(normalized, " ");
        normalized = YearOnlySuffix().Replace(normalized, " ");
        normalized = CaptureQualifier().Replace(normalized, " ");
        normalized = Whitespace().Replace(normalized, " ").Trim();
        return PitFightCompound().Replace(normalized, "pit fight");
    }

    /// <summary>Builds a human-readable location name while retaining the warp's original Unicode and casing.</summary>
    public static string DisplayIdentity(string? value)
    {
        if (!TryClean(value, out var cleaned)) return string.Empty;
        if (TryParseMilestone(cleaned, out var milestone)) return milestone.DisplayName;
        return DisplayRegularIdentity(cleaned);
    }

    private static string DisplayRegularIdentity(string cleaned)
    {
        cleaned = ArchiveProvenanceSuffix().Replace(cleaned, string.Empty);
        cleaned = ExhibitCollectionPrefix().Replace(cleaned, string.Empty);
        cleaned = DimensionQualifier().Replace(cleaned, string.Empty);
        cleaned = TrailingArticle().Replace(cleaned,
            match => $"The_{match.Groups["name"].Value}{match.Groups["separator"].Value}");
        var display = Whitespace().Replace(NonAlphanumeric().Replace(cleaned, " "), " ").Trim();
        display = ArchiveOrderingPrefix().Replace(display, string.Empty);
        display = IsoDate().Replace(display, " ");
        display = QuarterDate().Replace(display, " ");
        display = YearMonthSuffix().Replace(display, " ");
        display = YearOnlySuffix().Replace(display, " ");
        display = CaptureQualifier().Replace(display, " ");
        display = Whitespace().Replace(display, " ").Trim();
        return PitFightCompound().Replace(display, "Pit Fight");
    }

    /// <summary>
    /// Returns whether two otherwise-identical location identities specify different numbered iterations.
    /// Capture dates are removed first, but sequel numbers (including common Roman numerals and number words)
    /// are identity-bearing: <c>Bedrock City</c> and <c>Bedrock City 2</c> are different Atlas locations.
    /// </summary>
    public static bool HasIterationConflict(string? left, string? right)
    {
        var (leftCore, leftIteration) = SplitIteration(Identity(left));
        var (rightCore, rightIteration) = SplitIteration(Identity(right));
        // Archive authors commonly make the original base explicit as "1"/"I", while the
        // older Atlas row simply uses the base name. Both spellings mean the first iteration.
        // Later iterations remain identity-bearing and must never be folded into the original.
        leftIteration ??= "1";
        rightIteration ??= "1";
        return leftCore.Length > 0 && leftCore == rightCore &&
            !string.Equals(leftIteration, rightIteration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Produces the stable location identity used for matching. An omitted iteration and an explicit
    /// first iteration (<c>1</c>, <c>I</c>, or <c>one</c>) are canonicalized to the same value;
    /// second and later iterations remain part of the identity.
    /// </summary>
    public static string CanonicalLocationIdentity(string? value)
    {
        if (TryClean(value, out var cleaned) && TryParseMilestone(cleaned, out var milestone))
            return milestone.CanonicalIdentity;
        var (core, iteration) = SplitIteration(Identity(value));
        if (core.Length == 0) return string.Empty;
        return iteration is null or "1" ? core : $"{core} {iteration}";
    }

    /// <summary>
    /// Gets an explicit Archive exhibit collection prefix such as <c>Sky:</c>. The prefix is
    /// provenance, while the leaf remains the location identity.
    /// </summary>
    public static string ExplicitExhibitCollection(string? value)
    {
        if (!TryClean(value, out var cleaned)) return string.Empty;
        var match = ExhibitCollectionPrefix().Match(cleaned);
        return match.Success ? Normalize(match.Groups["collection"].Value) : string.Empty;
    }

    /// <summary>
    /// Returns the safe historical cutaway for a recognized Spawnmasons <c>Sky:</c> exhibit.
    /// The dedicated <c>Ceiling</c> capture remains uncut so the parent can preserve the roof itself.
    /// </summary>
    public static int? RecommendedRenderTopY(ArchiveWarpCandidate? candidate)
    {
        if (candidate?.IsTrusted != true ||
            !TryClean(candidate.Name, out var cleaned) ||
            !ArchiveProvenanceSuffix().IsMatch(cleaned) ||
            !string.Equals(ExplicitExhibitCollection(cleaned), "sky", StringComparison.Ordinal) ||
            string.Equals(CanonicalLocationIdentity(cleaned), "ceiling", StringComparison.Ordinal))
            return null;
        return SkyMasonsCutawayTopY;
    }

    private static bool TryParseMilestone(string value, out MilestoneIdentity milestone)
    {
        milestone = default;
        var withoutDimensionSuffix = DimensionQualifier().Replace(value, string.Empty).Trim();
        var pair = CoordinatePairMilestone().Match(withoutDimensionSuffix);
        if (pair.Success)
        {
            var label = CleanMilestoneLabel(pair.Groups["rest"].Value, out var dimension);
            var x = ParseMilestoneNumber(pair.Groups["x"].Value);
            var z = ParseMilestoneNumber(pair.Groups["z"].Value);
            if (x is null || z is null) return false;
            var coordinate = $"{FormatMilestoneAxis('X', x.Value)} / {FormatMilestoneAxis('Z', z.Value)}";
            milestone = BuildMilestone(dimension, coordinate,
                $"pair|{CanonicalNumber(x.Value)}|{CanonicalNumber(z.Value)}", label);
            return true;
        }

        var axis = AxisMilestone().Match(withoutDimensionSuffix);
        if (!axis.Success) return false;
        var number = ParseMilestoneNumber(axis.Groups["value"].Value);
        if (number is null) return false;
        var axisName = char.ToUpperInvariant(axis.Groups["axis"].Value[0]);
        var axisLabel = CleanMilestoneLabel(axis.Groups["rest"].Value, out var axisDimension);
        milestone = BuildMilestone(axisDimension, FormatMilestoneAxis(axisName, number.Value),
            $"axis|{char.ToLowerInvariant(axisName)}|{CanonicalNumber(number.Value)}", axisLabel);
        return true;
    }

    private static MilestoneIdentity BuildMilestone(
        string? dimension, string coordinate, string coordinateIdentity, string label)
    {
        var dimensionPrefix = dimension is null ? string.Empty : $"{dimension} ";
        var coordinateName = $"{dimensionPrefix}{coordinate}";
        var display = label.Length == 0 ? $"{coordinateName} Milestone" : $"{label} ({coordinateName})";
        var labelIdentity = label.Length == 0 ? string.Empty : $"|{Normalize(label)}";
        return new MilestoneIdentity(display,
            $"milestone|{dimension?.ToLowerInvariant() ?? "overworld"}|{coordinateIdentity}{labelIdentity}");
    }

    private static string CleanMilestoneLabel(string value, out string? dimension)
    {
        dimension = null;
        var dimensionMatch = InlineDimension().Match(value);
        if (dimensionMatch.Success)
        {
            dimension = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                dimensionMatch.Groups["dimension"].Value.ToLowerInvariant());
            value = InlineDimension().Replace(value, " ");
        }
        return DisplayRegularIdentity(value.Trim(' ', '_', ',', '-'));
    }

    private static decimal? ParseMilestoneNumber(string value) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string FormatMilestoneAxis(char axis, decimal millions)
    {
        var sign = millions < 0 ? "-" : "+";
        var absolute = Math.Abs(millions);
        if (absolute < 1)
            return $"{sign}{axis} {(absolute * 1000).ToString("0.###", CultureInfo.InvariantCulture)}k";
        return $"{sign}{axis} {absolute.ToString("0.###", CultureInfo.InvariantCulture)}M";
    }

    private static string CanonicalNumber(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct MilestoneIdentity(string DisplayName, string CanonicalIdentity);

    private static (string Core, string? Iteration) SplitIteration(string identity)
    {
        if (identity.Length == 0) return (string.Empty, null);
        var tokens = identity.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return (identity, null);
        var final = tokens[^1];
        string? iteration = null;
        if (uint.TryParse(final, out var numeric) && numeric is > 0 and <= 999)
            iteration = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if (IterationAliases.TryGetValue(final, out var alias))
            iteration = alias;
        else if (TryParseRomanIteration(final, out var roman))
            iteration = roman.ToString(CultureInfo.InvariantCulture);
        if (iteration is null) return (identity, null);
        return (string.Join(' ', tokens[..^1]), iteration);
    }

    private static bool TryParseRomanIteration(string value, out int result)
    {
        result = 0;
        if (!RomanNumeral().IsMatch(value)) return false;
        var previous = 0;
        for (var index = value.Length - 1; index >= 0; index--)
        {
            var current = value[index] switch
            {
                'i' => 1,
                'v' => 5,
                'x' => 10,
                'l' => 50,
                'c' => 100,
                _ => 0,
            };
            result += current < previous ? -current : current;
            previous = current;
        }
        return result is > 0 and <= 100;
    }

    private static bool IsArchiveAttribution(string? value) =>
        value?.Contains("archive", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryClean(string? value, out string cleaned)
    {
        cleaned = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.StartsWith("/warp ", StringComparison.OrdinalIgnoreCase)) candidate = candidate[6..].Trim();
        if (candidate.Length is < 2 or > 240 || candidate.Any(char.IsControl)) return false;
        if (candidate.Equals("world", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("wdl", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("download", StringComparison.OrdinalIgnoreCase)) return false;
        cleaned = candidate;
        return true;
    }

    [GeneratedRegex("(?:[-_ ]+)(?:1[0-9]{10,13})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WorldToolsEpochSuffix();
    [GeneratedRegex("@(?:overworld|nether|end)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DimensionQualifier();
    [GeneratedRegex("^(?<name>.+?)[,_ ]+The(?<separator>[_ ,\\-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TrailingArticle();
    [GeneratedRegex("[^\\p{L}\\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"\bpitfight\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PitFightCompound();
    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Whitespace();
    [GeneratedRegex("^l[0-9]{2}w[0-9]{2}\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ArchiveOrderingPrefix();
    [GeneratedRegex("\\b(?:19|20)[0-9]{2}[ -](?:0?[1-9]|1[0-2])[ -](?:0?[1-9]|[12][0-9]|3[01])\\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IsoDate();
    [GeneratedRegex("\\b(?:19|20)[0-9]{2}\\s*q[1-4]\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex QuarterDate();
    [GeneratedRegex("\\s+(?:19|20)[0-9]{2}\\s+(?:0?[1-9]|1[0-2])$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex YearMonthSuffix();
    [GeneratedRegex("\\s+(?:19|20)[0-9]{2}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex YearOnlySuffix();
    [GeneratedRegex("\\b(?:ruins?|restored|restoration|world\\s*download|wdl)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CaptureQualifier();
    [GeneratedRegex("^(?<x>[+-]?[0-9]+(?:\\.[0-9]+)?)m[,_ ]+(?<z>[+-]?[0-9]+(?:\\.[0-9]+)?)m(?:[,_ ]+(?<rest>.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CoordinatePairMilestone();
    [GeneratedRegex("^(?<axis>[xz])(?<value>[+-]?[0-9]+(?:\\.[0-9]+)?)m(?:[,_ ]+(?<rest>.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AxisMilestone();
    [GeneratedRegex("(?:^|[,_ ]+)(?<dimension>nether|end)(?:$|[,_ ]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex InlineDimension();
    [GeneratedRegex("^(?<collection>[\\p{L}\\p{N}][\\p{L}\\p{N}_ -]{1,40}):[ _-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ExhibitCollectionPrefix();
    [GeneratedRegex("@(?:spawnmasons?|spawnmason[_ ]lodge)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ArchiveProvenanceSuffix();
    [GeneratedRegex("^(?:c{0,1})(?:xc|xl|l?x{0,3})(?:ix|iv|v?i{0,3})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RomanNumeral();
}
