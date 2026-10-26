using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Typeahead;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Api.Endpoints;

public static class TypeaheadEndpoints
{
    /// <summary>The most title ids one scores request may carry (the local leg returns at most eight).</summary>
    public const int MaxScoredIds = 12;

    public static void MapTypeaheadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").RequireAccount();

        // The local lookup (TypeaheadSearch): titles and people ranked exact → prefix → word start →
        // fuzzy, canonical first; excluded titles hidden and counted (FD-5); watched titles badged.
        // No model is loaded here — the predicted-rating badge arrives after the rows from /scores,
        // so the leg answers inside its 120 ms budget. ?global=1 is still honoured for old clients;
        // the box now calls /typeahead/global only when the strong matches are sparse.
        group.MapGet("/typeahead", async (
            string q, IAccountContext accountContext, TypeaheadSearch search, ITmdbClient tmdb,
            CancellationToken ct, string? global = null) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var local = await search.LocalAsync(accountId, q ?? string.Empty, ct);
            var globalLeg = global is "1" or "true"
                ? await search.GlobalAsync(accountId, tmdb, q ?? string.Empty, ct)
                : null;
            return Results.Ok(new
            {
                titles = local.Titles.Select(TitleRow),
                people = local.People.Select(p => new
                {
                    personId = p.PersonId,
                    name = p.Name,
                    knownFor = p.KnownFor,
                    profilePath = p.ProfilePath,
                    tier = TierName(p.Tier),
                }),
                strongMatches = local.StrongMatches,
                hiddenByConditions = local.HiddenByConditions + (globalLeg?.HiddenByConditions ?? 0),
                global = globalLeg?.Rows ?? [],
            });
        });

        // The global leg alone — a TMDB lookup beyond the catalogue, judged by the account's
        // conditions like the local leg. Fired by the box only when the local strong matches are
        // sparse, and without re-running the local leg.
        group.MapGet("/typeahead/global", async (
            string q, IAccountContext accountContext, TypeaheadSearch search, ITmdbClient tmdb, CancellationToken ct) =>
        {
            var leg = await search.GlobalAsync(accountContext.AccountId!.Value, tmdb, q ?? string.Empty, ct);
            return Results.Ok(new { global = leg.Rows, hiddenByConditions = leg.HiddenByConditions });
        });

        // The personal badge for a handful of rows, fetched after the rows render: scored live against
        // the Active model (one model load + one taste build). No model yet ⇒ an empty map.
        group.MapGet("/typeahead/scores", async (
            string ids, IAccountContext accountContext, OnDemandScorer scorer, CancellationToken ct) =>
        {
            var parsed = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .Take(MaxScoredIds)
                .ToList();
            if (parsed.Count == 0)
            {
                return Results.Ok(new { scores = new Dictionary<Guid, decimal>() });
            }

            var scores = await scorer.ScoreAsync(accountContext.AccountId!.Value, parsed, DateTime.UtcNow, ct);
            return Results.Ok(new { scores });
        });

        // The conditions editor's genre chips, derived from the catalogue (H-7): every genre the
        // catalogue tags plus the composite and stand-in slugs, each with the films and shows an
        // exclusion of it would remove, and whether it reaches the bar to be offered. Read-only and
        // catalogue-wide, so not behind the settings PIN.
        group.MapGet("/filter-chips", async (FilterChipCounts chips, CancellationToken ct) =>
            Results.Ok(await chips.CountAsync(ct)));
    }

    private static object TitleRow(TypeaheadTitle t) => new
    {
        titleId = t.TitleId,
        mediaType = t.MediaType,
        tmdbId = t.TmdbId,
        name = t.Name,
        year = t.Year,
        posterPath = t.PosterPath,
        isFullyWatched = t.IsFullyWatched,
        // The badge is fetched after the rows (/typeahead/scores); the field stays for old clients.
        predictedRating = (decimal?)null,
        crowdRating = t.CrowdRating,
        crowdVotes = t.CrowdVotes,
        tier = TierName(t.Tier),
    };

    /// <summary>The tier as the client reads it: "exact", "prefix", "word" or "fuzzy".</summary>
    internal static string TierName(MatchTier tier) => tier switch
    {
        MatchTier.Exact => "exact",
        MatchTier.Prefix => "prefix",
        MatchTier.WordStart => "word",
        _ => "fuzzy",
    };
}
