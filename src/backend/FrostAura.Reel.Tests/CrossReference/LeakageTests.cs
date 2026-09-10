using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.Extensions.Caching.Memory;
using Pgvector;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The first test written for the cross-reference instrument, and the one that decides whether
/// anything else it reports means anything.
///
/// <para><b>The defect it exists to catch.</b> <c>PersonalisationContext.PersonaForAsync</c> reads
/// the stored taste narrative, which is regenerated weekly from EVERY rating the account has ever
/// made, and <c>AnchorCandidatesAsync</c> reads <c>UserRatings</c> with no as-of filter at all. A
/// backtest run through either hands the judge a description built from the very ratings it is
/// about to be scored against — not a subtle leak, the answer key. The same is true of the nearest
/// neighbours: "the nearest title you rated" is the strongest evidence in the prompt, and if that
/// set includes titles rated after the split, a judge can appear to rule out badly-rated films
/// simply by being shown their ratings.</para>
///
/// <para>Three surfaces, three assertions: the persona text, the anchor list, and the neighbour
/// set. Each seeds one fact strictly after the split that would be unmissable if it leaked.</para>
/// </summary>
[Collection("postgres")]
public class LeakageTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>
    /// A language nothing else in the postgres collection uses.
    ///
    /// <para>The catalogue is GLOBAL and shared by every class in this collection, and the
    /// eligibility layer's language allow-list defaults to English-only when an account configures
    /// no <c>Language</c> filter. Seeding this file's synthetic titles as English therefore put a
    /// hundred rows into every sibling test's eligible pool and displaced their fixtures — which is
    /// exactly the contamination class <c>CandidateRetrieverTests</c> already documents. A
    /// non-English language makes them invisible to every account but the ones seeded here, which
    /// opt into it explicitly.</para>
    /// </summary>
    /// <summary>
    /// The base dimension every embedding in this file is placed on.
    ///
    /// <para>The catalogue and its embeddings are GLOBAL and shared by every class in the postgres
    /// collection, and retrieval takes a fixed-size nearest-neighbour window BEFORE eligibility is
    /// applied — so a title seeded here that happens to sit near another test's query vector
    /// displaces that test's own fixture from the window, and the eligibility filter then removes it
    /// again, leaving the sibling short. Sibling fixtures use single-digit and low double-digit
    /// dimensions; reserving a block far above them makes every title seeded here exactly orthogonal
    /// to every query they issue, which is the smallest similarity there is.</para>
    ///
    /// <para>It must also be DETERMINISTIC. An earlier version derived the dimension from
    /// <c>string.GetHashCode</c>, which .NET randomises per process — so the fixture landed on a
    /// different set of dimensions on every run and the contamination came and went.</para>
    /// </summary>
    private const int EmbeddingDimensionBase = 1200;

    private const string TestLanguage = "zz";

    private static readonly DateTime SplitAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime BeforeSplit = SplitAt.AddDays(-30);

    private static readonly DateTime AfterSplit = SplitAt.AddDays(30);

    /// <summary>A per-instance genre so this test's pool is its own; the catalogue is shared.</summary>
    private readonly string _scope = $"xrefleak{Guid.NewGuid():N}";

    [Fact]
    public async Task As_of_persona_never_names_a_title_rated_after_the_split()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // Rated BEFORE the split at 10 — this one is fair evidence and must be visible.
        var beforeId = await SeedTitleAsync("Anchor Before Split", UnitVector(0));
        await SeedRatingAsync(accountId, beforeId, 10, BeforeSplit);

        // Rated AFTER the split at 10 — the leak. It would rank first in TopLovedTitleIds and first
        // in the anchor list on rating alone, so if the cut is missing it cannot hide.
        var afterId = await SeedTitleAsync("Anchor After Split", UnitVector(1));
        await SeedRatingAsync(accountId, afterId, 10, AfterSplit);

        await using var db = Fixture.CreateContext(new AccountContext());
        var personalisation = new PersonalisationContext(db, Dislikes(db));
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, SplitAt, CancellationToken.None);
        var persona = await personalisation.PersonaAsOfAsync(accountId, taste, SplitAt, CancellationToken.None);

        Assert.DoesNotContain("Anchor After Split", persona.Taste, StringComparison.Ordinal);
        Assert.Contains("Anchor Before Split", persona.Taste, StringComparison.Ordinal);
    }

    [Fact]
    public async Task As_of_anchor_candidates_never_carry_a_rating_first_seen_after_the_split()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        var beforeId = await SeedTitleAsync("Loved Before", UnitVector(0));
        await SeedRatingAsync(accountId, beforeId, 9, BeforeSplit);
        var afterId = await SeedTitleAsync("Loved After", UnitVector(1));
        await SeedRatingAsync(accountId, afterId, 9, AfterSplit);
        var hatedAfterId = await SeedTitleAsync("Hated After", UnitVector(2));
        await SeedRatingAsync(accountId, hatedAfterId, 2, AfterSplit);

        await using var db = Fixture.CreateContext(new AccountContext());
        var personalisation = new PersonalisationContext(db, Dislikes(db));
        var anchors = await personalisation.AnchorCandidatesAsOfAsync(accountId, SplitAt, CancellationToken.None);
        var names = anchors.Select(a => a.Name).ToList();

        Assert.Contains("Loved Before", names);
        Assert.DoesNotContain("Loved After", names);
        Assert.DoesNotContain("Hated After", names);

        // …and the live method, on the same data, DOES see them. Without this the test above would
        // pass just as happily against a query that returned nothing at all.
        var live = await personalisation.AnchorCandidatesAsync(
            accountId, await Dislikes(db).GetProfileAsync(accountId, CancellationToken.None), CancellationToken.None);
        Assert.Contains("Loved After", live.Select(a => a.Name));
    }

    [Fact]
    public async Task As_of_neighbours_never_include_a_title_rated_after_the_split()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // The candidate sits at dim 0. Both rated titles sit essentially on top of it, so the only
        // thing that can keep the post-split one out of a k=3 scan is the as-of filter.
        var candidateId = await SeedTitleAsync("Candidate", UnitVector(0));
        var beforeId = await SeedTitleAsync("Neighbour Before", NearUnit(0, 0.01f));
        await SeedRatingAsync(accountId, beforeId, 8, BeforeSplit);
        var afterId = await SeedTitleAsync("Neighbour After", NearUnit(0, 0.02f));
        await SeedRatingAsync(accountId, afterId, 3, AfterSplit);

        await using var db = Fixture.CreateContext(new AccountContext());
        var finder = new NeighbourFinder(db);

        var asOf = await finder.FindAsync(
            accountId, [candidateId], new NeighbourFinder.Options(AsOf: SplitAt), CancellationToken.None);
        var asOfNames = asOf[candidateId].Select(n => n.Name).ToList();
        Assert.Contains("Neighbour Before", asOfNames);
        Assert.DoesNotContain("Neighbour After", asOfNames);

        // Serving mode (AsOf null) sees both — the filter is doing the work, not an empty table.
        var serving = await finder.FindAsync(
            accountId, [candidateId], new NeighbourFinder.Options(), CancellationToken.None);
        var servingNames = serving[candidateId].Select(n => n.Name).ToList();
        Assert.Contains("Neighbour Before", servingNames);
        Assert.Contains("Neighbour After", servingNames);
    }

    [Fact]
    public async Task As_of_declined_line_never_carries_a_reaction_created_after_the_split()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var beforeId = await SeedTitleAsync("Declined Before", UnitVector(0));
        var afterId = await SeedTitleAsync("Declined After", UnitVector(1));
        await SeedReactionAsync(accountId, beforeId, BeforeSplit);
        await SeedReactionAsync(accountId, afterId, AfterSplit);

        await using var db = Fixture.CreateContext(new AccountContext());
        var personalisation = new PersonalisationContext(db, Dislikes(db));
        var line = await personalisation.DeclinedLineAsOfAsync(accountId, SplitAt, CancellationToken.None);

        Assert.NotNull(line);
        Assert.Contains("Declined Before", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Declined After", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revoked_reaction_is_still_declined_as_of_a_point_before_the_revocation()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The other direction of the same cut, and the one an "everything before asOf" filter gets
        // wrong: a reaction created before the split and revoked AFTER it was live at the split, so
        // the as-of line must still carry it. Cutting on CreatedAt alone would be right here by
        // accident; cutting on RevokedAt <= asOf would be wrong.
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync("Declined Then Revoked", UnitVector(0));
        await SeedReactionAsync(accountId, titleId, BeforeSplit, revokedAt: AfterSplit);

        await using var db = Fixture.CreateContext(new AccountContext());
        var personalisation = new PersonalisationContext(db, Dislikes(db));
        var line = await personalisation.DeclinedLineAsOfAsync(accountId, SplitAt, CancellationToken.None);

        Assert.NotNull(line);
        Assert.Contains("Declined Then Revoked", line, StringComparison.Ordinal);
    }

    private static DislikeSignal Dislikes(Infrastructure.Persistence.ReelDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()));

    private static float[] UnitVector(int index)
    {
        var v = new float[1536];
        v[EmbeddingDimensionBase + index] = 1f;
        return v;
    }

    private static float[] NearUnit(int index, float noise)
    {
        var v = UnitVector(index);
        v[EmbeddingDimensionBase + index + 1] = noise;
        return v;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xrefleak-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, float[] embedding)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            Language = TestLanguage,
            PosterPath = "/p.jpg",
            Genres = ["drama", _scope],
            TmdbPopularity = 1m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = title.Id,
            Embedding = new Vector(embedding),
            EmbeddingModel = "test",
            SourceTextHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId, short rating, DateTime at)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = at,
            // The immutable axis. Written equal to RatedAt here, as the backfill does.
            FirstRatedAt = at,
            SyncedAt = at,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedReactionAsync(Guid accountId, Guid titleId, DateTime at, DateTime? revokedAt = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = ReactionKind.NotInterested,
            CreatedAt = at,
            RevokedAt = revokedAt,
        });
        await db.SaveChangesAsync();
    }
}
