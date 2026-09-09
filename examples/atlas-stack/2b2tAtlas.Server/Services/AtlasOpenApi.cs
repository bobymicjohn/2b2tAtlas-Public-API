using Atlas.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace _2b2tAtlas.Server.Services;

/// <summary>Marks worker-key endpoints whose credentials are checked by the ingestion controller.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AtlasWorkerKeyAttribute : Attribute;

/// <summary>Separates public data discovery from permission-protected operational documentation.</summary>
public static class AtlasOpenApi
{
    /// <summary>Registers the public v1 and complete internal API documents.</summary>
    public static IServiceCollection AddAtlasOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.ShouldInclude = IsPublicRead;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "2b2t Atlas public read-only API";
                document.Info.Description = "Anonymous public data endpoints. Account, write, moderation and worker operations are excluded.";
                return Task.CompletedTask;
            });
        });
        services.AddOpenApi("internal", options =>
        {
            options.ShouldInclude = _ => true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "2b2t Atlas complete API (restricted)";
                document.Components ??= new();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                        Description = "Atlas JWT; each operation also enforces its required permission."
                    },
                    ["AtlasWorkerKey"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header,
                        Name = "X-Atlas-Worker-Key", Description = "Private ingestion worker credential."
                    }
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var worker = metadata.OfType<AtlasWorkerKeyAttribute>().Any();
                var auth = metadata.OfType<IAuthorizeData>().ToArray();
                var bearer = auth.Length > 0 && !metadata.OfType<IAllowAnonymous>().Any();
                if (!worker && !bearer) return Task.CompletedTask;
                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(worker ? "AtlasWorkerKey" : "Bearer", context.Document)] = []
                }];
                operation.Responses ??= new();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or invalid credentials." });
                if (bearer)
                {
                    operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Authenticated caller lacks the required permission." });
                    var policies = auth.Select(a => a.Policy).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
                    if (policies.Length > 0)
                        operation.Description = (operation.Description + "\n\nRequired permission(s): " + string.Join(", ", policies) + ".").Trim();
                    if (context.Description.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor action &&
                        context.Description.HttpMethod is not "GET" and not "HEAD" &&
                        AtlasWriteProtection.RequiresOwner(context.Description.HttpMethod!, action.ControllerName, action.ActionName))
                        operation.Description += " Only the database-verified owner atlas-owner may perform this mutation; role overrides cannot delegate it.";
                    if (context.Description.HttpMethod is not "GET" and not "HEAD")
                    {
                        operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Persistent human edit quota reached." });
                        operation.Responses.TryAdd("503", new OpenApiResponse { Description = "Required recovery snapshot unavailable; mutation refused." });
                    }
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }

    /// <summary>Explicit anonymous read metadata is required for public schema publication.</summary>
    public static bool IsPublicRead(ApiDescription description)
    {
        var metadata = description.ActionDescriptor.EndpointMetadata;
        return description.HttpMethod == "GET"
            && metadata.OfType<IAllowAnonymous>().Any()
            && !metadata.OfType<IAuthorizeData>().Any()
            && !metadata.OfType<AtlasWorkerKeyAttribute>().Any()
            && description.RelativePath != "api/newWarp.php";
    }

    /// <summary>Maps separate constrained routes so the public route cannot request the internal document.</summary>
    public static void MapAtlasOpenApi(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/openapi"))
                context.Response.Headers.CacheControl = "private, no-store";
            await next(context);
        });
        app.MapOpenApi("/openapi/{documentName:regex(^v1$)}.json").AllowAnonymous();
        app.MapOpenApi("/openapi/{documentName:regex(^internal$)}.json")
            .RequireAuthorization(Permissions.UsersManage);
    }
}
