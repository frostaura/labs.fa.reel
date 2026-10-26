using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The "through your lens" pipeline against real Postgres — the narrative job, the lens job's
/// selection and budget, the on-demand path, and the tenancy boundary.
///
/// <para>Everything here runs the DETERMINISTIC STUB. Not one test in this repository may reach
/// OpenRouter: a suite that spends money is a suite people stop running, and a suite whose answers
/// change under it is not a suite. The stub is wired exactly as the keyless-dev path is, so what
/// these tests exercise is the real code path with a fixture at the far end of it.</para>
/// </summary>
[Collection("postgres")]
public class LensPipelineTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Behind_an_open_provider_breaker_the_lens_and_narrative_jobs_pause_without_spending()
    {
        if (!fixture.Available)
        {
            return;
        }

        // WS-1 task 2/6: "LLM job kinds paused". The breaker is the provider's (a 402); both jobs
        // stop before reading anything, say why, and finish rather than fail — the status API
        // already carries the breaker, and a red job per build would be the same news twice.
        var health = new FrostAura.Reel.Infrastructure.Llm.ProviderHealthService();
        health.ReportRefused(ApiProvider.OpenRouter, 402);
        await using var db = fixture.CreateContext(new AccountContext());
        var lens = new PersonaliseTitlesJobHandler(db, null!, null!, null!, NullLogger<PersonaliseTitlesJobHandler>.Instance, health);
        var narrative = new BuildTasteNarrativeJobHandler(db, null!, null!, NullLogger<BuildTasteNarrativeJobHandler>.Instance, health);
        var lensJob = new SyncJob { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Kind = JobKind.PersonaliseTitles };
        var narrativeJob = new SyncJob { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Kind = JobKind.BuildTasteNarrative };

        await lens.ExecuteAsync(lensJob, CancellationToken.None);
        await narrative.ExecuteAsync(narrativeJob, CancellationToken.None);

        // Plain words, not the status API's code: a user reads this line in the pill.
        Assert.Equal("Lens paused: the model provider account is out of credit", lensJob.ProgressMessage);
        Assert.Equal("Taste profile paused: the model provider account is out of credit", narrativeJob.ProgressMessage);
    }

    [Theory]
    [InlineData(402, JobFailureReason.PaymentRequired)]
    [InlineData(401, JobFailureReason.Unauthorized)]
    public async Task A_forced_regenerate_behind_an_open_breaker_fails_visibly_instead_of_being_consumed(
        int status, string reason)
    {
        if (!fixture.Available)
        {
            return;
        }

        // The user pressed "regenerate" and the job was already queued when the breaker opened. A
        // quiet "paused" success consumed their request with nothing to show for it; the job now
        // fails, naming the refusal, so the status API and the pill say what happened.
        var health = new FrostAura.Reel.Infrastructure.Llm.ProviderHealthService();
        health.ReportRefused(ApiProvider.OpenRouter, status);
        await using var db = fixture.CreateContext(new AccountContext());
        var narrative = new BuildTasteNarrativeJobHandler(db, null!, null!, NullLogger<BuildTasteNarrativeJobHandler>.Instance, health);
        var forced = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Kind = JobKind.BuildTasteNarrative,
            CursorJson = BuildTasteNarrativeJobHandler.ForceCursor,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => narrative.ExecuteAsync(forced, CancellationToken.None));
        Assert.Equal(reason, JobFailureReason.Classify(error.Message));
        Assert.Contains("not rewritten", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Past_the_cool_off_a_background_job_goes_ahead_and_becomes_the_probe()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The breaker is half-open: no probe is out, so the job is NOT paused — it proceeds, and its
        // first call would take the probe slot. Paused-for-ever was the reviewers' "background work
        // never half-opens the breaker".
        var clock = new ManualClock();
        var health = new FrostAura.Reel.Infrastructure.Llm.ProviderHealthService(clock: clock);
        health.ReportRefused(ApiProvider.OpenRouter, 402);
        clock.Advance(FrostAura.Reel.Infrastructure.Llm.ProviderHealthService.CoolOff + TimeSpan.FromSeconds(1));

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var handler = new BuildTasteNarrativeJobHandler(
            db, deps.Features, deps.Narratives, NullLogger<BuildTasteNarrativeJobHandler>.Instance, health);
        var job = new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.BuildTasteNarrative, EnqueuedAt = DateTime.UtcNow };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();

        await handler.ExecuteAsync(job, CancellationToken.None);

        Assert.StartsWith("Taste profile rewritten", job.ProgressMessage, StringComparison.Ordinal);
        await CompleteAsync(db, job);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Stub mode on, and no key — the same configuration a keyless install runs.</summary>
    private static IConfiguration StubConfig(params (string Key, string Value)[] extra) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" }
                    .Concat(extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
                    .ToDictionary(kv => kv.Key, kv => kv.Value))
            .Build();

    private static OpenRouterLensWriter Writer(IConfiguration configuration) =>
        new(
            new HttpClient { BaseAddress = new Uri("http://stub.invalid/") },
            new ApiUsageRecorder(new NoopScopeFactory(), NullLogger<ApiUsageRecorder>.Instance),
            configuration,
            NullLogger<OpenRouterLensWriter>.Instance);

    /// <summary>An account-pinned context — what an HTTP request's DbContext actually carries.</summary>
    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private sealed class NoopScopeFactory : IServiceScopeFactory
    {
        // The recorder only touches a scope when it FLUSHES, and nothing here flushes: these tests
        // exercise generation and budgeting, not the ledger's own write path.
        public IServiceScope CreateScope() => throw new InvalidOperationException("No flush expected in these tests.");
    }

    private sealed class Deps(ReelDbContext db, IConfiguration configuration)
    {
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

        public DislikeSignal Dislikes => new(db, Cache);

        public FeatureVectorBuilder Features => new(db);

        public TasteFactsBuilder Facts => new(db, Dislikes, Features, Cache);

        public PersonalisationContext Personalisation => new(db, Dislikes);

        public LensBudget Budget => new(db, configuration);

        public LensService Lens => new(
            db,
            Writer(configuration),
            new LensInputBuilder(db, new RatingCalibrationService(db, Cache)),
            new LensVerifier(db),
            Budget,
            Personalisation,
            NullLogger<LensService>.Instance);

        public TasteNarrativeService Narratives => new(
            db, Writer(configuration), Facts, Personalisation, NullLogger<TasteNarrativeService>.Instance);
    }

    // ── The narrative job ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_narrative_job_writes_a_grounded_profile_and_stamps_its_fingerprint()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());

        await RunNarrativeAsync(db, deps, accountId);

        var narrative = await db.AccountTasteNarratives.SingleAsync(n => n.AccountId == accountId);
        Assert.Equal(1, narrative.Version);
        Assert.NotEmpty(narrative.Text);
        Assert.Null(narrative.EditedText);
        Assert.NotEmpty(narrative.ProfileHash);
        // The hysteresis anchor: without it every later computation re-quantises from scratch and
        // the whole cache-stability argument evaporates.
        Assert.NotEmpty(narrative.ProfileFingerprint);

        // The facts are persisted so the prose can be checked against its own evidence later.
        var facts = JsonSerializer.Deserialize<TasteFacts>(narrative.FactsJson, JsonOptions);
        Assert.NotNull(facts);
        Assert.Equal(narrative.RatingsCount, facts!.RatingsCount);
        Assert.Null(TasteNarrativeService.Ungrounded(narrative.Text, facts));
    }

    [Fact]
    public async Task A_second_run_with_unchanged_taste_spends_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());

        await RunNarrativeAsync(db, deps, accountId);
        var second = await deps.Narratives.WriteAsync(
            accountId, await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default),
            DateTime.UtcNow, force: false, default);

        Assert.Equal(NarrativeOutcome.UpToDate, second.Outcome);
        Assert.Equal(1, await db.AccountTasteNarratives.CountAsync(n => n.AccountId == accountId));
    }

    [Fact]
    public async Task A_user_edit_wins_and_survives_regeneration_until_they_ask_for_a_rewrite()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        await RunNarrativeAsync(db, deps, accountId);

        await deps.Narratives.SaveEditAsync(accountId, "You only ever watch heist films.", DateTime.UtcNow, default);

        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        // A forced rewrite that is NOT the user asking would still have to carry the edit forward.
        // Here `force` IS the user asking, so it clears it — the one case that may.
        var regenerated = await deps.Narratives.WriteAsync(accountId, taste, DateTime.UtcNow, force: true, default);

        Assert.Equal(NarrativeOutcome.Written, regenerated.Outcome);
        Assert.Equal(2, regenerated.Narrative!.Version);
        Assert.Null(regenerated.Narrative.EditedText);

        // And the personalisation context follows the effective text, not the generated one.
        await deps.Narratives.SaveEditAsync(accountId, "You only ever watch heist films.", DateTime.UtcNow, default);
        var context = await deps.Personalisation.ContextForAsync(accountId, taste, default);
        Assert.Equal("You only ever watch heist films.", context);
    }

    [Fact]
    public async Task Without_a_narrative_the_personalisation_context_is_the_compact_fallback_line()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());

        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var context = await deps.Personalisation.ContextForAsync(accountId, taste, default);

        // Not empty, and not a narrative: the compact summary every surface used before narratives
        // existed. A missing narrative degrades the prose, never the feed.
        Assert.NotEmpty(context);
        Assert.Contains("favours", context, StringComparison.Ordinal);
    }

    // ── The lens job ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_lens_job_writes_the_titles_that_carry_the_day_and_stops_at_fifteen()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // A hero of five plus four rails of six: forty cards, of which the selection may take
        // hero (5) + rank 0-2 of each rail (12) — capped at 15.
        await SeedSnapshotAsync(accountId, heroCount: 5, rails: 4, perRail: 6);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var handler = new PersonaliseTitlesJobHandler(
            db, deps.Features, deps.Facts, deps.Lens, NullLogger<PersonaliseTitlesJobHandler>.Instance);

        var selected = await handler.SelectAsync(accountId, includeSaved: false, default);

        Assert.Equal(PersonaliseTitlesJobHandler.MaxTitlesPerRun, selected.Count);
        Assert.Equal(selected.Count, selected.Distinct().Count());

        // Rank-major across rails: every rail gets its leader before any rail gets its third card,
        // or a cap that fell inside one rail would leave the others bare.
        var ranks = await db.FeedItems
            .Where(i => i.AccountId == accountId && selected.Contains(i.TitleId))
            .Select(i => new { i.TitleId, i.Row, i.Rank })
            .ToListAsync();
        Assert.All(ranks, r => Assert.True(r.Row == FeedRowKind.Hero || r.Rank < PersonaliseTitlesJobHandler.RailDepth));
    }

    [Fact]
    public async Task The_lens_job_writes_notes_that_pass_every_guard()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedSnapshotAsync(accountId, heroCount: 3, rails: 1, perRail: 3);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        await RunLensJobAsync(db, deps, accountId);

        var rows = await db.TitleLensCopies.Where(c => c.AccountId == accountId).ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(LensStatus.Done, r.Status));
        Assert.All(rows, r => Assert.Equal(OpenRouterLensWriter.CurrentLensPromptVersion, r.PromptVersion));

        foreach (var row in rows)
        {
            var note = JsonSerializer.Deserialize<LensNote>(row.LensJson, JsonOptions);
            Assert.NotNull(note);
            Assert.Contains(note!.Mood, LensVocabulary.Moods);
            Assert.False(SpoilerLexicon.Spoils(note.Lens));
        }
    }

    [Fact]
    public async Task A_second_pass_at_the_same_fingerprint_spends_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedSnapshotAsync(accountId, heroCount: 3, rails: 0, perRail: 0);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        await RunLensJobAsync(db, deps, accountId);
        var afterFirst = await db.TitleLensCopies.CountAsync(c => c.AccountId == accountId);

        await RunLensJobAsync(db, deps, accountId);

        Assert.Equal(afterFirst, await db.TitleLensCopies.CountAsync(c => c.AccountId == accountId));
    }

    [Fact]
    public async Task An_exhausted_budget_writes_nothing_and_the_surface_still_answers()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedSnapshotAsync(accountId, heroCount: 3, rails: 0, perRail: 0);

        await using var db = fixture.CreateContext(Scoped(accountId));
        // Zero calls a day. The ceiling is a real ceiling, not a guideline — and running out of it
        // must degrade the copy, never the page.
        var deps = new Deps(db, StubConfig((LensBudget.CallsSetting, "0")));

        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);
        var titleIds = await db.FeedItems
            .Where(i => i.AccountId == accountId)
            .Select(i => i.TitleId)
            .ToListAsync();

        var outcome = await deps.Lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, titleIds, default);

        Assert.Equal(0, outcome.Written);
        Assert.Equal("budget exhausted", outcome.Reason);
        Assert.Empty(await db.TitleLensCopies.Where(c => c.AccountId == accountId).ToListAsync());

        // And the render path still answers — with "none", which is what tells the client to keep
        // the templater sentence and the overview it already had.
        var resolved = await deps.Lens.ResolveAsync(accountId, titleIds[0], built.ProfileHash, default);
        Assert.Equal(LensSource.None, resolved.Source);
        Assert.Null(resolved.Note);
    }

    [Fact]
    public async Task The_budget_counts_what_is_already_on_the_ledger()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));

        db.ExternalApiUsages.Add(new ExternalApiUsage
        {
            Id = Guid.NewGuid(),
            Provider = ApiProvider.OpenRouter,
            Day = DateTime.UtcNow.Date,
            AccountId = accountId,
            Model = "openai/gpt-5.4-mini",
            Purpose = ApiUsagePurpose.Lens,
            CallCount = 40,
            TokensUsed = 55_000,
        });
        // A different purpose must NOT count against the lens ceiling — auditing a prompt version
        // cannot be allowed to eat the budget it is auditing.
        db.ExternalApiUsages.Add(new ExternalApiUsage
        {
            Id = Guid.NewGuid(),
            Provider = ApiProvider.OpenRouter,
            Day = DateTime.UtcNow.Date,
            AccountId = accountId,
            Model = "openai/gpt-5.4-mini",
            Purpose = ApiUsagePurpose.LensAudit,
            CallCount = 100,
            TokensUsed = 900_000,
        });
        await db.SaveChangesAsync();

        var allowance = await new LensBudget(db, StubConfig()).AllowanceAsync(accountId, default);

        Assert.Equal(40, allowance.CallsUsed);
        Assert.Equal(55_000, allowance.TokensUsed);
        Assert.True(allowance.HasHeadroom);
        Assert.Equal(LensBudget.DefaultDailyCalls - 40, allowance.CallsRemaining);
    }

    // ── Off the read path: queue, negative cache (WS-1 task 7, F-6) ─────────────────────────

    /// <summary>
    /// A writer whose every attempt ends one chosen way, counting the calls it would have paid for.
    /// Everything else (versions, salts, the stub's narrative) is the real stub writer's.
    /// </summary>
    private sealed class ScriptedLensWriter(ILensWriter inner, Func<LensTitleInput, LensAttempt> attempt) : ILensWriter
    {
        public int Calls { get; private set; }

        public bool IsAvailable => inner.IsAvailable;

        public string ModelId => inner.ModelId;

        public int LensPromptVersion => inner.LensPromptVersion;

        public int NarrativePromptVersion => inner.NarrativePromptVersion;

        public string LensCacheSalt => inner.LensCacheSalt;

        public string NarrativeCacheSalt => inner.NarrativeCacheSalt;

        public Task<NarrativeDraft?> WriteNarrativeAsync(TasteFacts facts, CancellationToken ct = default) =>
            inner.WriteNarrativeAsync(facts, ct);

        public async Task<IReadOnlyList<LensDraft?>> WriteLensAsync(
            IReadOnlyList<LensTitleInput> titles, LensPersona persona, CancellationToken ct = default) =>
            (await AttemptLensAsync(titles, persona, ct)).Select(a => a.Draft).ToList();

        public Task<LensAuditScore?> JudgeAsync(
            LensTitleInput title, LensPersona persona, LensNote note, CancellationToken ct = default) =>
            inner.JudgeAsync(title, persona, note, ct);

        public Task<IReadOnlyList<LensAttempt>> AttemptLensAsync(
            IReadOnlyList<LensTitleInput> titles, LensPersona persona, CancellationToken ct = default)
        {
            Calls += titles.Count;
            return Task.FromResult<IReadOnlyList<LensAttempt>>(titles.Select(attempt).ToList());
        }
    }

    private static LensService LensWith(ReelDbContext db, Deps deps, ILensWriter writer) => new(
        db,
        writer,
        new LensInputBuilder(db, new RatingCalibrationService(db, deps.Cache)),
        new LensVerifier(db),
        deps.Budget,
        deps.Personalisation,
        NullLogger<LensService>.Instance);

    [Fact]
    public async Task A_failed_generation_is_negatively_cached_for_six_hours_and_then_tried_again()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var failing = new ScriptedLensWriter(Writer(StubConfig()), _ => new LensAttempt(null));
        var lens = LensWith(db, deps, failing);
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var hash = (await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default)).ProfileHash;

        var first = await lens.GenerateForJobAsync(accountId, taste, hash, [titleId], default);
        var again = await lens.GenerateForJobAsync(accountId, taste, hash, [titleId], default);

        // One paid attempt; the second pass reads the failure and spends nothing.
        Assert.Equal(1, first.Failed);
        Assert.Equal(1, failing.Calls);
        Assert.Equal("already warm", again.Reason);
        var row = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        Assert.Equal(LensStatus.Failed, row.Status);
        // Never rendered: the ladder reads only Done rows.
        Assert.Equal(LensSource.None, (await lens.ResolveAsync(accountId, titleId, hash, default)).Source);

        // Past the window the title is tried again, in place (the unique key holds).
        row.GeneratedAt = DateTime.UtcNow - LensService.FailureBackoff - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync();
        var writing = LensWith(db, deps, Writer(StubConfig()));
        var retried = await writing.GenerateForJobAsync(accountId, taste, hash, [titleId], default);

        Assert.Equal(1, retried.Written);
        Assert.Equal(LensSource.Fresh, (await writing.ResolveAsync(accountId, titleId, hash, default)).Source);
        Assert.Single(await db.TitleLensCopies.Where(c => c.AccountId == accountId && c.TitleId == titleId).ToListAsync());
    }

    [Fact]
    public async Task A_stopped_attempt_is_not_remembered_as_the_titles_failure()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A spent allowance or an open breaker refuses the call before it is sent. That says nothing
        // about the title, so nothing may keep it dark for six hours after the allowance resets.
        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var stopped = new ScriptedLensWriter(Writer(StubConfig()), _ => new LensAttempt(null, Stopped: true));
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var hash = (await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default)).ProfileHash;

        var outcome = await LensWith(db, deps, stopped).GenerateForJobAsync(accountId, taste, hash, [titleId], default);

        Assert.Equal(0, outcome.Failed);
        Assert.Empty(await db.TitleLensCopies.Where(c => c.AccountId == accountId && c.TitleId == titleId).ToListAsync());
        Assert.Equal(
            LensService.RequestOutcome.Queued,
            await deps.Lens.RequestAsync(accountId, titleId, hash, new ResolvedLens(null, LensSource.None, hash, null), default));
    }

    [Fact]
    public async Task Opening_an_uncached_title_queues_one_pass_however_often_it_is_opened()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);
        var other = await SeedTitleAsync("Sicario", 2015);
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var hash = await deps.Facts.CurrentHashAsync(accountId, DateTime.UtcNow, default);
        var none = new ResolvedLens(null, LensSource.None, hash, null);

        for (var open = 0; open < 3; open++)
        {
            Assert.Equal(LensService.RequestOutcome.Queued, await deps.Lens.RequestAsync(accountId, titleId, hash, none, default));
        }

        // A second title merges into the same pending pass rather than racing the one-per-account index.
        Assert.Equal(LensService.RequestOutcome.Queued, await deps.Lens.RequestAsync(accountId, other, hash, none, default));
        var job = await db.SyncJobs.SingleAsync(j => j.AccountId == accountId && j.Kind == JobKind.PersonaliseTitles);
        Assert.Equal([titleId, other], PersonaliseTitlesJobHandler.RequestedTitles(job.CursorJson));
        Assert.Equal(PersonaliseTitlesJobHandler.OnOpenPriority, job.Priority);
        Assert.Equal(PersonaliseTitlesJobHandler.OnOpenReason, job.TriggerReason);

        // The pass writes the opened titles first, with the real handler and the stub writer.
        var handler = new PersonaliseTitlesJobHandler(
            db, deps.Features, deps.Facts, deps.Lens, NullLogger<PersonaliseTitlesJobHandler>.Instance);
        await handler.ExecuteAsync(job, CancellationToken.None);
        await CompleteAsync(db, job);

        Assert.Equal(LensSource.Fresh, (await deps.Lens.ResolveAsync(accountId, titleId, hash, default)).Source);
        Assert.Equal(LensSource.Fresh, (await deps.Lens.ResolveAsync(accountId, other, hash, default)).Source);
        var fresh = await deps.Lens.ResolveAsync(accountId, titleId, hash, default);
        Assert.Equal(LensService.RequestOutcome.Current, await deps.Lens.RequestAsync(accountId, titleId, hash, fresh, default));
    }

    [Fact]
    public async Task A_pending_build_pass_takes_the_opened_title_first_and_keeps_its_own_fields()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var hash = await deps.Facts.CurrentHashAsync(accountId, DateTime.UtcNow, default);
        var nightly = new SyncJob
        {
            Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.PersonaliseTitles, Priority = 3,
            EnqueuedAt = DateTime.UtcNow, CursorJson = PersonaliseTitlesJobHandler.SavedCursor,
        };
        db.SyncJobs.Add(nightly);
        await db.SaveChangesAsync();

        Assert.Equal(
            LensService.RequestOutcome.Queued,
            await deps.Lens.RequestAsync(accountId, titleId, hash, new ResolvedLens(null, LensSource.None, hash, null), default));

        Assert.Equal(PersonaliseTitlesJobHandler.OnOpenPriority, nightly.Priority);
        Assert.Contains("\"Saved\":true", nightly.CursorJson, StringComparison.Ordinal);
        Assert.Equal([titleId], PersonaliseTitlesJobHandler.RequestedTitles(nightly.CursorJson));
        Assert.Single(await db.SyncJobs.Where(j => j.AccountId == accountId && j.Kind == JobKind.PersonaliseTitles).ToListAsync());

        // A RUNNING pass already chose its titles: the open is not promised to it.
        nightly.Status = JobStatus.Running;
        await db.SaveChangesAsync();
        var later = await SeedTitleAsync("Heat", 1995);
        Assert.Equal(
            LensService.RequestOutcome.NotQueued,
            await deps.Lens.RequestAsync(accountId, later, hash, new ResolvedLens(null, LensSource.None, hash, null), default));
        await CompleteAsync(db, nightly);
    }

    [Fact]
    public async Task A_recent_failure_or_a_refusal_on_record_queues_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var failedTitle = await SeedTitleAsync("Soldado", 2018);
        var spoiledTitle = await SeedTitleAsync("Sicario", 2015);
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var hash = await deps.Facts.CurrentHashAsync(accountId, DateTime.UtcNow, default);
        await LensWith(db, deps, new ScriptedLensWriter(Writer(StubConfig()), _ => new LensAttempt(null)))
            .GenerateForJobAsync(accountId, taste, hash, [failedTitle], default);
        await deps.Lens.GenerateForJobAsync(accountId, taste, hash, [spoiledTitle], default);
        await deps.Lens.ReportSpoiledAsync(accountId, spoiledTitle, default);
        var none = new ResolvedLens(null, LensSource.None, hash, null);

        Assert.Equal(LensService.RequestOutcome.NotQueued, await deps.Lens.RequestAsync(accountId, failedTitle, hash, none, default));
        Assert.Equal(LensService.RequestOutcome.NotQueued, await deps.Lens.RequestAsync(accountId, spoiledTitle, hash, none, default));
        Assert.Empty(await db.SyncJobs.Where(j => j.AccountId == accountId && j.Kind == JobKind.PersonaliseTitles).ToListAsync());
    }

    [Fact]
    public async Task A_spent_lens_allowance_queues_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig((LensBudget.CallsSetting, "0")));
        var hash = await deps.Facts.CurrentHashAsync(accountId, DateTime.UtcNow, default);

        Assert.Equal(
            LensService.RequestOutcome.NotQueued,
            await deps.Lens.RequestAsync(accountId, titleId, hash, new ResolvedLens(null, LensSource.None, hash, null), default));
        Assert.Empty(await db.SyncJobs.Where(j => j.AccountId == accountId && j.Kind == JobKind.PersonaliseTitles).ToListAsync());
    }

    // ── The on-demand path ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_on_demand_path_generates_once_and_is_cached_after()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);

        var first = await deps.Lens.ResolveOrGenerateAsync(accountId, titleId, taste, built.ProfileHash, default);
        Assert.Equal(LensSource.Fresh, first.Source);
        Assert.NotNull(first.Note);

        var second = await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default);
        Assert.Equal(LensSource.Fresh, second.Source);
        Assert.Single(await db.TitleLensCopies.Where(c => c.AccountId == accountId && c.TitleId == titleId).ToListAsync());
    }

    [Fact]
    public async Task A_note_written_against_an_older_fingerprint_is_served_and_flagged_stale()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        await deps.Lens.ResolveOrGenerateAsync(accountId, titleId, taste, "oldfingerprint", default);

        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);
        var resolved = await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default);

        // Old prose beats no prose — and saying it is old is cheaper than quietly showing something
        // that no longer fits.
        Assert.Equal(LensSource.Stale, resolved.Source);
        Assert.NotNull(resolved.Note);
    }

    [Fact]
    public async Task A_reported_spoiler_withdraws_the_note_and_is_never_rewritten_at_that_key()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);
        await deps.Lens.ResolveOrGenerateAsync(accountId, titleId, taste, built.ProfileHash, default);

        Assert.Equal(1, await deps.Lens.ReportSpoiledAsync(accountId, titleId, default));

        var resolved = await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default);
        Assert.Equal(LensSource.None, resolved.Source);

        // The refusal is the record: a later pass must not write the same thing again at the same
        // (profile hash, prompt version).
        var outcome = await deps.Lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, [titleId], default);
        Assert.Equal(0, outcome.Written);
        Assert.Equal("already warm", outcome.Reason);

        var row = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        Assert.Equal(LensStatus.Rejected, row.Status);
        Assert.Equal(LensRejectionReasons.UserReportedSpoiler, row.RejectionReason);
    }

    // ── Model and effort are part of the key (WS-1 task 1) ─────────────────────────────────

    [Fact]
    public async Task A_note_from_another_model_or_effort_is_stale_and_is_rewritten_in_place()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);
        await deps.Lens.ResolveOrGenerateAsync(accountId, titleId, taste, built.ProfileHash, default);

        // The same (hash, prompt) written by a different model-and-effort — what every pre-WS-1
        // row looks like (model id only, no effort).
        var row = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        var currentSalt = deps.Lens.CacheSalt;
        row.ModelId = "openai/gpt-6-luna";
        await db.SaveChangesAsync();

        var resolved = await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default);
        Assert.Equal(LensSource.Stale, resolved.Source);
        Assert.NotNull(resolved.Note); // old prose beats none while it is rewritten

        var outcome = await deps.Lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, [titleId], default);
        Assert.Equal(1, outcome.Written);

        var rows = await db.TitleLensCopies.Where(c => c.AccountId == accountId && c.TitleId == titleId).ToListAsync();
        var only = Assert.Single(rows); // rewritten in place: the unique key is (hash, prompt)
        Assert.Equal(currentSalt, only.ModelId);
        Assert.Equal(LensSource.Fresh, (await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default)).Source);
    }

    // ── A hedged hook is never served (round-1 review: hedges rendered on Home) ────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stored_note_with_a_hedged_hook_is_not_served_and_is_rewritten_in_place(bool olderCleanNote)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Parked", 2010);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);
        await deps.Lens.ResolveOrGenerateAsync(accountId, titleId, taste, built.ProfileHash, default);

        // A note written before the hedge rule: same key, a hook that argues against its pick.
        var row = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        var clean = JsonSerializer.Deserialize<LensNote>(row.LensJson, JsonOptions)!;
        row.LensJson = JsonSerializer.Serialize(clean with { Hook = "A quieter, heavier detour you might still take." }, JsonOptions);
        if (olderCleanNote)
        {
            // Vary the fixture: with an older clean note on the ladder, that is what is served.
            db.TitleLensCopies.Add(new TitleLensCopy
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ProfileHash = "older0000000000a",
                PromptVersion = row.PromptVersion,
                ModelId = row.ModelId,
                LensJson = JsonSerializer.Serialize(clean, JsonOptions),
                Status = LensStatus.Done,
                GeneratedAt = row.GeneratedAt.AddDays(-3),
            });
        }

        await db.SaveChangesAsync();

        var resolved = await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default);
        var many = await deps.Lens.ResolveManyAsync(accountId, [titleId], built.ProfileHash, default);
        if (olderCleanNote)
        {
            Assert.Equal(LensSource.Stale, resolved.Source);
            Assert.Equal(clean.Hook, resolved.Note!.Hook);
            Assert.Equal(clean.Hook, many[titleId].Note!.Hook);
        }
        else
        {
            Assert.Equal(LensSource.None, resolved.Source);
            Assert.False(many.ContainsKey(titleId));
        }

        // The next pass rewrites it under today's guards, once, in place.
        var outcome = await deps.Lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, [titleId], default);
        Assert.Equal(1, outcome.Written);
        var current = await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default);
        Assert.Equal(LensSource.Fresh, current.Source);
        Assert.False(SecondPersonVoice.SoundsHedged(current.Note!.Hook));
        Assert.Single(await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && c.TitleId == titleId && c.ProfileHash == built.ProfileHash)
            .ToListAsync());

        // And a second pass spends nothing: the rewritten note is servable.
        var again = await deps.Lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, [titleId], default);
        Assert.Equal(0, again.Written);
    }

    // ── A cut-off hook is never served, and costs no rewrite (IR2S-03) ──────────────────────

    [Theory]
    [InlineData("You get a heavy family drama where grief and pregnancy bring strangers into one…")]
    [InlineData("Give this a quiet evening if you want friendship and emotional weight over...")]
    public async Task A_stored_note_with_a_cut_off_hook_is_served_without_it_and_is_not_rewritten(string cutHook)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Strangers", 2021);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);
        await deps.Lens.ResolveOrGenerateAsync(accountId, titleId, taste, built.ProfileHash, default);

        // The shape 24 of 216 Done rows in the eval database carry: a hook that stops mid-phrase.
        var row = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        var clean = JsonSerializer.Deserialize<LensNote>(row.LensJson, JsonOptions)!;
        row.LensJson = JsonSerializer.Serialize(clean with { Hook = cutHook }, JsonOptions);
        await db.SaveChangesAsync();

        // Every read path — the title page, and the feed, digest and saved readers — gets the note
        // with no hook, so the hero and the digest fall back to their why-line; the lens stays.
        var resolved = await deps.Lens.ResolveAsync(accountId, titleId, built.ProfileHash, default);
        var many = await deps.Lens.ResolveManyAsync(accountId, [titleId], built.ProfileHash, default);
        Assert.Equal(LensSource.Fresh, resolved.Source);
        Assert.Equal(string.Empty, resolved.Note!.Hook);
        Assert.Equal(clean.Lens, resolved.Note.Lens);
        Assert.Equal(string.Empty, many[titleId].Note!.Hook);
        Assert.Equal(clean.Lens, many[titleId].Note!.Lens);
        Assert.Null(FrostAura.Reel.Api.Endpoints.LensEndpoints.ToResponse(resolved).Hook);
        Assert.Equal(clean.Lens, FrostAura.Reel.Api.Endpoints.LensEndpoints.ToResponse(resolved).Lens);

        // The row is evidence, not damage to repair with money: no pass rewrites it.
        var outcome = await deps.Lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, [titleId], default);
        Assert.Equal(0, outcome.Written);
        var after = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        Assert.Equal(cutHook, JsonSerializer.Deserialize<LensNote>(after.LensJson, JsonOptions)!.Hook);
    }

    [Fact]
    public async Task A_spoiler_report_stands_whatever_model_asks_again()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);

        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var built = await deps.Facts.BuildAsync(accountId, taste, DateTime.UtcNow, default);
        await deps.Lens.ResolveOrGenerateAsync(accountId, titleId, taste, built.ProfileHash, default);
        await deps.Lens.ReportSpoiledAsync(accountId, titleId, default);
        var row = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        row.ModelId = "openai/some-older-model";
        await db.SaveChangesAsync();

        var outcome = await deps.Lens.GenerateForJobAsync(accountId, taste, built.ProfileHash, [titleId], default);

        Assert.Equal(0, outcome.Written);
        var after = await db.TitleLensCopies.SingleAsync(c => c.AccountId == accountId && c.TitleId == titleId);
        Assert.Equal(LensRejectionReasons.UserReportedSpoiler, after.RejectionReason);
    }

    [Fact]
    public async Task A_narrative_from_another_model_or_effort_is_rewritten_once_and_keeps_the_edit()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        var deps = new Deps(db, StubConfig());
        await RunNarrativeAsync(db, deps, accountId);
        await deps.Narratives.SaveEditAsync(accountId, "My own words.", DateTime.UtcNow, default);

        var current = await db.AccountTasteNarratives.Where(n => n.AccountId == accountId).OrderByDescending(n => n.Version).FirstAsync();
        current.ModelId = "openai/gpt-6-luna";
        await db.SaveChangesAsync();

        var taste = await deps.Features.BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        var rewritten = await deps.Narratives.WriteAsync(accountId, taste, DateTime.UtcNow, force: false, default);
        Assert.Equal(NarrativeOutcome.Written, rewritten.Outcome);
        Assert.Equal("My own words.", rewritten.Narrative!.EditedText);

        var again = await deps.Narratives.WriteAsync(accountId, taste, DateTime.UtcNow, force: false, default);
        Assert.Equal(NarrativeOutcome.UpToDate, again.Outcome);
    }

    // ── Tenancy ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_accounts_lens_and_narrative_are_invisible_to_another()
    {
        if (!fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Soldado", 2018);

        await using (var db = fixture.CreateContext(Scoped(mine)))
        {
            var deps = new Deps(db, StubConfig());
            var taste = await deps.Features.BuildTasteStateAsync(mine, DateTime.UtcNow, default);
            var built = await deps.Facts.BuildAsync(mine, taste, DateTime.UtcNow, default);
            await deps.Lens.ResolveOrGenerateAsync(mine, titleId, taste, built.ProfileHash, default);
            await RunNarrativeAsync(db, deps, mine);
        }

        // Unlike TitleAttributes, lens copy is deliberately NOT shared across tenants — being
        // per-account is the entire point of it — so the global query filter has to hold.
        await using (var db = fixture.CreateContext(Scoped(theirs)))
        {
            Assert.Empty(await db.TitleLensCopies.ToListAsync());
            Assert.Empty(await db.AccountTasteNarratives.ToListAsync());

            var deps = new Deps(db, StubConfig());
            var taste = await deps.Features.BuildTasteStateAsync(theirs, DateTime.UtcNow, default);
            var built = await deps.Facts.BuildAsync(theirs, taste, DateTime.UtcNow, default);

            var resolved = await deps.Lens.ResolveAsync(theirs, titleId, built.ProfileHash, default);
            Assert.Equal(LensSource.None, resolved.Source);

            // And the personalisation context must not borrow the other account's narrative.
            var context = await deps.Personalisation.ContextForAsync(theirs, taste, default);
            Assert.Contains("favours", context, StringComparison.Ordinal);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static async Task RunNarrativeAsync(ReelDbContext db, Deps deps, Guid accountId)
    {
        var handler = new BuildTasteNarrativeJobHandler(
            db, deps.Features, deps.Narratives, NullLogger<BuildTasteNarrativeJobHandler>.Instance);
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.BuildTasteNarrative,
            EnqueuedAt = DateTime.UtcNow,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        await handler.ExecuteAsync(job, default);
        await CompleteAsync(db, job);
    }

    private static async Task RunLensJobAsync(ReelDbContext db, Deps deps, Guid accountId)
    {
        var handler = new PersonaliseTitlesJobHandler(
            db, deps.Features, deps.Facts, deps.Lens, NullLogger<PersonaliseTitlesJobHandler>.Instance);
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.PersonaliseTitles,
            EnqueuedAt = DateTime.UtcNow,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        await handler.ExecuteAsync(job, default);
        await CompleteAsync(db, job);
    }

    /// <summary>
    /// Retires the job the way the runner does. Without this a second call in the same test hits
    /// the partial unique index on (AccountId, Kind) WHERE Status IN ('Pending','Running') — which
    /// is the index working exactly as designed, and a test harness leaving jobs in flight is the
    /// harness lying about what production looks like.
    /// </summary>
    private static async Task CompleteAsync(ReelDbContext db, SyncJob job)
    {
        job.Status = JobStatus.Succeeded;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>An account with enough rated history for a fact sheet worth writing from.</summary>
    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var accountId = Guid.NewGuid();
        db.Accounts.Add(new Account
        {
            Id = accountId,
            TraktUserSlug = $"lens-{accountId:N}",
            TraktUsername = "lens",
            DisplayName = "Lens",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });

        for (var i = 0; i < 12; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"rated-{accountId:N}-{i}",
                TmdbId = Random.Shared.Next(1, int.MaxValue),
                Name = $"Rated {i}",
                Year = 2010 + (i % 10),
                Genres = i % 2 == 0 ? ["crime", "thriller"] : ["drama"],
                CreatedAt = DateTime.UtcNow,
            };
            db.Titles.Add(title);
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(i % 2 == 0 ? 9 : 6),
                RatedAt = DateTime.UtcNow.AddDays(-i),
            });
        }

        await db.SaveChangesAsync();
        return accountId;
    }

    private async Task<Guid> SeedTitleAsync(string name, int year)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktSlug = $"lens-title-{Guid.NewGuid():N}",
            TmdbId = Random.Shared.Next(1, int.MaxValue),
            Name = name,
            Year = year,
            Genres = ["crime", "thriller"],
            Overview = "A task force escalates the war on the cartels.",
            Keywords = ["cartel", "border"],
            RuntimeMinutes = 122,
            CreatedAt = DateTime.UtcNow,
        };
        db.Titles.Add(title);
        db.TitleAttributes.Add(new TitleAttributes
        {
            TitleId = title.Id,
            Darkness = 0.81m,
            Pacing = 0.62m,
            Complexity = 0.55m,
            EmotionalIntensity = 0.70m,
            Humor = 0.05m,
            Optimism = 0.18m,
            EnsembleVsSolo = 0.40m,
            Tone = "bleak",
            Era = "contemporary",
            Themes = ["cartel violence"],
            ExtractorModel = "stub/deterministic-v1",
            ExtractorVersion = 2,
            Status = AttributeExtractionStatus.Done,
            ExtractedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>An Active snapshot with a hero and the requested rails, so selection has shape to read.</summary>
    private async Task SeedSnapshotAsync(Guid accountId, int heroCount, int rails, int perRail)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            GeneratedAt = DateTime.UtcNow,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);

        async Task AddAsync(FeedRowKind row, Guid? anchor, int count)
        {
            for (var rank = 0; rank < count; rank++)
            {
                var titleId = await SeedTitleAsync($"{row}-{anchor}-{rank}", 2020);
                db.FeedItems.Add(new FeedItem
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    FeedSnapshotId = snapshot.Id,
                    Row = row,
                    AnchorTitleId = anchor,
                    Rank = rank,
                    TitleId = titleId,
                    PredictedRating = 8m,
                    FinalScore = 8m,
                    WhyThisSentence = "You rate this kind of thing highly.",
                });
            }
        }

        await AddAsync(FeedRowKind.Hero, null, heroCount);
        for (var rail = 0; rail < rails; rail++)
        {
            await AddAsync(FeedRowKind.BecauseYouLoved, await SeedTitleAsync($"anchor-{rail}", 2015), perRail);
        }

        await db.SaveChangesAsync();
    }
}
