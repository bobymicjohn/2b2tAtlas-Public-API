using Atlas;
using Atlas.Locations;

namespace _2b2tAtlas.Ingestor.Tests;

public sealed class LocationComputedPropertiesTests
{
    [Fact]
    public void BlueMapReadyRenderCount_CountsDistinctValidatedViewsFromEverySource()
    {
        var location = new Location
        {
            Renders =
            [
                Render(1, 11, "/bluemap/render-1/"),
                Render(1, 11, "/bluemap/render-1-duplicate/"),
                Render(2, 12, null),
                Render(3, 13, " "),
                Render(4, null, "/bluemap/manual-render/")
            ]
        };

        Assert.Equal(2, location.BlueMapReadyRenderCount);
    }

    private static Render Render(int id, int? archiveWarpId, string? blueMapPath) => new()
    {
        Id = id,
        ArchiveWarpId = archiveWarpId,
        BlueMapPath = blueMapPath
    };
}
