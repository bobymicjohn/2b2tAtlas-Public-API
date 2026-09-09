using _2b2tAtlas.Client;
using _2b2tAtlas.Client.Services;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

//Radzen
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();

var configuredApiBase = builder.Configuration["ApiBaseUrl"];
var apiBase = string.IsNullOrWhiteSpace(configuredApiBase)
	? new Uri(builder.HostEnvironment.BaseAddress)
	: new Uri(configuredApiBase.TrimEnd('/') + "/", UriKind.Absolute);
if (!apiBase.IsLoopback && apiBase.Scheme != Uri.UriSchemeHttps)
	throw new InvalidOperationException("ApiBaseUrl must use HTTPS unless it is loopback.");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = apiBase });

builder.Services.AddBlazoredLocalStorage();

// Auth Service
builder.Services.AddScoped<AuthService>();

// Location data service
builder.Services.AddScoped<LocationService>();

// Highway data service
builder.Services.AddScoped<HighwayService>();

// Group data service
builder.Services.AddScoped<GroupService>();

// Role/permission matrix service
builder.Services.AddScoped<RoleService>();

// Revision/moderation service
builder.Services.AddScoped<RevisionService>();

// AI wiki-enrichment service
builder.Services.AddScoped<EnrichmentService>();

// Render-settings (unMINED tuning) service
builder.Services.AddScoped<RenderSettingsService>();
builder.Services.AddScoped<IngestionUploadService>();

// World render (map layer) service
builder.Services.AddScoped<MapRenderService>();

await builder.Build().RunAsync();
