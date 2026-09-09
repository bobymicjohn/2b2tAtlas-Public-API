namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>The outcome of comparing a location's coordinates to those parsed from a wiki page.</summary>
/// <param name="HasWikiCoordinates">Whether the wiki page yielded any usable coordinates.</param>
/// <param name="Agrees">Whether at least one wiki coordinate matched the location within tolerance.</param>
/// <param name="BestDistanceBlocks">The smallest Overworld-scale Chebyshev distance found, or -1 when none.</param>
/// <param name="Basis">A short human-readable explanation of the closest comparison.</param>
public readonly record struct CoordinateAgreement(
    bool HasWikiCoordinates,
    bool Agrees,
    long BestDistanceBlocks,
    string Basis);

/// <summary>
/// Compares an Atlas location's coordinates to coordinates parsed from a wiki page, aware that 2b2t wiki
/// pages usually list Overworld coordinates while a location may be Nether. All comparisons are normalized
/// to the Overworld plane (Nether ×8), and each wiki coordinate is tried both as-is and as a Nether value
/// projected to the Overworld, so a genuine base matches regardless of which dimension the wiki quoted.
/// </summary>
public static class CoordinateMatcher
{
    private const int NetherToOverworld = 8;

    /// <summary>Evaluates whether any wiki coordinate agrees with the location, dimension-aware.</summary>
    /// <param name="locationX">Location block X in its own dimension.</param>
    /// <param name="locationZ">Location block Z in its own dimension.</param>
    /// <param name="locationDimension">0 Overworld, 1 Nether, 2 End.</param>
    /// <param name="wikiCoordinates">Coordinates parsed from the wiki page.</param>
    /// <param name="toleranceBlocks">Maximum Overworld-scale distance treated as agreement.</param>
    /// <returns>The agreement result, including whether any wiki coordinates were present at all.</returns>
    public static CoordinateAgreement Evaluate(
        int locationX,
        int locationZ,
        int locationDimension,
        IReadOnlyList<WikiCoordinate> wikiCoordinates,
        int toleranceBlocks)
    {
        if (wikiCoordinates.Count == 0)
            return new CoordinateAgreement(false, false, -1, "No coordinates on the wiki page.");

        // Normalize the location to the Overworld plane so a Nether base and its Overworld coordinates align.
        var (locOverworldX, locOverworldZ) = locationDimension == 1
            ? ((long)locationX * NetherToOverworld, (long)locationZ * NetherToOverworld)
            : (locationX, locationZ);

        var bestDistance = long.MaxValue;
        var bestBasis = "No coordinate within tolerance.";
        foreach (var coordinate in wikiCoordinates)
        {
            // Candidate readings: the wiki value as an Overworld coordinate, and as a Nether coordinate.
            var asOverworld = Chebyshev(locOverworldX, locOverworldZ, coordinate.X, coordinate.Z);
            if (asOverworld < bestDistance)
            {
                bestDistance = asOverworld;
                bestBasis = $"Wiki ({coordinate.X}, {coordinate.Z}) ≈ location, {asOverworld} blocks apart.";
            }

            var asNether = Chebyshev(locOverworldX, locOverworldZ, coordinate.X * NetherToOverworld, coordinate.Z * NetherToOverworld);
            if (asNether < bestDistance)
            {
                bestDistance = asNether;
                bestBasis = $"Wiki Nether ({coordinate.X}, {coordinate.Z}) ≈ location, {asNether} blocks apart.";
            }
        }

        var agrees = bestDistance <= toleranceBlocks;
        return new CoordinateAgreement(true, agrees, bestDistance, bestBasis);
    }

    /// <summary>Chebyshev (max-axis) distance, adequate for a coarse proximity gate.</summary>
    private static long Chebyshev(long ax, long az, long bx, long bz) =>
        Math.Max(Math.Abs(ax - bx), Math.Abs(az - bz));
}
