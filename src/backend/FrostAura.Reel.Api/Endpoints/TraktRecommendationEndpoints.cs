using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Recommendations;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// Home's "Trakt recommends" rail: Trakt's own personalised recommendations, every one the account
/// has not seen, in Trakt's order.
///
/// <para><b>Whose list it is.</b> Trakt's. Reel adds nothing to its selection or its order; it only
/// takes things OUT — whatever the account has watched, rated, saved, declined or dropped, whatever a
/// standing condition or the maturity ceiling rules out — and says how many of each. The "for you"
/// number on a card is still Reel's prediction where the account's model has one, so a card carries
/// no number when there is none, rather than a placeholder that would read as a score.</para>
///
/// <para><b>The "for you" bar.</b> A card whose Reel prediction sits under the account's own
/// minimum is held back, because the bar's promise is "nothing under it is shown" and a visible 5.8
/// under a 7 bar breaks it on its face. A card with no prediction makes no claim the bar could
/// contradict, so it stays — the list is Trakt's, and dropping its unscored half would quietly turn
/// it into Reel's.</para>
///
/// <para>The handler body is a public static method, following <see cref="SavedEndpoints"/>, so
/// tests exercise the exact endpoint semantics without an HTTP host.</para>
/// </summary>
public static class TraktRecommendationEndpoints
{
    /// <summary>Unscored cards scored on demand per request, in Trakt's order — Saved's budget.</summary>
    public const int OnDemandScoreBudget = SavedEndpoints.OnDemandScoreBudget;

    /// <summary>One card. The <see cref="FeedEndpoints.FeedCard"/> fields the rail renders, with an honest null prediction.</summary>
    /// <param name="PredictedRating">Reel's "for you" number (display-rounded), or null where the model has none.</param>
    /// <param name="TraktRank">The card's 1-based position in Trakt's list for its media type.</param>
    public sealed record TraktRecommendationCard(
        Guid TitleId, string MediaType, long? TmdbId, string Name, int? Year, int? RuntimeMinutes,
        string? PosterPath, string? BackdropPath, string[] Genres,
        decimal? PredictedRating, decimal? CrowdRating, int? CrowdVotes,
        string WhyThis, bool IsReleased, IReadOnlyList<FeedEndpoints.CardProvider> Providers, int TraktRank);

    /// <summary>The rail's payload: the state it can honestly claim, what Trakt sent, and what was taken out and why.</summary>
    /// <param name="FetchedAt">When Trakt gave this list; null when there is no list.</param>
    /// <param name="ReturnedByTrakt">Movies plus shows Trakt sent.</param>
    /// <param name="Unresolved">Entries Reel cannot open a title page for (no TMDB id).</param>
    /// <param name="HiddenAsSeen">Watched (even partly) or rated.</param>
    /// <param name="HiddenAsSetAside">Saved, "Not for me" or dropped.</param>
    /// <param name="HiddenByConditions">Ruled out by a standing condition or the maturity ceiling.</param>
    /// <param name="HeldBelowBar">Scored under the account's "for you" minimum.</param>
    /// <param name="Bar">That minimum; null when none is set.</param>
    /// <param name="UnscoredRemaining">Cards still without a prediction after this request's scoring budget.</param>
    public sealed record TraktRecommendationsResponse(
        TraktRecommendationsState State,
        DateTime? FetchedAt,
        int ReturnedByTrakt,
        int Unresolved,
        int HiddenAsSeen,
        int HiddenAsSetAside,
        int HiddenByConditions,
        int HeldBelowBar,
        decimal? Bar,
        int UnscoredRemaining,
        IReadOnlyList<TraktRecommendationCard> Items);

    public static void MapTraktRecommendationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feed").RequireAccount();

