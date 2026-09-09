using System.Text;
using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class ArchiveWdlEvidenceTests
{
    [Fact]
    public void Archive_world_downloader_report_identifies_source_and_capture_name()
    {
        using var temporary = new TempDirectory();
        var report = """
            {"downloadName":"Temple_of_the_Talion_2017-03-06","sourceAddress":"survival.thearchive.world","sourceName":"The Archive","sourceMotd":"Historical worlds","dimensionName":"minecraft:overworld"}
            """;
        var archive = ZipFixture.Create(temporary,
            ("capture/wdl/download.jsonl", Encoding.UTF8.GetBytes(report)));

        var evidence = ArchiveWdlEvidenceReader.ReadArchive(archive, "fallback.zip");

        Assert.Equal("archive-world-downloader", evidence.DownloaderKind);
        Assert.True(evidence.IsArchiveSource);
        Assert.Equal("Temple_of_the_Talion_2017-03-06", evidence.DownloadName);
        Assert.Equal("minecraft:overworld", evidence.ReportedDimension);
        Assert.Contains("Temple_of_the_Talion_2017-03-06", evidence.NameCandidates);
    }

    [Fact]
    public void Archive_world_downloader_report_retains_health_software_counts_and_dimension_breakdown()
    {
        using var temporary = new TempDirectory();
        var report = """
            {"v":1,"id":"capture-1","startedAt":"2026-08-14T12:00:00Z","finishedAt":"2026-08-14T12:04:00Z","status":"partial","downloadName":"Old Base","sourceAddress":"thearchive.world","sourceKind":"multiplayer","serverBrand":"Paper","dimensionName":"minecraft:overworld","minecraftVersion":"1.21.8","modVersion":"1.1.0","loaderName":"Fabric Loader","loaderVersion":"0.16.14","chunks":120,"saveChunks":145,"entities":9,"containers":3,"d.minecraft:overworld":120,"sd.minecraft:overworld":145}
            """;
        var archive = ZipFixture.Create(temporary,
            ("capture/wdl/download.jsonl", Encoding.UTF8.GetBytes(report)));

        var evidence = ArchiveWdlEvidenceReader.ReadArchive(archive, "old-base.zip");

        Assert.Equal(1, evidence.ReportSchemaVersion);
        Assert.Equal(1, evidence.ReportSessionCount);
        Assert.Equal("partial", evidence.CompletionStatus);
        Assert.Equal(145, evidence.SavedChunkCount);
        Assert.Equal("1.21.8", evidence.MinecraftVersion);
        Assert.Equal("1.1.0", evidence.ModVersion);
        Assert.Contains("minecraft:overworld", evidence.RawDimensionIds);
        Assert.Contains(evidence.Warnings, warning => warning.Contains("marked the latest capture partial", StringComparison.Ordinal));
    }

    [Fact]
    public void Pending_capture_is_flagged_as_interrupted()
    {
        using var temporary = new TempDirectory();
        var pending = """
            {"v":1,"id":"capture-1","startedAt":"2026-08-14T12:00:00Z","downloadName":"Old Base","sourceAddress":"thearchive.world"}
            """;
        var archive = ZipFixture.Create(temporary,
            ("capture/wdl/download.pending", Encoding.UTF8.GetBytes(pending)));

        var evidence = ArchiveWdlEvidenceReader.ReadArchive(archive, "old-base.zip");

        Assert.True(evidence.HasPendingCapture);
        Assert.Equal("interrupted", evidence.CompletionStatus);
        Assert.True(evidence.IsArchiveSource);
        Assert.Contains(evidence.Warnings, warning => warning.Contains("unfinished", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Worldtools_metadata_retains_raw_dimensions_and_strips_only_epoch_filename_suffix()
    {
        using var temporary = new TempDirectory();
        var archive = ZipFixture.Create(temporary,
            ("world/WorldTools/Capture Metadata.md", Encoding.UTF8.GetBytes("Server IP: archive.spawnmason.com\nMOTD: The Archive")),
            ("world/WorldTools/Dimension Tree.txt", Encoding.UTF8.GetBytes("archive:museum_2017\nminecraft:the_nether")));

        var evidence = ArchiveWdlEvidenceReader.ReadArchive(
            archive, "Temple_of_the_Talion_2017-03-06-1754179200000.zip");

        Assert.Equal("worldtools", evidence.DownloaderKind);
        Assert.True(evidence.IsArchiveSource);
        Assert.Contains("archive:museum_2017", evidence.RawDimensionIds);
        Assert.Contains("Temple_of_the_Talion_2017-03-06", evidence.NameCandidates);
    }

    [Fact]
    public void Archive_reader_retains_custom_dimension_ids_from_the_selected_world_layout()
    {
        using var temporary = new TempDirectory();
        var archive = ZipFixture.Create(temporary,
            ("world/wdl/download.jsonl", Encoding.UTF8.GetBytes("{\"downloadName\":\"Museum Base\"}")),
            ("world/dimensions/thearchive/museum_2017/region/r.0.0.mca", new byte[8192]));

        var evidence = ArchiveWdlEvidenceReader.ReadArchive(archive, "museum.zip");

        Assert.Contains("thearchive:museum_2017", evidence.RawDimensionIds);
    }

    [Fact]
    public void Multiple_world_reports_do_not_cross_contaminate_identity_without_selected_root()
    {
        using var temporary = new TempDirectory();
        var first = Encoding.UTF8.GetBytes("{\"downloadName\":\"First\",\"sourceAddress\":\"survival.thearchive.world\"}");
        var second = Encoding.UTF8.GetBytes("{\"downloadName\":\"Second\",\"sourceAddress\":\"survival.thearchive.world\"}");
        var archive = ZipFixture.Create(temporary,
            ("first/wdl/download.jsonl", first),
            ("second/wdl/download.jsonl", second));

        var ambiguous = ArchiveWdlEvidenceReader.ReadArchive(archive, "bundle.zip");
        var selected = ArchiveWdlEvidenceReader.ReadArchive(archive, "bundle.zip", "second");

        Assert.False(ambiguous.IsArchiveSource);
        Assert.Contains(ambiguous.Warnings, warning => warning.Contains("multiple world roots", StringComparison.Ordinal));
        Assert.Equal("Second", selected.DownloadName);
        Assert.True(selected.IsArchiveSource);
    }
}
