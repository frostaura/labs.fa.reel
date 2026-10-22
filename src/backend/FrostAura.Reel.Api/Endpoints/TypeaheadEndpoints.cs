using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class TypeaheadEndpoints
{
    public static void MapTypeaheadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").RequireAccount();

        // Typeahead with the personal lens: watched titles appear (badged), content filters and the
        // maturity ceiling are airtight, and the shortlist is scored LIVE against the Active model so
        // the best-for-you matches float up rather than the merely-popular ones.
        group.MapGet("/typeahead", async (
            string q, IReelDbContext db, IAccountContext accountContext,
            EligibilityQueryBuilder eligibility, OnDemandScorer scorer, ITmdbClient tmdb,
            CancellationToken ct, string? global = null) =>
        {
            var accountId = accountContext.AccountId!.Value;
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            {
                return Results.Ok(new
                {
                    titles = Array.Empty<object>(),
                    people = Array.Empty<object>(),
                    global = Array.Empty<GlobalTypeaheadRow>(),
                });
            }

            var term = q.Trim();
            var maturityCeiling = await eligibility.MaturityCeilingAsync(accountId, ct);

            // ContentFilteredTitles (not EligibleTitles): watched titles legitimately appear here
            // badged — but every exclusion still applies, airtight. Over-fetch, then apply the
            // in-memory maturity gate (certifications don't order in SQL) and keep 8, so a
            // restricted profile still lands a full shortlist instead of a starved one.
            //
            // Match on substring OR trigram similarity. Substring alone is exact-spelling-only:
            // "inceptoin" and "spiderman" both returned literally nothing, because ILIKE '%term%'
            // cannot see past a transposed letter or a missing hyphen. pg_trgm has been installed
            // and declared in every migration since the project started — and is half the reason
            // this project deviates from the house postgres image to pgvector/pgvector:pg16 — but
            // nothing ever queried it. Exact substring still wins the ordering, so a correctly
            // spelled query is unaffected; fuzzy matches only fill in behind it.
            var matched = await eligibility.ContentFilteredTitles(accountId)
                .Where(t => EF.Functions.ILike(t.Name, $"%{term}%") || EF.Functions.TrigramsAreSimilar(t.Name, term))
                .Where(t => !db.UserTitleReactions.Any(r =>
                    r.AccountId == accountId && r.TitleId == t.Id && r.Kind == ReactionKind.NotInterested && r.RevokedAt == null))
                .OrderByDescending(t => EF.Functions.ILike(t.Name, $"%{term}%"))
                .ThenByDescending(t => EF.Functions.TrigramsSimilarity(t.Name, term))
                .ThenByDescending(t => t.TmdbPopularity)
                .Take(24)
                .ToListAsync(ct);

            var candidates = EligibilityQueryBuilder.WithinMaturity(matched, maturityCeiling).Take(8).ToList();
            var candidateIds = candidates.Select(t => t.Id).ToList();

            // Relevance tier per candidate, recomputed here because the SQL ordering is lost the
            // moment these are re-sorted by predicted rating below. 2 = the term appears verbatim,
            // 1 = a close fuzzy match, 0 = a distant one.
            //
            // This exists because fuzzy matching widened the candidate set: before trigram search,
            // every row was an exact substring match and sorting the survivors purely by predicted
            // rating was harmless. Now a loosely-similar title can outrank the one the user
            // actually typed — "spiderman" surfaced Superman above Spider-Man, because Superman
            // simply scored better for this account. Relevance has to dominate for a LOOKUP;
            // the personal lens is the tiebreak within a tier, not the primary key.
            var tiers = candidates.ToDictionary(
                t => t.Id,
                t => t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ? 2
                    : TitleSimilarity(t.Name, term) >= 0.5 ? 1
                    : 0);

            // Live per-user scoring of the ≤8 survivors against the Active artifact (cheap: one model
            // load + one taste build). No model yet ⇒ empty map ⇒ order falls back to popularity.
            var predicted = await scorer.ScoreAsync(accountId, candidateIds, DateTime.UtcNow, ct);
            var watchedFull = (await db.WatchedTitles
                .Where(w => w.AccountId == accountId && candidateIds.Contains(w.TitleId) && w.IsFullyWatched)
                .Select(w => w.TitleId)
                .ToListAsync(ct)).ToHashSet();

            var titles = candidates
                .OrderByDescending(t => tiers[t.Id])
                .ThenByDescending(t => predicted.TryGetValue(t.Id, out var p) ? p : decimal.MinValue)
                .ThenByDescending(t => t.TmdbPopularity ?? 0)
                .Select(t => new
                {
                    titleId = t.Id,
                    mediaType = t.MediaType.ToString(),
                    tmdbId = t.TmdbId,
                    name = t.Name,
                    year = t.Year,
                    posterPath = t.PosterPath,
                    isFullyWatched = watchedFull.Contains(t.Id),
                    predictedRating = predicted.TryGetValue(t.Id, out var p) ? (decimal?)p : null,
                    crowdRating = CrowdRating.For(t),
                    crowdVotes = CrowdRating.VotesFor(t),
                })
                .ToList();

            var people = await db.Persons
                .Where(p => EF.Functions.ILike(p.Name, $"%{term}%") || EF.Functions.TrigramsAreSimilar(p.Name, term))
                .OrderByDescending(p => EF.Functions.ILike(p.Name, $"%{term}%"))
                .ThenByDescending(p => EF.Functions.TrigramsSimilarity(p.Name, term))
                .Take(4)
                .Select(p => new { personId = p.Id, name = p.Name, knownFor = p.KnownForDepartment, profilePath = p.ProfilePath })
                .ToListAsync(ct);

            // The global leg — LOOKUP beyond the ingested catalogue, opt-in via ?global=1. "1"
            // rather than bool binding because the frontend sends &global=1 and minimal-API bool
            // parsing rejects it. Runs AFTER the local shortlist so its (isMovie, tmdbId) pairs can
            // be deduped away, and deliberately applies NO eligibility/footprint filtering: this is
            // the same precedent that lets typeahead show watched titles — a lookup answers "does
            // this exist", not "should you watch it".
            IReadOnlyList<GlobalTypeaheadRow> globalRows = global is "1" or "true"
                ? await GlobalLookupAsync(
                    tmdb,
                    term,
                    candidates.Where(t => t.TmdbId is not null)
                        .Select(t => (IsMovie: t.MediaType == MediaType.Movie, TmdbId: t.TmdbId!.Value))
                        .ToHashSet(),
                    ct)
                : [];

            return Results.Ok(new { titles, people, global = globalRows });
        });
    }

    /// <summary>
    /// The typeahead's global TMDB lookup — one movie-search and one show-search page, unioned,
    /// minus anything the local shortlist already shows, poster-less rows dropped (a bare text row
    /// in a poster list reads as broken), popularity-ordered, capped at six. Failure shape is part
    /// of the contract: TMDB being down degrades to an empty global list, never to a failed
    /// typeahead — the local results always survive.
    /// </summary>
    internal static async Task<IReadOnlyList<GlobalTypeaheadRow>> GlobalLookupAsync(
        ITmdbClient tmdb, string term, IReadOnlySet<(bool IsMovie, long TmdbId)> localKeys, CancellationToken ct)
    {
        try
        {
            var batches = await Task.WhenAll(
                tmdb.SearchTitlesAsync(true, term, 1, ct),
                tmdb.SearchTitlesAsync(false, term, 1, ct));
            return batches
                .SelectMany(b => b)
                .GroupBy(i => (i.IsMovie, i.Id))
                .Select(g => g.First())
                .Where(i => !localKeys.Contains((i.IsMovie, i.Id)))
                .Where(i => !string.IsNullOrEmpty(i.PosterPath))
                .OrderByDescending(i => i.Popularity)
                .Take(6)
                .Select(i => new GlobalTypeaheadRow(
                    Source: "tmdb",
                    MediaType: i.IsMovie ? "movie" : "show",
                    TmdbId: i.Id,
                    Name: i.Name,
                    Year: i.ReleasedAt?.Year,
                    PosterPath: i.PosterPath,
                    CrowdRating: i.VoteAverage == 0m ? null : i.VoteAverage,
                    PredictedRating: null))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lookup beyond the catalogue is a bonus, never a dependency — swallow and degrade.
            return [];
        }
    }

    /// <summary>
    /// One "beyond your catalogue" typeahead row. A record rather than an anonymous type so tests
    /// can assert on it; serializes camelCase like every other minimal-API payload here.
    /// </summary>
    internal sealed record GlobalTypeaheadRow(
        string Source,
        string MediaType,
        long TmdbId,
        string Name,
        int? Year,
        string? PosterPath,
        decimal? CrowdRating,
        decimal? PredictedRating);

    /// <summary>
    /// Trigram similarity between a title and a search term, mirroring how pg_trgm computes it:
    /// lowercase, split on non-alphanumerics, pad each word with two leading spaces and one
    /// trailing, then |shared trigrams| / |union|. Reimplemented in memory only because the SQL
    /// ordering is lost once the shortlist is re-sorted by predicted rating — the database remains
    /// the authority on which rows MATCH; this only re-establishes how well, for the tiebreak.
    /// </summary>
    public static double TitleSimilarity(string? candidate, string term)
    {
        var a = Trigrams(candidate);
        var b = Trigrams(term);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        var shared = a.Intersect(b).Count();
        var union = a.Count + b.Count - shared;
        return union == 0 ? 0d : (double)shared / union;
    }

    private static HashSet<string> Trigrams(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        // Every non-alphanumeric is a SEPARATOR, not something to strip out of a word. That
        // distinction is the whole behaviour: pg_trgm reads "Spider-Man" as two words, which is why
        // the live database scores it 0.615 against "spiderman" rather than 1.0. Stripping the
        // hyphen instead would make this helper disagree with the query that selected the rows.
        foreach (var word in value.ToLowerInvariant().Split(
                     value.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = "  " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                result.Add(padded.Substring(i, 3));
            }
        }

        return result;
    }
}