        // ?retry=true is the rail's Retry: it asks Trakt now even inside the failure cooldown. It never
        // bypasses a fresh answer — that answer is already the newest Trakt has.
        group.MapGet("/trakt-recommendations", async (
            bool? retry,
            IReelDbContext db,
            TraktRecommendationsService recommendations,
            OnDemandScorer scorer,
            ExposureLog exposures,
            HttpContext http,
            CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await GetAsync(account, retry ?? false, db, recommendations, scorer, exposures, DateTime.UtcNow, ct));
        })
        .Produces<TraktRecommendationsResponse>()
        .Produces(StatusCodes.Status401Unauthorized);
    }

    /// <summary>The handler body: Trakt's filtered list, scored within budget, under the bar, as cards.</summary>
    public static async Task<TraktRecommendationsResponse> GetAsync(
        Account account,
        bool retry,
        IReelDbContext db,
        TraktRecommendationsService recommendations,
        OnDemandScorer scorer,
        ExposureLog exposures,
        DateTime nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(recommendations);
        var result = await recommendations.GetAsync(account, retry, nowUtc, ct);
        var titles = result.Titles;
        var ids = titles.Select(t => t.Id).ToList();

        var scores = await SavedEndpoints.ActiveArtifactScoresAsync(db, account.Id, ids, ct);
        var unscored = ids.Where(id => !scores.ContainsKey(id)).ToList();
        if (unscored.Count > 0)
        {
            // Trakt's order is the priority: the cards at the head of the rail are the ones on screen.
            var fresh = await scorer.ScoreAsync(account.Id, unscored.Take(OnDemandScoreBudget).ToList(), nowUtc, ct);
            foreach (var (titleId, predicted) in fresh)
            {
                scores[titleId] = predicted;
            }
        }

        var bar = await ForYouBar.ReadAsync(db, account.Id, ct);
        var providers = await FeedEndpoints.ProvidersForAsync(db, account.Region, ids, nowUtc, ct);
        var ranks = result.Ranks;

        var cards = new List<TraktRecommendationCard>(titles.Count);
        var heldBelowBar = 0;
        foreach (var title in titles)
        {
            decimal? predicted = scores.TryGetValue(title.Id, out var p) ? p : null;
            if (predicted is { } value && !ForYouBar.Clears(value, bar))
            {
                heldBelowBar++;
                continue;
            }

            var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
            var (crowd, crowdVotes) = CrowdRating.Resolve(title);
            cards.Add(new TraktRecommendationCard(
                title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                title.PosterPath, title.BackdropPath, title.Genres,
                predicted is { } shown ? ForYouBar.Shown(shown) : null, crowd, crowdVotes,
                // No why-line: Reel did not choose this title, Trakt did, and the rail's heading says so.
                // A sentence here would be Reel explaining a pick it never made.
                WhyThis: string.Empty,
                IsReleased: releaseDate is null || releaseDate <= nowUtc,
                providers.GetValueOrDefault(title.Id, []),
                ranks.GetValueOrDefault(title.Id)));
        }

        await RecordExposuresAsync(account.Id, cards, db, exposures, nowUtc, ct);

        return new TraktRecommendationsResponse(
            result.State,
            result.FetchedAt,
            result.ReturnedByTrakt,
            result.Unresolved,
            result.HiddenAsSeen,
            result.HiddenAsSetAside,
            result.HiddenByConditions,
            heldBelowBar,
            bar,
            ids.Count(id => !scores.ContainsKey(id)),
            cards);
    }

    /// <summary>
    /// The exposure log records promises, and the promise on this rail is the "for you" number — so
    /// only cards that carry one are logged, against the Active artifact that made it. With no Active
    /// artifact nothing is logged, as on the digest: an exposure that cannot name its model proves
    /// nothing. The why-line is empty by design and recorded as <see cref="ExposureWhySource.None"/>.
    /// </summary>
    private static async Task RecordExposuresAsync(
        Guid accountId, IReadOnlyList<TraktRecommendationCard> cards, IReelDbContext db, ExposureLog exposures,
        DateTime nowUtc, CancellationToken ct)
    {
        // Rank is the card's position on the rail, counted over every card served, scored or not.
        var promised = cards
            .Select((c, rank) => (Card: c, Rank: rank))
            .Where(x => x.Card.PredictedRating is not null)
            .ToList();
        if (promised.Count == 0)
        {
            return;
        }

        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (artifactId is not { } activeArtifactId)
        {
            return;
        }

        await exposures.RecordAsync(
            accountId, ExposureSurface.TraktRecommendations, feedSnapshotId: null, activeArtifactId, nowUtc,
            promised.Select(x => new ExposureCard(
                x.Card.TitleId, x.Rank, x.Card.PredictedRating!.Value,
                WhySentence: x.Card.WhyThis, WhySource: ExposureWhySource.None)).ToList(),
            ct);
    }
}
