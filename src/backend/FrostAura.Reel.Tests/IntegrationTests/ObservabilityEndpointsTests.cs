using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The observability surface over HTTP: <c>POST /api/client-errors</c> (the sink the browser's
/// error boundary already posts to) and <c>GET /api/observability/metrics</c>, whose job figures
/// come from the M5 columns. Asserted on the wire JSON, which is what a dashboard reads.
/// </summary>
[Collection("postgres")]
public class ObservabilityEndpointsTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private static object BrowserReport(string path = "/feed") => new
    {
        name = "TypeError",
        message = "Cannot read properties of undefined (reading 'title')",
        stack = "TypeError: Cannot read properties of undefined\n    at FeedCard (FeedCard.tsx:41:9)",
        componentStack = "\n    at FeedCard\n    at Feed",
        origin = "uncaught",
        path,
        occurredAt = "2026-10-25T09:00:00.000Z",
    };

    [Fact]
    public async Task A_signed_in_browser_report_is_accepted_until_its_accounts_budget_is_spent()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"client-errors-{Guid.NewGuid():N}");
        using (client)
        {
            var budget = new Infrastructure.Telemetry.ClientErrorSinkOptions().PerAccount;
            for (var i = 0; i < budget; i++)
            {
                using var accepted = await client.PostAsJsonAsync("/api/client-errors", BrowserReport($"/feed/{i}"));
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }

            using var refused = await client.PostAsJsonAsync("/api/client-errors", BrowserReport());
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.True(refused.Headers.RetryAfter?.Delta > TimeSpan.Zero, "429 without a Retry-After");
            Assert.Equal("rate_limited", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

            var metrics = await GetMetricsAsync(client);
            var clientErrors = metrics.GetProperty("clientErrors");
            Assert.Equal(budget, clientErrors.GetProperty("accepted").GetInt64());
            Assert.Equal(1, clientErrors.GetProperty("rateLimited").GetInt64());
        }

        // Another account's budget is untouched by the first one's loop.
        var (_, other) = await api.CreateSignedInAccountAsync($"client-errors-other-{Guid.NewGuid():N}");
        using (other)
        {
            using var accepted = await other.PostAsJsonAsync("/api/client-errors", BrowserReport());
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.NotEqual(Guid.Empty, accountId);
        }
    }

    [Fact]
    public async Task A_malformed_report_is_a_400_and_a_signed_out_report_is_accepted_into_the_shared_budget()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var anonymous = api.CreateClient();
        using (var bad = await anonymous.PostAsJsonAsync("/api/client-errors", new { name = "Error", message = "m", origin = "server", path = "/feed" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("invalid_report", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        using (var notJson = await anonymous.PostAsync("/api/client-errors", new StringContent("{", System.Text.Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        }

        using var accepted = await anonymous.PostAsJsonAsync("/api/client-errors", BrowserReport("/signin"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        // The metrics report is the caller's own, so it needs an account.
        using var metrics = await anonymous.GetAsync("/api/observability/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, metrics.StatusCode);
    }

    [Fact]
    public async Task The_job_metrics_report_the_m5_columns_and_failure_codes_for_the_callers_jobs_and_global_ones_only()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"job-metrics-{Guid.NewGuid():N}");
        var stranger = await api.SeedAccountAsync($"job-metrics-stranger-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;

        // Inside the window, this account. CrossReference is unique to this test's account rows, so
        // its figures are exact even with other tests' jobs in the shared database.
        await AddAsync(accountId, JobKind.CrossReference, JobStatus.Succeeded, completedAt: now.AddHours(-1), deadlineYields: 3, requeuedAt: now.AddHours(-2));
        await AddAsync(accountId, JobKind.CrossReference, JobStatus.Failed, completedAt: now.AddMinutes(-30),
            error: "Response status code does not indicate success: 402 (Payment Required).");
        await AddAsync(accountId, JobKind.CrossReference, JobStatus.Failed, completedAt: now.AddMinutes(-20),
            error: "Timed out: ran past its 12-minute deadline.", interruptions: 2);

        // Outside the window, and another account's: neither may show.
        await AddAsync(accountId, JobKind.CrossReference, JobStatus.Failed, enqueuedAt: now.AddDays(-3), completedAt: now.AddDays(-3),
            error: "401 (Unauthorized)");
        await AddAsync(stranger, JobKind.CrossReference, JobStatus.Failed, completedAt: now.AddMinutes(-5), error: "401 (Unauthorized)",
            deadlineYields: 50);

        using (client)
        {
            var report = await GetMetricsAsync(client, hours: 24);
            Assert.Equal(24, report.GetProperty("windowHours").GetInt32());

            var xref = report.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("kind").GetString() == "CrossReference");
            Assert.Equal(1, xref.GetProperty("succeeded").GetInt32());
            Assert.Equal(2, xref.GetProperty("failed").GetInt32());
            Assert.Equal(0, xref.GetProperty("retrying").GetInt32());
            Assert.Equal(3, xref.GetProperty("deadlineYields").GetInt32());
            Assert.Equal(2, xref.GetProperty("interruptions").GetInt32());
            Assert.Equal(1, xref.GetProperty("requeued").GetInt32());

            var reasons = xref.GetProperty("failureReasons");
            Assert.Equal(1, reasons.GetProperty("payment_required").GetInt32());
            Assert.Equal(1, reasons.GetProperty("timed_out").GetInt32());
            Assert.False(reasons.TryGetProperty("unauthorized", out _), "an old or another account's failure leaked in");

            // Codes and counts only: no error text leaves.
            Assert.DoesNotContain("Payment Required", report.GetRawText(), StringComparison.Ordinal);

            // A wider window takes the old failure in; the stranger's never.
            var week = await GetMetricsAsync(client, hours: 24 * 7);
            var weekXref = week.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("kind").GetString() == "CrossReference");
            Assert.Equal(3, weekXref.GetProperty("failed").GetInt32());
            Assert.Equal(1, weekXref.GetProperty("failureReasons").GetProperty("unauthorized").GetInt32());
            Assert.Equal(3, weekXref.GetProperty("deadlineYields").GetInt32());

            // The window is bounded both ways.
            Assert.Equal(1, (await GetMetricsAsync(client, hours: 0)).GetProperty("windowHours").GetInt32());
            Assert.Equal(24 * 7, (await GetMetricsAsync(client, hours: 100_000)).GetProperty("windowHours").GetInt32());
        }
    }

    [Fact]
    public async Task The_metrics_report_ask_latency_and_fallbacks_from_the_callers_own_turns_and_model_calls_per_purpose()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"ask-metrics-{Guid.NewGuid():N}");
        var stranger = await api.SeedAccountAsync($"ask-metrics-stranger-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;

        await AddTurnAsync(accountId, now.AddMinutes(-50), 1000, """{"interpret":200,"rerank":700}""", flags: null);
        await AddTurnAsync(accountId, now.AddMinutes(-40), 3000, """{"interpret":400,"discover":100,"rerank":2500}""", flags: "plan");
        await AddTurnAsync(accountId, now.AddMinutes(-30), 9000, """{"interpret":800}""", flags: "judge:3/9", status: SearchTurnStatus.Failed);
        // A timing row this build cannot read still counts as a turn but adds no stage samples.
        await AddTurnAsync(accountId, now.AddMinutes(-20), null, """{"interpret":"n/a","rerank":-5}""", flags: "judge:0/9");
        // Excluded: still pending, a user row, another account's, and outside the window.
        await AddTurnAsync(accountId, now.AddMinutes(-10), 99_000, """{"interpret":99000}""", flags: "plan", status: SearchTurnStatus.Pending);
        await AddTurnAsync(accountId, now.AddMinutes(-10), 99_000, """{"interpret":99000}""", flags: "plan", role: "user");
        await AddTurnAsync(stranger, now.AddMinutes(-10), 99_000, """{"interpret":99000}""", flags: "plan");
        await AddTurnAsync(accountId, now.AddDays(-3), 99_000, """{"interpret":99000}""", flags: "plan");

        // Model calls are counted per purpose across the process; a purpose of this test's own.
        var purpose = $"probe-metrics-{Guid.NewGuid():N}";
        LlmCallMetrics.Record(purpose, LlmResult(LlmCallFailure.Truncated));
        LlmCallMetrics.Record(purpose, LlmResult(LlmCallFailure.TimedOut));
        LlmCallMetrics.Record(purpose, LlmResult(failure: null));

        using (client)
        {
            var report = await GetMetricsAsync(client, hours: 24);
            var ask = report.GetProperty("ask");
            Assert.Equal(4, ask.GetProperty("turns").GetInt32());
            Assert.Equal(1, ask.GetProperty("failed").GetInt32());
            Assert.Equal(1, ask.GetProperty("planFallbacks").GetInt32());
            Assert.Equal(1, ask.GetProperty("judgeFallbacks").GetInt32());

            var latency = ask.GetProperty("latency");
            Assert.Equal(3, latency.GetProperty("samples").GetInt32());
            Assert.Equal(3000, latency.GetProperty("p50").GetInt64());
            Assert.Equal(9000, latency.GetProperty("p95").GetInt64());

            // Pipeline order, not alphabetical; each stage over the turns that ran it.
            var stages = ask.GetProperty("stages").EnumerateArray().ToList();
            Assert.Equal(["interpret", "discover", "rerank"], stages.Select(s => s.GetProperty("stage").GetString() ?? string.Empty).ToArray());
            Assert.Equal(400, stages[0].GetProperty("latency").GetProperty("p50").GetInt64());
            Assert.Equal(800, stages[0].GetProperty("latency").GetProperty("p95").GetInt64());
            Assert.Equal(2, stages[2].GetProperty("latency").GetProperty("samples").GetInt32());

            var llm = report.GetProperty("llm").EnumerateArray().Single(p => p.GetProperty("purpose").GetString() == purpose);
            Assert.Equal(3, llm.GetProperty("calls").GetInt64());
            Assert.Equal(1, llm.GetProperty("answered").GetInt64());
            Assert.Equal(1, llm.GetProperty("truncated").GetInt64());
            Assert.Equal(2, llm.GetProperty("fallbacks").GetInt64());
            Assert.Equal(1, llm.GetProperty("outcomes").GetProperty("timed_out").GetInt64());

            // Operator alerts are the Founder tier's; this caller is Free.
            Assert.Equal(0, report.GetProperty("alerts").GetArrayLength());
        }
    }

    private static async Task<JsonElement> GetMetricsAsync(HttpClient client, int? hours = null)
    {
        using var response = await client.GetAsync(hours is null ? "/api/observability/metrics" : $"/api/observability/metrics?hours={hours}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task AddTurnAsync(
        Guid accountId, DateTime completedAt, int? latencyMs, string timings, string? flags,
        string status = SearchTurnStatus.Completed, string role = "assistant")
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        db.SearchTurns.Add(new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = Guid.NewGuid(),
            Role = role,
            Text = "metrics probe",
            CreatedAt = completedAt.AddSeconds(-10),
            CompletedAt = status == SearchTurnStatus.Pending ? null : completedAt,
            Status = status,
            LatencyMs = latencyMs,
            StageTimingsJson = timings,
            DegradedFlags = flags,
        });
        await db.SaveChangesAsync();
    }

    private static LlmChatResult LlmResult(LlmCallFailure? failure) =>
        new(null, failure, 200, failure == LlmCallFailure.Truncated ? "length" : "stop", 1, 100, 10, 10, null, "probe/model", null, null, TimeSpan.Zero);

    private async Task AddAsync(
        Guid accountId,
        JobKind kind,
        JobStatus status,
        DateTime? completedAt = null,
        DateTime? enqueuedAt = null,
        string? error = null,
        int deadlineYields = 0,
        int interruptions = 0,
        DateTime? requeuedAt = null)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Status = status,
            EnqueuedAt = enqueuedAt ?? (completedAt ?? DateTime.UtcNow).AddMinutes(-5),
            StartedAt = completedAt?.AddMinutes(-4),
            CompletedAt = completedAt,
            AttemptCount = 1,
            Error = error,
            DeadlineYields = deadlineYields,
            Interruptions = interruptions,
            RequeuedAt = requeuedAt,
        });
        await db.SaveChangesAsync();
    }
}
