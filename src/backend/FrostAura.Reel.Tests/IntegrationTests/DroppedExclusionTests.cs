using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Regression for the founder report of 2026-08-07: "I tried to drop the show, and it says dropped,
/// but it's still on my tonight's pick list."
///
/// <see cref="ReactionKind.Dropped"/> was scoped to Continue Watching only, so it was absent from
/// both discovery rules. A dropped title with no watch footprint therefore stayed eligible and was
/// re-selected on every feed build, for ever — and the materialised snapshot was never pruned of it
/// either, so it survived even before a rebuild. The reproduction title (tv/95439) had zero rows in
/// WatchedTitles and UserRatings, which is why the existing watched/rated exclusions never caught it.
///
/// Run on real Postgres because both rules are SQL predicates. Skips when
/// REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class DroppedExclusionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Dropping_a_title_removes_it_from_the_next_feed_build_and_undropping_restores_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var dropped = await SeedTitleAsync();
        var untouched = await SeedTitleAsync();
        var mine = new[] { dropped, untouched };

        // Both eligible to begin with — neither carries any interaction footprint.
        Assert.Equal(mine.ToHashSet(), (await EligibleAsync(accountId, mine)).ToHashSet());

        var reactionId = await DropAsync(accountId, dropped);

        var survivors = (await EligibleAsync(accountId, mine)).ToHashSet();
        Assert.DoesNotContain(dropped, survivors);
        Assert.Contains(untouched, survivors);

        // Un-drop (the DELETE /reactions/dropped path revokes rather than deletes) restores it
        // symmetrically — Dropped is a reversible exclusion, like not-interested and save-for-later.
        await RevokeAsync(reactionId);
        Assert.Equal(mine.ToHashSet(), (await EligibleAsync(accountId, mine)).ToHashSet());
    }

    [Fact]
    public async Task Dropping_a_title_prunes_it_from_an_existing_snapshot_without_a_rebuild()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var dropped = await SeedTitleAsync();
        var untouched = await SeedTitleAsync();
        var mine = new[] { dropped, untouched };

        // GET /api/feed prunes the materialised snapshot through this exact call, so the picks list
        // is correct the moment the drop lands rather than at the next build.
        Assert.Empty(await InteractedAsync(accountId, mine));

        var reactionId = await DropAsync(accountId, dropped);

        Assert.Equal([dropped], await InteractedAsync(accountId, mine));

        await RevokeAsync(reactionId);
        Assert.Empty(await InteractedAsync(accountId, mine));
    }

    [Fact]
    public async Task The_snapshot_pruner_and_the_build_rule_agree_on_every_exclusion_kind()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The defect was one rule learning about Dropped and the other not. Pin the two together:
        // for every exclusion the account can express, "pruned from the snapshot" and "excluded from
        // the next build" must be the same answer.
        var accountId = await SeedAccountAsync();
        var notInterested = await SeedTitleAsync();
        var saved = await SeedTitleAsync();
        var droppedTitle = await SeedTitleAsync();
        var watched = await SeedTitleAsync();
        var rated = await SeedTitleAsync();
        var clean = await SeedTitleAsync();
        var mine = new[] { notInterested, saved, droppedTitle, watched, rated, clean };

        await ReactAsync(accountId, notInterested, ReactionKind.NotInterested);
        await ReactAsync(accountId, saved, ReactionKind.SaveForLater);
        await ReactAsync(accountId, droppedTitle, ReactionKind.Dropped);
        await SeedWatchedAsync(accountId, watched);
        await SeedRatingAsync(accountId, rated);

        var pruned = (await InteractedAsync(accountId, mine)).ToHashSet();
        var stillEligible = (await EligibleAsync(accountId, mine)).ToHashSet();

        Assert.Equal(mine.Where(id => id != clean).ToHashSet(), pruned);
        Assert.Equal([clean], stillEligible);
        // Same rule, two shapes: nothing may be in both, and together they must cover the pool.
        Assert.Empty(pruned.Intersect(stillEligible));
        Assert.Equal(mine.ToHashSet(), pruned.Union(stillEligible).ToHashSet());
    }

    /// <summary>Eligible ids among <paramref name="mine"/> — the rule the next feed build applies.</summary>
    private async Task<IReadOnlyList<Guid>> EligibleAsync(Guid accountId, IReadOnlyCollection<Guid> mine)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new EligibilityQueryBuilder(db)
            .EligibleTitles(accountId)
            .Where(t => mine.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync();
    }

    /// <summary>Ids GET /api/feed prunes from a materialised snapshot, sorted for stable comparison.</summary>
    private async Task<IReadOnlyList<Guid>> InteractedAsync(Guid accountId, IReadOnlyCollection<Guid> mine)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var interacted = await new EligibilityQueryBuilder(db).InteractedTitleIdsAsync(accountId, mine);
        return interacted.OrderBy(id => id).ToList();
    }

    private async Task<Guid> DropAsync(Guid accountId, Guid titleId) =>
        await ReactAsync(accountId, titleId, ReactionKind.Dropped);

    private async Task<Guid> ReactAsync(Guid accountId, Guid titleId, ReactionKind kind)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var reaction = new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
        };
        db.UserTitleReactions.Add(reaction);
        await db.SaveChangesAsync();
        return reaction.Id;
    }

    private async Task RevokeAsync(Guid reactionId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var reaction = await db.UserTitleReactions.IgnoreQueryFilters().FirstAsync(r => r.Id == reactionId);
        reaction.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = 8,
            RatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"dropped-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Show,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"dropped-fixture {Guid.NewGuid():N}",
            // Eligibility defaults to English-only when no language filter is configured.
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            // Bottom of the popularity order so these fixtures never displace another integration
            // test's top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }
}
