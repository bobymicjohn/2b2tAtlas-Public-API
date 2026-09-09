namespace Atlas;

/// <summary>One dimension's render centroid computed client-side from a WDL's region file names.</summary>
/// <param name="Dimension">The dimension key: <c>overworld</c>, <c>nether</c>, or <c>end</c>.</param>
/// <param name="CenterX">The approximate block X centroid of the dimension's region data.</param>
/// <param name="CenterZ">The approximate block Z centroid of the dimension's region data.</param>
public sealed record MatchPreviewCandidate(string Dimension, int CenterX, int CenterZ);

/// <summary>A drop-time request to preview which existing locations a WDL's dimensions would match.</summary>
public sealed class MatchPreviewRequest
{
    /// <summary>Gets or sets the render name used for name-similarity scoring.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the per-dimension centroids to score against existing locations.</summary>
    public IReadOnlyList<MatchPreviewCandidate> Candidates { get; set; } = [];
}

/// <summary>The previewed match outcome for one dimension, without creating any job.</summary>
/// <param name="Dimension">The dimension key the preview was computed for.</param>
/// <param name="CenterX">The block X centroid used for matching.</param>
/// <param name="CenterZ">The block Z centroid used for matching.</param>
/// <param name="AutoAttachLocationId">The confidently matched location id, or <see langword="null"/>.</param>
/// <param name="AutoAttachName">The confidently matched location name, or <see langword="null"/>.</param>
/// <param name="Suggestions">Ranked suggestions offered when there is no confident match.</param>
public sealed record MatchPreviewResult(
    string Dimension,
    int CenterX,
    int CenterZ,
    int? AutoAttachLocationId,
    string? AutoAttachName,
    IReadOnlyList<LocationMatchSuggestion> Suggestions);
