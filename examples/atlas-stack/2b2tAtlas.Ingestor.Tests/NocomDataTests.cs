using _2b2tAtlas.Server.Services;
using _2b2tAtlas.Server.Services.Mcp;
using _2b2tAtlas.Server.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Ingestor.Tests;

public sealed class NocomDataTests
{
    private static NocomDataService Load() => new(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "nocom-world-pulse-manifest.json")),
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "nocom-highway-observations.json")));

    [Fact]
    public void Runtime_loader_reads_packaged_data_relative_to_binaries()
    {
        Assert.Equal(Load().Dataset.GroupedSourceSha256, new NocomDataService().Dataset.GroupedSourceSha256);
    }

    [Fact]
    public void Released_counts_coordinates_periods_and_dimension_conventions_are_preserved()
    {
        var data = Load();
        Assert.Equal(537689881L, data.Dataset.GroupedRows);
        Assert.Equal(3133950352L, data.Dataset.Observations);
        Assert.Equal(39, data.Periods().Count);
        Assert.Equal(17, data.Periods("overworld").Count);
        Assert.Equal(17, data.Periods("nether").Count);
        Assert.Equal(5, data.Periods("end").Count);
        var first = data.Periods("nether")[0];
        Assert.Equal(new DateTimeOffset(2020,3,9,0,0,0,TimeSpan.Zero), first.PeriodStartUtc);
        Assert.Equal(TimeSpan.FromDays(30), first.PeriodEndExclusiveUtc-first.PeriodStartUtc);
        Assert.Equal(1, first.AtlasDimension);
        Assert.Equal(-1, first.SourceDimension);
        Assert.Equal(-210942L*16, first.ObservedExtentBlocks.MinX);
        Assert.Equal((234392L+1)*16-1, first.ObservedExtentBlocks.MaxXInclusive);
        Assert.EndsWith("nether/{z}/{y}/{x}.png", first.TileUrlTemplate);
        Assert.All(data.Periods("end"), p => Assert.Equal(2, p.AtlasDimension));
        Assert.Equal(new DateOnly(2021,7,15), data.Dataset.ExploitPatchedDate);
        Assert.Equal(new DateTimeOffset(2021,8,1,0,0,0,TimeSpan.Zero), data.Periods("overworld")[^1].PeriodEndExclusiveUtc);
    }

    [Fact]
    public void Overlapping_bucket_filters_are_explicit_and_bad_filters_are_rejected()
    {
        var data=Load();
        Assert.Empty(data.Periods(from:new DateOnly(2018,1,1),to:new DateOnly(2019,12,31)));
        var result=Assert.Single(data.Periods("overworld",new DateOnly(2020,4,8),new DateOnly(2020,4,8)));
        Assert.Equal("2020-04-08", result.Key);
        Assert.Equal(2,data.Periods("overworld",new DateOnly(2020,4,7),new DateOnly(2020,4,8)).Count);
        Assert.Throws<ArgumentException>(()=>data.Periods("moon"));
        Assert.Throws<ArgumentException>(()=>data.Periods(from:new DateOnly(2021,1,1),to:new DateOnly(2020,1,1)));
        Assert.IsType<BadRequestObjectResult>(new NocomController(data).Periods("../private").Result);
        Assert.Throws<ArgumentException>(()=>new NocomMcpTools(data).get_nocom_periods(from:"yesterday"));
    }

    [Fact]
    public void Highway_release_has_exact_counts_compass_directions_and_matching_periods()
    {
        var data=Load();
        Assert.Equal(136,data.Highways().Count);
        Assert.Equal(136,data.Highways("overworld").Count);
        Assert.Empty(data.Highways("end"));
        var northwest=data.Highways("nether","northwest");
        Assert.Equal(17,northwest.Count);
        Assert.Equal(4341773L,northwest[0].Observations);
        Assert.Equal(-1,northwest[0].DirectionX);
        Assert.Equal(-1,northwest[0].DirectionZ);
        Assert.Equal(data.Periods("nether")[0].PeriodStartUtc,northwest[0].PeriodStartUtc);
        Assert.Throws<ArgumentException>(()=>data.Highways("nether","unknown"));
        Assert.Equal(data.Dataset,new NocomMcpTools(data).get_nocom_dataset());
    }
}
