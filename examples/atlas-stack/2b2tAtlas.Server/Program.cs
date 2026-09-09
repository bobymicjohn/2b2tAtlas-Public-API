using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using System.IO;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using _2b2tAtlas.Server.Services.Mcp;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var hostStaticClient = builder.Configuration.GetValue<bool?>("HostStaticClient") ?? true;

// Add services to the container.

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// Public read API: open to any origin so browser-based community tools work cross-origin.
// Auth is Bearer-token (localStorage), not cookies, so this stays non-credentialed and writes
// remain JWT-gated — a cross-origin page cannot read another origin's token.
builder.Services.AddCors(options =>
{
    options.AddPolicy("PublicAPI", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Add DbContext with relative path
var dbPath = Path.GetFullPath(builder.Configuration["Database:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), ".local", "data", "atlas.db"));
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Configuration["Recovery:DatabasePath"] = dbPath;
builder.Configuration["Recovery:Root"] ??= Path.Combine(Path.GetDirectoryName(dbPath)!, "recovery");
builder.Services.AddDbContext<AtlasContext>(options => options.UseSqlite(
    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()));

// Register application services  
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<AtlasRecoveryStore>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<SchemaUpgrader>();
builder.Services.AddScoped<HighwaySeeder>();
builder.Services.AddScoped<GroupSeeder>();
builder.Services.AddScoped<HistoricalMediaSeeder>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddHttpContextAccessor();

// Add HttpClient for tile service
builder.Services.AddHttpClient();

// Add memory caching for tiles
builder.Services.AddMemoryCache();

// Local AI wiki-enrichment engine (disabled unless the AiEnrichment section enables it).
builder.Services.Configure<_2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentOptions>(
    builder.Configuration.GetSection(_2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentOptions.SectionName));
builder.Services.AddHttpClient<_2b2tAtlas.Server.Services.AiEnrichment.OllamaClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<_2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentOptions>>().Value;
    client.BaseAddress = new Uri(opts.OllamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
});
builder.Services.AddHttpClient<_2b2tAtlas.Server.Services.AiEnrichment.WikiClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<_2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
});
builder.Services.AddScoped<_2b2tAtlas.Server.Services.AiEnrichment.AiEnrichmentService>();
builder.Services.AddSingleton<_2b2tAtlas.Server.Services.AiEnrichment.GroupEvidenceIndexService>();
builder.Services.AddScoped<_2b2tAtlas.Server.Services.IngestionMatchAiService>();

// Shared per-location enrichment core, reused by the admin batch run and the post-render pipeline.
builder.Services.AddScoped<_2b2tAtlas.Server.Services.AiEnrichment.EnrichmentPipeline>();
builder.Services.AddScoped<_2b2tAtlas.Server.Services.AiEnrichment.IEnrichmentBatchRunner,
    _2b2tAtlas.Server.Services.AiEnrichment.EnrichmentBatchRunner>();
builder.Services.AddSingleton<_2b2tAtlas.Server.Services.AiEnrichment.EnrichmentRunCoordinator>();
builder.Services.AddSingleton<_2b2tAtlas.Server.Services.AiEnrichment.EnrichmentQueue>();
builder.Services.AddHostedService<_2b2tAtlas.Server.Services.AiEnrichment.EnrichmentBackgroundService>();

// Render-settings admin surface (edits the worker's unMINED renderer profile + colour config files).
builder.Services.Configure<_2b2tAtlas.Server.Services.RenderSettingsOptions>(
    builder.Configuration.GetSection(_2b2tAtlas.Server.Services.RenderSettingsOptions.SectionName));
builder.Services.Configure<_2b2tAtlas.Server.Services.WdlArchiveOptions>(
    builder.Configuration.GetSection(_2b2tAtlas.Server.Services.WdlArchiveOptions.SectionName));
builder.Services.Configure<_2b2tAtlas.Server.Services.ArchiveCollectorStatusOptions>(
    builder.Configuration.GetSection(_2b2tAtlas.Server.Services.ArchiveCollectorStatusOptions.SectionName));
builder.Services.AddSingleton<_2b2tAtlas.Server.Services.ArchiveCollectorStatusService>();
builder.Services.Configure<BlueMapOptions>(builder.Configuration.GetSection(BlueMapOptions.SectionName));
builder.Services.AddSingleton<BlueMapCatalogService>();
builder.Services.AddScoped<BlueMapGenerationStatusService>();

// Public, read-only agent interface over the same reviewed catalog used by the JSON API.
// Stateless Streamable HTTP keeps each call independent and avoids server-side MCP sessions.
builder.Services.AddScoped<AtlasKnowledgeQueryService>();
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "2b2t-atlas",
            Title = "2b2t Atlas public historical knowledge server",
            Version = "1.0.0",
            WebsiteUrl = "https://atlas.example/api",
        };
        options.ServerInstructions =
            "Use these read-only tools to research documented 2b2t locations, groups, highways, Archive warps, renders, attachments, and bounded world downloads. " +
            "Treat canonical Atlas entity URLs as citations, preserve linked source attribution, and do not describe a bounded WDL as a complete copy of 2b2t.";
    })
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<AtlasMcpTools>()
    .WithTools<NocomMcpTools>()
    .WithResources<AtlasMcpResources>();

