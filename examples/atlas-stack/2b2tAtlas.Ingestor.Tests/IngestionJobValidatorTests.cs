using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class IngestionJobValidatorTests
{
    [Fact]
    public void Request_accepts_leaf_zip_and_bounded_metadata()
    {
        Assert.Empty(IngestionJobValidator.ValidateRequest(ValidRequest()));
    }

    [Theory]
    [InlineData("../world.zip")]
    [InlineData("..\\world.zip")]
    [InlineData("C:\\world.zip")]
    [InlineData("/world.zip")]
    [InlineData("world.zip\n")]
    [InlineData("world.zip:stream.zip")]
    [InlineData("CON.zip")]
    [InlineData("world.tar")]
    public void Request_rejects_paths_and_non_zip_intake_names(string fileName)
    {
        var request = ValidRequest();
        request.IntakeFileName = fileName;

        Assert.NotEmpty(IngestionJobValidator.ValidateRequest(request));
    }

    [Fact]
    public void Completion_requires_hash_and_location_render()
    {
        var missing = new IngestionJobUpdate { Status = "completed" };
        Assert.NotEmpty(IngestionJobValidator.ValidateUpdate(missing));

        var valid = new IngestionJobUpdate
        {
            ClaimToken = new string('A', 43),
            Status = "completed",
            ArchiveSha256 = new string('a', 64),
            LocationRender = ValidRender(ValidRequest()),
        };
        Assert.Empty(IngestionJobValidator.ValidateUpdate(valid));
    }

    [Fact]
    public void Running_update_validates_progress_and_eta_bounds()
    {
        var update = new IngestionJobUpdate
        {
            ClaimToken = new string('A', 43),
            Status = "running",
            ProgressPercent = 101,
            EtaSeconds = 8 * 24 * 60 * 60,
        };

        var errors = IngestionJobValidator.ValidateUpdate(update);

        Assert.Contains(errors, error => error.Contains("Progress", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("ETA", StringComparison.Ordinal));
    }

    [Fact]
    public void Running_update_rejects_spoofed_or_invalid_world_inspection()
    {
        var update = new IngestionJobUpdate
        {
            ClaimToken = new string('A', 43),
            Status = "running",
            Inspection = new IngestionWorldInspection
            {
                StorageEra = "unknown-era",
                ProvenanceStatus = "verified-2b2t",
                ProvenanceMessage = "trusted",
                Dimensions =
                [
                    new IngestionDimensionInspection
                    {
                        Key = "moon",
                        StorageEra = "anvil",
                    },
                ],
            },
        };

        var errors = IngestionJobValidator.ValidateUpdate(update);

        Assert.Contains(errors, error => error.Contains("storage era", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("provenance status", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("dimension inspection", StringComparison.Ordinal));
    }

    [Fact]
    public void Request_accepts_day_night_and_rejects_unbounded_scale()
    {
        var request = ValidRequest();
        request.DayNight = true;
        request.Scale = new string('9', 100) + "k";

        var errors = IngestionJobValidator.ValidateRequest(request);

        Assert.DoesNotContain(errors, error => error.Contains("Day/night", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Scale", StringComparison.Ordinal));
    }

    [Fact]
    public void Request_rejects_world_root_traversal()
    {
        var request = ValidRequest();
        request.WorldRoot = "../other-world";

        Assert.Contains(
            IngestionJobValidator.ValidateRequest(request),
            error => error.Contains("World root", StringComparison.Ordinal));
    }

    [Fact]
    public void Completion_binding_rejects_another_tile_path()
    {
        var request = ValidRequest();
        var render = ValidRender(request);
        render.TilesPath = "https://tiles.atlas.example/AtlasTiles/existing/overworld/{z}/{y}/{x}.png";

        var errors = IngestionCompletionValidator.Validate(
            request, render, ["https://tiles.atlas.example/AtlasTiles/"]);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Completion_binding_accepts_exact_sparse_location_render()
    {
        var request = ValidRequest();

        Assert.Empty(IngestionCompletionValidator.Validate(
            request, ValidRender(request), ["https://tiles.atlas.example/AtlasTiles/"]));
    }

    [Fact]
    public void Completion_binding_accepts_exact_dual_sparse_location_render()
    {
        var request = ValidRequest();
        var render = ValidRender(request);
        render.HasDayNight = true;
        render.TilesPath = $"https://tiles.atlas.example/AtlasTiles/{request.Slug}/overworld/g-20260819183000-acde1234/{{dn}}/{{z}}/{{y}}/{{x}}.png";

        Assert.Empty(IngestionCompletionValidator.Validate(
            request, render, ["https://tiles.atlas.example/AtlasTiles/"]));
    }

    [Fact]
    public void Completion_binding_rejects_day_night_flag_without_token()
    {
        var request = ValidRequest();
        var render = ValidRender(request);
        render.HasDayNight = true;

        Assert.NotEmpty(IngestionCompletionValidator.Validate(
            request, render, ["https://tiles.atlas.example/AtlasTiles/"]));
    }

    [Fact]
    public void Completion_binding_accepts_exact_sparse_end_render()
    {
        var request = ValidRequest();
        var render = ValidRender(request);
        render.Dimension = 2;
        render.TilesPath = $"https://tiles.atlas.example/AtlasTiles/{request.Slug}/end/{{z}}/{{y}}/{{x}}.png";

        Assert.Empty(IngestionCompletionValidator.Validate(
            request, render, ["https://tiles.atlas.example/AtlasTiles/"]));
    }

    [Fact]
    public void Completion_binding_allows_nether_render()
    {
        var request = ValidRequest();
        request.Dimension = "nether";
        var render = ValidRender(request);
        render.Dimension = 1;
        render.TilesPath = $"https://tiles.atlas.example/AtlasTiles/{request.Slug}/nether/{{z}}/{{y}}/{{x}}.png";

        Assert.Empty(IngestionCompletionValidator.Validate(
            request, render, ["https://tiles.atlas.example/AtlasTiles/"]));
    }

    [Fact]
    public void Completion_binding_allows_one_native_tile_at_world_border()
    {
        var request = ValidRequest();
        var render = ValidRender(request);
        render.MinZ = 29_989_792;
        render.MaxZExclusive = 30_000_176;

        Assert.Empty(IngestionCompletionValidator.Validate(
            request, render, ["https://tiles.atlas.example/AtlasTiles/"]));

        render.MaxZExclusive = 30_000_257;
        Assert.Contains(IngestionCompletionValidator.Validate(
            request, render, ["https://tiles.atlas.example/AtlasTiles/"]),
            error => error.Contains("tile margin", StringComparison.Ordinal));
    }

    private static IngestionJobRequest ValidRequest() => new()
    {
        IntakeFileName = "TGG_wdl.zip",
        Slug = "tgg-2021-test",
        Name = "TGG (2021)",
        WorldDownloadDate = "2021-06-29",
        Source = "2b2t world download",
        Scale = "256k",
    };

    private static LocationRenderCompletion ValidRender(IngestionJobRequest request) => new()
    {
        TilesPath = $"https://tiles.atlas.example/AtlasTiles/{request.Slug}/overworld/{{z}}/{{y}}/{{x}}.png",
        Dimension = 0,
        MinX = -1024,
        MinZ = 2048,
        MaxXExclusive = 512,
        MaxZExclusive = 4096,
        MaxNativeZoom = 9,
        CoordinateScheme = "atlas-sparse-v1",
    };
}