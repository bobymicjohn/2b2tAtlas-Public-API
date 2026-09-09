using System.Net;
using System.Text;
using Atlas;
using Microsoft.Extensions.Options;
using _2b2tAtlas.Server.Services;
using _2b2tAtlas.Server.Services.AiEnrichment;

namespace Atlas.Ingestor.Tests;

public sealed class IngestionMatchAiServiceTests
{
    [Fact]
    public async Task Accepts_only_a_returned_candidate_id()
    {
        var service = Create("{\"decision\":\"existing\",\"locationId\":42,\"confidence\":0.98,\"reason\":\"same named build\"}");
        var result = await service.EvaluateAsync("later capture", "overworld",
            new ArchiveWarpCandidate("Temple_2020-01-01", "operator", 1, true),
            [new LocationMatchCandidate(42, "Temple", 0, 0, 0, ["Temple_2017-01-01"])],
            [new LocationMatchSuggestion(42, "Temple", 50_000, 0.7, "dated identity")],
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result?.LocationId);
        Assert.Equal(0.98, result?.Confidence);
    }

    [Fact]
    public async Task Rejects_an_invented_location_id()
    {
        var service = Create("{\"decision\":\"existing\",\"locationId\":999,\"confidence\":1.0,\"reason\":\"invented\"}");
        var result = await service.EvaluateAsync("capture", "overworld",
            new ArchiveWarpCandidate("Known", "operator", 1, true),
            [new LocationMatchCandidate(42, "Known", 0, 0, 0)],
            [new LocationMatchSuggestion(42, "Known", 100, 0.7, "candidate")],
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Model_failure_falls_back_to_deterministic_review()
    {
        var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://127.0.0.1:11434") };
        var options = Options.Create(new AiEnrichmentOptions
        {
            Enabled = true,
            Model = "test",
            GameModeLockPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".lock"),
        });
        var service = new IngestionMatchAiService(new OllamaClient(http, options), options);

        var result = await service.EvaluateAsync("capture", "overworld",
            new ArchiveWarpCandidate("Known", "operator", 1, true),
            [new LocationMatchCandidate(42, "Known", 0, 0, 0)],
            [new LocationMatchSuggestion(42, "Known", 100, 0.7, "candidate")],
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    private static IngestionMatchAiService Create(string modelJson)
    {
        var response = "{\"response\":" + System.Text.Json.JsonSerializer.Serialize(modelJson) + "}";
        var http = new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("http://127.0.0.1:11434") };
        var options = Options.Create(new AiEnrichmentOptions
        {
            Enabled = true,
            Model = "test",
            GameModeLockPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".lock"),
        });
        return new IngestionMatchAiService(new OllamaClient(http, options), options);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("local model unavailable");
    }
}
