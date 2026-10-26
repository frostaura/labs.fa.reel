using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
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
/// <remarks>
/// Inherits <see cref="EmbeddedCatalogTestBase"/> even though it never writes a
/// <c>TitleEmbedding</c> itself. The sweep under test embeds every hydrated title in the SHARED
/// catalogue — the ceiling is system-wide, not per-account — so it picks up whatever titles earlier
/// classes left behind and embeds those too: a thousand vectors into the shared HNSW index on a
/// measured run, none of them this class's, none of them ever removed. Leftover <c>Title</c> rows
/// are tolerated by design; the vectors they acquire here are not.
/// </remarks>
[Collection("postgres")]
public class EnrichCatalogJobHandlerTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task Populates_embeddings_and_attributes_then_chains_train()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(3);

        await RunEnrichAsync(accountId, embeddings: new StubEmbeddingProvider(true), extractor: StubExtractor(stub: true));

        await using var db = Fixture.CreateContext(new AccountContext());
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
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);

        await RunEnrichAsync(accountId, embeddings: new StubEmbeddingProvider(false), extractor: StubExtractor(stub: false));

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.Equal(0, await db.TitleEmbeddings.CountAsync(e => titleIds.Contains(e.TitleId)));
        Assert.Equal(0, await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId)));
        Assert.True(await db.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.Train));
    }

    [Fact]
    public async Task Second_run_is_idempotent_no_duplicate_embeddings()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(true), StubExtractor(true));
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(true), StubExtractor(true));

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.Equal(2, await db.TitleEmbeddings.CountAsync(e => titleIds.Contains(e.TitleId)));
        Assert.Equal(2, await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId)));
    }

    [Fact]
    public async Task Switching_extractor_model_re_extracts_stub_rows()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), StubExtractor(stub: true));

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            Assert.All(
                await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync(),
                a => Assert.Equal("stub/deterministic-v1", a.ExtractorModel));
        }

        // A different model id arrives (simulating real OpenRouter): stub rows must re-extract,
        // not stay frozen. The fake reports a distinct ModelId and writes it onto each row.
        //
        // Re-extraction waits behind every title that has NO answer at all (NEW-3), and this
        // catalogue is shared — so the cap is sized to clear whatever unanswered backlog the other
        // classes left behind, and the order is asserted rather than assumed: every unanswered title
        // the run touched came before these two answered ones.
        int backlog;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            backlog = await db.Titles.CountAsync(t => t.LastMetadataRefreshAt != null
                && !db.TitleAttributes.Any(a => a.TitleId == t.Id
                    && a.Status == AttributeExtractionStatus.Done && a.ExtractorVersion >= 1));
        }

        var renaming = new RenamingExtractor("openai/gpt-5.4-mini");
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), renaming, cap: backlog + titleIds.Count);

        var firstReExtraction = titleIds.Min(id => renaming.TitlesSeen.IndexOf(id));
        Assert.True(firstReExtraction >= 0, "the stub rows were never re-extracted");
        Assert.All(renaming.TitlesSeen.Take(firstReExtraction), id => Assert.DoesNotContain(id, titleIds));

        await using (var db = Fixture.CreateContext(new AccountContext()))
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
        if (!Fixture.Available)
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

        await using var db = Fixture.CreateContext(new AccountContext());
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
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        var extractor = new RenamingExtractor("cap/test-model");

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(1, await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId)));
        }

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);

        await using (var db = Fixture.CreateContext(new AccountContext()))
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
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, libraryIds) = await SeedLibraryAsync(2);
        var servedId = await SeedServedTitleAsync(accountId);
        var extractor = new RenamingExtractor("priority/test-model");

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);

        Assert.Equal([servedId], extractor.TitlesSeen);

        await using var db = Fixture.CreateContext(new AccountContext());
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
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var v1 = new RenamingExtractor("version/test-model", version: 1);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), v1, cap: 1);

        await using (var db = Fixture.CreateContext(new AccountContext()))
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

        await using (var db = Fixture.CreateContext(new AccountContext()))
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
        if (!Fixture.Available)
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
        await using (var db = Fixture.CreateContext(new AccountContext()))
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
        await using (var db = Fixture.CreateContext(new AccountContext()))
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
        if (!Fixture.Available)
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

        await using var db = Fixture.CreateContext(new AccountContext());
        var row = await db.TitleAttributes.SingleAsync(a => titleIds.Contains(a.TitleId));
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(2, row.ExtractorVersion);
        Assert.NotEqual(AttributeExtractionStatus.Done, row.Status);
    }

    /// <summary>
    /// E-6: 1,110 of 1,116 extractor warnings in 72 hours were dropped connections, and each one
    /// spent half of a title's two-attempt budget. A transport failure now backs off by TIME and
    /// leaves the budget alone: the row is not Failed, its attempts are untouched, the title is not
    /// re-asked inside its backoff, the wait doubles on a repeat, and the next answer clears it all.
    /// The run log counts transport and parse failures separately.
    /// </summary>
    [Fact]
    public async Task Transport_failures_back_off_by_time_and_never_spend_the_attempt_budget()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var titleId = titleIds[0];
        var dropping = new ClassifyingExtractor("transport/test-model", _ => AttributeFailureClass.Transport);
        var log = new CapturingLogger();

        var before = DateTime.UtcNow;
        for (var run = 0; run < 3; run++)
        {
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), dropping, cap: 1, logger: log);
        }

        // Asked once: the 30-minute gate keeps the next two runs off it.
        Assert.Equal(1, dropping.TitlesSeen.Count(seen => seen == titleId));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleId);
            Assert.Equal(AttributeExtractionStatus.Pending, row.Status);
            Assert.Equal(0, row.AttemptCount);
            Assert.Equal(1, row.TransportFailures);
            Assert.NotNull(row.LastAttemptAt);
            Assert.InRange(row.RetryAfter!.Value, before.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));
            Assert.Null(row.ClaimedBy); // released after the batch
        }

        Assert.Contains(log.Lines, l => l.Contains("1 transport failure(s)", StringComparison.Ordinal)
            && l.Contains("0 parse failure(s)", StringComparison.Ordinal));

        // The gate lapses; a second drop doubles the wait instead of charging an attempt.
        await ExpireRetryGateAsync(titleId);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), dropping, cap: 1);
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleId);
            Assert.Equal(0, row.AttemptCount);
            Assert.Equal(2, row.TransportFailures);
            Assert.True(row.RetryAfter > DateTime.UtcNow.AddMinutes(59));
        }

        // And the answer, when it comes, clears the backoff completely.
        await ExpireRetryGateAsync(titleId);
        var answering = new RenamingExtractor("transport/test-model");
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), answering, cap: 1);
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleId);
            Assert.Equal(AttributeExtractionStatus.Done, row.Status);
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal(0, row.TransportFailures);
            Assert.Null(row.RetryAfter);
        }
    }

    /// <summary>The same three runs with a classified PARSE failure do spend the budget, and stop at it.</summary>
    [Fact]
    public async Task Parse_failures_spend_the_attempt_budget_and_stop_at_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var malformed = new ClassifyingExtractor("parse/test-model", _ => AttributeFailureClass.Parse);

        for (var run = 0; run < 3; run++)
        {
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), malformed, cap: 1);
        }

        Assert.Equal(2, malformed.TitlesSeen.Count(seen => seen == titleIds[0]));
        await using var db = Fixture.CreateContext(new AccountContext());
        var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleIds[0]);
        Assert.Equal(AttributeExtractionStatus.Failed, row.Status);
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(0, row.TransportFailures);
        Assert.Null(row.RawJson);
    }

    /// <summary>
    /// NEW-1/NEW-2, the exact race: a run's slow model call fails while ANOTHER run answers the same
    /// title. The old handler wrote Failed through the entity it loaded after the call, leaving a
    /// complete answer under a Failed status that every reader ignores — 1,077 rows on the founder's
    /// catalogue. The failure write is now conditional on the row holding no answer.
    /// </summary>
    [Theory]
    [InlineData(AttributeFailureClass.Parse)]
    [InlineData(AttributeFailureClass.Transport)]
    [InlineData(AttributeFailureClass.Unclassified)]
    public async Task A_failure_landing_after_another_run_answered_never_demotes_the_answer(AttributeFailureClass failure)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var titleId = titleIds[0];
        var racing = new ClassifyingExtractor("race/test-model", _ => failure)
        {
            // Inside the model call — after this run claimed the title, before it writes its
            // outcome — a run that ignored the claim (its lease had lapsed) answers the title.
            DuringCall = async () =>
            {
                await using var other = Fixture.CreateContext(new AccountContext());
                await other.TitleAttributes
                    .Where(a => a.TitleId == titleId)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(a => a.Status, AttributeExtractionStatus.Done)
                        .SetProperty(a => a.RawJson, "{\"answeredBy\":\"the other run\"}")
                        .SetProperty(a => a.Darkness, 0.9m)
                        .SetProperty(a => a.ExtractedAt, DateTime.UtcNow));
            },
        };

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), racing, cap: 1);

        await using var db = Fixture.CreateContext(new AccountContext());
        var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleId);
        Assert.Equal(AttributeExtractionStatus.Done, row.Status);
        Assert.Equal(0.9m, row.Darkness);
        Assert.Contains("the other run", row.RawJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed RE-extraction of a row that already holds an answer (here: an older prompt
    /// generation's Done row) keeps the answer and is time-gated for a week instead — the attempt
    /// cannot be charged without demoting it, so time bounds the spend.
    /// </summary>
    [Fact]
    public async Task A_failed_re_extraction_keeps_the_existing_answer_and_waits_a_week()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var titleId = titleIds[0];
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), new RenamingExtractor("reextract/test-model", version: 1), cap: 1);

        var failingV2 = new ClassifyingExtractor("reextract/test-model", _ => AttributeFailureClass.Parse, version: 2);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), failingV2, cap: 1);
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), failingV2, cap: 1);

        Assert.Equal(1, failingV2.TitlesSeen.Count(seen => seen == titleId));
        await using var db = Fixture.CreateContext(new AccountContext());
        var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleId);
        Assert.Equal(AttributeExtractionStatus.Done, row.Status);
        Assert.Equal(1, row.ExtractorVersion);
        Assert.Equal(0.5m, row.Darkness);
        Assert.True(row.RetryAfter > DateTime.UtcNow.AddDays(6));
    }

    /// <summary>
    /// Review defect 1. <c>JobRunnerService</c> revives a stale Running job by putting the SAME row
    /// back to Pending, so a revived run and its still-live original share a job id. Claims keyed on
    /// the job id let each treat the other's titles as its own; claims keyed on the execution do not.
    /// </summary>
    [Fact]
    public async Task A_revived_job_and_its_live_original_never_extract_the_same_title_twice()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(4);
        var extractor = new ClassifyingExtractor("revived/test-model", _ => AttributeFailureClass.None)
        {
            Delay = TimeSpan.FromMilliseconds(300),
        };
        var jobId = Guid.NewGuid();
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.SyncJobs.Add(new SyncJob
            {
                Id = jobId,
                AccountId = accountId,
                Kind = JobKind.EnrichCatalog,
                Status = JobStatus.Running,
                EnqueuedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await Task.WhenAll(
            RunExistingJobAsync(jobId, accountId, new StubEmbeddingProvider(false), extractor, cap: 4),
            RunExistingJobAsync(jobId, accountId, new StubEmbeddingProvider(false), extractor, cap: 4));

        Assert.All(titleIds, id => Assert.Equal(1, extractor.TitlesSeen.Count(seen => seen == id)));
        await using var verify = Fixture.CreateContext(new AccountContext());
        var rows = await verify.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        Assert.All(rows, a => Assert.Equal(AttributeExtractionStatus.Done, a.Status));
        Assert.All(rows, a => Assert.Null(a.ClaimedBy));
    }

    /// <summary>
    /// Review defect 2. Embedding must not be ordered by ATTRIBUTE status: an account's own library
    /// title that is attributed but has no vector goes before a never-attributed stranger, even with
    /// the extractor switched off.
    /// </summary>
    [Fact]
    public async Task Embedding_takes_the_accounts_library_before_the_never_attributed_backlog()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var library = titleIds[0];
        Guid stranger;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.TitleAttributes.Add(new TitleAttributes
            {
                TitleId = library,
                ExtractorModel = "embed-order/model",
                ExtractorVersion = 1,
                Status = AttributeExtractionStatus.Done,
                RawJson = "{}",
                ExtractedAt = DateTime.UtcNow,
            });
            var s = NewHydratedTitle("Never attributed, far more popular");
            s.TmdbVoteCount = int.MaxValue;
            db.Add(s);
            await db.SaveChangesAsync();
            stranger = s.Id;
        }

        try
        {
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(true), new UnavailableExtractor(), cap: 1);

            await using var verify = Fixture.CreateContext(new AccountContext());
            Assert.True(await verify.TitleEmbeddings.AnyAsync(e => e.TitleId == library));
            Assert.False(await verify.TitleEmbeddings.AnyAsync(e => e.TitleId == stranger));
        }
        finally
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            await db.Titles.Where(t => t.Id == stranger)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastMetadataRefreshAt, (DateTime?)null));
        }
    }

    /// <summary>
    /// Review defect 3. Only a recognisable transport failure is booked as Transport. An extractor
    /// that throws anything else — here a bug — fails the job, where it is visible, and leaves the
    /// row exactly as it was: no backoff, no attempt charged, no claim held.
    /// </summary>
    [Fact]
    public async Task An_extractor_bug_fails_the_job_instead_of_backing_off_as_transport()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var extractor = new ThrowingExtractor("bug/test-model", () => new NullReferenceException("a defect, not the network"));

        await Assert.ThrowsAsync<NullReferenceException>(
            () => RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1));

        Assert.Contains(titleIds[0], extractor.TitlesSeen);
        await using var db = Fixture.CreateContext(new AccountContext());
        var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleIds[0]);
        Assert.Equal(AttributeExtractionStatus.Pending, row.Status);
        Assert.Equal(0, row.TransportFailures);
        Assert.Null(row.RetryAfter);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.ClaimedBy);
    }

    /// <summary>
    /// The throws that ARE transport — the adapter's typed exception, a network or I/O error, a
    /// timeout, and an HTTP client timeout (a cancellation nobody asked for) — back off by time and
    /// charge nothing.
    /// </summary>
    [Theory]
    [InlineData("typed")]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("http-timeout")]
    public async Task A_whole_batch_transport_throw_backs_off_and_charges_nothing(string kind)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var extractor = new ThrowingExtractor($"transport-{kind}/test-model", () => kind switch
        {
            "typed" => new AttributeExtractionTransportException("connection reset"),
            "http" => new HttpRequestException("connection reset"),
            "io" => new IOException("body read died after the headers"),
            "timeout" => new TimeoutException("no response"),
            _ => new TaskCanceledException("HttpClient.Timeout elapsed"),
        });

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);

        await using var db = Fixture.CreateContext(new AccountContext());
        var row = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleIds[0]);
        Assert.Equal(1, row.TransportFailures);
        Assert.Equal(0, row.AttemptCount);
        Assert.True(row.RetryAfter > DateTime.UtcNow.AddMinutes(25));
        Assert.NotEqual(AttributeExtractionStatus.Failed, row.Status);
    }

    [Fact]
    public void The_callers_own_cancellation_and_a_defect_are_not_transport()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(EnrichCatalogJobHandler.IsTransportFailure(new OperationCanceledException(), cancelled.Token));
        Assert.True(EnrichCatalogJobHandler.IsTransportFailure(new OperationCanceledException(), CancellationToken.None));
        Assert.False(EnrichCatalogJobHandler.IsTransportFailure(new InvalidOperationException(), CancellationToken.None));
        Assert.False(EnrichCatalogJobHandler.IsTransportFailure(new DbUpdateException(), CancellationToken.None));
    }

    /// <summary>
    /// Review D6. A row that holds an answer cannot be charged against its attempt budget without
    /// being demoted, so it used to be retried once a week for ever. Its failed re-extractions are
    /// now counted per question (model at prompt version): two, then the current model leaves it
    /// alone. A new question gets a fresh budget, and an answer clears the count.
    /// </summary>
    [Fact]
    public async Task A_re_extraction_that_keeps_failing_is_billed_twice_per_question_not_weekly_for_ever()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var titleId = titleIds[0];
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), new RenamingExtractor("reattempt/test-model", version: 1), cap: 1);

        async Task<int> FailRepeatedlyAsync(int version)
        {
            var failing = new ClassifyingExtractor("reattempt/test-model", _ => AttributeFailureClass.Parse, version: version);
            for (var week = 0; week < 4; week++)
            {
                await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), failing, cap: 1);
                await ExpireRetryGateAsync(titleId);
            }

            return failing.TitlesSeen.Count(seen => seen == titleId);
        }

        Assert.Equal(2, await FailRepeatedlyAsync(version: 2));
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var row = await db.TitleAttributes.AsNoTracking().SingleAsync(a => a.TitleId == titleId);
            Assert.Equal(AttributeExtractionStatus.Done, row.Status);
            Assert.Equal(1, row.ExtractorVersion);
            Assert.Equal(0.5m, row.Darkness);
            Assert.Equal(2, row.ReattemptFailures);
            Assert.Equal(EnrichCatalogJobHandler.ReattemptQuestionFor("reattempt/test-model", 2), row.ReattemptQuestion);
        }

        // A newer prompt asks a different question: a fresh budget of two, no more.
        Assert.Equal(2, await FailRepeatedlyAsync(version: 3));

        // An answer under yet another question clears the count.
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), new RenamingExtractor("reattempt/test-model", version: 4), cap: 1);
        await using var verify = Fixture.CreateContext(new AccountContext());
        var answered = await verify.TitleAttributes.SingleAsync(a => a.TitleId == titleId);
        Assert.Equal(AttributeExtractionStatus.Done, answered.Status);
        Assert.Equal(4, answered.ExtractorVersion);
        Assert.Equal(0, answered.ReattemptFailures);
        Assert.Null(answered.ReattemptQuestion);
    }

    /// <summary>
    /// A title held by another live run is skipped, not bought twice; once that run's lease lapses
    /// the title is fair game again.
    /// </summary>
    [Fact]
    public async Task A_title_claimed_by_another_live_run_is_skipped_until_the_lease_lapses()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(1);
        var titleId = titleIds[0];
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.TitleAttributes.Add(new TitleAttributes
            {
                TitleId = titleId,
                ExtractorModel = "claim/test-model",
                ExtractorVersion = 1,
                ClaimedBy = Guid.NewGuid(),
                ClaimedUntil = DateTime.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        var extractor = new RenamingExtractor("claim/test-model");
        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);
        Assert.DoesNotContain(titleId, extractor.TitlesSeen);

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            await db.TitleAttributes
                .Where(a => a.TitleId == titleId)
                .ExecuteUpdateAsync(set => set.SetProperty(a => a.ClaimedUntil, DateTime.UtcNow.AddMinutes(-1)));
        }

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);
        Assert.Equal(1, extractor.TitlesSeen.Count(seen => seen == titleId));
    }

    /// <summary>
    /// Two runs started together over the same titles — the shape of the 24 overlapping
    /// EnrichCatalog pairs found in ten days — buy each title exactly once between them.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_runs_never_extract_the_same_title_twice()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Two accounts whose libraries hold the same six titles: both runs rank them first (tier 2),
        // exactly as a global and an account run both reached the founder's titles.
        var (accountId, titleIds) = await SeedLibraryAsync(6);
        var (otherAccountId, _) = await SeedLibraryAsync(0);
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            seed.UserRatings.AddRange(titleIds.Select(id => new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = otherAccountId,
                TitleId = id,
                SubjectType = RatingSubjectType.Movie,
                Rating = 7,
                RatedAt = DateTime.UtcNow,
                SyncedAt = DateTime.UtcNow,
            }));
            await seed.SaveChangesAsync();
        }

        var extractor = new ClassifyingExtractor("concurrent/test-model", _ => AttributeFailureClass.None)
        {
            Delay = TimeSpan.FromMilliseconds(300),
        };

        await Task.WhenAll(
            RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 6),
            RunEnrichAsync(otherAccountId, new StubEmbeddingProvider(false), extractor, cap: 6));

        Assert.All(titleIds, id => Assert.Equal(1, extractor.TitlesSeen.Count(seen => seen == id)));
        await using var db = Fixture.CreateContext(new AccountContext());
        var rows = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        Assert.Equal(6, rows.Count);
        Assert.All(rows, a => Assert.Equal(AttributeExtractionStatus.Done, a.Status));
        Assert.All(rows, a => Assert.Null(a.ClaimedBy));
    }

    /// <summary>
    /// NEW-3: after the model switch the ceiling was spent re-attributing titles that already had
    /// usable answers while ~24k had none. A title with no answer goes first even against a far
    /// more popular title that only awaits re-extraction under a new model.
    /// </summary>
    [Fact]
    public async Task Never_attributed_titles_go_before_re_extraction_under_a_new_model()
    {
        if (!Fixture.Available)
        {
            return;
        }

        Guid reExtract;
        Guid neverAttributed;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var popular = NewHydratedTitle("Popular, answered by the old model");
            popular.TmdbVoteCount = 2_000_000_000;
            var unanswered = NewHydratedTitle("Less popular, never answered");
            unanswered.TmdbVoteCount = 1_900_000_000;
            db.AddRange(popular, unanswered);
            db.TitleAttributes.Add(new TitleAttributes
            {
                TitleId = popular.Id,
                ExtractorModel = "order/old-model",
                ExtractorVersion = 1,
                Status = AttributeExtractionStatus.Done,
                RawJson = "{}",
                ExtractedAt = DateTime.UtcNow.AddDays(-30),
            });
            await db.SaveChangesAsync();
            (reExtract, neverAttributed) = (popular.Id, unanswered.Id);
        }

        var extractor = new RenamingExtractor("order/new-model");
        await RunEnrichAsync(null, new StubEmbeddingProvider(false), extractor, cap: 1);

        Assert.Equal([neverAttributed], extractor.TitlesSeen);
        Assert.DoesNotContain(reExtract, extractor.TitlesSeen);
    }

    /// <summary>
    /// E-8: an account run's last tier was the whole rest of the catalogue in id order — a random
    /// sample. It now takes the account's ELIGIBLE POOL first: here a title the account has turned
    /// down loses to an eligible one despite out-voting it.
    /// </summary>
    [Fact]
    public async Task An_account_run_takes_its_eligible_pool_before_the_rest_of_the_catalogue()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, _) = await SeedLibraryAsync(0);
        Guid eligible;
        Guid turnedDown;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            // Both English (no language rows means English-only), so the ONLY difference between
            // them is the account's "not interested".
            var e = NewHydratedTitle("Eligible for this account");
            e.TmdbVoteCount = int.MaxValue - 1;
            e.Language = "en";
            var x = NewHydratedTitle("Turned down by this account");
            x.TmdbVoteCount = int.MaxValue;
            x.Language = "en";
            db.AddRange(e, x);
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = x.Id,
                Kind = ReactionKind.NotInterested,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            (eligible, turnedDown) = (e.Id, x.Id);
        }

        try
        {
            var extractor = new RenamingExtractor("pool/test-model");
            await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 1);
            Assert.Equal([eligible], extractor.TitlesSeen);
        }
        finally
        {
            // The turned-down title would out-vote every later run's fixtures in this shared
            // catalogue; un-hydrate it so it leaves the sweep's scope.
            await using var db = Fixture.CreateContext(new AccountContext());
            await db.Titles.Where(t => t.Id == turnedDown)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastMetadataRefreshAt, (DateTime?)null));
        }
    }

    /// <summary>
    /// The two attribute repairs, on rows shaped like the founder's: a Failed row holding a
    /// current-era answer becomes Done; a Failed row holding an answer older than every era goes back
    /// to Pending at version 0; a Failed row with no answer is reopened with a fresh budget; and a
    /// row the fixed handler has touched (<c>LastAttemptAt</c> set) is left alone. A second pass
    /// changes nothing.
    /// </summary>
    [Fact]
    public async Task The_attribute_repairs_settle_answers_reopen_the_rest_and_are_idempotent()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var eraStart = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ids = new Dictionary<string, Guid>();
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            TitleAttributes Row(string key, Action<TitleAttributes> shape)
            {
                // Unhydrated, so no enrich run in this collection ever selects these fixtures.
                var title = NewHydratedTitle(key);
                title.LastMetadataRefreshAt = null;
                db.Add(title);
                ids[key] = title.Id;
                var row = new TitleAttributes { TitleId = title.Id, ExtractorVersion = 2, AttemptCount = 2 };
                shape(row);
                return row;
            }

            db.TitleAttributes.AddRange(
                // The era: a Done row under "repair/era-model" v2 from 2001-01-01.
                Row("era", a => { a.Status = AttributeExtractionStatus.Done; a.ExtractorModel = "repair/era-model"; a.RawJson = "{}"; a.ExtractedAt = eraStart; }),
                // Answered inside that era, then demoted by a later model's stale failure.
                Row("demoted", a => { a.Status = AttributeExtractionStatus.Failed; a.ExtractorModel = "repair/later-model"; a.RawJson = "{\"d\":1}"; a.ExtractedAt = eraStart.AddMonths(5); }),
                // Answered before any era existed: an older prompt generation's values.
                Row("prehistoric", a => { a.Status = AttributeExtractionStatus.Failed; a.ExtractorModel = "repair/later-model"; a.RawJson = "{\"p\":1}"; a.ExtractedAt = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc); }),
                // Two dropped connections and no answer.
                Row("dropped", a => { a.Status = AttributeExtractionStatus.Failed; a.ExtractorModel = "repair/later-model"; }),
                // The same shape, but written by the fixed handler: a genuine failure, not backlog.
                Row("genuine", a => { a.Status = AttributeExtractionStatus.Failed; a.ExtractorModel = "repair/later-model"; a.LastAttemptAt = DateTime.UtcNow; }),
                // A pre-fix demoted answer that a pending enrich job reached BEFORE the repair ran:
                // the fixed handler's failure path kept its answer and stamped LastAttemptAt. Still
                // settled — Failed-with-an-answer is a pre-fix shape whoever touched it last.
                Row("attempted-first", a => { a.Status = AttributeExtractionStatus.Failed; a.ExtractorModel = "repair/later-model"; a.RawJson = "{\"f\":1}"; a.ExtractedAt = eraStart.AddMonths(6); a.LastAttemptAt = DateTime.UtcNow; a.RetryAfter = DateTime.UtcNow.AddDays(7); }));
            await db.SaveChangesAsync();
        }

        async Task<Dictionary<string, TitleAttributes>> SnapshotAsync()
        {
            await using var db = Fixture.CreateContext(new AccountContext());
            var rows = await db.TitleAttributes.AsNoTracking()
                .Where(a => ids.Values.Contains(a.TitleId)).ToDictionaryAsync(a => a.TitleId);
            return ids.ToDictionary(kv => kv.Key, kv => rows[kv.Value]);
        }

        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            await DataRepairs.RunAllAsync(db, DateTime.UtcNow, CancellationToken.None);
        }

        var first = await SnapshotAsync();
        Assert.Equal(AttributeExtractionStatus.Done, first["demoted"].Status);
        Assert.Equal("repair/era-model", first["demoted"].ExtractorModel);
        Assert.Equal(2, first["demoted"].ExtractorVersion);
        Assert.Equal(0, first["demoted"].AttemptCount);
        Assert.Equal(AttributeExtractionStatus.Pending, first["prehistoric"].Status);
        Assert.Equal(0, first["prehistoric"].ExtractorVersion);
        Assert.Equal(AttributeExtractionStatus.Pending, first["dropped"].Status);
        Assert.Equal(0, first["dropped"].AttemptCount);
        Assert.Equal(AttributeExtractionStatus.Failed, first["genuine"].Status);
        Assert.Equal(2, first["genuine"].AttemptCount);
        Assert.Equal(AttributeExtractionStatus.Done, first["era"].Status);
        Assert.Equal(AttributeExtractionStatus.Done, first["attempted-first"].Status);
        Assert.Equal("repair/era-model", first["attempted-first"].ExtractorModel);

        IReadOnlyList<DataRepairResult> second;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            second = await DataRepairs.RunAllAsync(db, DateTime.UtcNow, CancellationToken.None);
            Assert.All(await DataRepairs.PendingAsync(db, CancellationToken.None), p => Assert.Equal(0, p.RowsChanged));
        }

        Assert.Equal(0, second.Single(r => r.Name == DataRepairs.SettleAnsweredFailuresName).RowsChanged);
        Assert.Equal(0, second.Single(r => r.Name == DataRepairs.ReopenUnansweredFailuresName).RowsChanged);
        var again = await SnapshotAsync();
        foreach (var key in ids.Keys)
        {
            Assert.Equal(
                (first[key].Status, first[key].ExtractorModel, first[key].ExtractorVersion, first[key].AttemptCount),
                (again[key].Status, again[key].ExtractorModel, again[key].ExtractorVersion, again[key].AttemptCount));
        }
    }

    /// <summary>
    /// A GLOBAL run — the shape the weekly catalogue-growth sweep queues. It has no account, so it
    /// must neither demand one nor chain a Train for one, and it still enriches. This used to throw
    /// on the first line of the handler.
    /// </summary>
    [Fact]
    public async Task A_global_run_enriches_without_an_account_and_chains_no_train()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var titleId = await SeedOrphanTitleAsync();
        var extractor = new RenamingExtractor("global/test-model");

        await RunEnrichAsync(accountId: null, new StubEmbeddingProvider(true), extractor, cap: 400);

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.Contains(titleId, extractor.TitlesSeen);
        Assert.True(await db.TitleEmbeddings.AnyAsync(e => e.TitleId == titleId));
        Assert.Equal(AttributeExtractionStatus.Done,
            (await db.TitleAttributes.SingleAsync(a => a.TitleId == titleId)).Status);
        Assert.False(await db.SyncJobs.AnyAsync(j => j.AccountId == null && j.Kind == JobKind.Train));
    }

    /// <summary>
    /// R1-47 (CR-8): extraction waits behind an open breaker. The runner already declines to claim
    /// an enrich job while the breaker is open; this is the breaker that is open when the run reaches
    /// extraction. Nothing is asked, nothing is claimed, nothing is charged, and the pill says why.
    /// One fixture field varies: whose breaker is open. A breaker on another purpose must not pause
    /// extraction — only the provider's or extraction's own.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(ApiUsagePurpose.ExtractAttributes, true)]
    [InlineData(ApiUsagePurpose.Lens, false)]
    public async Task An_open_breaker_pauses_extraction_before_anything_is_asked_or_charged(string? breakerPurpose, bool paused)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(3);
        var health = new FakeProviderHealth();
        health.Open(breakerPurpose, ProviderBreakerReasons.PaymentRequired);
        var extractor = new RenamingExtractor("breaker/test-model");

        var job = await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), extractor, cap: 3, health: health);

        await using var db = Fixture.CreateContext(new AccountContext());
        var rows = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        if (paused)
        {
            Assert.Empty(extractor.TitlesSeen);
            Assert.Empty(rows); // never claimed: no placeholder, no attempt, no backoff
            Assert.Equal("Reading the mood · paused: the model provider account is out of credit", job.ProgressMessage);
        }
        else
        {
            Assert.Equal(titleIds.Order(), extractor.TitlesSeen.Order());
            Assert.All(rows, r => Assert.Equal(AttributeExtractionStatus.Done, r.Status));
            Assert.Equal(3, rows.Count);
        }
    }

    /// <summary>
    /// R1-47: a breaker that opens DURING a run. The first call comes back 402 and every later call
    /// in the batch is refused before it is sent; the live adapter reports each as a bare "no answer".
    /// Those used to be charged as parse failures — 24 attempts burned on a credit outage, and the
    /// next 24 in the next batch. Now the batch is booked as transport (backed off, charged nothing)
    /// and the run pauses before the second batch. The control row runs the identical fixture with
    /// the breaker staying shut, and charges, so the test is not green by construction.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refusals_in_the_batch_that_opened_the_breaker_are_transport_and_the_rest_waits(bool breakerOpens)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(30);
        var health = new FakeProviderHealth();
        var refusing = new RefusingExtractor("refusal/test-model", () =>
        {
            if (breakerOpens)
            {
                health.Open(null, ProviderBreakerReasons.PaymentRequired);
            }
        });
        var log = new CapturingLogger();

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), refusing, cap: 30, logger: log, health: health);

        await using var db = Fixture.CreateContext(new AccountContext());
        var rows = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        if (breakerOpens)
        {
            Assert.Equal(1, refusing.Calls);
            Assert.Equal(24, rows.Count); // the first batch only; the second was never claimed
            Assert.All(rows, r =>
            {
                Assert.Equal(AttributeExtractionStatus.Pending, r.Status);
                Assert.Equal(0, r.AttemptCount);
                Assert.Equal(1, r.TransportFailures);
                Assert.NotNull(r.RetryAfter);
                Assert.Null(r.ClaimedBy);
            });
            Assert.Contains(log.Lines, l => l.Contains("24 transport failure(s)", StringComparison.Ordinal)
                && l.Contains("0 unclassified failure(s)", StringComparison.Ordinal)
                && l.Contains("paused behind the provider breaker: True", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(2, refusing.Calls);
            Assert.Equal(30, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal(AttributeExtractionStatus.Failed, r.Status);
                Assert.Equal(1, r.AttemptCount);
                Assert.Equal(0, r.TransportFailures);
            });
        }
    }

    /// <summary>
    /// A failure the extractor DID classify keeps its class when the breaker opens in its batch: a
    /// completion came back and was unusable, which no refusal explains. Only the unexplained
    /// no-answers move to transport.
    /// </summary>
    [Fact]
    public async Task A_classified_parse_failure_is_still_charged_when_the_breaker_opens_in_its_batch()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        var health = new FakeProviderHealth();
        var malformed = titleIds[0];
        var classifying = new ClassifyingExtractor(
            "classified-refusal/test-model",
            id => id == malformed ? AttributeFailureClass.Parse : AttributeFailureClass.Unclassified)
        {
            DuringCall = () =>
            {
                health.Open(null, ProviderBreakerReasons.Unauthorized);
                return Task.CompletedTask;
            },
        };

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), classifying, cap: 2, health: health);

        await using var db = Fixture.CreateContext(new AccountContext());
        var parse = await db.TitleAttributes.SingleAsync(a => a.TitleId == malformed);
        Assert.Equal(AttributeExtractionStatus.Failed, parse.Status);
        Assert.Equal(1, parse.AttemptCount);
        var refused = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleIds[1]);
        Assert.Equal(0, refused.AttemptCount);
        Assert.Equal(1, refused.TransportFailures);
    }

    /// <summary>
    /// R1-48, the early-stop declaration. EnrichCatalog is declared <c>StopsEarlyOnCancellation</c>,
    /// so a deadline is honoured by RETURNING at a checkpoint. The deadline here lands while the model
    /// is answering the first batch: those answers were bought and are kept, the second batch is never
    /// asked, and none of a finished run's work happens — no Train for an account with no model (which
    /// a finished run WOULD chain; the control row proves it) and no completion event.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_deadline_mid_batch_keeps_what_was_bought_and_stops_without_finishing(bool deadline)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(30);
        using var slice = new CancellationTokenSource();
        var extractor = new DeadlineExtractor("deadline/test-model", () =>
        {
            if (deadline)
            {
                slice.Cancel();
            }
        });
        var events = new CapturingEventHub();

        // Returns normally either way: an early stop is not an exception.
        await RunEnrichAsync(
            accountId, new StubEmbeddingProvider(false), extractor, cap: 30, events: events, ct: slice.Token);

        await using var db = Fixture.CreateContext(new AccountContext());
        var done = await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done);
        var train = await db.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.Train);
        var completed = events.Types.Contains(PipelineEventTypes.JobCompleted);
        if (deadline)
        {
            Assert.Equal(1, extractor.Calls);
            Assert.Equal(24, done);
            Assert.Equal(0, await db.TitleAttributes.CountAsync(a => titleIds.Contains(a.TitleId) && a.ClaimedBy != null));
            Assert.False(train);
            Assert.False(completed);
        }
        else
        {
            Assert.Equal(2, extractor.Calls);
            Assert.Equal(30, done);
            Assert.True(train);
            Assert.True(completed);
        }
    }

    /// <summary>
    /// R1-48: Train is chained only when it has something to learn from. One fixture field at a time:
    /// whether the account has a model, whether a rating landed after it, and what the run wrote.
    /// An empty enrich behind every feed build used to queue a Train that then skipped itself.
    /// </summary>
    [Theory]
    [InlineData(true, false, "nothing", false)]
    [InlineData(true, false, "only-failures", false)]
    [InlineData(true, true, "nothing", true)]
    [InlineData(true, false, "answers", true)]
    [InlineData(true, false, "vectors", true)]
    [InlineData(false, false, "nothing", true)]
    public async Task Train_is_chained_only_when_the_run_or_the_ratings_gave_it_something_new(
        bool hasModel, bool ratingSinceModel, string wrote, bool chainsTrain)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        var trainedAt = DateTime.UtcNow.AddDays(-1);
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            // The library's ratings predate the model.
            await seed.UserRatings.Where(r => r.AccountId == accountId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.FirstRatedAt, trainedAt.AddDays(-1)));
            if (hasModel)
            {
                seed.ModelArtifacts.Add(new ModelArtifact
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Version = 1,
                    TrainingRunId = Guid.NewGuid(),
                    Status = ArtifactStatus.Active,
                    TrainedAt = trainedAt,
                    FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(new { version = FeatureSchema.Version }),
                });
            }

            if (ratingSinceModel)
            {
                await seed.UserRatings.Where(r => r.AccountId == accountId && r.TitleId == titleIds[0])
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.FirstRatedAt, DateTime.UtcNow));
            }

            await seed.SaveChangesAsync();
        }

        var (embeddings, extractor) = wrote switch
        {
            "nothing" => ((IEmbeddingProvider)new StubEmbeddingProvider(false), (ITitleAttributeExtractor)new UnavailableExtractor()),
            "only-failures" => (new StubEmbeddingProvider(false), new AlwaysFailingExtractor("train-chain/failing")),
            "answers" => (new StubEmbeddingProvider(false), new RenamingExtractor("train-chain/answering")),
            "vectors" => (new StubEmbeddingProvider(true), new UnavailableExtractor()),
            _ => throw new ArgumentOutOfRangeException(nameof(wrote), wrote, null),
        };

        await RunEnrichAsync(accountId, embeddings, extractor, cap: 2);

        await using var db = Fixture.CreateContext(new AccountContext());
        Assert.Equal(chainsTrain, await db.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.Train));
    }

    /// <summary>
    /// R1-47 end to end, through the PRODUCTION adapter and a canned HTTP transport: a title whose
    /// request died in the network is backed off and never charged; a title the model answered
    /// badly is charged one attempt. Run with the drop on either title, so the class follows the
    /// title and not its position in the batch.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Through_the_real_adapter_a_network_drop_is_backed_off_and_a_bad_answer_is_charged(int dropping)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        var malformed = """{"darkness":0.8,"pacing":0.3,"complexity":0.6,"emotionalIntensity":0.7,"optimism":0.2,"ensembleVsSolo":0.4,"tone":"brooding","era":"modern","themes":["grief"]}""";
        var transport = new RoutingHttpHandler(body => body.Contains($"Library Title {dropping}", StringComparison.Ordinal)
            ? null
            : ChatEnvelope(malformed));
        var log = new CapturingLogger();

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), LiveExtractor(transport), cap: 2, logger: log);

        await using var db = Fixture.CreateContext(new AccountContext());
        var dropped = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleIds[dropping]);
        Assert.Equal(0, dropped.AttemptCount);
        Assert.Equal(1, dropped.TransportFailures);
        Assert.NotEqual(AttributeExtractionStatus.Failed, dropped.Status);
        Assert.True(dropped.RetryAfter > DateTime.UtcNow.AddMinutes(25));

        var answeredBadly = await db.TitleAttributes.SingleAsync(a => a.TitleId == titleIds[1 - dropping]);
        Assert.Equal(AttributeExtractionStatus.Failed, answeredBadly.Status);
        Assert.Equal(1, answeredBadly.AttemptCount);
        Assert.Equal(0, answeredBadly.TransportFailures);

        Assert.Contains(log.Lines, l => l.Contains("1 transport failure(s)", StringComparison.Ordinal)
            && l.Contains("1 parse failure(s)", StringComparison.Ordinal)
            && l.Contains("0 unclassified failure(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole batch dropped: the production adapter throws its typed transport exception, every
    /// title is backed off, nothing is charged, and the run stops.
    /// </summary>
    [Fact]
    public async Task Through_the_real_adapter_a_wholly_dropped_batch_charges_nothing()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, titleIds) = await SeedLibraryAsync(2);
        var transport = new RoutingHttpHandler(_ => null);

        await RunEnrichAsync(accountId, new StubEmbeddingProvider(false), LiveExtractor(transport), cap: 2);

        await using var db = Fixture.CreateContext(new AccountContext());
        var rows = await db.TitleAttributes.Where(a => titleIds.Contains(a.TitleId)).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(0, row.AttemptCount);
            Assert.Equal(1, row.TransportFailures);
            Assert.NotEqual(AttributeExtractionStatus.Failed, row.Status);
        });
    }

    private static OpenRouterAttributeExtractor LiveExtractor(HttpMessageHandler transport)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = "test-key-not-a-real-one",
                ["TestHelpers:ModelStubMode"] = "false",
                ["OPENROUTER_MAX_CONCURRENCY"] = "1",
            })
            .Build();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new ApiUsageRecorder(scopeFactory, NullLogger<ApiUsageRecorder>.Instance);
        var client = new HttpClient(transport) { BaseAddress = new Uri("https://openrouter.invalid/api/v1/") };
        return new OpenRouterAttributeExtractor(client, recorder, config, NullLogger<OpenRouterAttributeExtractor>.Instance);
    }

    private static string ChatEnvelope(string content) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
        });

    /// <summary>
    /// Answers each request from its body; a null answer is a connection that died before any
    /// response, which is what a laptop host's sleep or a flaky uplink looks like to HttpClient.
    /// </summary>
    private sealed class RoutingHttpHandler(Func<string, string?> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var reply = answer(body) ?? throw new HttpRequestException("Connection reset by peer");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(reply, System.Text.Encoding.UTF8, "application/json"),
            };
        }
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
    private async Task<SyncJob> RunEnrichAsync(
        Guid? accountId, IEmbeddingProvider embeddings, ITitleAttributeExtractor extractor, int? cap = null,
        Microsoft.Extensions.Logging.ILogger<EnrichCatalogJobHandler>? logger = null,
        IProviderHealth? health = null,
        IPipelineEventHub? events = null,
        CancellationToken ct = default)
    {
        var scoped = new AccountContext();
        if (accountId is { } pinned)
        {
            scoped.SetAccount(pinned);
        }

        await using var db = Fixture.CreateContext(scoped);
        var configuration = Config(cap);
        var handler = new EnrichCatalogJobHandler(
            db, embeddings, extractor, events ?? new NoopEventHub(), configuration,
            new CatalogEnrichmentBudget(db, configuration),
            logger ?? NullLogger<EnrichCatalogJobHandler>.Instance,
            health);
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
        await handler.ExecuteAsync(job, ct);

        // The runner owns status transitions; mirror it so the in-flight unique index
        // (AccountId, Kind) releases and a second enrich run can be enqueued.
        job.Status = JobStatus.Succeeded;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return job;
    }

    /// <summary>
    /// Executes an ALREADY-STORED job row, as a revived job is: <c>JobRunnerService</c> hands
    /// the same <see cref="SyncJob"/> id to a second execution while the first may still be running.
    /// </summary>
    private async Task RunExistingJobAsync(
        Guid jobId, Guid accountId, IEmbeddingProvider embeddings, ITitleAttributeExtractor extractor, int cap)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = Fixture.CreateContext(scoped);
        var configuration = Config(cap);
        var handler = new EnrichCatalogJobHandler(
            db, embeddings, extractor, new NoopEventHub(), configuration,
            new CatalogEnrichmentBudget(db, configuration), NullLogger<EnrichCatalogJobHandler>.Instance);
        var job = await db.SyncJobs.SingleAsync(j => j.Id == jobId);
        await handler.ExecuteAsync(job, CancellationToken.None);
    }

    private async Task ExpireRetryGateAsync(Guid titleId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        await db.TitleAttributes
            .Where(a => a.TitleId == titleId)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.RetryAfter, DateTime.UtcNow.AddSeconds(-1)));
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
        await using var db = Fixture.CreateContext(new AccountContext());
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
        await using var db = Fixture.CreateContext(new AccountContext());
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
    /// A hydrated title belonging to no account, carrying a vote count no real TMDB row could reach.
    /// <para>
    /// The global sweep has no account to prioritise for and takes the catalogue in SUPPLY order —
    /// highest <c>TmdbVoteCount</c>, then recency — so an absurd vote count is how a test over a
    /// shared catalogue names the row a capped global run will pick. It used to be an id sorting
    /// first in Postgres's uuid order, which was the same trick against the ordering the sweep used
    /// before the catalogue bulk seed made a global backlog worth prioritising.
    /// </para>
    /// </summary>
    private async Task<Guid> SeedOrphanTitleAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = NewHydratedTitle("Orphan Title");
        title.TmdbVoteCount = 8_000_000;
        title.Year = 2999;
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

    /// <summary>A configured-off extractor: the embedding phase runs alone.</summary>
    private sealed class UnavailableExtractor : ITitleAttributeExtractor
    {
        public bool IsAvailable => false;

        public string ModelId => "unavailable/test-model";

        public int Version => 1;

        public Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default) =>
            throw new InvalidOperationException("an unavailable extractor is never called");
    }

    /// <summary>An extractor whose whole batch throws the given exception.</summary>
    private sealed class ThrowingExtractor(string modelId, Func<Exception> failure) : ITitleAttributeExtractor
    {
        public bool IsAvailable => true;

        public string ModelId => modelId;

        public int Version => 1;

        public List<Guid> TitlesSeen { get; } = [];

        public Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
        {
            TitlesSeen.AddRange(inputs.Select(i => i.TitleId));
            throw failure();
        }
    }

    /// <summary>
    /// An extractor that classifies its failures, as the production adapter does once it implements
    /// <see cref="IClassifyingAttributeExtractor"/>. <see cref="AttributeFailureClass.None"/> answers.
    /// Thread-safe, because two runs share one instance in the concurrency test.
    /// </summary>
    private sealed class ClassifyingExtractor(
        string modelId, Func<Guid, AttributeFailureClass> outcome, int version = 1)
        : ITitleAttributeExtractor, IClassifyingAttributeExtractor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _seen = new();

        public bool IsAvailable => true;

        public string ModelId => modelId;

        public int Version => version;

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public Func<Task>? DuringCall { get; init; }

        public IReadOnlyList<Guid> TitlesSeen => _seen.ToList();

        public async Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default) =>
            (await ExtractClassifiedAsync(inputs, ct)).Select(o => o.Attributes).ToList();

        public async Task<IReadOnlyList<AttributeExtractionOutcome>> ExtractClassifiedAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
        {
            foreach (var input in inputs)
            {
                _seen.Enqueue(input.TitleId);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            if (DuringCall is { } during)
            {
                await during();
            }

            return inputs
                .Select(i => outcome(i.TitleId) is var failure && failure == AttributeFailureClass.None
                    ? AttributeExtractionOutcome.Success(new ExtractedTitleAttributes(
                        0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, "neutral", "contemporary", ["t"], "{}"))
                    : AttributeExtractionOutcome.Failed(failure))
                .ToList();
        }
    }

    /// <summary>Keeps every formatted log line, so the run log's separate counters can be asserted.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<EnrichCatalogJobHandler>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines.ToList();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => _lines.Enqueue(formatter(state, exception));
    }

    /// <summary>
    /// A live extractor at the moment the provider starts refusing: <paramref name="onCall"/> runs
    /// as the batch is sent (the first call's 402 opens the breaker there), and every title comes back
    /// as a bare null, which is all the plain port can say about a refusal.
    /// </summary>
    private sealed class RefusingExtractor(string modelId, Action onCall) : ITitleAttributeExtractor
    {
        public bool IsAvailable => true;

        public string ModelId => modelId;

        public int Version => 1;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
        {
            Calls++;
            onCall();
            IReadOnlyList<ExtractedTitleAttributes?> result = inputs.Select(_ => (ExtractedTitleAttributes?)null).ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>An extractor that answers every title, running <paramref name="onCall"/> while it does.</summary>
    private sealed class DeadlineExtractor(string modelId, Action onCall) : ITitleAttributeExtractor
    {
        public bool IsAvailable => true;

        public string ModelId => modelId;

        public int Version => 1;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
            IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
        {
            Calls++;
            onCall();
            IReadOnlyList<ExtractedTitleAttributes?> result = inputs
                .Select(_ => (ExtractedTitleAttributes?)new ExtractedTitleAttributes(
                    0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, "neutral", "contemporary", ["t"], "{}"))
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Provider health with one breaker the test opens by hand: the provider's own (a null purpose)
    /// or one purpose's. Only <see cref="OpenBreakerFor"/> is read by the handler.
    /// </summary>
    private sealed class FakeProviderHealth : IProviderHealth
    {
        private volatile ProviderBreaker? _open;

        public void Open(string? purpose, string reasonCode) =>
            _open = new ProviderBreaker(ApiProvider.OpenRouter, purpose, ProviderBreakerState.Open, reasonCode, DateTime.UtcNow, DateTime.UtcNow.AddHours(1));

        public ProviderBreaker? OpenBreakerFor(ApiProvider provider, string purpose) =>
            _open is { } open && open.Provider == provider && (open.Purpose is null || open.Purpose == purpose) ? open : null;

        public bool TryAdmit(ApiProvider provider, string purpose, out ProviderBreaker? breaker, out bool probe)
        {
            breaker = OpenBreakerFor(provider, purpose);
            probe = false;
            return breaker is null;
        }

        public void ReportRefused(ApiProvider provider, int httpStatus) => Open(null, ProviderBreakerReasons.ForStatus(httpStatus));

        public void ReportAnswered(ApiProvider provider) => _open = null;

        public void ReportInconclusive(ApiProvider provider)
        {
        }

        public void OpenPurpose(ApiProvider provider, string purpose, string reasonCode, DateTime until) => Open(purpose, reasonCode);
    }

    /// <summary>Records the event types published, so a run's completion event can be asserted.</summary>
    private sealed class CapturingEventHub : IPipelineEventHub
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _types = new();

        public IReadOnlyList<string> Types => _types.ToList();

        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) => _types.Enqueue(type);

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
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
