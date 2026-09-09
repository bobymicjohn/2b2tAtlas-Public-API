using Atlas;

namespace _2b2tAtlas.Ingestor.Tests;

public class LocationDirectoryProjectionTests
{
    [Fact]
    public void GroupNames_IsStableSearchableAndDeduplicated()
    {
        var location = new Location
        {
            Groups =
            [
                new LocationGroupAttribution { GroupId = 2, GroupName = "Spawn Masons" },
                new LocationGroupAttribution { GroupId = 1, GroupName = "DonFuer" },
                new LocationGroupAttribution { GroupId = 3, GroupName = "spawn masons" },
                new LocationGroupAttribution { GroupId = 4, GroupName = " " },
            ],
        };

        Assert.Equal("DonFuer, Spawn Masons", location.GroupNames);
        Assert.Contains("spawn masons", location.GroupNames, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GroupNames_IsEmptyWhenAttributionsAreMissing()
    {
        Assert.Empty(new Location().GroupNames);
    }
}
