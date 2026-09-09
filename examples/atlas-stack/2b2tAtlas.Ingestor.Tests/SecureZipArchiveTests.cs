using System.IO.Compression;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Tests;

public sealed class SecureZipArchiveTests
{
    private static readonly IngestLimits Limits = new()
    {
        MaxArchiveBytes = 16 * IngestLimits.MiB,
        MaxExpandedBytes = 32 * IngestLimits.MiB,
        MaxSingleEntryBytes = 16 * IngestLimits.MiB,
        MinimumFreeBytes = 1,
    };

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("world/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("world/CON.txt")]
    [InlineData("world/trailing. ")]
    [InlineData("world//empty.txt")]
    public async Task Inspect_rejects_unsafe_paths(string path)
    {
        using var temporary = new TempDirectory();
        var zip = ZipFixture.Create(temporary,
            ("world/level.dat", ZipFixture.MinimalLevelDat()),
            (path, [1]));

        await Assert.ThrowsAsync<InputSecurityException>(() => SecureZipArchive.InspectAsync(zip, Limits, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_rejects_case_colliding_paths()
    {
        using var temporary = new TempDirectory();
        var zip = ZipFixture.Create(temporary,
            ("world/level.dat", ZipFixture.MinimalLevelDat()),
            ("world/region/r.0.0.mca", [1]),
            ("WORLD/REGION/R.0.0.MCA", [2]));

        await Assert.ThrowsAsync<InputSecurityException>(() => SecureZipArchive.InspectAsync(zip, Limits, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_rejects_symbolic_links()
    {
        using var temporary = new TempDirectory();
        var zip = temporary.Resolve("input.zip");
        using (var output = File.Create(zip))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            var level = archive.CreateEntry("world/level.dat");
            await using (var stream = level.Open())
                await stream.WriteAsync(ZipFixture.MinimalLevelDat(), TestContext.Current.CancellationToken);
            var link = archive.CreateEntry("world/link");
            link.ExternalAttributes = 0xA1FF << 16;
            await using var linkStream = link.Open();
            await linkStream.WriteAsync("target"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InputSecurityException>(() => SecureZipArchive.InspectAsync(zip, Limits, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_rejects_compression_bombs()
    {
        using var temporary = new TempDirectory();
        var zip = ZipFixture.Create(temporary,
            ("world/level.dat", ZipFixture.MinimalLevelDat()),
            ("world/region/r.0.0.mca", new byte[2 * IngestLimits.MiB]));
        var restrictive = Limits with { MaxCompressionRatio = 2 };

        await Assert.ThrowsAsync<InputSecurityException>(() => SecureZipArchive.InspectAsync(zip, restrictive, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_rejects_corrupt_zip()
    {
        using var temporary = new TempDirectory();
        var zip = temporary.Resolve("input.zip");
        await File.WriteAllBytesAsync(zip, "not a zip"u8.ToArray(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InputValidationException>(() => SecureZipArchive.InspectAsync(zip, Limits, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_rejects_encrypted_entry_flag_during_preflight()
    {
        using var temporary = new TempDirectory();
        var zip = ZipFixture.Create(temporary, ("world/level.dat", ZipFixture.MinimalLevelDat()));
        await PatchFirstCentralEntryAsync(zip, flags => (ushort)(flags | 0x0001), null);

        await Assert.ThrowsAsync<InputSecurityException>(() => SecureZipArchive.InspectAsync(
            zip, Limits, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_rejects_unsupported_compression_method_during_preflight()
    {
        using var temporary = new TempDirectory();
        var zip = ZipFixture.Create(temporary, ("world/level.dat", ZipFixture.MinimalLevelDat()));
        await PatchFirstCentralEntryAsync(zip, null, 12);

        await Assert.ThrowsAsync<InputSecurityException>(() => SecureZipArchive.InspectAsync(
            zip, Limits, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inspect_and_extract_accept_nested_world_without_escape()
    {
        using var temporary = new TempDirectory();
        var zip = ZipFixture.Create(temporary,
            ("Outer/World/level.dat", ZipFixture.MinimalLevelDat()),
            ("Outer/World/region/r.0.0.mca", [1, 2, 3]));
        var report = await SecureZipArchive.InspectAsync(zip, Limits, cancellationToken: TestContext.Current.CancellationToken);
        var destination = temporary.Resolve("extracted");

        await SecureZipArchive.ExtractAsync(zip, destination, report, Limits, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.FileCount);
        Assert.Contains("Outer/World/level.dat", report.LevelDatCandidates);
        Assert.True(File.Exists(System.IO.Path.Combine(destination, "Outer", "World", "level.dat")));
    }

    [Fact]
    public async Task Inspect_accepts_old_worldtools_export_with_region_storage_but_no_level_dat()
    {
        using var temporary = new TempDirectory();
        var zip = ZipFixture.Create(temporary,
            ("Old Export/region/r.-2.3.mca", new byte[8192]));

        var report = await SecureZipArchive.InspectAsync(
            zip, Limits, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(report.LevelDatCandidates);
        Assert.Equal(1, report.FileCount);
    }

    private static async Task PatchFirstCentralEntryAsync(
        string path,
        Func<ushort, ushort>? updateFlags,
        ushort? method)
    {
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var signature = new byte[] { 0x50, 0x4b, 0x01, 0x02 };
        var offset = bytes.AsSpan().IndexOf(signature);
        Assert.True(offset >= 0);
        if (updateFlags is not null)
        {
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8), updateFlags(flags));
        }
        if (method.HasValue)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10), method.Value);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
    }
}
