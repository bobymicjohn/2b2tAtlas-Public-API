using System.Text.RegularExpressions;

namespace Atlas;

/// <summary>An existing Atlas location the matcher may link a completed render to.</summary>
/// <param name="LocationId">The location row identifier.</param>
/// <param name="Name">The location display name.</param>
/// <param name="X">The location block X coordinate.</param>
/// <param name="Z">The location block Z coordinate.</param>
/// <param name="Dimension">The map dimension index: 0 Overworld, 1 Nether, 2 End.</param>
/// <param name="WarpNames">Known Archive warp aliases belonging to the location.</param>
/// <param name="RenderFootprints">Bounds of prior WDL renders already attached to the location.</param>
public sealed record LocationMatchCandidate(
    int LocationId,
    string Name,
    int X,
    int Z,
    int Dimension,
    IReadOnlyList<string>? WarpNames = null,
    IReadOnlyList<LocationRenderFootprint>? RenderFootprints = null);

/// <summary>Bounds of a previously imported render used to recognize overlapping WDL captures.</summary>
public sealed record LocationRenderFootprint(int MinX, int MinZ, int MaxXExclusive, int MaxZExclusive);

/// <summary>One ranked existing-location suggestion for a render awaiting a manual match.</summary>
/// <param name="LocationId">The suggested location row identifier.</param>
/// <param name="Name">The suggested location display name.</param>
/// <param name="Distance">Block distance from the render centroid to the location.</param>
/// <param name="Confidence">Combined 0..1 confidence from proximity and name similarity.</param>
/// <param name="Reason">Human-readable explanation of why the location was suggested.</param>
public sealed record LocationMatchSuggestion(int LocationId, string Name, int Distance, double Confidence, string Reason);

/// <summary>The outcome of matching a render's bounds and name against existing locations.</summary>
public sealed class LocationMatchResult
{
    /// <summary>Gets the confidently matched location to auto-attach, or <see langword="null"/> when manual review is required.</summary>
    public int? AutoAttachLocationId { get; init; }

    /// <summary>Gets the ranked suggestions offered when no confident match exists.</summary>
    public IReadOnlyList<LocationMatchSuggestion> Suggestions { get; init; } = [];

    /// <summary>Gets the reviewable reason for an automatic attachment.</summary>
    public string? AutoAttachReason { get; init; }

    /// <summary>Gets whether strong evidence supports creating a new location without stopping the worker.</summary>
    public bool CreateNewLocation { get; init; }

    /// <summary>Gets the confidence of the automatic existing/new decision, or the strongest suggestion.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets a stable decision label persisted for operator review.</summary>
    public string Decision => AutoAttachLocationId is not null ? "existing" : CreateNewLocation ? "new" : "review";

    /// <summary>Gets whether the render can be linked without operator review.</summary>
    public bool IsConfident => AutoAttachLocationId is not null || CreateNewLocation;
}

/// <summary>
/// Deterministic (no-AI) matcher that links a completed WDL render to an existing location using
/// same-dimension proximity and name similarity. An unambiguous, near match auto-attaches; otherwise
/// the render is parked for manual matching with ranked suggestions. BlackBrain augmentation is layered later.
/// </summary>
public static class LocationMatcher
{
    /// <summary>Returns the same-dimension candidate search radius in blocks.</summary>
    public static int CandidateRadius(int dimension) => dimension == 2 ? 2_000 : 5_000;

    /// <summary>Computes the integer block centroid of a half-open render bounds box.</summary>
    public static (int X, int Z) Centroid(int minX, int minZ, int maxXExclusive, int maxZExclusive) =>
        ((int)(((long)minX + maxXExclusive) / 2), (int)(((long)minZ + maxZExclusive) / 2));

