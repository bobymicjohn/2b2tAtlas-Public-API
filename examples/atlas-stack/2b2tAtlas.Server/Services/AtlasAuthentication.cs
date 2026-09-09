using System.Text;
using Atlas.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace _2b2tAtlas.Server.Services;

/// <summary>Shared runtime authentication registration, also exercised by HTTP security tests.</summary>
public static class AtlasAuthentication
{
    /// <summary>Validates Atlas JWTs and requires authentication plus the requested permission.</summary>
    public static IServiceCollection AddAtlasAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AtlasSessionValidator>();
        var settings = configuration.GetSection("JwtSettings");
        var secret = settings["SecretKey"] ?? throw new InvalidOperationException("JwtSettings:SecretKey is not configured");
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = settings["Issuer"] ?? "2b2tAtlas",
                ValidAudience = settings["Audience"] ?? "2b2tAtlas",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var validator = context.HttpContext.RequestServices.GetRequiredService<AtlasSessionValidator>();
                    var current = await validator.ValidateAsync(context.Principal!, context.HttpContext.RequestAborted);
                    if (current == null) context.Fail("Account or session is no longer valid. Please sign in again.");
                    else context.Principal = current;
                }
            };
        });
        services.AddAuthorization(options =>
        {
            foreach (var permission in Permissions.All)
            {
                var required = permission;
                options.AddPolicy(required, policy => policy.RequireAuthenticatedUser().RequireAssertion(ctx =>
                    ctx.User.HasClaim("superadmin", "true") || ctx.User.HasClaim("perm", required)));
            }
        });
        return services;
    }
}
