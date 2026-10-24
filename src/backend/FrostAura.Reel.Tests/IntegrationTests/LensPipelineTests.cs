using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
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

        Assert.Equal("lens paused (payment_required)", lensJob.ProgressMessage);
        Assert.Equal("taste profile paused (payment_required)", narrativeJob.ProgressMessage);
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
