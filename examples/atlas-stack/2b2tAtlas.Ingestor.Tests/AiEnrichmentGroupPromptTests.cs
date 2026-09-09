using System.Net;
using System.Text;
using System.Text.Json;
using Atlas.Enrichment;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using _2b2tAtlas.Server.Services.AiEnrichment;

namespace Atlas.Ingestor.Tests;

public sealed class AiEnrichmentGroupPromptTests
{
    [Fact]
    public async Task Group_description_prompt_preserves_the_reviewed_attribution_lessons()
    {
        var handler = new RecordingHandler("A careful source-bound description.");
        var options = Options.Create(new AiEnrichmentOptions
        {
            Enabled = true,
            Model = "test-model",
            GameModeLockPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            MaxDescriptionChars = 600,
        });
        var ollamaHttp = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434") };
        var wikiHttp = new HttpClient(new RecordingHandler("unused"))
        {
            BaseAddress = new Uri("https://2b2t.miraheze.org"),
        };
        var service = new AiEnrichmentService(
            new WikiClient(wikiHttp, options),
            new OllamaClient(ollamaHttp, options),
            options,
            NullLogger<AiEnrichmentService>.Instance);

        var result = await service.DraftGroupDescriptionAsync(new GroupDiscoverySuggestion
        {
            Name = "The Imperials",
            Type = GroupType.Build,
            Intro = "The source introduction.",
            WikiUrl = "https://2b2t.wikioasis.org/wiki/The_Imperials",
            SourceRevisionId = 12345,
            Locations =
            [
                new GroupDiscoveryLocation
                {
                    LocationId = 10,
                    LocationName = "Imperial Base II",
                    Role = "Builder",
                    Evidence = "Infobox group bases",
                },
            ],
        }, CancellationToken.None);

        Assert.Equal("A careful source-bound description.", result);
        var prompt = Assert.IsType<string>(handler.Prompt);
        Assert.Contains("ONLY the revision-pinned source data", prompt);
        Assert.Contains("visit, residence, grief, alliance", prompt);
        Assert.Contains("Preserve numbered base iterations", prompt);
        Assert.Contains("The Imperials, The Emperium, and Imperator's Group distinct", prompt);
        Assert.Contains("Imperial Base II (Infobox group bases)", prompt);
        Assert.Contains("revision 12345", prompt);
    }

    private sealed class RecordingHandler(string responseText) : HttpMessageHandler
    {
        public string? Prompt { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                var json = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("prompt", out var prompt))
                    Prompt = prompt.GetString();
            }

            var response = JsonSerializer.Serialize(new { response = responseText });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
