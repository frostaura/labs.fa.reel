using System.Globalization;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// The observability surface (WS-6 task 6): the browser's client-error sink, and the metrics report —
/// job failures from the runner's M5 columns, Ask stage latency and fallbacks from the turn record,
/// and, for the operator only, model truncation and fallback per purpose and the active alerts.
/// </summary>
public static class ObservabilityEndpoints
{
    /// <summary>The longest window the metrics report will read, so one request cannot scan the whole history.</summary>
    public const int MaxWindowHours = 24 * 7;

    /// <summary>When this process started counting client errors (the report's honest "since").</summary>
    private static readonly DateTime ProcessStartedAt = DateTime.UtcNow;

    public static void MapObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        // Open to signed-out callers on purpose — a sign-in page that crashes must be able to say so —
        // but they share one small budget, and a signed-in caller's reports are filed under, and
        // limited by, their own account.
        app.MapPost("/api/client-errors", (ClientErrorReport? report, IAccountContext accountContext, ClientErrorSink sink, HttpContext http) =>
        {
            var accountId = accountContext.AccountId;
            switch (sink.Record(accountId, report))
            {
                case ClientErrorOutcome.Accepted:
                    return Results.Accepted();

                case ClientErrorOutcome.RateLimited:
                    var seconds = Math.Max(1, (int)Math.Ceiling(sink.RetryAfter(accountId).TotalSeconds));
                    http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                    return Results.Json(new { error = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests);

                default:
                    return Results.Json(new { error = "invalid_report" }, statusCode: StatusCodes.Status400BadRequest);
            }
        })
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status429TooManyRequests);

        var group = app.MapGroup("/api/observability").RequireAccount();

        group.MapGet("/metrics", async (
            int? hours, IAccountContext accountContext, IReelDbContext db, JobMetricsQuery jobs, AskMetricsQuery ask,
            ClientErrorSink sink, IOperatorAlertReader alerts, ILlmCallHealthReader llm, CancellationToken ct) =>
        {
            var windowHours = Math.Clamp(hours ?? 24, 1, MaxWindowHours);
            var accountId = accountContext.AccountId!.Value;
            var now = DateTime.UtcNow;
            var since = now.AddHours(-windowHours);
            var totals = sink.TotalsFor(accountId);
            var founder = await db.Accounts.AnyAsync(a => a.Id == accountId && a.Tier == AccountTier.Founder, ct);

            return Results.Ok(new MetricsReport(
                now,
                since,
                windowHours,
                await jobs.GetAsync(accountId, since, ct),
                new ClientErrorMetrics(totals.Accepted, totals.RateLimited, ProcessStartedAt),
                await ask.GetAsync(accountId, since, ct),
                // The model-call tally carries no account — it is every account's calls — so, like the
                // alerts and the status API's llmHealth, it is the operator's alone (HREV2-PLAT-2).
                founder ? llm.Snapshot() : [],
                ProcessStartedAt,
                founder ? alerts.Current() : []));
        })
        .Produces<MetricsReport>()
        .Produces(StatusCodes.Status401Unauthorized);
    }
}
