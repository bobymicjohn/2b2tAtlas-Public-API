using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class MapRenderRegistrationValidatorTests
{
    private static readonly string[] AllowedPrefixes = ["https://tiles.atlas.example/AtlasTiles/"];

    [Fact]
    public void Validate_accepts_controlled_https_tile_template()
    {
        var dto = ValidDto();

        var errors = MapRenderRegistrationValidator.Validate(dto, AllowedPrefixes);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("javascript:alert(1)/{z}/{y}/{x}.png")]
    [InlineData("https://tiles.atlas.example.evil.example/AtlasTiles/{z}/{y}/{x}.png")]
    [InlineData("https://tiles.atlas.example/Other/{z}/{y}/{x}.png")]
    [InlineData("https://tiles.atlas.example/AtlasTiles/{z}/{y}/same.png")]
    [InlineData("https://tiles.atlas.example/AtlasTiles/{{z}}/{y}/{x}.png")]
    [InlineData("https://tiles.atlas.example/AtlasTiles/{z}/{y}/{x}.png?redirect=https://evil.example")]
    public void Validate_rejects_hostile_or_incomplete_templates(string template)
    {
        var dto = ValidDto();
        dto.UrlTemplate = template;

        var errors = MapRenderRegistrationValidator.Validate(dto, AllowedPrefixes);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_requires_day_night_token_to_match_flag()
    {
        var dto = ValidDto();
        dto.HasDayNight = true;

        Assert.Contains(MapRenderRegistrationValidator.Validate(dto, AllowedPrefixes),
            error => error.Contains("{dn}", StringComparison.Ordinal));
    }

    private static MapRenderDto ValidDto() => new()
    {
        Slug = "spawn-2026-overworld",
        Name = "Spawn (2026)",
        Dimension = 0,
        Scale = "5k",
        UrlTemplate = "https://tiles.atlas.example/AtlasTiles/spawn/overworld/{z}/{y}/{x}.png",
        HasDayNight = false,
        MaxNativeZoom = 10,
        WorldDownloadDate = "2026-01-01",
        Source = "2b2t world download",
        SortOrder = 10,
        IsPublished = false,
    };
}
