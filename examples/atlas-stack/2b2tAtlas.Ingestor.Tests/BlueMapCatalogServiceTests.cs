using Microsoft.Extensions.Options;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class BlueMapCatalogServiceTests
{
    [Fact]
    public void Catalog_advertises_highest_complete_non_superseded_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"atlas-bluemap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            WriteGeneration(root, "render-42-source-v5.23", 42, 1, complete: true);
            WriteGeneration(root, "render-42-source-v5.23-p2", 42, 2, complete: true);
            WriteGeneration(root, "render-42-source-v5.23-p3", 42, 3, complete: false);
            WriteGeneration(root, "render-42-source-v5.23-p9.superseded-20260906", 42, 9, complete: true);

            var service = new BlueMapCatalogService(Options.Create(new BlueMapOptions
            {
                OutputRoot = root,
                RequestPath = "/bluemap",
                PublicOrigin = "https://api.example.test",
                CatalogCacheSeconds = 30,
                MinimumProfileVersion = 1,
            }));

            var result = Assert.IsType<BlueMapGeneration>(service.Find(42));
            Assert.Equal(2, result.RendererProfileVersion);
            Assert.Equal("render-42-source-v5.23-p2", result.GenerationName);
            Assert.Equal("/bluemap/render-42-source-v5.23-p2/web/", result.RelativeUrl);
            Assert.Equal("https://api.example.test/bluemap/render-42-source-v5.23-p2/web/", result.PublicUrl);
            Assert.True(service.IsAdvertisedGeneration("render-42-source-v5.23-p2"));
            Assert.False(service.IsAdvertisedGeneration("render-42-source-v5.23"));
            var summary = service.GetSummary();
            Assert.Equal(1, summary.ValidatedRenderCount);
            Assert.Equal(4, summary.DiagnosticGenerationCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_hides_profiles_below_configured_safety_floor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"atlas-bluemap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            WriteGeneration(root, "render-42-source-v5.23-p2", 42, 2, complete: true);

            var service = new BlueMapCatalogService(Options.Create(new BlueMapOptions
            {
                OutputRoot = root,
                MinimumProfileVersion = 7,
            }));

            Assert.Null(service.Find(42));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_requires_profile7_quality_exact_relight_and_location_start_audits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"atlas-bluemap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            WriteGeneration(root, "render-42-ungated-v5.23-p7", 42, 7, complete: true);

            var service = new BlueMapCatalogService(Options.Create(new BlueMapOptions
            {
                OutputRoot = root,
                MinimumProfileVersion = 7,
            }));

            Assert.Null(service.Find(42));

            WriteGeneration(root, "render-43-missing-start-v5.23-p7", 43, 7, complete: true, relightValidated: true);
            WriteGeneration(root, "render-44-validated-v5.23-p7", 44, 7, complete: true,
                relightValidated: true, locationStartValidated: true);
            var refreshed = new BlueMapCatalogService(Options.Create(new BlueMapOptions
            {
                OutputRoot = root,
                MinimumProfileVersion = 7,
            }));

            Assert.Null(refreshed.Find(43));
            Assert.Equal(7, Assert.IsType<BlueMapGeneration>(refreshed.Find(44)).RendererProfileVersion);
            Assert.True(refreshed.IsAdvertisedGeneration("render-44-validated-v5.23-p7"));
            Assert.False(refreshed.IsAdvertisedGeneration("render-43-missing-start-v5.23-p7"));
            Assert.False(refreshed.IsAdvertisedGeneration("render-42-ungated-v5.23-p7"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteGeneration(
        string root,
        string name,
        int renderId,
        int profile,
        bool complete,
        bool relightValidated = false,
        bool locationStartValidated = false)
    {
        var generation = Path.Combine(root, name);
        Directory.CreateDirectory(generation);
        var validation = relightValidated
            ? $"\"QualityGate\":{{\"Passed\":true{(locationStartValidated ? ",\"LocationStartExact\":true" : string.Empty)}}},\"RenderingProfile\":{{\"Relight\":{{\"FootprintAuditExact\":true}}}},"
            : string.Empty;
        File.WriteAllText(Path.Combine(generation, "manifest.json"),
            $"{{\"Status\":\"complete\",\"RenderId\":{renderId},\"RendererProfileVersion\":{profile},{validation}\"GeneratedUtc\":\"2026-09-06T00:00:00Z\"}}");
        if (!complete) return;
        var web = Path.Combine(generation, "web");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "index.html"), "<!doctype html>");
    }
}
