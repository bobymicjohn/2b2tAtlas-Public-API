using System.Threading.Channels;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>
/// A bounded in-memory queue of location ids awaiting background AI enrichment after a render is attached to
/// them. Producers (the ingestion pipeline) enqueue without blocking; the background worker drains it. The
/// queue is best-effort: if it is full the oldest pending id is dropped, since any missed location can be
/// caught later by an admin batch run.
/// </summary>
public sealed class EnrichmentQueue
{
    private readonly Channel<int> _channel = Channel.CreateBounded<int>(new BoundedChannelOptions(500)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    /// <summary>Gets the reader the background worker consumes.</summary>
    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>Enqueues a location for background enrichment. Never blocks.</summary>
    /// <param name="locationId">The location row id to enrich.</param>
    /// <returns>True when accepted; false when the id could not be written.</returns>
    public bool Enqueue(int locationId) => _channel.Writer.TryWrite(locationId);
}