    /// <summary>Scores existing locations against a render centroid, name, and dimension.</summary>
    /// <param name="dimension">The render's map dimension index.</param>
    /// <param name="centerX">The render centroid block X.</param>
    /// <param name="centerZ">The render centroid block Z.</param>
    /// <param name="renderName">The render's proposed name.</param>
    /// <param name="candidates">Existing locations to consider (any dimension; filtered internally).</param>
    /// <param name="maxSuggestions">Maximum ranked suggestions to return when parking for review.</param>
    /// <param name="archiveEvidence">Optional untrusted Archive metadata used for exact warp-alias evidence.</param>
    /// <param name="archiveWarp">The single reviewed or strongly inferred Archive command for this WDL.</param>
    /// <param name="incomingFootprint">Authoritative bounds used to compare this WDL with earlier renders.</param>
    /// <returns>An auto-attach decision, or ranked suggestions for manual matching.</returns>
    public static LocationMatchResult Match(
        int dimension,
        long centerX,
        long centerZ,
        string renderName,
        IEnumerable<LocationMatchCandidate> candidates,
        int maxSuggestions = 5,
        ArchiveWdlEvidence? archiveEvidence = null,
        ArchiveWarpCandidate? archiveWarp = null,
        LocationRenderFootprint? incomingFootprint = null)
    {
        var radius = CandidateRadius(dimension);
        var normalizedRender = Normalize(renderName);
        var archiveAliases = (archiveWarp is not null
                ? new[] { archiveWarp.Name }
                : (archiveEvidence?.NameCandidates ?? []).Append(archiveEvidence?.DownloadName ?? string.Empty))
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var scored = new List<LocationMatchSuggestion>();
        var exactWarpMatches = new List<LocationMatchSuggestion>();
        var trustedIdentityCompatibleLocations = new HashSet<int>();
        var strongestIdentity = 0d;
        var closestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (candidate.Dimension != dimension) continue;
            // Numbered Archive iterations are separate bases even when an old Atlas row incorrectly owns
            // the exact warp or shares the same coordinates. Do not let proximity repair that bad ownership.
            if (archiveWarp?.IsTrusted == true &&
                ArchiveWarpResolver.HasIterationConflict(archiveWarp.Name, candidate.Name))
                continue;
            var incomingCollection = ArchiveWarpResolver.ExplicitExhibitCollection(archiveWarp?.Name);
            if (archiveWarp?.IsTrusted == true && incomingCollection.Length > 0 &&
                candidate.WarpNames?.Any(warp =>
                    ArchiveWarpResolver.ExplicitExhibitCollection(warp) == incomingCollection) == true &&
                candidate.WarpNames.Where(warp =>
                        ArchiveWarpResolver.ExplicitExhibitCollection(warp) == incomingCollection)
                    .All(warp => ArchiveWarpResolver.CanonicalLocationIdentity(warp) !=
                        ArchiveWarpResolver.CanonicalLocationIdentity(archiveWarp.Name)))
                continue;
            long dx = candidate.X - centerX;
            long dz = candidate.Z - centerZ;
            var distance = (int)Math.Min(int.MaxValue, Math.Sqrt(dx * (double)dx + dz * (double)dz));
            closestDistance = Math.Min(closestDistance, distance);
            var matchedWarp = candidate.WarpNames?.FirstOrDefault(warp =>
                !ArchiveWarpResolver.HasIterationConflict(warp, candidate.Name) &&
                archiveAliases.Contains(Normalize(warp)));
            var incomingIdentity = ArchiveWarpResolver.CanonicalLocationIdentity(archiveWarp?.Name);
            var historicalIdentityScore = candidate.WarpNames?
                .Where(warp => !ArchiveWarpResolver.HasIterationConflict(warp, candidate.Name))
                .Select(warp => IdentitySimilarity(incomingIdentity, ArchiveWarpResolver.CanonicalLocationIdentity(warp)))
                .DefaultIfEmpty(0).Max() ?? 0;
            var locationIdentityScore = IdentitySimilarity(
                incomingIdentity, ArchiveWarpResolver.CanonicalLocationIdentity(candidate.Name));
            var identityScore = Math.Max(historicalIdentityScore, locationIdentityScore);
            strongestIdentity = Math.Max(strongestIdentity, identityScore);
            var overlap = incomingFootprint is null ? 0 : BestOverlap(incomingFootprint, candidate.RenderFootprints);
            var (nameScore, nameReason) = NameSimilarity(normalizedRender, Normalize(candidate.Name));
            var identityCompatible = matchedWarp is not null || identityScore >= 0.5 || nameScore >= 0.7;
            // A large historic WDL may geometrically contain many unrelated later captures. Containment
            // proves that the chunks coexisted in one saved world, not that the smaller capture represents
            // the same Atlas location. At long range, overlap is supporting evidence only and must be
            // corroborated by the Archive warp identity or the proposed/location name.
            if (distance > radius && !identityCompatible) continue;
            if (identityCompatible)
                trustedIdentityCompatibleLocations.Add(candidate.LocationId);
            var corroboratedOverlap = identityCompatible || distance <= radius ? overlap : 0;
            var confidence = matchedWarp is not null
                ? archiveEvidence?.IsArchiveSource == true && distance <= radius ? 0.99 : distance <= radius ? 0.9 : 0.65
                : Math.Clamp(Math.Max(
                    DistanceScore(distance) * 0.6 + nameScore * 0.15 + identityScore * 0.25,
                    Math.Max(identityScore * 0.96,
                        corroboratedOverlap * 0.9 + Math.Max(nameScore, identityScore) * 0.1)), 0, 1);
            var reason = matchedWarp is not null
                ? $"exact Archive warp alias '{matchedWarp}', {distance:N0} blocks from WDL center"
                : BuildReason(distance, nameScore, nameReason, identityScore, corroboratedOverlap);
            var suggestion = new LocationMatchSuggestion(candidate.LocationId, candidate.Name, distance, confidence, reason);
            scored.Add(suggestion);
            if (matchedWarp is not null) exactWarpMatches.Add(suggestion);
        }

