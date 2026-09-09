using _2b2tAtlas.Server.Services.AiEnrichment;

namespace Atlas.Ingestor.Tests;

public sealed class WikiCoordinateExtractorTests
{
    [Fact]
    public void Extracts_labeled_x_z_pair()
    {
        var coords = WikiCoordinateExtractor.Extract("The base sits at X: 1,234 Z: -5,678 in the wild.");

        Assert.Contains(new WikiCoordinate(1234, -5678), coords);
    }

    [Fact]
    public void Extracts_labeled_triple_with_y_ignored()
    {
        var coords = WikiCoordinateExtractor.Extract("Coordinates X 100 Y 64 Z 200.");

        Assert.Contains(new WikiCoordinate(100, 200), coords);
    }

    [Fact]
    public void Extracts_infobox_field()
    {
        var coords = WikiCoordinateExtractor.Extract("| coordinates = 12000, 64, -8000\n| dimension = Overworld");

        Assert.Contains(new WikiCoordinate(12000, -8000), coords);
    }

    [Fact]
    public void Extracts_coord_template()
    {
        var coords = WikiCoordinateExtractor.Extract("Located near {{Coord|4200|-4200}} on the axis.");

        Assert.Contains(new WikiCoordinate(4200, -4200), coords);
    }

    [Fact]
    public void Rejects_out_of_range_numbers()
    {
        var coords = WikiCoordinateExtractor.Extract("Player count was X 999999999 Z 12 in 2020.");

        Assert.DoesNotContain(new WikiCoordinate(999999999, 12), coords);
    }

    [Fact]
    public void Returns_empty_for_no_coordinates()
    {
        var coords = WikiCoordinateExtractor.Extract("This article is about a notable player group.");

        Assert.Empty(coords);
    }

    [Fact]
    public void Returns_empty_for_null_or_blank()
    {
        Assert.Empty(WikiCoordinateExtractor.Extract(null));
        Assert.Empty(WikiCoordinateExtractor.Extract("   "));
    }
}
