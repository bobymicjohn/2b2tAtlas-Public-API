using _2b2tAtlas.Server.Services.AiEnrichment;

namespace Atlas.Ingestor.Tests;

public sealed class CoordinateMatcherTests
{
    [Fact]
    public void No_wiki_coordinates_reports_absent()
    {
        var result = CoordinateMatcher.Evaluate(100, 200, 0, [], 512);

        Assert.False(result.HasWikiCoordinates);
        Assert.False(result.Agrees);
        Assert.Equal(-1, result.BestDistanceBlocks);
    }

    [Fact]
    public void Overworld_within_tolerance_agrees()
    {
        var wiki = new[] { new WikiCoordinate(1000, 1000) };

        var result = CoordinateMatcher.Evaluate(1200, 1100, 0, wiki, 512);

        Assert.True(result.HasWikiCoordinates);
        Assert.True(result.Agrees);
        Assert.Equal(200, result.BestDistanceBlocks);
    }

    [Fact]
    public void Overworld_beyond_tolerance_disagrees()
    {
        var wiki = new[] { new WikiCoordinate(1000, 1000) };

        var result = CoordinateMatcher.Evaluate(5000, 5000, 0, wiki, 512);

        Assert.True(result.HasWikiCoordinates);
        Assert.False(result.Agrees);
        // Closest reading is the wiki value projected as Nether→Overworld (8000), i.e. 3000 blocks away.
        Assert.Equal(3000, result.BestDistanceBlocks);
    }

    [Fact]
    public void Nether_location_matches_overworld_wiki_coordinates()
    {
        // Nether base at (1000, 1000) corresponds to Overworld (8000, 8000), which the wiki lists.
        var wiki = new[] { new WikiCoordinate(8000, 8000) };

        var result = CoordinateMatcher.Evaluate(1000, 1000, 1, wiki, 512);

        Assert.True(result.Agrees);
        Assert.Equal(0, result.BestDistanceBlocks);
    }

    [Fact]
    public void Overworld_location_matches_wiki_quoting_nether_coordinates()
    {
        // Overworld base at (8000, 8000); wiki quoted the Nether coordinate (1000, 1000).
        var wiki = new[] { new WikiCoordinate(1000, 1000) };

        var result = CoordinateMatcher.Evaluate(8000, 8000, 0, wiki, 512);

        Assert.True(result.Agrees);
        Assert.Equal(0, result.BestDistanceBlocks);
    }

    [Fact]
    public void Picks_closest_of_multiple_candidates()
    {
        var wiki = new[]
        {
            new WikiCoordinate(50_000, 50_000),
            new WikiCoordinate(1050, 1050),
        };

        var result = CoordinateMatcher.Evaluate(1000, 1000, 0, wiki, 512);

        Assert.True(result.Agrees);
        Assert.Equal(50, result.BestDistanceBlocks);
    }
}
