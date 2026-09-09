using Atlas.Ingestor.Rendering;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Tests;

public sealed class RenderProvenanceTests
{
    [Fact]
    public async Task Resume_requires_matching_plan_profile_and_arguments()
    {
        using var temporary = new TempDirectory();
        var output = temporary.Resolve("render", "overworld");
        var provenancePath = temporary.Resolve("render", ".atlas-render-provenance-overworld.json");
        var expected = CreateProvenance("plan-a", "hash-a", ["web", "render", "--world={world}", "--output={output}"]);

        await RenderProvenanceStore.ValidateAndWriteAsync(
            provenancePath, output, expected, false, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(output);
        await RenderProvenanceStore.ValidateAndWriteAsync(
            provenancePath, output, expected with { UpdatedAtUtc = DateTimeOffset.UtcNow }, true, TestContext.Current.CancellationToken);

        var changed = expected with { PlanSha256 = "plan-b", UpdatedAtUtc = DateTimeOffset.UtcNow };
        await Assert.ThrowsAsync<InputSecurityException>(() => RenderProvenanceStore.ValidateAndWriteAsync(
            provenancePath, output, changed, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resume_rejects_output_without_provenance()
    {
        using var temporary = new TempDirectory();
        var output = temporary.Resolve("render", "nether");
        Directory.CreateDirectory(output);

        await Assert.ThrowsAsync<InputValidationException>(() => RenderProvenanceStore.ValidateAndWriteAsync(
            temporary.Resolve("render", ".atlas-render-provenance-nether.json"),
            output,
            CreateProvenance("plan", "hash", ["web", "render", "--world={world}", "--output={output}"]),
            true,
            TestContext.Current.CancellationToken));
    }

    private static RenderProvenance CreateProvenance(string plan, string renderer, string[] arguments) => new(
        plan,
        "overworld",
        "C:\\world",
        "0.19.60-dev",
        renderer,
        arguments,
        "started",
        DateTimeOffset.UtcNow);
}