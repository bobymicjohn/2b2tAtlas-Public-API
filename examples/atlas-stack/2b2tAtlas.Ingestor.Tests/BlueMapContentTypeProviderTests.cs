using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class BlueMapContentTypeProviderTests
{
    [Theory]
    [InlineData("/generation/web/lang/settings.conf", true)]
    [InlineData("/generation/web/lang/en.conf", true)]
    [InlineData("/generation/web/lang/sr-Latn-RS.conf", true)]
    [InlineData("/generation/config/core.conf", false)]
    [InlineData("/generation/web/lang/../core.conf", false)]
    [InlineData("/generation/web/lang/nested/private.conf", false)]
    [InlineData("/generation/web/secret.unknown", false)]
    public void Allows_only_public_translation_configuration(string path, bool expected)
    {
        Assert.Equal(expected, new BlueMapContentTypeProvider().TryGetContentType(path, out _));
    }

    [Fact]
    public async Task Static_middleware_serves_real_language_files_and_rejects_other_configuration()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "atlas-bluemap-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "generation", "web", "lang"));
        Directory.CreateDirectory(Path.Combine(root, "generation", "config"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "generation/web/lang/settings.conf"), "{ default: \"en\" }", ct);
            await File.WriteAllTextAsync(Path.Combine(root, "generation/web/lang/en.conf"), "{ pageTitle: \"BlueMap\" }", ct);
            await File.WriteAllTextAsync(Path.Combine(root, "generation/config/core.conf"), "private configuration", ct);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            using var files = new PhysicalFileProvider(root);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                RequestPath = "/bluemap",
                ContentTypeProvider = new BlueMapContentTypeProvider()
            });
            await app.StartAsync(ct);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var name in new[] { "settings", "en" })
            {
                using var response = await client.GetAsync($"/bluemap/generation/web/lang/{name}.conf", ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
                Assert.Contains(name == "settings" ? "default" : "pageTitle", await response.Content.ReadAsStringAsync(ct));
            }
            using var privateResponse = await client.GetAsync("/bluemap/generation/config/core.conf", ct);
            Assert.Equal(HttpStatusCode.NotFound, privateResponse.StatusCode);
            using var missingResponse = await client.GetAsync("/bluemap/generation/web/lang/undefined.conf", ct);
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
            await app.StopAsync(ct);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
