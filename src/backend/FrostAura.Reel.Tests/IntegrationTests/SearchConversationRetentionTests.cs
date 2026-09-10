using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The 90-day Ask Reel conversation sweep, on real Postgres.
///
/// <para>Three properties, and the third is the one that would be catastrophic and silent. The
/// depth is a DATE, not a count. Cards and turns die with their conversation rather than being
/// orphaned — none of those references is a foreign key, so a wrong delete order leaves rows
/// nothing can ever reach again and nothing raises. And <b>an exposure row is never touched</b>:
/// the log is append-only, every accuracy number is computed from it, and a prune that quietly
/// shortened it would leave no evidence that it had.</para>
/// </summary>
[Collection("postgres")]
public class SearchConversationRetentionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Conversations_past_the_cutoff_go_with_their_turns_and_cards()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync("ask-retention");
        var control = await SeedAccountAsync("ask-retention-control");
        var now = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);

        var stale = await SeedConversationAsync(accountId, now.AddDays(-91), cards: 3);
        var fresh = await SeedConversationAsync(accountId, now.AddDays(-89), cards: 2);
        var othersStale = await SeedConversationAsync(control, now.AddDays(-400), cards: 2);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var cutoff = now.AddDays(-SearchConversationRetention.DefaultRetentionDays);
            Assert.Equal(1, await SearchConversationRetention.PruneAsync(db, accountId, cutoff, CancellationToken.None));
        }

        await using var verify = fixture.CreateContext(new AccountContext());
        // The stale one is gone, root and branches.
        Assert.False(await verify.SearchConversations.AnyAsync(c => c.Id == stale));
        Assert.False(await verify.SearchTurns.AnyAsync(t => t.ConversationId == stale));
        Assert.False(await verify.SearchTurnCards.AnyAsync(c => c.ConversationId == stale));

        // The fresh one is untouched, cards included.
        Assert.True(await verify.SearchConversations.AnyAsync(c => c.Id == fresh));
        Assert.Equal(2, await verify.SearchTurnCards.CountAsync(c => c.ConversationId == fresh));

        // And another account's ancient thread is not this account's business to delete.
        Assert.True(await verify.SearchConversations.AnyAsync(c => c.Id == othersStale));
    }

    /// <summary>
    /// THE PROBE: one day either side of the cutoff, and exactly one conversation moves. An
    /// off-by-one in the comparison or in the day arithmetic is invisible to every other assertion
    /// here, and would show up in production as a transcript that vanished a day early.
    /// </summary>
    [Fact]
    public async Task One_day_either_side_of_the_cutoff_moves_exactly_one_conversation()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync("ask-retention-edge");
        var now = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var cutoff = now.AddDays(-SearchConversationRetention.DefaultRetentionDays);

        var justInside = await SeedConversationAsync(accountId, cutoff.AddHours(1), cards: 1);
        var justOutside = await SeedConversationAsync(accountId, cutoff.AddHours(-1), cards: 1);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(1, await SearchConversationRetention.PruneAsync(db, accountId, cutoff, CancellationToken.None));
        }

        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.True(await verify.SearchConversations.AnyAsync(c => c.Id == justInside));
        Assert.False(await verify.SearchConversations.AnyAsync(c => c.Id == justOutside));
    }

    /// <summary>
    /// The sweep never deletes an exposure — not the ones whose <c>SourceTurnId</c> points at the
    /// turns it just removed, and not anyone else's. A dangling <c>SourceTurnId</c> afterwards is
    /// the designed state: the log records that the product served those cards, which stays true
    /// after the transcript is gone.
    /// </summary>
    [Fact]
    public async Task The_sweep_never_deletes_an_exposure()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync("ask-retention-exposures");
        var now = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        var stale = await SeedConversationAsync(accountId, now.AddDays(-200), cards: 2);

        Guid turnId;
        int exposuresBefore;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            turnId = await db.SearchTurns
                .Where(t => t.ConversationId == stale && t.Role == "assistant")
                .Select(t => t.Id)
                .FirstAsync();
            db.ExposureEvents.Add(new ExposureEvent
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = Guid.NewGuid(),
                OccurredAt = now.AddDays(-200),
                Surface = ExposureSurface.AskReel,
                Rank = 0,
                ModelArtifactId = Guid.NewGuid(),
                PredictedRating = 7.25m,
                WhySource = "Llm",
                SourceTurnId = turnId,
            });
            await db.SaveChangesAsync();
            exposuresBefore = await db.ExposureEvents.CountAsync();
        }

        var configuration = new ConfigurationBuilder().Build();
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(1, await SearchConversationRetention.SweepAsync(db, configuration, now, CancellationToken.None));
        }

        await using var verify = fixture.CreateContext(new AccountContext());
        Assert.False(await verify.SearchTurns.AnyAsync(t => t.Id == turnId));
        // Unchanged in total, and the row itself still there — still pointing at a turn that no
        // longer exists, which is exactly right.
        Assert.Equal(exposuresBefore, await verify.ExposureEvents.CountAsync());
        Assert.True(await verify.ExposureEvents.AnyAsync(e => e.SourceTurnId == turnId));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedAccountAsync(string prefix)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"{prefix}-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>One conversation, one exchange, and <paramref name="cards"/> stored cards on the reply.</summary>
    private async Task<Guid> SeedConversationAsync(Guid accountId, DateTime lastTurnAt, int cards)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = "seeded",
            CreatedAt = lastTurnAt,
            LastTurnAt = lastTurnAt,
            TurnCount = 2,
            LastCardsAt = cards > 0 ? lastTurnAt : null,
        };
        db.SearchConversations.Add(conversation);

        var assistant = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Text = "seeded reply",
            CreatedAt = lastTurnAt,
            Sequence = 1,
            ClientTurnId = Guid.NewGuid(),
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Completed,
            CompletedAt = lastTurnAt,
        };
        db.SearchTurns.AddRange(
            new SearchTurn
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                Role = "user",
                Text = "seeded question",
                CreatedAt = lastTurnAt,
                Sequence = 0,
                Kind = SearchTurnKind.Ask,
                Status = SearchTurnStatus.Completed,
                CompletedAt = lastTurnAt,
            },
            assistant);

        for (var rank = 0; rank < cards; rank++)
        {
            db.SearchTurnCards.Add(new SearchTurnCard
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                TurnId = assistant.Id,
                TitleId = Guid.NewGuid(),
                TmdbId = 880000 + rank,
                Rank = rank,
                Blend = 0.5m,
                WhySource = "Templater",
                CreatedAt = lastTurnAt,
            });
        }

        await db.SaveChangesAsync();
        return conversation.Id;
    }
}
