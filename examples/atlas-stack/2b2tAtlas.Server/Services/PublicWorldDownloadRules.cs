using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

// Catalog links and download endpoints must agree on which sources are public.
// The download endpoint still checks that the preserved object exists and matches.
internal static class PublicWorldDownloadRules
{
    public static bool HasArchiveSource(Warp warp) =>
        HasSha256(warp.ArchiveSha256) &&
        warp.Source?.StartsWith("The Archive automated sync", StringComparison.OrdinalIgnoreCase) == true;

    public static bool HasRenderSource(IngestionJob job) =>
        job.RenderId.HasValue && job.WarpId is null &&
        job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
        HasSha256(job.ArchiveSha256) &&
        !string.IsNullOrWhiteSpace(job.Source);

    private static bool HasSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
