using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// EnrichCatalog on real Postgres. Two contracts: with providers available it writes a
/// TitleEmbedding and a Done TitleAttributes row for every hydrated library title and chains
/// Train; with no provider configured it is a clean no-op that still chains Train, so the
/// pipeline never stalls for want of LLM keys.
/// </summary>
[Collection("postgres")]
public class EnrichCatalogJobHandlerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Populates_embeddings_and_attributes_then_chains_train()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(3);

        await RunEnrichAsync(accountId, embeddings: new StubEmbeddingProvider(true), extractor: StubExtractor(stub: true));

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(3, await db.TitleEmbeddings.CountAsync(e => titleIds.Contains(e.TitleId)));
        var attrs = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        Assert.Equal(3, attrs.Count);
        Assert.All(attrs, a => Assert.Equal(AttributeExtractionStatus.Done, a.Status));
        Assert.All(attrs, a => Assert.Equal("stub/deterministic-v1", a.ExtractorModel));
        Assert.True(await db.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.Train));
    }

    [Fact]
    public async Task No_provider_is_a_clean_noop_that_still_chains_train()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);

        await RunEnrichAsync(accountId, embeddings: new StubEmbeddingProvider(false), extractor: StubExtractor(stub: false));

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(0, await db.TitleEmbeddings.CountAsync(e => titleIds.Contains(e.TitleId)));
        Assert.Equal(0, await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId)));
        Assert.True(await db.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.Train));
    }

    [Fact]
    public async Task Second_run_is_idempotent_no_duplicate_embeddings()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(true), StubExtractor(true));
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(true), StubExtractor(true));

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(2, await db.TitleEmbeddings.CountAsync(e => titleIds.Contains(e.TitleId)));
        Assert.Equal(2, await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId)));
    }

    [Fact]
    public async Task Switching_extractor_model_re_extracts_stub_rows()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), StubExtractor(stub: true));

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            Assert.All(
                await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync(),
                a => Assert.Equal("stub/deterministic-v1", a.ExtractorModel));
        }

        // A different model id arrives (simulating real OpenRouter): stub rows must re-extract,
        // not stay frozen. The fake reports a distinct ModelId and writes it onto each row.
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), new RenamingExtractor("openai/gpt-5.4-mini"));

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var rows = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
            Assert.Equal(2, rows.Count); // upserted in place, not duplicated
            Assert.All(rows, a => Assert.Equal("openai/gpt-5.4-mini", a.ExtractorModel));
        }
    }

    /// <summary>
    /// De-quarantined 2026-09-09. It failed in the full local run for a real reason, not a flaky
    /// one: the sweep is global by design, so <c>TitlesSeen</c> as a TOTAL counted every other
    /// class's seeded titles (expected 4, observed 212). Two changes make it deterministic without
    /// weakening it — the handler now takes titles in account-priority order under a per-run cap, so
    /// a cap of 2 over an account with exactly 2 library titles selects exactly those two; and the
    /// assertion counts calls PER TITLE, which is the contract that was always meant and is exact
    /// whatever else shares the database. CI still runs it against the authoritative pgvector
    /// service, as it always did.
    /// </summary>
    [Fact]
    public async Task An_unextractable_title_is_never_marked_Done_and_stops_billing_after_the_attempt_budget()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        var extractor = new AlwaysFailingExtractor("openai/gpt-5.4-mini");

        // Three runs, but only two may ever reach the model: the eligibility query counts attempts
        // made by the CURRENT ExtractorModel, so the failure path has to stamp the model or the
        // retry cap can never engage and the title bills for ever.
        for (var run = 0; run < 3; run++)
        {
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: titleIds.Count);
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var rows = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        Assert.Equal(2, rows.Count);

        // No fabricated judgement is ever recorded for a title the model did not answer for.
        Assert.All(rows, a => Assert.NotEqual(AttributeExtractionStatus.Done, a.Status));
        Assert.All(rows, a => Assert.Null(a.ExtractedAt));
        Assert.All(rows, a => Assert.Equal(0m, a.Darkness));
        Assert.All(rows, a => Assert.Equal("openai/gpt-5.4-mini", a.ExtractorModel));
        Assert.All(rows, a => Assert.Equal(2, a.AttemptCount));

        // 2 attempts each, not 3 — and asserted per title, so the global sweep's other work cannot
        // move the number in either direction.
        Assert.All(titleIds, id => Assert.Equal(2, extractor.TitlesSeen.Count(seen => seen == id)));
    }

    /// <summary>
    /// The per-run cap is the whole reason enrichment can now be scheduled from three places. It is
    /// asserted here on the account's OWN titles: with a cap of 1 and two eligible library titles,
    /// exactly one is settled per run and the other waits for the next one.
    /// </summary>
    [Fact]
    public async Task The_per_run_cap_bounds_how_many_titles_one_run_touches()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        var extractor = new RenamingExtractor("cap/test-model");

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(1, await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId)));
        }

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var rows = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, a => Assert.Equal(AttributeExtractionStatus.Done, a.Status));
        }

        // One title per run, each seen exactly once — the cap bounds work, it does not re-do it.
        Assert.All(titleIds, id => Assert.Equal(1, extractor.TitlesSeen.Count(seen => seen == id)));
    }

    /// <summary>
    /// The account's own titles come first. Seeded here as a title this account has a FeedItem for
    /// (tier 0) alongside two library titles (tier 2) and a bare catalogue title it has never met
    /// (tier 3): with a cap of 1, the run must pick the feed title, whatever the ids sort to.
    /// <para>
    /// Without this ordering a capped run over a 5,000-title catalogue enriches, on average, titles
    /// that have nothing to do with the account that asked — which would make the cap a way of
    /// never getting to the features behind your own next feed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_sweep_takes_the_accounts_own_titles_before_the_rest_of_the_catalogue()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, libraryIds) = await SeedLibraryAsync(2);
        var servedId = await SeedServedTitleAsync(accountId);
        var extractor = new RenamingExtractor("priority/test-model");

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);

        Assert.Equal([servedId], extractor.TitlesSeen);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(0, await db.TitleAttributes.CountAsync(a => libraryIds.Contains(a.TitleId)));
    }

    /// <summary>
    /// ExtractorVersion invalidation, the attribute-side analogue of the feature-schema sweep: a Done
    /// row written by an older prompt generation is re-extracted and re-stamped, and a fresh attempt
    /// budget comes with it. Without this a prompt improvement could never reach the 2,312 rows
    /// written before the Tone/Era vocabularies closed — they look settled and read as "no tone".
    /// </summary>
    [Fact]
    public async Task A_row_written_by_an_older_prompt_version_is_re_extracted_and_restamped()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var v1 = new RenamingExtractor("version/test-model", version: 1);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), v1, cap: 1);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.SingleAsync(a => titleIds.Contains(a.TitleId));
            Assert.Equal(AttributeExtractionStatus.Done, row.Status);
            Assert.Equal(1, row.ExtractorVersion);
        }

        // Same model, same Done row — only the prompt generation moved. A version-blind sweep would
        // treat it as settled for ever.
        var v2 = new RenamingExtractor("version/test-model", version: 2);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), v2, cap: 1);

        Assert.Equal(titleIds, v2.TitlesSeen);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.SingleAsync(a => titleIds.Contains(a.TitleId));
            Assert.Equal(2, row.ExtractorVersion);
            Assert.Equal(1, row.AttemptCount); // budget reset, not inherited
            Assert.Equal(AttributeExtractionStatus.Done, row.Status);
        }
    }

    /// <summary>
    /// The other half of version-scoping, and the half the budget test could not reach: a row that
    /// EXHAUSTED its attempts under one prompt version must become eligible again when the version
    /// moves, with a fresh budget rather than an inherited one.
    ///
    /// <para>
    /// This is the case the ordering of the two eligibility effects was written for — "an exhausted
    /// row with a stale version is eligible again, which is deliberate: the reason it failed may be
    /// the reason the prompt changed". Without it, a prompt written specifically to fix the titles
    /// the old one choked on could never reach them: they are the exact rows sitting at the cap.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_version_bump_reopens_the_attempt_budget_a_failed_row_had_exhausted()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var v1 = new AlwaysFailingExtractor("budget/test-model", version: 1);

        // Burn the budget: three runs, two of which may reach the model.
        for (var run = 0; run < 3; run++)
        {
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), v1, cap: 1);
        }

        Assert.Equal(2, v1.TitlesSeen.Count(seen => seen == titleIds[0]));
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.SingleAsync(a => titleIds.Contains(a.TitleId));
            Assert.Equal(2, row.AttemptCount);
            Assert.Equal(1, row.ExtractorVersion);
            Assert.NotEqual(AttributeExtractionStatus.Done, row.Status);
        }

        // Same model, new prompt generation. The row is at the cap and NOT Done, so a version-blind
        // sweep leaves it there for ever.
        var v2 = new RenamingExtractor("budget/test-model", version: 2);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), v2, cap: 1);

        Assert.Equal(titleIds, v2.TitlesSeen);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.SingleAsync(a => titleIds.Contains(a.TitleId));
            Assert.Equal(AttributeExtractionStatus.Done, row.Status);
            Assert.Equal(2, row.ExtractorVersion);
            Assert.Equal(1, row.AttemptCount); // reset, not 3
        }
    }

    /// <summary>
    /// A model that keeps failing at a NEWER version must still stop at the cap. The reset is what
    /// makes a prompt change reach an exhausted row; it must not become a way to bill for ever by
    /// bumping a number.
    /// </summary>
    [Fact]
    public async Task A_version_bump_grants_one_fresh_budget_not_an_unlimited_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var v1 = new AlwaysFailingExtractor("budget/still-failing", version: 1);
        for (var run = 0; run < 3; run++)
        {
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), v1, cap: 1);
        }

        var v2 = new AlwaysFailingExtractor("budget/still-failing", version: 2);
        for (var run = 0; run < 3; run++)
        {
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), v2, cap: 1);
        }

        // Two attempts under each version, never three — asserted per title, because the sweep is
        // global and other classes' titles share this database.
        Assert.Equal(2, v1.TitlesSeen.Count(seen => seen == titleIds[0]));
        Assert.Equal(2, v2.TitlesSeen.Count(seen => seen == titleIds[0]));

        await using var db = fixture.CreateContext(new AccountContext());
        var row = await db.TitleAttributes.SingleAsync(a => titleIds.Contains(a.TitleId));
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(2, row.ExtractorVersion);
        Assert.NotEqual(AttributeExtractionStatus.Done, row.Status);
    }

    /// <summary>
    /// A GLOBAL run — the shape the weekly catalogue-growth sweep queues. It has no account, so it
    /// must neither demand one nor chain a Train for one, and it still enriches. This used to throw
    /// on the first line of the handler.
    /// </summary>
    [Fact]
    public async Task A_global_run_enriches_without_an_account_and_chains_no_train()
    {
        if (!fixture.Available)
        {
            return;
        }

        var titleId = await SeedOrphanTitleAsync();
        var extractor = new RenamingExtractor("global/test-model");

        await RunEnrichAsync(accountId: null, new StubEmbeddingProvider(true), extractor, cap: 400);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Contains(titleId, extractor.TitlesSeen);
        Assert.True(await db.TitleEmbeddings.AnyAsync(e => e.TitleId == titleId));
        Assert.Equal(AttributeExtractionStatus.Done,
            (await db.TitleAttributes.SingleAsync(a => a.TitleId == titleId)).Status);
        Assert.False(await db.SyncJobs.AnyAsync(j => j.AccountId == null && j.Kind == JobKind.Train));
    }

    /// <summary>
    /// Runs one enrich pass exactly as JobRunnerService would.
    /// <para>
    /// <paramref name="cap"/> sets <c>ENRICH_MAX_TITLES_PER_RUN</c>. Tests that assert on WHICH
    /// titles a run touched must set it: the catalogue is deliberately global and shared with every
    /// other class in this collection, so a run at the production cap sweeps whatever they seeded
    /// too. A small cap plus the handler's account-first ordering is what makes a run over a shared
    /// database deterministic for the caller that seeded it.
    /// </para>
    /// <para>
    /// A null <paramref name="accountId"/> is the GLOBAL sweep the weekly catalogue growth pass
    /// queues — no account pinned, so no priority tiers, no SSE and no Train chain.
    /// </para>
    /// </summary>
    private async Task RunEnrichAsync(
        Guid? accountId, IEmbeddingProvider embeddings, ITitleAttributeExtractor extractor, int? cap = null)
    {
        var scoped = new AccountContext();
        if (accountId is { } pinned)
        {
            scoped.SetAccount(pinned);
        }

        await using var db = fixture.CreateContext(scoped);
        var handler = new EnrichCatalogJobHandler(
            db, embeddings, extractor, new NoopEventHub(), Config(cap), NullLogger<EnrichCatalogJobHandler>.Instance);
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = JobKind.EnrichCatalog,
            Status = JobStatus.Running,
            EnqueuedAt = DateTime.UtcNow,
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        await handler.ExecuteAsync(job, CancellationToken.None);

        // The runner owns status transitions; mirror it so the in-flight unique index
        // (AccountId, Kind) releases and a second enrich run can be enqueued.
        job.Status = JobStatus.Succeeded;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static IConfiguration Config(int? cap) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EnrichCatalogJobHandler.MaxTitlesPerRunKey] =
                    cap?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

    private async Task<(Guid AccountId, List<Guid> TitleIds)> SeedLibraryAsync(int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"enrich-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                TraktSlug = $"title-{i}-{Guid.NewGuid():N}",
                Name = $"Library Title {i}",
                Overview = "A film the account has seen.",
                Genres = ["drama"],
                LastMetadataRefreshAt = DateTime.UtcNow, // hydrated → eligible for enrichment
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            // A rating makes it a library (label) title the enricher must cover.
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(7 + i % 3),
                RatedAt = DateTime.UtcNow,
                SyncedAt = DateTime.UtcNow,
            });
            ids.Add(title.Id);
        }

        await db.SaveChangesAsync();
        return (account.Id, ids);
    }

    /// <summary>
    /// A hydrated title this account has actually been SHOWN — tier 0 of the sweep's priority order.
    /// No snapshot row is needed: FeedItem carries plain ids with no foreign keys, and this test
    /// cares about the tier, not about a renderable feed.
    /// </summary>
    private async Task<Guid> SeedServedTitleAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = NewHydratedTitle("Served Title");
        db.Add(title);
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FeedSnapshotId = Guid.NewGuid(),
            Row = FeedRowKind.Hero,
            Rank = 0,
            TitleId = title.Id,
            PredictedRating = 8m,
            FinalScore = 8m,
            WhyThisSentence = "seeded",
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    /// <summary>
    /// A hydrated title belonging to no account, with an id that sorts FIRST in Postgres's uuid
    /// order. The global sweep has no priority tiers and falls back to id order, so this is how a
    /// test over a shared catalogue can name the row a capped global run will pick — without a
    /// large cap that would sweep every other class's titles as a side effect.
    /// </summary>
    private async Task<Guid> SeedOrphanTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = NewHydratedTitle("Orphan Title");
        title.Id = new Guid($"00000000-0000-0000-0000-{Guid.NewGuid():N}"[..36]);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private static Title NewHydratedTitle(string name) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TraktId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"title-{Guid.NewGuid():N}",
        Name = $"{name} {Guid.NewGuid():N}",
        Overview = "A film in the shared catalogue.",
        Genres = ["drama"],
        LastMetadataRefreshAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private static OpenRouterAttributeExtractor StubExtractor(bool stub)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TestHelpers:ModelStubMode"] = stub ? "true" : "false",
            })
            .Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        return new OpenRouterAttributeExtractor(
            new HttpClient(), recorder, config, NullLogger<OpenRouterAttributeExtractor>.Instance);
    }

    /// <summary>Deterministic 1536-dim unit vectors — exercises the write path without a network.</summary>
    private sealed class StubEmbeddingProvider(bool available) : IEmbeddingProvider
    {
        public bool IsAvailable => available;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var vectors = texts.Select(t =>
            {
                var v = new float[1536];
                var seed = t.Length;
                for (var i = 0; i < v.Length; i++)
                {
                    v[i] = (float)Math.Sin(seed + i);
                }

                var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
                for (var i = 0; i < v.Length; i++)
                {
                    v[i] /= norm;
                }

                return v;
            }).ToArray();
            return Task.FromResult(vectors);
        }
    }

    /// <summary>An available extractor that reports a given model id and stamps it on every row.</summary>
    private sealed class RenamingExtractor(string modelId, int version = 1) : ITitleAttributeExtractor
    {
        public bool IsAvailable => true;

        public string ModelId => modelId;

        public int Version => version;

        public List<Guid> TitlesSeen { get; } = [];

        public Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
        {
            TitlesSeen.AddRange(inputs.Select(i => i.TitleId));
            IReadOnlyList<ExtractedTitleAttributes?> result = inputs
                .Select(_ => (ExtractedTitleAttributes?)new ExtractedTitleAttributes(
                    0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, "neutral", "contemporary", ["t"], "{}"))
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// An available extractor that can never answer — the shape a title takes when the model
    /// keeps omitting or mistyping a required scalar. The adapter returns null rather than
    /// substituting neutral values, and the handler must honour that.
    /// </summary>
    private sealed class AlwaysFailingExtractor(string modelId, int version = 1) : ITitleAttributeExtractor
    {
        public bool IsAvailable => true;

        public string ModelId => modelId;

        public int Version => version;

        /// <summary>
        /// Every title id this extractor was asked about, in order and WITH repeats. A total call
        /// count cannot be asserted on in a shared catalogue — the sweep is global by design, so the
        /// number includes whatever other test classes seeded. The per-title tally is the contract
        /// that actually matters and it is exact regardless of what else is in the database.
        /// </summary>
        public List<Guid> TitlesSeen { get; } = [];

        public Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
        {
            TitlesSeen.AddRange(inputs.Select(i => i.TitleId));
            IReadOnlyList<ExtractedTitleAttributes?> result = inputs.Select(_ => (ExtractedTitleAttributes?)null).ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