        scored.Sort((a, b) => b.Confidence != a.Confidence
            ? b.Confidence.CompareTo(a.Confidence)
            : a.Distance.CompareTo(b.Distance));

        int? autoAttach = null;
        string? autoAttachReason = null;
        var uniqueExactWarpLocations = exactWarpMatches.Select(value => value.LocationId).Distinct().ToList();
        if ((archiveEvidence?.IsArchiveSource == true || archiveWarp?.IsTrusted == true) && uniqueExactWarpLocations.Count == 1)
        {
            var exact = exactWarpMatches.OrderBy(value => value.Distance).First();
            // Archive warp commands are globally identifying. Coordinates may legitimately differ between
            // captures of the same place, so an existing unique warp wins even when this WDL's centroid moved.
            autoAttach = exact.LocationId;
            autoAttachReason = exact.Reason;
        }
        if (autoAttach is null && scored.Count > 0)
        {
            var top = scored[0];
            var runnerUpConfidence = scored.Count > 1 ? scored[1].Confidence : 0;
            var unambiguous = scored.Count == 1 || scored[1].Distance > 2_000 ||
                top.Confidence >= 0.8 && top.Confidence - runnerUpConfidence >= 0.15;
            var veryClose = top.Distance < 500;
            var closeAndNamed = top.Distance < 2_000 &&
                NameSimilarity(normalizedRender, Normalize(top.Name)).Score >= 0.7;
            var strongHistoricalIdentity = top.Confidence >= 0.88;
            var trustedExact = exactWarpMatches.Count == 0 ||
                archiveEvidence?.IsArchiveSource == true || archiveWarp?.IsTrusted == true;
            var trustedIdentityCompatible = archiveWarp?.IsTrusted != true ||
                trustedIdentityCompatibleLocations.Contains(top.LocationId);
            if (trustedExact && trustedIdentityCompatible && unambiguous &&
                (veryClose || closeAndNamed || strongHistoricalIdentity))
            {
                autoAttach = top.LocationId;
                autoAttachReason = top.Reason;
            }
        }

