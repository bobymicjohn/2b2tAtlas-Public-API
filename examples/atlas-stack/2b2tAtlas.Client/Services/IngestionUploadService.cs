using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas;
using Microsoft.AspNetCore.Components.Forms;

namespace _2b2tAtlas.Client.Services;

/// <summary>Uploads WDLs in proxy-safe resumable chunks and finalizes them through the secure ingestion API.</summary>
public sealed class IngestionUploadService
{
    private const long MaxUploadBytes = 32L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    /// <summary>Initializes the chunked uploader.</summary>
    /// <param name="http">API client.</param>
    /// <param name="auth">Bearer-token source.</param>
    public IngestionUploadService(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Uploads and queues one browser-selected ZIP.</summary>
    /// <param name="file">Browser file.</param>
    /// <param name="request">Validated ingestion metadata.</param>
    /// <param name="progress">Optional uploaded-byte callback.</param>
    /// <param name="cancellationToken">Token that cancels upload.</param>
    public async Task<IngestionJobDto?> UploadAsync(
        IBrowserFile file,
        IngestionJobRequest request,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Your session has expired. Sign in again to upload.");
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "api/ingestion-jobs/upload-sessions")
        {
            Content = JsonContent.Create(new ChunkedUploadStartRequest
            {
                Metadata = JsonSerializer.Serialize(request),
                FileName = file.Name,
                TotalBytes = file.Size,
            }),
        };
        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var startResponse = await _http.SendAsync(startRequest, cancellationToken);
        if (!startResponse.IsSuccessStatusCode)
            throw await UploadFailureAsync("start", startResponse, cancellationToken);
        var session = await startResponse.Content.ReadFromJsonAsync<ChunkedUploadSession>(cancellationToken)
            ?? throw new InvalidOperationException("Upload session response was empty.");

        await using var stream = file.OpenReadStream(MaxUploadBytes, cancellationToken);
        var buffer = new byte[session.ChunkSizeBytes];
        long offset = 0;
        while (offset < file.Size)
        {
            var wanted = (int)Math.Min(buffer.Length, file.Size - offset);
            var read = 0;
            while (read < wanted)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, wanted - read), cancellationToken);
                if (count == 0) throw new EndOfStreamException("Browser file ended before its declared size.");
                read += count;
            }

            using var chunkRequest = new HttpRequestMessage(
                HttpMethod.Put, $"api/ingestion-jobs/upload-sessions/{session.Id}/chunks?offset={offset}")
            {
                Content = new ByteArrayContent(buffer, 0, read),
            };
            chunkRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            chunkRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var chunkResponse = await _http.SendAsync(chunkRequest, cancellationToken);
            if (!chunkResponse.IsSuccessStatusCode)
                throw await UploadFailureAsync("chunk", chunkResponse, cancellationToken);
            offset += read;
            progress?.Report(offset);
        }

        using var completeRequest = new HttpRequestMessage(
            HttpMethod.Post, $"api/ingestion-jobs/upload-sessions/{session.Id}/complete");
        completeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var completeResponse = await _http.SendAsync(completeRequest, cancellationToken);
        if (!completeResponse.IsSuccessStatusCode)
            throw await UploadFailureAsync("complete", completeResponse, cancellationToken);
        return await completeResponse.Content.ReadFromJsonAsync<IngestionJobDto>(cancellationToken);
    }

    private static async Task<InvalidOperationException> UploadFailureAsync(
        string stage,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        return new InvalidOperationException(
            $"Upload {stage} rejected ({(int)response.StatusCode}). {detail}");
    }
}
