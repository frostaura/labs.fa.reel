using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// GET /api/taste/dislikes on real Postgres. The dislike set is the strongest negative signal the
/// product collects — a recency-decayed, reason-weighted demotion applied on every scoring surface
/// — and it had no list at all: nothing the user could read, audit or undo from one place.
///
/// The contract here: newest first, live rows only (a revoked reaction is gone, not greyed out),
/// paginated with an honest total, and a profile summary reported SEPARATELY from that total,
/// because the demotion is windowed and legitimately built from fewer rows than the user has
/// dismissed in total.
/// </summary>
[Collection("postgres")]
public class DislikeHistoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Live_dislikes_come_back_newest_first_and_a_revoked_one_is_gone()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var now = DateTime.UtcNow;

        var oldest = await SeedTitleAsync();
        var middle = await SeedTitleAsync();
        var newest = await SeedTitleAsync();
        var revoked = await SeedTitleAsync();
        var saved = await SeedTitleAsync();

        await SeedReactionAsync(accountId, oldest, ReactionKind.NotInterested, ReactionReason.Genre, now.AddDays(-30));
        await SeedReactionAsync(accountId, middle, ReactionKind.Dropped, null, now.AddDays(-10));
        await SeedReactionAsync(accountId, newest, ReactionKind.NotInterested, ReactionReason.Tone, now.AddDays(-1));
        await SeedReactionAsync(
            accountId, revoked, ReactionKind.NotInterested, ReactionReason.Genre, now.AddHours(-2), revokedAt: now);
        // SaveForLater is not a dislike; it must never appear in this list.
        await SeedReactionAsync(accountId, saved, ReactionKind.SaveForLater, null, now.AddHours(-1));

        var response = await DislikesAsync(accountId);
        var mine = new[] { oldest, middle, newest, revoked, saved };
        var listed = response.Items.Where(i => mine.Contains(i.TitleId)).ToList();

        Assert.Equal(new[] { newest, middle, oldest }, listed.Select(i => i.TitleId).ToArray());
        Assert.Equal("Tone", listed[0].Reason);
        // Dropping carries no reason — the panel says "stopped watching" rather than inventing one.
        Assert.Equal("Dropped", listed[1].Kind);
        Assert.Null(listed[1].Reason);
        Assert.Equal(3, response.Total);
    }

    [Fact]
    public async Task Pagination_pages_the_history_and_reports_when_it_has_run_out()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var now = DateTime.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var titleId = await SeedTitleAsync();
            ids.Add(titleId);
            await SeedReactionAsync(
                accountId, titleId, ReactionKind.NotInterested, ReactionReason.NotForMe, now.AddDays(-i));
        }

        var first = await DislikesAsync(accountId, offset: 0, limit: 2);
        Assert.Equal(5, first.Total);
        Assert.True(first.HasMore);
        Assert.Equal(ids.Take(2), first.Items.Select(i => i.TitleId));

        var last = await DislikesAsync(accountId, offset: 4, limit: 2);
        Assert.False(last.HasMore);
        Assert.Single(last.Items);
        Assert.Equal(ids[4], last.Items[0].TitleId);
    }

    [Fact]
    public async Task An_out_of_range_page_size_is_clamped_rather_than_trusted()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        Assert.Equal(50, (await DislikesAsync(accountId, offset: 0, limit: 5_000)).Limit);
        Assert.Equal(1, (await DislikesAsync(accountId, offset: 0, limit: 0)).Limit);
        Assert.Equal(0, (await DislikesAsync(accountId, offset: -10, limit: null)).Offset);
    }

    [Fact]
    public async Task The_profile_summary_names_the_genres_and_tones_actually_being_demoted()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var now = DateTime.UtcNow;

        // Two "not my genre" passes on horror titles, one on a western: horror outweighs it.
        var horrorA = await SeedTitleAsync(t => t.Genres = ["horror"]);
        var horrorB = await SeedTitleAsync(t => t.Genres = ["horror"]);
        var western = await SeedTitleAsync(t => t.Genres = ["western"]);
        // A reasonless pass teaches the profile nothing about genre — which is exactly the gap the
        // card-overlay reason picker closes.
        var reasonless = await SeedTitleAsync(t => t.Genres = ["comedy"]);

        await SeedReactionAsync(accountId, horrorA, ReactionKind.NotInterested, ReactionReason.Genre, now.AddDays(-1));
        await SeedReactionAsync(accountId, horrorB, ReactionKind.NotInterested, ReactionReason.Genre, now.AddDays(-2));
        await SeedReactionAsync(accountId, western, ReactionKind.NotInterested, ReactionReason.Genre, now.AddDays(-3));
        await SeedReactionAsync(accountId, reasonless, ReactionKind.NotInterested, null, now.AddDays(-4));

        var response = await DislikesAsync(accountId);

        Assert.Equal("horror", response.Profile.Genres[0].Genre);
        Assert.Contains("western", response.Profile.Genres.Select(g => g.Genre));
        Assert.DoesNotContain("comedy", response.Profile.Genres.Select(g => g.Genre));
        Assert.Equal(DislikeSignal.WindowDays, response.Profile.WindowDays);
        // Four live dislikes, all inside the window, and the total agrees with them.
        Assert.Equal(4, response.Profile.ActiveCount);
        Assert.Equal(4, response.Total);
    }

    [Fact]
    public async Task Another_accounts_dislikes_are_never_listed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var titleId = await SeedTitleAsync();
        await SeedReactionAsync(
            theirs, titleId, ReactionKind.NotInterested, ReactionReason.Genre, DateTime.UtcNow);

        var response = await DislikesAsync(mine);

        Assert.Equal(0, response.Total);
        Assert.Empty(response.Items);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    private async Task<TasteEndpoints.DislikesResponse> DislikesAsync(
        Guid accountId, int? offset = null, int? limit = null)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        var dislikes = new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions()));
        return await TasteEndpoints.BuildDislikesAsync(db, dislikes, accountId, offset, limit, CancellationToken.None);
    }

    private static AccountContext Pinned(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"dislike-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(Action<Title>? configure = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"dislike-fixture {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            CreatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(title);
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedReactionAsync(
        Guid accountId, Guid titleId, ReactionKind kind, ReactionReason? reason, DateTime createdAt,
        DateTime? revokedAt = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = kind,
            Reason = reason,
            CreatedAt = createdAt,
            RevokedAt = revokedAt,
        });
        await db.SaveChangesAsync();
    }
}
