using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Ingestor.Tests;

public sealed class PrimaryMapRendersTests
{
    [Fact]
    public void Legacy_nether_layers_keep_their_original_coordinate_contract()
    {
        var nether = PrimaryMapRenders.All.Where(render => render.Dimension == 1).ToList();
        var large = Assert.Single(nether, render => render.Id == "43k");
        Assert.DoesNotContain(nether, render => render.Id == "5k");
        Assert.Equal("atlas-nether-legacy-v1", large.CoordinateScheme);
        Assert.Equal(9, large.MaxNativeZoom);
        Assert.Contains("/Nether/43k/7/{z}/{y}/{x}.png", large.TileUrlTemplate);
        Assert.Equal(PrimaryMapRenders.All.Count,
            PrimaryMapRenders.All.Select(render => (render.Dimension, render.Id)).Distinct().Count());
    }
}