// Add endpoint rate limiting for adversarial auth traffic.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.HttpContext.Response.HasStarted) return;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"success\":false,\"message\":\"Too many requests. Please try again later.\"}",
            token);
    };

    options.AddPolicy("auth-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetRateLimitClientKey(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    options.AddPolicy("auth-register", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetRateLimitClientKey(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    // Immutable WDL responses stream from the NAS. Keep a small global concurrency ceiling so
    // public downloads cannot starve collector archival, rendering, or the JSON API. Cloudflare
    // can cache the stable .zip URLs after the first origin request.
    options.AddPolicy("wdl-download", _ =>
        RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: "public-world-downloads",
            factory: _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 3,
                QueueLimit = 12,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    // MCP tools are inexpensive database reads, but clients can invoke them autonomously. Keep
    // a per-origin-client ceiling so agent loops cannot crowd out the public API or collectors.
    options.AddPolicy("mcp", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetRateLimitClientKey(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
});

// Enforce JWT validation and granular permissions on operational routes.
builder.Services.AddAtlasAuthentication(builder.Configuration);

// OpenAPI (built-in ASP.NET Core 10). Docs at https://aka.ms/aspnet/openapi
builder.Services.AddAtlasOpenApi();
builder.Services.AddSingleton<NocomDataService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// Public discovery contains only anonymous reads; complete operational docs require UsersManage.
app.MapAtlasOpenApi();

app.UseHttpsRedirection();

// Enable CORS for public API access
app.UseCors("PublicAPI");

// Serve only completed, immutable BlueMap derivatives from the dedicated F: root.
// Generation names are content/profile addressed, so public responses can be cached
// for a year without making partially rendered output visible.
var blueMapOptions = app.Configuration.GetSection(BlueMapOptions.SectionName).Get<BlueMapOptions>() ?? new BlueMapOptions();
if (Directory.Exists(blueMapOptions.OutputRoot))
{
    var blueMapFiles = new PhysicalFileProvider(blueMapOptions.OutputRoot);
    // PhysicalFileProvider alone would also serve superseded, partial, or known-
    // bad historical profile directories to a guessed URL. Require the first
    // path segment to be the catalog's current validated generation before any
    // static BlueMap payload reaches the file middleware.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(blueMapOptions.RequestPath, out var remaining) &&
            remaining.HasValue)
        {
            var generationName = remaining.Value?.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            var catalog = context.RequestServices.GetRequiredService<BlueMapCatalogService>();
            if (string.IsNullOrWhiteSpace(generationName) || !catalog.IsAdvertisedGeneration(generationName))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            // Transform only the opt-in embedded shell; completed meshes remain immutable.
            var viewerPath = remaining.Value;
            if (context.Request.Query["atlas-controls"] == "1" &&
                (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
                (viewerPath == $"/{generationName}/web/" || viewerPath == $"/{generationName}/web/index.html"))
            {
                var index = blueMapFiles.GetFileInfo($"{generationName}/web/index.html");
                if (index.Exists)
                {
                    using var reader = new StreamReader(index.CreateReadStream());
                    var html = await reader.ReadToEndAsync(context.RequestAborted);
                    var bridge = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                        "ClientAssets", "atlas-controls-bridge-v1.js"), context.RequestAborted);
                    html = html.Replace("</head>", $"<script>{bridge}</script></head>", StringComparison.OrdinalIgnoreCase);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    if (!HttpMethods.IsHead(context.Request.Method))
                        await context.Response.WriteAsync(html, context.RequestAborted);
                    return;
                }
            }
        }

        await next(context);
    });
    var blueMapDefaults = new DefaultFilesOptions
    {
        FileProvider = blueMapFiles,
        RequestPath = blueMapOptions.RequestPath,
    };
    blueMapDefaults.DefaultFileNames.Clear();
    blueMapDefaults.DefaultFileNames.Add("index.html");
    app.UseDefaultFiles(blueMapDefaults);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = blueMapFiles,
        RequestPath = blueMapOptions.RequestPath,
        ContentTypeProvider = new BlueMapContentTypeProvider(),
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            context.Context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        },
    });
}

