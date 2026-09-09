using Atlas.Ingestor.Security;
using Atlas.Ingestor.Worker;

namespace Atlas.Ingestor.Tests;

public sealed class IngestionWorkerPublishedStateTests
{
    [Fact]
    public void Published_state_reuses_its_existing_generation()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "atlas-published-state", "slug", "overworld"));
        var generation = "g-20260825012459-b87aa11c";

        Assert.Equal(generation, IngestionWorker.ResolvePublishedGeneration(
            Path.Combine(root, generation), root, receiptIsVariant: false));
        Assert.Equal(generation, IngestionWorker.ResolvePublishedGeneration(
            Path.Combine(root, generation, "day"), root, receiptIsVariant: true));
    }

    [Theory]
    [InlineData("g-not-a-generation")]
    [InlineData("g-20260825012459-b87aa11c\\..\\other")]
    public void Published_state_rejects_invalid_or_escaping_generation(string value)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "atlas-published-state", "slug", "overworld"));

        Assert.Throws<InputSecurityException>(() => IngestionWorker.ResolvePublishedGeneration(
            Path.Combine(root, value), root, receiptIsVariant: false));
    }
}
