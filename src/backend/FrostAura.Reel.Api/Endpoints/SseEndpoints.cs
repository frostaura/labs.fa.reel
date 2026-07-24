using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;

namespace FrostAura.Reel.Api.Endpoints;

public static class SseEndpoints
{
    /// <summary>
    /// The single pipeline event channel (onboarding build-up + steady-state sync narration).
    /// nginx's /sse/ location runs unbuffered with a 24h read timeout; heartbeats every ≤10s
    /// keep intermediaries from reaping idle streams.
    /// </summary>
    public static void MapSseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sse/pipeline", async (HttpContext http, IAccountContext accountContext, IPipelineEventHub hub, CancellationToken ct) =>
        {
            if (accountContext.AccountId is not { } accountId)
            {
                return Results.Unauthorized();
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await http.Response.Body.FlushAsync(ct);

            // On auto-reconnect the browser echoes the last id it saw as Last-Event-ID; the hub replays
            // the buffered events past it so a missed feed-ready still lands. Absent/garbage → full live.
            var lastEventId = long.TryParse(http.Request.Headers["Last-Event-ID"].FirstOrDefault(), out var since)
                ? since
                : (long?)null;

            try
            {
                await foreach (var sseEvent in hub.SubscribeAsync(accountId, lastEventId, ct))
                {
                    // Emit the id: line only for real events (seq > 0). Heartbeats carry seq 0 and must
                    // NOT write id: 0, which would reset the client's Last-Event-ID cursor to the start.
                    var frame = sseEvent.Seq > 0
                        ? $"id: {sseEvent.Seq}\nevent: {sseEvent.Type}\ndata: {sseEvent.JsonData}\n\n"
                        : $"event: {sseEvent.Type}\ndata: {sseEvent.JsonData}\n\n";
                    await http.Response.WriteAsync(frame, ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client went away — normal stream teardown
            }

            return Results.Empty;
        });
    }
}