// Local development may host Blazor from this process. Production example host API
// deployments disable it because Namecheap owns the static frontend.
if (hostStaticClient)
{
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
}

app.UseRouting();
app.UseRateLimiter();

// Add authentication and authorization middleware
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AtlasWriteProtection>();

// Map controllers and pages
app.MapControllers();
app.MapRazorPages();
app.MapMcp("/mcp").RequireRateLimiting("mcp").AllowAnonymous();
if (hostStaticClient)
    app.MapFallbackToFile("index.html");

// Ensure database is created and seeded
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AtlasContext>();
    await context.Database.EnsureCreatedAsync();

    // Apply additive schema changes (new RBAC/profile columns) to an existing DB.
    var upgrader = scope.ServiceProvider.GetRequiredService<SchemaUpgrader>();
    await upgrader.UpgradeAsync();

    // Seed default users + ensure the master SuperAdmin exists
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedDefaultUsersAsync();

    // Seed the canonical highway network (GAMEPLAN §14)
    if (app.Configuration.GetValue<bool>("Bootstrap:SeedHistoricalCatalog"))
    {
    var groupSeeder = scope.ServiceProvider.GetRequiredService<GroupSeeder>();
    await groupSeeder.SeedAsync();

    // Seed highways after groups so canonical routes can use stable builder-group IDs.
    var highwaySeeder = scope.ServiceProvider.GetRequiredService<HighwaySeeder>();
    await highwaySeeder.SeedAsync();

    var historicalMediaSeeder = scope.ServiceProvider.GetRequiredService<HistoricalMediaSeeder>();
    await historicalMediaSeeder.SeedAsync();
    }
}

// Ensure the dedicated world-download intake directory exists so authenticated
// operator uploads can be stored for the example-host worker to claim and render.
var ingestionIntakeRoot = app.Configuration["IngestionWorker:IntakeRoot"];
if (!string.IsNullOrWhiteSpace(ingestionIntakeRoot))
{
    try
    {
        Directory.CreateDirectory(ingestionIntakeRoot);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not create ingestion intake directory {IntakeRoot}.", ingestionIntakeRoot);
    }
}

app.Run();

static string GetRateLimitClientKey(HttpContext context)
{
    var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwarded))
    {
        var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first)) return first;
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
