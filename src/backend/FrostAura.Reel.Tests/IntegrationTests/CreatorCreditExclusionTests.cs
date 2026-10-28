using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// A show's creators (HREV2-D-02) are stored as <see cref="CreditRole.Creator"/> credits, a role
/// the trained model never saw. These tests pin that the model's inputs do not move when those
/// credits arrive: the credit features (FeatureVectorBuilder's per-title switch sends any role it
/// does not name to the CAST branch, where a creator's order 0 or 1 passes the top-billed cut) and
/// the per-role person pools that PersonAffinityWriter materialises. Using creators as a model
/// signal is a feature-schema change and a retrain, made deliberately or not at all.
///
/// <para>
/// Isolation: Titles, Persons and TitleCredits are GLOBAL in this collection's database, so every
/// assertion is scoped to the people and titles a test seeded.
/// </para>
/// </summary>
[Collection("postgres")]
public class CreatorCreditExclusionTests(PostgresFixture fixture)
{
    /// <summary>
    /// The creator of an unrated show, at every top-billed order, both as someone the user's taste
    /// already reaches as a director and as someone it reaches ONLY through creator credits: the
    /// show's vector is identical before and after the creator credits land.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task Creator_credits_move_no_feature(int creatorOrder, bool creatorAlsoDirects)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var creator = await SeedPersonAsync();
        var lead = await SeedPersonAsync();

        var rated = new List<Guid>();
        foreach (var rating in new short[] { 10, 9, 9 })
        {
            var show = await SeedShowAsync();
            if (creatorAlsoDirects)
            {
                await CreditAsync(show, creator, CreditRole.Director, null);
            }

            await CreditAsync(show, lead, CreditRole.Actor, 0);
            await RateAsync(accountId, show, rating);
            rated.Add(show);
        }

        var candidate = await SeedShowAsync();
        await CreditAsync(candidate, lead, CreditRole.Actor, 0);
        if (creatorAlsoDirects)
        {
            await CreditAsync(candidate, creator, CreditRole.Director, null);
        }

        var asOf = DateTime.UtcNow;
        var before = await VectorsAsync(accountId, [candidate, .. rated], asOf);

        await CreditAsync(candidate, creator, CreditRole.Creator, creatorOrder);
        foreach (var show in rated)
        {
            await CreditAsync(show, creator, CreditRole.Creator, 0);
        }

        var after = await VectorsAsync(accountId, [candidate, .. rated], asOf);

        foreach (var titleId in before.Keys)
        {
            Assert.Equal(before[titleId], after[titleId]);
        }
    }

    /// <summary>
    /// A creator behind enough rated shows to clear VerifiedPeople.MinRatedTitles gets no
    /// Creator-role affinity row — the row typeahead would rank them by — and a person who also
    /// directed those shows keeps exactly the director row they had.
    /// </summary>
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public async Task Creator_credits_write_no_affinity_rows(bool creatorAlsoDirects, int ratedShows)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var creator = await SeedPersonAsync();
        for (var i = 0; i < ratedShows; i++)
        {
            var show = await SeedShowAsync();
            await CreditAsync(show, creator, CreditRole.Creator, 0);
            if (creatorAlsoDirects)
            {
                await CreditAsync(show, creator, CreditRole.Director, null);
            }

            await RateAsync(accountId, show, (short)(9 - i));
        }

        await RefreshAffinitiesAsync(accountId);

        await using var db = fixture.CreateContext(new AccountContext());
        var rows = await db.PersonAffinities
            .Where(a => a.AccountId == accountId && a.PersonId == creator)
            .ToListAsync();
        Assert.DoesNotContain(rows, r => r.Role == CreditRole.Creator);
        if (creatorAlsoDirects)
        {
            var row = Assert.Single(rows);
            Assert.Equal(CreditRole.Director, row.Role);
            Assert.Equal(ratedShows, row.RatedTitleCount);
        }
        else
        {
            Assert.Empty(rows);
        }

        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        Assert.False(taste.PersonRoleRatings!.ContainsKey((creator, CreditRole.Creator)));
        // The role-blind pool the credit features read counts each DIRECTING credit, never the
        // creator credit beside it.
        Assert.Equal(creatorAlsoDirects ? ratedShows : 0, taste.PersonRatings.GetValueOrDefault(creator)?.Count ?? 0);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, float[]>> VectorsAsync(Guid accountId, IReadOnlyCollection<Guid> titleIds, DateTime asOf)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var builder = new FeatureVectorBuilder(db);
        var taste = await builder.BuildTasteStateAsync(accountId, asOf, default);
        return await builder.BuildAsync(taste, titleIds, asOf, default);
    }

    private async Task RefreshAffinitiesAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        await new PersonAffinityWriter(db, NullLogger<PersonAffinityWriter>.Instance)
            .RefreshAsync(accountId, taste, DateTime.UtcNow, default);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"creator-excl-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedPersonAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = new Person
        {
            Id = Guid.NewGuid(),
            TmdbId = Random.Shared.NextInt64(1_000_000, 9_000_000),
            Name = $"person {Guid.NewGuid():N}",
        };
        db.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task<Guid> SeedShowAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Show,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"show {Guid.NewGuid():N}",
            Genres = ["drama"],
            Language = "en",
            Year = 2012,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task CreditAsync(Guid titleId, Guid personId, CreditRole role, int? order)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleCredits.Add(new TitleCredit
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            PersonId = personId,
            Role = role,
            CastOrder = order,
        });
        await db.SaveChangesAsync();
    }

    private async Task RateAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var ratedAt = DateTime.UtcNow.AddDays(-Random.Shared.Next(30, 400));
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Show,
            Rating = rating,
            RatedAt = ratedAt,
            FirstRatedAt = ratedAt,
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
