using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class WdlArchiveStoreTests
{
    [Fact]
    public async Task Duplicate_sources_converge_on_one_verified_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var sourceA = temporary.Resolve("a.zip");
        var sourceB = temporary.Resolve("b.zip");
        var archive = temporary.Resolve("archive");
        await File.WriteAllBytesAsync(sourceA, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3], cancellationToken);
        File.Copy(sourceA, sourceB);

        var first = await WdlArchiveStore.ArchiveAsync(sourceA, archive, null, cancellationToken);
        var second = await WdlArchiveStore.ArchiveAsync(sourceB, archive, first.Sha256, cancellationToken);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Path, second.Path);
        Assert.Single(Directory.EnumerateFiles(archive, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Tampered_existing_object_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("source.zip");
        var archive = temporary.Resolve("archive");
        await File.WriteAllBytesAsync(source, [0x50, 0x4B, 0x03, 0x04, 4, 5, 6], cancellationToken);
        var stored = await WdlArchiveStore.ArchiveAsync(source, archive, null, cancellationToken);
        await File.WriteAllTextAsync(stored.Path, "tampered", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WdlArchiveStore.ArchiveAsync(source, archive, stored.Sha256, cancellationToken));
    }

    [Fact]
    public async Task Materialize_verifies_and_reuses_identical_local_copy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var source = temporary.Resolve("source.zip");
        var archive = temporary.Resolve("archive");
        var local = temporary.Resolve("intake", "source.zip");
        await File.WriteAllBytesAsync(source, [0x50, 0x4B, 0x03, 0x04, 7, 8, 9], cancellationToken);
        var stored = await WdlArchiveStore.ArchiveAsync(source, archive, null, cancellationToken);

        await WdlArchiveStore.MaterializeAsync(archive, stored.Sha256, local, cancellationToken);
        File.Delete(stored.Path); // A verified local copy must not need a serving-disk read.
        await WdlArchiveStore.MaterializeAsync(archive, stored.Sha256, local, cancellationToken);

        Assert.Equal(stored.Sha256, await WdlArchiveStore.ComputeSha256Async(local, cancellationToken));
    }

    [Fact]
    public async Task Materialize_rejects_corrupt_source_without_publishing_local_file()
    {
        using var temporary = new TempDirectory();
        var archive = temporary.Resolve("archive");
        var digest = new string('a', 64);
        var source = WdlArchiveStore.ObjectPath(archive, digest);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "wrong bytes", TestContext.Current.CancellationToken);
        var local = temporary.Resolve("intake", "source.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WdlArchiveStore.MaterializeAsync(archive, digest, local, CancellationToken.None));
        Assert.False(File.Exists(local));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(local)!));
    }
}
