using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The store's three load-bearing behaviours against real Postgres: the warm-skip anti-join that
/// IS the job's resume state, the stale ladder that a fingerprint move produces instead of a
/// deletion, and the duplicate absorber on the unique key.
/// </summary>
[Collection("postgres")]
public class CrossReferenceServiceTests(PostgresFixture fixture)
{
    private const string Hash = "abc123";

    private const string NewHash = "def456";

    private const int PromptVersion = 1;

    [Fact]
    public async Task A_warm_title_is_never_returned_as_work_again()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var judged = Guid.NewGuid();
        var unjudged = Guid.NewGuid();
        await RecordAsync(accountId, judged, Hash, CrossReferenceRound.First, CrossReferenceVerdict.Keep);

        await using var db = fixture.CreateContext(new AccountContext());
        var service = Service(db);

        var work = await service.UnjudgedAsync(
            accountId, [judged, unjudged], Hash, PromptVersion, CrossReferenceRound.First, CancellationToken.None);

        Assert.Equal([unjudged], work);
        Assert.Equal(
            1,
            await service.UnjudgedCountAsync(
                accountId, [judged, unjudged], Hash, PromptVersion, CrossReferenceRound.First, CancellationToken.None));
    }

    [Fact]
    public async Task The_work_list_preserves_the_callers_order()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A deterministic order in must give a deterministic order out, or two runs over an
        // unchanged pool batch the same titles differently and the prompt cache never warms.
        var accountId = await SeedAccountAsync();
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();

        await using var db = fixture.CreateContext(new AccountContext());
        var work = await Service(db).UnjudgedAsync(
            accountId, ids, Hash, PromptVersion, CrossReferenceRound.First, CancellationToken.None);

        Assert.Equal(ids, work);
    }

    [Fact]
    public async Task Rounds_and_prompt_versions_are_separate_work()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Round is in the key because "two keeps" has to have something to count. A round-1 verdict
        // must therefore leave round 2 outstanding, and a prompt bump must leave everything
        // outstanding rather than invalidating anything.
        var accountId = await SeedAccountAsync();
        var titleId = Guid.NewGuid();
        await RecordAsync(accountId, titleId, Hash, CrossReferenceRound.First, CrossReferenceVerdict.Keep);

        await using var db = fixture.CreateContext(new AccountContext());
        var service = Service(db);

        Assert.Empty(await service.UnjudgedAsync(
            accountId, [titleId], Hash, PromptVersion, CrossReferenceRound.First, CancellationToken.None));
        Assert.Single(await service.UnjudgedAsync(
            accountId, [titleId], Hash, PromptVersion, CrossReferenceRound.Second, CancellationToken.None));
        Assert.Single(await service.UnjudgedAsync(
            accountId, [titleId], Hash, PromptVersion + 1, CrossReferenceRound.First, CancellationToken.None));
    }

    [Fact]
    public async Task A_fingerprint_move_makes_a_verdict_stale_rather_than_deleting_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var moved = Guid.NewGuid();
        var neverJudged = Guid.NewGuid();
        await RecordAsync(accountId, moved, Hash, CrossReferenceRound.First, CrossReferenceVerdict.Out, confidence: 0.9);

        await using var db = fixture.CreateContext(new AccountContext());
        var service = Service(db);

        // Outstanding at the NEW hash…
        var work = await service.UnjudgedAsync(
            accountId, [neverJudged, moved], NewHash, PromptVersion, CrossReferenceRound.First, CancellationToken.None);
        Assert.Contains(moved, work);

        // …re-judged FIRST, because a stale verdict is the one place the instrument is actively out
        // of date; a never-judged title is merely unknown.
        var ladder = await service.StaleFirstAsync(accountId, work, NewHash, CancellationToken.None);
        Assert.Equal(moved, ladder[0]);

        // …and still there, still readable, still recorded on the exposure row — and, since the
        // keep-only decision of 2026-09-11, still not moving the title an inch. The row survives a
        // fingerprint move because deleting it would cost a re-judgement of the whole pool every
        // time the account's taste ticks over.
        var factors = await service.VerdictFactorsAsync(
            accountId, [moved], NewHash, PromptVersion, CancellationToken.None);
        Assert.Equal(AggregateVerdict.Out, factors[moved].Verdict);
        Assert.Equal(VerdictAggregator.OutFactor, factors[moved].Factor);
        Assert.Equal(1.0d, factors[moved].Factor);
        Assert.True(factors[moved].Stale);
        Assert.True(factors[moved].HeroAllowed);
    }

    [Fact]
    public async Task A_duplicate_insert_is_absorbed_rather_than_thrown()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Two runners at the same prompt version, or a re-run racing a batch that has already
        // landed. Losing that race means the verdict already exists, which is what both callers
        // wanted — the same resolution LensService makes for the same race.
        var accountId = await SeedAccountAsync();
        var titleId = Guid.NewGuid();
        await RecordAsync(accountId, titleId, Hash, CrossReferenceRound.First, CrossReferenceVerdict.Keep);

        await using var db = fixture.CreateContext(new AccountContext());
        var service = Service(db);
        var written = await service.RecordBatchAsync(
            accountId, Hash, PromptVersion, CrossReferenceRound.First, "stub/deterministic-v1", null, 0,
            [
                new CrossReferenceEntry(titleId, CrossReferenceVerdict.Out, CrossReferenceReasons.ToneMismatch, 0.9d),
                new CrossReferenceEntry(Guid.NewGuid(), CrossReferenceVerdict.Keep, CrossReferenceReasons.ToneMatch, 0.6d),
            ],
            new Dictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>(),
            DateTime.UtcNow,
            CancellationToken.None);

        Assert.Equal(2, written);

        // The row that was already there stands; only the losing insert was dropped.
        await using var check = fixture.CreateContext(new AccountContext());
        var stored = check.TitleCrossReferenceVerdicts
            .Where(v => v.AccountId == accountId && v.TitleId == titleId)
            .ToList();
        Assert.Equal(CrossReferenceVerdict.Keep, Assert.Single(stored).Verdict);
    }

    [Fact]
    public async Task Factors_roll_two_rounds_into_one_answer_and_persist_the_evidence()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = Guid.NewGuid();
        var neighbourId = Guid.NewGuid();

        await using var db = fixture.CreateContext(new AccountContext());
        var service = Service(db);
        await service.RecordBatchAsync(
            accountId, Hash, PromptVersion, CrossReferenceRound.First, "stub/deterministic-v1", null, 0,
            [new CrossReferenceEntry(titleId, CrossReferenceVerdict.Keep, CrossReferenceReasons.NeighbourLoved, 0.8d)],
            new Dictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>
            {
                [titleId] = [new CrossReferenceNeighbour("Sicario", 2015, 10, 0.83d, neighbourId)],
            },
            DateTime.UtcNow,
            CancellationToken.None);
        await service.RecordBatchAsync(
            accountId, Hash, PromptVersion, CrossReferenceRound.Second, "stub/deterministic-v1", null, 0,
            [new CrossReferenceEntry(titleId, CrossReferenceVerdict.Keep, CrossReferenceReasons.ThemeMatch, 0.6d)],
            new Dictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>(),
            DateTime.UtcNow,
            CancellationToken.None);

        var factors = await service.VerdictFactorsAsync(
            accountId, [titleId], Hash, PromptVersion, CancellationToken.None);

        Assert.Equal(AggregateVerdict.ConfirmedKeep, factors[titleId].Verdict);
        Assert.Equal(1d + (0.2d * 0.7d), factors[titleId].Factor, 6);

        // The evidence travels with the verdict, so a surprising answer can be re-read against what
        // it actually saw rather than against today's nearest neighbours.
        await using var check = fixture.CreateContext(new AccountContext());
        var round1 = check.TitleCrossReferenceVerdicts
            .Single(v => v.AccountId == accountId && v.TitleId == titleId && v.Round == CrossReferenceRound.First);
        Assert.Contains(neighbourId.ToString(), round1.NeighbourIdsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_title_nobody_judged_is_absent_rather_than_neutral()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Callers read a miss as VerdictFactor.Neutral; returning a row for every title asked about
        // would make "nothing has judged this" and "something judged it and could not tell"
        // indistinguishable downstream.
        var accountId = await SeedAccountAsync();

        await using var db = fixture.CreateContext(new AccountContext());
        var factors = await Service(db).VerdictFactorsAsync(
            accountId, [Guid.NewGuid()], Hash, PromptVersion, CancellationToken.None);

        Assert.Empty(factors);
    }

    [Fact]
    public async Task Verdicts_written_at_an_older_prompt_version_are_ignored_entirely()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A stale FINGERPRINT is half-strength evidence about the same question. A stale PROMPT is
        // a different question, and averaging the two would be averaging two estimands.
        var accountId = await SeedAccountAsync();
        var titleId = Guid.NewGuid();
        await RecordAsync(
            accountId, titleId, Hash, CrossReferenceRound.First, CrossReferenceVerdict.Out,
            confidence: 0.95, promptVersion: PromptVersion - 1);

        await using var db = fixture.CreateContext(new AccountContext());
        var factors = await Service(db).VerdictFactorsAsync(
            accountId, [titleId], Hash, PromptVersion, CancellationToken.None);

        Assert.Empty(factors);
    }

    private static CrossReferenceService Service(Infrastructure.Persistence.ReelDbContext db) =>
        new(db, NullLogger<CrossReferenceService>.Instance);

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xrefsvc-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task RecordAsync(
        Guid accountId, Guid titleId, string profileHash, CrossReferenceRound round,
        CrossReferenceVerdict verdict, double confidence = 0.75d, int promptVersion = PromptVersion)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleCrossReferenceVerdicts.Add(new TitleCrossReferenceVerdict
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            ProfileHash = profileHash,
            PromptVersion = promptVersion,
            Round = round,
            Verdict = verdict,
            ReasonCode = verdict == CrossReferenceVerdict.Keep
                ? CrossReferenceReasons.NeighbourLoved
                : CrossReferenceReasons.ToneMismatch,
            Confidence = (decimal)confidence,
            NeighbourIdsJson = "[]",
            ModelId = "stub/deterministic-v1",
            BatchIndex = 0,
            GeneratedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();
    }
}
