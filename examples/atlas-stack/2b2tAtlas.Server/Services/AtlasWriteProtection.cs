using System.Security.Claims;
using Atlas.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace _2b2tAtlas.Server.Services;

/// <summary>Central guard for human mutations, independent of editable role profiles.</summary>
public sealed class AtlasWriteProtection(RequestDelegate next)
{
    /// <summary>These permissions cannot be delegated through role overrides.</summary>
    public static readonly HashSet<string> OwnerPermissions = new(StringComparer.Ordinal)
    {
        Permissions.UsersRolesAssign, Permissions.RolesManage, Permissions.LocationsDelete,
        Permissions.HighwaysDelete, Permissions.RenderSettingsManage, Permissions.SettingsManage,
    };

    /// <summary>Classifies global/destructive operations from MVC metadata, not raw URL spelling.</summary>
    public static bool RequiresOwner(string method, string controller, string action) =>
        HttpMethods.IsDelete(method) || controller is "Admin" or "Roles" or "RenderSettings" or "MapRenders" ||
        (controller == "EnrichmentAdmin" && action is "Run" or "RunGroupDiscovery") ||
        (controller == "IngestionJobs" && action != "MatchPreview");

    /// <summary>Checks ownership, reserves a durable quota slot and verifies a recovery snapshot before execution.</summary>
    public async Task InvokeAsync(HttpContext http, AtlasRecoveryStore recovery, ILogger<AtlasWriteProtection> logger)
    {
        var endpoint = http.GetEndpoint();
        var action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action == null || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) ||
            HttpMethods.IsOptions(http.Request.Method) || endpoint!.Metadata.GetMetadata<IAllowAnonymous>() != null ||
            http.User.Identity?.IsAuthenticated != true)
        { await next(http); return; }

        var owner = AtlasSessionValidator.IsOwner(http.User);
        if (!owner && RequiresOwner(http.Request.Method, action.ControllerName, action.ActionName))
        {
            http.Response.StatusCode = 403;
            await http.Response.WriteAsJsonAsync(new { message = "Only atlas-owner can perform this operation." });
            return;
        }
        // These authenticated actions do not mutate catalog records. Upload bytes have
        // their own existing size/session bounds; session creation/completion is guarded.
        if (action.ControllerName == "Auth" ||
            (action.ControllerName == "IngestionJobs" && action.ActionName is "UploadChunk" or "MatchPreview"))
        { await next(http); return; }

        try
        {
            var allowed = await recovery.BeforeWriteAsync(
                int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!), owner,
                $"{http.Request.Method} {action.ControllerName}.{action.ActionName} {http.Request.Path.Value}", http.RequestAborted);
            if (!allowed)
            {
                http.Response.StatusCode = 429;
                http.Response.Headers.RetryAfter = "3600";
                await http.Response.WriteAsJsonAsync(new { message = "Atlas edit limit reached. Please wait or ask atlas-owner to review the activity." });
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Recovery checkpoint failed; refusing human mutation");
            http.Response.StatusCode = 503;
            await http.Response.WriteAsJsonAsync(new { message = "A recovery checkpoint could not be verified. Editing is paused until backup storage is available." });
            return;
        }
        await next(http);
    }
}
