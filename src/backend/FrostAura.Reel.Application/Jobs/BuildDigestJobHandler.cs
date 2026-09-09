using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Builds the account's weekly digest: eligible titles that either released in the trailing
/// 7 days or newly landed on one of the account's flatrate providers, scored by the personal
/// model, floored at 7.0 predicted — softly, see <see cref="MinPredictedRating"/> — top 10 kept.
/// Upserts <see cref="AccountDigest"/> on
/// (AccountId, WeekStart = Monday of the current ISO week, UTC) so re-runs within a week
/// refresh rather than duplicate. When the digest has items, notifies every Web Push
/// subscription the account holds — dead endpoints (404/410) are deleted on the spot and
/// repeat failures pruned at 5.
/// </summary>
public sealed class BuildDigestJobHandler(
    IReelDbContext db,
    EligibilityQueryBuilder eligibility,
    OnDemandScorer scorer,
    IWebPushSender pushSender,
    ILogger<BuildDigestJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.BuildDigest;

    /// <summary>
    /// The "high for you" bar. SOFT, like the feed's own MinPredictedRating: a hard 7.0 sat above
    /// the catalogue's p90 prediction (~6.9), so the weekly digest shipped zero items, the Home
    /// rail hid itself, and the push read "0 new high-for-you titles" — a floor nothing could clear
    /// is a blank page, not a standard. When fewer than <see cref="ThinDigestCount"/> candidates
    /// clear it the floor drops to <see cref="RelaxedMinPredictedRating"/> and the digest SAYS SO,
    /// exactly as BuildFeedJobHandler does. Content rules are unaffected: the trailing-7-day window,
    /// the zero-interaction-footprint rule and last week's suppression all still bind absolutely.
    /// </summary>
    private const decimal MinPredictedRating = 7.0m;

    /// <summary>The relaxed floor. Still a floor — below this a title is not worth a push.</summary>
    private const decimal RelaxedMinPredictedRating = 6.0m;

    /// <summary>A digest thinner than this is treated as a failure to find, not a verdict.</summary>
    private const int ThinDigestCount = 5;

    private const int MaxItems = 10;

    /// <summary>On-demand scoring budget per build: only the most popular unscored candidates.</summary>
    private const int OnDemandScoreBudget = 30;

    private const int MaxPushFailures = 5;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("BuildDigest requires an account.");
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var now = DateTime.UtcNow;
        var windowStart = now.AddDays(-7);
        var weekStart = DigestWeek.MondayOf(now);
        var previousWeekStart = weekStart.AddDays(-7);

        job.ProgressMessage = "Collecting this week's arrivals";

        // "New on provider" must not resurface what last week's digest already showed — a title
        // whose availability row merely got re-fetched would otherwise reappear every week.
        var previous = await db.AccountDigests
            .FirstOrDefaultAsync(d => d.AccountId == accountId && d.WeekStart == previousWeekStart, ct);
        var previousTitleIds = previous is null
            ? []
            : (JsonSerializer.Deserialize<List<DigestItem>>(previous.ItemsJson) ?? [])
                .Select(i => i.TitleId)
                .ToHashSet();

        // Poster + TmdbId are the card-renderability gate every feed surface applies.
        var newReleaseIds = await eligibility.EligibleTitles(accountId)
            .Where(t => t.PosterPath != null && t.TmdbId != null)
            .Where(t => t.MediaType == MediaType.Movie
                ? t.ReleasedAt != null && t.ReleasedAt >= windowStart && t.ReleasedAt <= now
                : t.FirstAiredAt != null && t.FirstAiredAt >= windowStart && t.FirstAiredAt <= now)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var newOnProviderIds = await eligibility.EligibleTitles(accountId)
            .Where(t => t.PosterPath != null && t.TmdbId != null)
            .Where(t => db.TitleAvailabilities.Any(a =>
                a.TitleId == t.Id
                && a.Region == account.Region
                && a.Kind == AvailabilityKind.Flatrate
                && a.FetchedAt >= windowStart))
            .Select(t => t.Id)
            .ToListAsync(ct);

        // A brand-new release that also just landed on a provider is a "new-release" — the
        // stronger, more specific reason wins.
        var reasons = new Dictionary<Guid, string>();
        foreach (var id in newReleaseIds)
        {
            reasons[id] = DigestReasons.NewRelease;
        }

        foreach (var id in newOnProviderIds)
        {
            if (!reasons.ContainsKey(id) && !previousTitleIds.Contains(id))
            {
                reasons[id] = DigestReasons.NewOnProvider;
            }
        }

        var candidateIds = reasons.Keys.ToList();
        var scores = new Dictionary<Guid, decimal>();
        if (candidateIds.Count > 0)
        {
            scores = await db.TitleScores
                .Where(s => s.AccountId == accountId && candidateIds.Contains(s.TitleId))
                .Join(
                    db.ModelArtifacts.Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active),
                    s => s.ModelArtifactId,
                    a => a.Id,
                    (s, a) => s)
                .ToDictionaryAsync(s => s.TitleId, s => s.PredictedRating, ct);

            var unscored = candidateIds.Where(id => !scores.ContainsKey(id)).ToList();
            if (unscored.Count > 0)
            {
                var toScore = await db.Titles
                    .Where(t => unscored.Contains(t.Id))
                    .OrderByDescending(t => t.TmdbPopularity)
                    .Take(OnDemandScoreBudget)
                    .Select(t => t.Id)
                    .ToListAsync(ct);

                job.ProgressMessage = $"Scoring {toScore.Count} fresh candidates";
                var fresh = await scorer.ScoreAsync(accountId, toScore, now, ct);
                foreach (var (titleId, predicted) in fresh)
                {
                    scores[titleId] = predicted;
                }
            }
        }

        var qualified = scores.Where(kv => kv.Value >= MinPredictedRating).ToList();
        var selected = qualified;
        string? degradedReason = null;
        if (qualified.Count < ThinDigestCount)
        {
            var relaxed = scores.Where(kv => kv.Value >= RelaxedMinPredictedRating).ToList();
            if (relaxed.Count > qualified.Count)
            {
                selected = relaxed;

                // Invariant culture, deliberately: these are ratings on the same 0-10 scale the
                // settings screen shows, and a server whose locale renders "7,0" would quote the
                // user a threshold they have never seen. Same reasoning as the feed's degraded copy.
                degradedReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Only {0} of this week's arrivals cleared the {1:0.#} \u201Chigh for you\u201D bar, so it was "
                        + "relaxed to {2:0.#} for this digest.",
                    qualified.Count,
                    MinPredictedRating,
                    RelaxedMinPredictedRating);
            }
        }

        var items = selected
            .OrderByDescending(kv => kv.Value)
            .Take(MaxItems)
            .Select(kv => new DigestItem(kv.Key, Math.Round(kv.Value, 2), reasons[kv.Key], degradedReason))
            .ToList();

        var digest = await db.AccountDigests
            .FirstOrDefaultAsync(d => d.AccountId == accountId && d.WeekStart == weekStart, ct);
        if (digest is null)
        {
            digest = new AccountDigest { Id = Guid.NewGuid(), AccountId = accountId, WeekStart = weekStart };
            db.AccountDigests.Add(digest);
        }

        digest.GeneratedAt = now;
        digest.ItemsJson = JsonSerializer.Serialize(items);
        job.ProgressMessage = degradedReason is null
            ? $"Digest ready: {items.Count} picks"
            : $"Digest ready: {items.Count} picks (floor relaxed)";
        await db.SaveChangesAsync(ct);

        // Never a push with nothing in it. "0 new high-for-you titles" is what the hard floor used
        // to deliver every week, and it teaches the user to ignore the notification entirely.
        if (items.Count > 0)
        {
            await NotifyAsync(accountId, items, now, ct);
        }
        else
        {
            logger.LogInformation(
                "Digest for {AccountId} is empty even at the relaxed {Floor:0.#} floor — no push sent.",
                accountId, RelaxedMinPredictedRating);
        }
    }

    /// <summary>
    /// The web-push payload for a built digest. Pure and internal so the copy is unit-testable
    /// without a database or a push service.
    ///
    /// <para>Title: the top pick by name with its predicted rating, plus how many others are
    /// waiting — "Sicario: Day of the Soldado — 8.2 for you, plus 9 more this week". A digest of
    /// one drops the "plus" clause rather than saying "plus 0 more".</para>
    ///
    /// <para>Body: the top item's own why — or, when the "high for you" floor had to be relaxed,
    /// the degraded reason instead, because a push that oversells a relaxed digest is exactly the
    /// dishonesty the relaxed floor exists to avoid.</para>
    ///
    /// <para>Url: the deep link to that title. Falls back to "/" only when the title row is missing
    /// or carries no TMDB id, which is the one case the route cannot express.</para>
    ///
    /// <para>Invariant culture on the rating, deliberately: the same reasoning as the degraded
    /// copy above — a server whose locale renders "8,2" would quote a number the user has never
    /// seen anywhere else in the product.</para>
    /// </summary>
    internal static DigestPushPayload BuildPushPayload(
        string? topName, string? topMediaType, long? topTmdbId, DigestItem top, int itemCount)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var rating = Math.Round(top.PredictedRating, 1).ToString("0.0", culture);
        var others = itemCount - 1;

        var title = topName is { Length: > 0 } name
            ? others > 0
                ? string.Format(culture, "{0} \u2014 {1} for you, plus {2} more this week", name, rating, others)
                : string.Format(culture, "{0} \u2014 {1} for you", name, rating)
            : $"Your week in Reel: {itemCount} new high-for-you titles";

        var url = topMediaType is { Length: > 0 } mediaType && topTmdbId is { } tmdbId
            ? $"/title/{mediaType.ToLowerInvariant()}/{tmdbId}"
            : "/";

        return new DigestPushPayload(title, top.DegradedReason ?? DigestReasons.CopyFor(top.Reason), url);
    }

    private async Task NotifyAsync(Guid accountId, IReadOnlyList<DigestItem> items, DateTime now, CancellationToken ct)
    {
        var subscriptions = await db.PushSubscriptions
            .Where(s => s.AccountId == accountId)
            .ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            return;
        }

        // The digest itself is personalised; the notification announcing it was two hardcoded
        // strings and a link to the home page. Name the pick, quote its number, and land the tap on
        // the title it is talking about.
        var top = items[0];
        var topTitle = await db.Titles
            .Where(t => t.Id == top.TitleId)
            .Select(t => new { t.Name, t.MediaType, t.TmdbId })
            .FirstOrDefaultAsync(ct);

        var payload = JsonSerializer.Serialize(BuildPushPayload(
            topTitle?.Name,
            topTitle?.MediaType.ToString(),
            topTitle?.TmdbId,
            top,
            items.Count));

        foreach (var subscription in subscriptions)
        {
            var result = await pushSender.SendAsync(
                subscription.Endpoint, subscription.P256dh, subscription.Auth, payload, ct);
            switch (result.Outcome)
            {
                case WebPushOutcome.Success:
                    subscription.LastSuccessAt = now;
                    subscription.FailCount = 0;
                    break;
                case WebPushOutcome.Gone:
                    // The push service says this endpoint no longer exists — not an error.
                    db.PushSubscriptions.Remove(subscription);
                    break;
                case WebPushOutcome.Failure:
                default:
                    subscription.FailCount++;
                    if (subscription.FailCount >= MaxPushFailures)
                    {
                        db.PushSubscriptions.Remove(subscription);
                        logger.LogWarning(
                            "Pruning push subscription {SubscriptionId} after {FailCount} consecutive failures.",
                            subscription.Id, subscription.FailCount);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Web push delivery failed for subscription {SubscriptionId} ({FailCount}/{Max}): {Error}",
                            subscription.Id, subscription.FailCount, MaxPushFailures, result.Error);
                    }

                    break;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// The web-push notification body. Property names are pinned to the lowercase keys the service
/// worker reads — a rename of the C# property must not silently change the wire contract.
/// </summary>
internal sealed record DigestPushPayload(
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("body")] string Body,
    [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url);
