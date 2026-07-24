namespace FrostAura.Reel.Application.Pipeline;

/// <summary>
/// One named SSE frame: <c>id: {Seq}\nevent: {Type}\ndata: {JsonData}</c>. <see cref="Seq"/> is the
/// monotonic per-account stamp emitted as the SSE <c>id:</c> field so the browser echoes it back as
/// <c>Last-Event-ID</c> on reconnect; a value of <c>0</c> (heartbeats) means "no id line" — writing
/// <c>id: 0</c> would wrongly reset the client's cursor.
/// </summary>
public record PipelineSseEvent(string Type, string JsonData, long Seq = 0);

/// <summary>
/// Per-account fan-out channel between pipeline jobs and SSE subscribers. Every published event
/// carries a monotonic per-account <c>seq</c> so clients dedupe EventSource replays. A small bounded
/// per-account ring buffer retains the most recent events so a reconnecting EventSource can replay the
/// events it missed (via <c>Last-Event-ID</c>); the frontend's sync-status poll remains the backstop
/// for gaps older than the buffer.
/// </summary>
public interface IPipelineEventHub
{
    /// <summary>Publishes a named event to all of the account's live subscribers (no-op when none).</summary>
    void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data);

    /// <summary>
    /// Subscribes to the account's stream until cancellation; heartbeats every ≤10s. When
    /// <paramref name="lastEventId"/> is supplied (the reconnecting client's <c>Last-Event-ID</c>),
    /// buffered events with a greater seq are replayed first so a missed <c>feed-ready</c> still lands.
    /// </summary>
    IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(Guid accountId, long? lastEventId, CancellationToken ct);
}

/// <summary>Canonical event names (mirror src/frontend/src/lib/onboardingStream.ts).</summary>
public static class PipelineEventTypes
{
    public const string Connected = "connected";
    public const string StageChanged = "stage-changed";
    public const string IngestProgress = "ingest-progress";
    public const string Insight = "insight";
    public const string ModelProgress = "model-progress";
    public const string FeedReady = "feed-ready";
    public const string Failed = "failed";
    public const string Heartbeat = "heartbeat";
    public const string JobProgress = "job-progress";
    public const string JobCompleted = "job-completed";
}
