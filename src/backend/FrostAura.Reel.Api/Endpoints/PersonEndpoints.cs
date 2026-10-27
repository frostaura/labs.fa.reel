using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// Explicit person (actor/director/writer) ratings + the actor page. A person rating is a
/// strong direct taste signal that overrides the derived affinity for that person in the
/// model (see FeatureVectorBuilder). Never synced to Trakt — Trakt has no person rating.
/// </summary>
public static class PersonEndpoints
{
    public record PersonRateRequest(short Rating);

    public static void MapPersonEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/people/{personId:guid}").RequireAccount();

        group.MapPost("/rating", async (
            Guid personId, PersonRateRequest request,
            IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            if (request.Rating is < 1 or > 10)
            {
                return Results.BadRequest(new { error = "rating must be 1-10" });
            }

            var accountId = accountContext.AccountId!.Value;
            if (!await db.Persons.AnyAsync(p => p.Id == personId, ct))
            {
                return Results.NotFound();
            }

            var now = DateTime.UtcNow;
            var existing = await db.UserPersonRatings.FirstOrDefaultAsync(
                r => r.AccountId == accountId && r.PersonId == personId, ct);
            if (existing is null)
            {
                existing = new UserPersonRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    PersonId = personId,
                    RatedAt = now,
                };
                db.UserPersonRatings.Add(existing);
            }

            existing.Rating = request.Rating;
            existing.Source = RatingSource.Reel;
            existing.UpdatedAt = now;
            // RatedAt set once on create; explicit re-rates keep the original time axis.

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { personId, rating = existing.Rating });
        });

        group.MapDelete("/rating", async (
            Guid personId, IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var existing = await db.UserPersonRatings.FirstOrDefaultAsync(
                r => r.AccountId == accountId && r.PersonId == personId, ct);
            if (existing is not null)
            {
                db.UserPersonRatings.Remove(existing);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { personId, rating = (short?)null });
        });

        // Actor page: who they are, the user's explicit rating, the derived affinity, and the
        // user's filmography with their title ratings + predicted scores.
        group.MapGet("/", async (
            Guid personId, IReelDbContext db, IAccountContext accountContext, EligibilityQueryBuilder eligibility,
            CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var person = await db.Persons
                .Where(p => p.Id == personId)
                .Select(p => new { p.Id, p.Name, p.KnownForDepartment, p.ProfilePath })
                .FirstOrDefaultAsync(ct);
            if (person is null)
            {
                return Results.NotFound();
            }

            var userRating = await db.UserPersonRatings
                .Where(r => r.AccountId == accountId && r.PersonId == personId)
                .Select(r => (short?)r.Rating)
                .FirstOrDefaultAsync(ct);

            // Titles this person is credited in (global), decorated with the user's title rating,
            // predicted score (active artifact), and watched flag. Every credit row is kept, so the
            // filmography can say what the person did on each title (F-24): a director's page used
            // to list everything they appear in with nothing to tell the two apart.
            var credits = await db.TitleCredits
                .Where(c => c.PersonId == personId)
                .Select(c => new { c.TitleId, c.Role, c.CharacterName, c.CastOrder })
                .ToListAsync(ct);
            var creditedTitleIds = credits.Select(c => c.TitleId).Distinct().ToList();
            var creditsByTitle = credits.GroupBy(c => c.TitleId).ToDictionary(
                g => g.Key,
                g => (
                    // Director, Writer, Actor — in that order, the order a filmography line reads.
                    Roles: g.Select(c => c.Role).Distinct().OrderBy(r => RoleOrder(r)).Select(r => r.ToString()).ToList(),
                    Character: g.Where(c => c.Role == CreditRole.Actor && !string.IsNullOrWhiteSpace(c.CharacterName))
                        .OrderBy(c => c.CastOrder ?? int.MaxValue)
                        .Select(c => c.CharacterName!.Trim())
                        .FirstOrDefault()));

            // A lookup may show what you have seen, never what you ruled out (XM-01; the airtight
            // lock, and FD-5's default for the other lookup, typeahead): the listing holds out every
            // content rule and the maturity ceiling, and says how many it held out. The derived
            // affinity below still reads every title you rated — it describes the person, and
            // hiding a rated title from a list does not unrate it.
            var visibleIds = (await eligibility.ContentFilteredTitles(accountId)
                    .Where(t => creditedTitleIds.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToListAsync(ct))
                .ToHashSet();
            var maturityCeiling = await eligibility.MaturityCeilingAsync(accountId, ct);

            // The title entity comes back whole and the card shape is built in memory, so the crowd
            // rating is resolved by the one shared resolver rather than a second copy of its fallback
            // rule inlined into SQL.
            var credited = await db.Titles
                .Where(t => creditedTitleIds.Contains(t.Id))
                .Select(t => new
                {
                    Title = t,
                    userRating = db.UserRatings
                        .Where(r => r.AccountId == accountId && r.TitleId == t.Id
                            && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
                        .Select(r => (short?)r.Rating)
                        .FirstOrDefault(),
                    // Active-artifact scores only (mirrors TitleEndpoints.ActiveScoreAsync) — the
                    // newest ScoredAt row can belong to a superseded model and disagree with every
                    // other surface for the same title.
                    predictedRating = db.TitleScores
                        .Where(s => s.AccountId == accountId && s.TitleId == t.Id)
                        .Join(db.ModelArtifacts.Where(a => a.Status == ArtifactStatus.Active),
                            s => s.ModelArtifactId, a => a.Id, (s, a) => (decimal?)s.PredictedRating)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct);

            var shown = credited
                .Where(x => visibleIds.Contains(x.Title.Id)
                    && EligibilityQueryBuilder.PassesMaturity(x.Title, maturityCeiling))
                .Select(x => x.Title.Id)
                .ToHashSet();

            var filmography = credited.Select(x => new
            {
                titleId = x.Title.Id,
                mediaType = x.Title.MediaType.ToString(),
                tmdbId = x.Title.TmdbId,
                name = x.Title.Name,
                year = x.Title.Year,
                posterPath = x.Title.PosterPath,
                tmdbPopularity = x.Title.TmdbPopularity,
                crowdRating = CrowdRating.For(x.Title),
                crowdVotes = CrowdRating.VotesFor(x.Title),
                x.userRating,
                // The badge's one decimal, by the same rule as the feed and title detail (ForYouBar.Shown).
                predictedRating = x.predictedRating is { } predicted ? Domain.Feed.ShownRating.Of(predicted) : (decimal?)null,
                // What they did on it, from the credits only — never guessed from the department.
                roles = creditsByTitle.TryGetValue(x.Title.Id, out var c) ? c.Roles : [],
                character = creditsByTitle.TryGetValue(x.Title.Id, out var a) ? a.Character : null,
            }).ToList();

            // Derived affinity = shrunken mean of the user's ratings of this person's titles.
            var userMean = await db.UserRatings
                .Where(r => r.AccountId == accountId
                    && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
                .Select(r => (double?)r.Rating)
                .AverageAsync(ct) ?? 7d;
            var ratedInFilmography = filmography.Where(f => f.userRating != null).Select(f => (short)f.userRating!).ToList();
            decimal? derivedAffinity = ratedInFilmography.Count > 0
                ? Math.Round(TasteMath.ShrunkenMean(ratedInFilmography, (decimal)userMean), 2)
                : null;

            return Results.Ok(new
            {
                person.Id,
                person.Name,
                department = person.KnownForDepartment,
                person.ProfilePath,
                userRating,
                derivedAffinity,
                ratedTitleCount = ratedInFilmography.Count,
                // Credited titles your conditions (content rules, maturity) hold out of the list.
                hiddenByConditions = filmography.Count(f => !shown.Contains(f.titleId)),
                filmography = filmography
                    .Where(f => shown.Contains(f.titleId))
                    .OrderByDescending(f => f.userRating ?? 0)
                    .ThenByDescending(f => f.tmdbPopularity ?? 0)
                    .Take(40),
            });
        });
    }

    /// <summary>Director first, then writer, then cast; an unknown role last.</summary>
    private static int RoleOrder(CreditRole role) => role switch
    {
        CreditRole.Director => 0,
        CreditRole.Writer => 1,
        CreditRole.Actor => 2,
        _ => 3,
    };
}
