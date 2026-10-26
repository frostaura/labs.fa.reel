using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The why-line the title modal carries. The rule under test is "show the sentence this user was
/// actually shown": the ACTIVE snapshot's persisted line, never a superseded build's, and never
/// another account's. When the title is outside the current build the endpoint falls back to the
/// templater — covered by <see cref="Ml.ExplanationTemplaterTests"/> — and when neither can speak,
/// null, so the modal renders nothing rather than boilerplate.
/// </summary>
[Collection("postgres")]
public class TitleWhyLineTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_active_snapshots_line_wins_over_a_superseded_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync();

        await SeedSnapshotAsync(accountId, titleId, SnapshotStatus.Superseded, "Last week's reason.");
        await SeedSnapshotAsync(accountId, titleId, SnapshotStatus.Active, "Shares the slow-burn dread you love.");

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(
            "Shares the slow-burn dread you love.",
            await TitleEndpoints.SnapshotWhyAsync(db, accountId, titleId, CancellationToken.None));
    }

    [Fact]
    public async Task A_title_outside_the_active_build_has_no_snapshot_line()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var inBuild = await SeedTitleAsync();
        var notInBuild = await SeedTitleAsync();
        await SeedSnapshotAsync(accountId, inBuild, SnapshotStatus.Active, "In the build.");

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Null(await TitleEndpoints.SnapshotWhyAsync(db, accountId, notInBuild, CancellationToken.None));
    }

    [Fact]
    public async Task Another_accounts_line_is_never_borrowed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var titleId = await SeedTitleAsync();
        await SeedSnapshotAsync(theirs, titleId, SnapshotStatus.Active, "Their reason, not mine.");

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Null(await TitleEndpoints.SnapshotWhyAsync(db, mine, titleId, CancellationToken.None));
    }

    [Theory]
    [InlineData("A action pick aligned with your taste.")]
    [InlineData("From the 2010s, a decade you rate highly.")]
    [InlineData("A adventure for a quiet night.")]
    [InlineData("Sharp legal drama, though the series format is a weaker fit for you.")]
    [InlineData("Shares the anchor's gothic dread.")]
    [InlineData("Strong fit with your taste profile.")]
    [InlineData("strong fit with your taste profile")]
    public async Task A_line_the_feed_refuses_to_show_does_not_reappear_in_the_modal(string refused)
    {
        if (!fixture.Available)
        {
            return;
        }

        // Until the deploy purge rebuilds the snapshot, Home blanks these persisted lines; the modal
        // opened from that card must not then show the very sentence the card hid. Absent here
        // means the templater fallback answers instead, exactly as for a blank caption.
        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync();
        await SeedSnapshotAsync(accountId, titleId, SnapshotStatus.Active, refused);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Null(await TitleEndpoints.SnapshotWhyAsync(db, accountId, titleId, CancellationToken.None));
    }

    [Theory]
    [InlineData(
        "Written by Arthur Conan Doyle, like Sherlock: The Abominable Bride, and Strong fit with your taste profile.",
        "Written by Arthur Conan Doyle, like Sherlock: The Abominable Bride.")]
    [InlineData(
        "Shares Twitches Too's theme of magic, and strong fit with your taste profile",
        "Shares Twitches Too's theme of magic.")]
    [InlineData("Shares the anchor's gothic dread, and Strong fit with your taste profile.", null)]
    public async Task A_spliced_generic_tail_is_cut_back_to_the_anchor_clause_the_card_shows(string persisted, string? shown)
    {
        if (!fixture.Available)
        {
            return;
        }

        // The card and the modal read the same persisted line through the same guard, so they agree:
        // the anchor clause alone, or nothing when the clause itself is refused.
        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync();
        await SeedSnapshotAsync(accountId, titleId, SnapshotStatus.Active, persisted);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Equal(shown, await TitleEndpoints.SnapshotWhyAsync(db, accountId, titleId, CancellationToken.None));
        Assert.Equal(shown ?? string.Empty, Application.Ranking.FeedWhyGuard.Servable(persisted));
    }

    [Fact]
    public async Task A_blank_persisted_line_reads_as_absent()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var titleId = await SeedTitleAsync();
        await SeedSnapshotAsync(accountId, titleId, SnapshotStatus.Active, "   ");

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Null(await TitleEndpoints.SnapshotWhyAsync(db, accountId, titleId, CancellationToken.None));
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"why-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(10_000_000, long.MaxValue),
            Name = "Quiet Harbor",
            Year = 2024,
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedSnapshotAsync(Guid accountId, Guid titleId, SnapshotStatus status, string why)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = DateTime.UtcNow,
            Status = status,
        };
        db.FeedSnapshots.Add(snapshot);
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FeedSnapshotId = snapshot.Id,
            Row = FeedRowKind.Hero,
            Rank = 0,
            TitleId = titleId,
            PredictedRating = 8m,
            FinalScore = 8m,
            WhyThisSentence = why,
        });
        await db.SaveChangesAsync();
    }
}
