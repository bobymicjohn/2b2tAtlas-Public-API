using System.Text;

namespace Atlas;

/// <summary>Creates stable, human-readable filenames for public Atlas downloads.</summary>
public static class DownloadFileNames
{
    private const int MaxLocationSlugLength = 72;

    /// <summary>Creates a filesystem-safe bounded-WDL filename that includes the location name and warp ID.</summary>
    public static string WorldDownload(string? locationName, int warpId)
        => BuildWorldDownload(locationName, "warp", warpId);

    /// <summary>Creates a filesystem-safe preserved-source WDL filename that includes the location name and render ID.</summary>
    public static string RenderWorldDownload(string? locationName, int renderId)
        => BuildWorldDownload(locationName, "render", renderId);

    private static string BuildWorldDownload(string? locationName, string identityType, int identityId)
    {
        var slug = new StringBuilder();
        var needsSeparator = false;

        foreach (var character in locationName ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (needsSeparator && slug.Length > 0 && slug[^1] != '-')
                    slug.Append('-');
                slug.Append(character);
                needsSeparator = false;
            }
            else
            {
                needsSeparator = slug.Length > 0;
            }

            if (slug.Length >= MaxLocationSlugLength)
                break;
        }

        var safeName = slug.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "location";

        return $"2b2tAtlas-{safeName}-{identityType}-{identityId}.zip";
    }
}
