using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>
/// A minimal client for a loopback Ollama server's non-streaming <c>/api/generate</c> endpoint. It is used
/// only from the trusted server host to draft descriptions and rank matches with the local model; it never
/// streams, uses tools, or leaves the machine.
/// </summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;
    private readonly AiEnrichmentOptions _options;

    /// <summary>Initializes the client with a configured HTTP client and enrichment options.</summary>
    /// <param name="http">The typed HTTP client whose base address targets the loopback Ollama server.</param>
    /// <param name="options">The enrichment options supplying the model tag.</param>
    public OllamaClient(HttpClient http, IOptions<AiEnrichmentOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    /// <summary>Generates a single completion for the given prompt.</summary>
    /// <param name="prompt">The fully-formed prompt (including any system framing).</param>
    /// <param name="temperature">Sampling temperature; low values keep output faithful to the source.</param>
    /// <param name="maxTokens">Upper bound on generated tokens.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The trimmed model output, or <see langword="null"/> when the call fails or is empty.</returns>
    public async Task<string?> GenerateAsync(string prompt, double temperature, int maxTokens, CancellationToken cancellationToken)
    {
        var request = new GenerateRequest(_options.Model, prompt, false, new GenerateTuning(temperature, maxTokens));
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/generate", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken);
            var text = body?.Response?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The model host is unavailable or slow; enrichment is best-effort and degrades to "no suggestion".
            return null;
        }
    }

    /// <summary>The Ollama generate request body.</summary>
    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] GenerateTuning Options);

    /// <summary>Sampling controls forwarded to Ollama.</summary>
    private sealed record GenerateTuning(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    /// <summary>The relevant portion of the Ollama generate response.</summary>
    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string? Response);
}
