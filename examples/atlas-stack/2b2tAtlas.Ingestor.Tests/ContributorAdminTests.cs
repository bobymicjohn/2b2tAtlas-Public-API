using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Atlas.Auth;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;
using _2b2tAtlas.Client.Services;
using AdminPage = _2b2tAtlas.Client.Pages.Admin;

namespace Atlas.Ingestor.Tests;

public sealed class ContributorAdminTests
{
    [Theory]
    [InlineData(RoleNames.Cartographer)]
    [InlineData(RoleNames.HighwayArchitect)]
    public async Task Contributor_initialization_never_requests_owner_users_or_statistics(string role)
    {
        var user = new User { Id = 10, Username = "example-cartographer", MinecraftUsername = "crxyne", Role = role,
            Permissions = RolePermissions.ForRole(role).ToList() };
        var storage = DispatchProxy.Create<ILocalStorageService, Storage>();
        ((Storage)(object)storage).Token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(expires: DateTime.UtcNow.AddHours(1)));
        using var handler = new Requests(user);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") });
        services.AddSingleton(storage);
        services.AddSingleton<IJSRuntime, NoJs>();
        services.AddSingleton<NavigationManager, Navigation>();
        services.AddRadzenComponents();
        services.AddSingleton<AuthService>(); services.AddSingleton<HighwayService>();
        services.AddSingleton<GroupService>(); services.AddSingleton<RoleService>();
        services.AddSingleton<RevisionService>(); services.AddSingleton<EnrichmentService>();
        services.AddSingleton<RenderSettingsService>(); services.AddSingleton<MapRenderService>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var page = await renderer.RenderComponentAsync<AdminPage>();
            var html = page.ToHtmlString();
            Assert.Contains("Highway change history", html);
            Assert.Contains("https://mc-heads.net/avatar/crxyne/80", html);
            Assert.DoesNotContain("Database Stats", html);
            Assert.DoesNotContain("Failed to load users", html);
        });
        Assert.DoesNotContain("/api/admin/users", handler.Paths);
        Assert.DoesNotContain("/api/admin/stats", handler.Paths);
        Assert.Contains("/api/highways/all", handler.Paths);
        if (role == RoleNames.HighwayArchitect) Assert.DoesNotContain("/api/locations", handler.Paths);
        else Assert.Contains("/api/locations", handler.Paths);
        Assert.Empty(provider.GetRequiredService<NotificationService>().Messages);
    }

    [Fact]
    public void Skin_identity_is_independent_of_login_and_escaped_as_one_path_segment()
    {
        Assert.Equal("https://mc-heads.net/avatar/crxyne/80", UserAvatar.Url(new User { Username="example-cartographer", MinecraftUsername=" crxyne " }));
        Assert.Equal("https://mc-heads.net/avatar/old_name/80", UserAvatar.Url(new User { Username="old_name" }));
        Assert.Equal("https://mc-heads.net/avatar/a%2Fb%3Fc/80", UserAvatar.Url(new User { Username="a/b?c" }));
        Assert.Equal("./Images/logo.png", UserAvatar.Url(null));
    }

    public class Storage : DispatchProxy
    {
        public string Token = "";
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "GetItemAsync" && method.GetGenericArguments()[0] == typeof(string)) return new ValueTask<string>(Token);
            if (method.Name == "SetItemAsync") return ValueTask.CompletedTask;
            throw new NotSupportedException(method.Name);
        }
    }
    private sealed class Requests(User user) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            if (path == "/api/auth/profile") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=JsonContent.Create(user) });
            if (path.StartsWith("/api/admin/")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var body = path.EndsWith("/status") ? "{}" : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(body, System.Text.Encoding.UTF8,"application/json") });
        }
    }
    private sealed class Navigation : NavigationManager { public Navigation() => Initialize("https://fixture.invalid/", "https://fixture.invalid/admin"); }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier,args);
    }
}
