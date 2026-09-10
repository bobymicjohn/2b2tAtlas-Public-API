namespace _2b2tAtlas.Server.Services;

/// <summary>Canonical public links embedded in Atlas API relationship records.</summary>
internal static class PublicAtlasUrls
{
    private const string SiteBase = "https://atlas.example";
    private const string ApiBase = "http://127.0.0.1:5297";

    public static string Location(int id) => $"{SiteBase}/entities/locations/{id}/";
    public static string LocationInteractive(int id) => $"{SiteBase}/location/{id}";
    public static string LocationApi(int id) => $"{ApiBase}/api/locations/{id}";
    public static string Group(int id) => $"{SiteBase}/entities/groups/{id}/";
    public static string GroupInteractive(int id) => $"{SiteBase}/group/{id}";
    public static string GroupApi(int id) => $"{ApiBase}/api/groups/{id}";
    public static string HighwayApi(int id) => $"{ApiBase}/api/highways/{id}";
    public static string HighwayMap(int dimension) => $"{SiteBase}/map?dimension={DimensionSlug(dimension)}";
    public static string RenderApi(int id) => $"{ApiBase}/api/renders/{id}";
    public static string AttachmentApi(int id) => $"{ApiBase}/api/attachments/{id}";
    public static string WarpApi(int id) => $"{ApiBase}/api/warps/{id}";
    public static string WorldDownloadMetadata(int warpId) => $"{ApiBase}/api/warps/{warpId}/world-download";
    public static string WorldDownload(int warpId, string? locationName, string? sha256)
    {
        var fileName = Atlas.DownloadFileNames.WorldDownload(locationName, warpId);
        return $"{ApiBase}/api/warps/{warpId}/world-download.zip?filename={Uri.EscapeDataString(fileName)}&sha256={Uri.EscapeDataString(sha256?.ToLowerInvariant() ?? string.Empty)}";
    }
    public static string RenderWorldDownloadMetadata(int renderId) => $"{ApiBase}/api/renders/{renderId}/world-download";
    public static string RenderWorldDownload(int renderId, string? locationName, string? sha256)
    {
        var fileName = Atlas.DownloadFileNames.RenderWorldDownload(locationName, renderId);
        return $"{ApiBase}/api/renders/{renderId}/world-download.zip?filename={Uri.EscapeDataString(fileName)}&sha256={Uri.EscapeDataString(sha256?.ToLowerInvariant() ?? string.Empty)}";
    }

    private static string DimensionSlug(int dimension) => dimension switch
    {
        1 => "nether",
        2 => "end",
        _ => "overworld",
    };
}