        var bestConfidence = autoAttach is not null
            ? scored.FirstOrDefault(value => value.LocationId == autoAttach)?.Confidence ?? 0.99
            : scored.FirstOrDefault()?.Confidence ?? 0;
        // Do not create a duplicate merely because a museum WDL's name and centroid shifted. Any
        // same-dimension location inside the normal candidate radius is enough uncertainty to require
        // review unless the positive matching rules above selected it.
        var confidentNew = autoAttach is null && archiveWarp is { IsTrusted: true, Confidence: >= 0.9 } &&
            bestConfidence < 0.5 && strongestIdentity < 0.5 && closestDistance > radius;

        return new LocationMatchResult
        {
            AutoAttachLocationId = autoAttach,
            AutoAttachReason = autoAttachReason,
            CreateNewLocation = confidentNew,
            Confidence = confidentNew ? Math.Min(0.97, archiveWarp?.Confidence ?? 0.9) : bestConfidence,
            Suggestions = autoAttach is null ? scored.Take(maxSuggestions).ToList() : [],
        };
    }

    private static string BuildReason(int distance, double nameScore, string nameReason, double identityScore, double overlap)
    {
        var parts = new List<string> { $"{distance:N0} blocks from WDL center" };
        if (identityScore >= 0.5) parts.Add($"{identityScore:P0} dated-warp identity");
        if (overlap >= 0.01) parts.Add($"{overlap:P0} prior-render overlap");
        if (nameScore > 0) parts.Add(nameReason);
        return string.Join(", ", parts);
    }

    private static double BestOverlap(LocationRenderFootprint incoming, IReadOnlyList<LocationRenderFootprint>? existing)
    {
        if (existing is null || existing.Count == 0) return 0;
        var incomingArea = Math.Max(1d, (long)incoming.MaxXExclusive - incoming.MinX) *
            Math.Max(1d, (long)incoming.MaxZExclusive - incoming.MinZ);
        var best = 0d;
        foreach (var prior in existing)
        {
            var width = Math.Max(0L, Math.Min(incoming.MaxXExclusive, prior.MaxXExclusive) - Math.Max(incoming.MinX, prior.MinX));
            var height = Math.Max(0L, Math.Min(incoming.MaxZExclusive, prior.MaxZExclusive) - Math.Max(incoming.MinZ, prior.MinZ));
            var priorArea = Math.Max(1d, (long)prior.MaxXExclusive - prior.MinX) *
                Math.Max(1d, (long)prior.MaxZExclusive - prior.MinZ);
            best = Math.Max(best, width * (double)height / Math.Min(incomingArea, priorArea));
        }
        return Math.Clamp(best, 0, 1);
    }

    private static double IdentitySimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (ArchiveWarpResolver.HasIterationConflict(a, b)) return 0;
        if (!HasSpecificIdentity(a) || !HasSpecificIdentity(b)) return 0;
        if (a == b) return 1;
        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (at.Count == 0 || bt.Count == 0) return 0;
        var shared = at.Count(bt.Contains);
        var union = at.Count + bt.Count - shared;
        var jaccard = shared / (double)union;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            return Math.Max(0.8, jaccard);
        return jaccard;
    }

    private static bool HasSpecificIdentity(string value)
    {
        var generic = new HashSet<string>(["base", "spawn", "world", "wdl", "download", "location", "home"],
            StringComparer.Ordinal);
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Length >= 3 && !generic.Contains(token));
    }

    private static double DistanceScore(int distance) => distance switch
    {
        < 500 => 1.0,
        < 2_000 => 0.6,
        < 5_000 => 0.3,
        _ => 0.0,
    };

    private static (double Score, string Reason) NameSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return (0, string.Empty);
        if (a == b) return (1.0, "exact name match");
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))
            return (0.7, "name prefix match");
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            return (0.5, "name contains match");
        var overlap = TokenOverlap(a, b);
        return overlap > 0 ? (Math.Min(0.4, overlap * 0.4), "shared name words") : (0, string.Empty);
    }

    private static double TokenOverlap(string a, string b)
    {
        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (at.Count == 0 || bt.Count == 0) return 0;
        var intersection = at.Count(bt.Contains);
        return (double)intersection / Math.Max(at.Count, bt.Count);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9 ]+", " ");
        return Regex.Replace(cleaned, "\\s+", " ").Trim();
    }
}
