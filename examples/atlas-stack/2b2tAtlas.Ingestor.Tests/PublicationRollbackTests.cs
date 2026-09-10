using Atlas.Ingestor.Worker;

namespace Atlas.Ingestor.Tests;

public sealed class PublicationRollbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_registration_preserves_published_bytes_and_rollback(bool hadPrevious)
    {
        using var temp = new TempDirectory();
        var published = Path.Combine(temp.Path, "g-current");
        var backup = Path.Combine(temp.Path, "g-previous");
        Directory.CreateDirectory(published);
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(published, "tile.png"), "registered generation", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(backup, "tile.png"), "previous generation", TestContext.Current.CancellationToken);

        // The server may commit and then lose the response (timeout/disconnect).
        // Neither deletion nor restoring old bytes over the public URL is safe.
        await IngestionWorker.RestorePublishedTilesAsync(published, backup, null,
            hadPrevious, hadPrevious, TestContext.Current.CancellationToken, registrationAttempted: true);

        Assert.Equal("registered generation", await File.ReadAllTextAsync(Path.Combine(published, "tile.png"), TestContext.Current.CancellationToken));
        Assert.Equal("previous generation", await File.ReadAllTextAsync(Path.Combine(backup, "tile.png"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failure_before_registration_can_remove_its_unpublished_generation()
    {
        using var temp = new TempDirectory();
        var published = Path.Combine(temp.Path, "new-generation");
        var previous = Path.Combine(temp.Path, "older-public-generation");
        Directory.CreateDirectory(published);
        Directory.CreateDirectory(previous);
        await File.WriteAllTextAsync(Path.Combine(previous, "tile.png"), "still public", TestContext.Current.CancellationToken);

        await IngestionWorker.RestorePublishedTilesAsync(published, published + ".backup", null,
            false, false, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(published));
        Assert.Equal("still public", await File.ReadAllTextAsync(Path.Combine(previous, "tile.png"), TestContext.Current.CancellationToken));
    }
}
