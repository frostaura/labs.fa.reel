using System.Text.Json;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Saved;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class DigestEndpoints
{
    public static void MapDigestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/digest").RequireAccount();

        // The current ISO week's digest as feed-shaped cards; { items: [] } until the weekly
        // build has run — the Home rail hides itself on empty rather than erroring.
        // degradedReason is non-null exactly when the build relaxed the "high for you" floor, so
        // the rail can stop claiming a bar its cards did not clear.
        group.MapGet("/current", async (IReelDbContext db, EligibilityQueryBuilder eligibility, ExposureLog exposures, LensService lens, TasteFactsBuilder tasteFacts, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var nowUtc = DateTime.UtcNow;
            var weekStart = DigestWeek.MondayOf(nowUtc);
            var digest = await db.AccountDigests
                .FirstOrDefaultAsync(d => d.AccountId == account.Id && d.WeekStart == weekStart, ct);
            if (digest is null)
            {
                return Results.Ok(new
                {
                    weekStart = (DateOnly?)null,
                    generatedAt = (DateTime?)null,
                    degradedReason = (string?)null,
                    items = Array.Empty<object>(),
                });
            }

            var digestItems = JsonSerializer.Deserialize<List<DigestItem>>(digest.ItemsJson) ?? [];

            // Built on Friday, read all week (XM-04): a card watched, rated, saved, dismissed or
            // dropped since the build, ruled out by a content rule added since, or under a For-you
            // bar raised since, is a suggestion the user has already answered — re-checked here on
            // every read through the same eligibility definition the build used, and never shown.
            var builtIds = digestItems.Select(i => i.TitleId).ToList();
            var stillEligible = builtIds.Count == 0
                ? new HashSet<Guid>()
                : (await eligibility.EligibleTitles(account.Id)
                    .Where(t => builtIds.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToListAsync(ct)).ToHashSet();
            var bar = await ForYouBar.ReadAsync(db, account.Id, ct);
            digestItems = digestItems
                .Where(i => stillEligible.Contains(i.TitleId) && ForYouBar.Clears(i.PredictedRating, bar))
                .ToList();
            var ids = digestItems.Select(i => i.TitleId).ToList();
            var titles = ids.Count == 0
                ? []
                : await db.Titles.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            var providers = await FeedEndpoints.ProvidersForAsync(db, account.Region, ids, nowUtc, ct);

            // Read-only: the digest is rendered, never generated from. The job that built it
            // already asked for lens copy on its top pick.
            var notes = await lens.ResolveManyAsync(
                account.Id, ids, await tasteFacts.CurrentHashAsync(account.Id, nowUtc, ct), ct);

            var cards = new List<FeedEndpoints.FeedCard>();
            var whySources = new List<ExposureWhySource>();
            foreach (var item in digestItems)
            {
                if (!titles.TryGetValue(item.TitleId, out var title)
                    || !EligibilityQueryBuilder.PassesMaturity(title, account.Settings.MaturityCeiling))
                {
                    continue;
                }

                var shown = providers.GetValueOrDefault(title.Id, []);
                if (ReasonCopy(item, shown) is not { } reasonCopy)
                {
                    // An arrival the card cannot back: picked off a cache refresh before IR2S-02, or
                    // its offer has since lapsed. Rendering it would repeat the lie, so it is not shown.
                    continue;
                }

                var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
                var isReleased = releaseDate is null || releaseDate <= nowUtc;
                var (crowd, crowdVotes) = CrowdRating.Resolve(title);
                var note = notes.GetValueOrDefault(title.Id)?.Note;
                // The lens's hook where one exists, and the reason copy where it does not. Both are
                // true statements about why the card is here; the hook is simply the one written
                // for this reader.
                var (whyThis, whySource) = DigestWhyLine(LensGuards.ServableHook(note), reasonCopy);
                whySources.Add(whySource);
                cards.Add(new(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    ForYouBar.Shown(item.PredictedRating), crowd, crowdVotes, whyThis, isReleased,
                    shown,
                    Confidence: null, FloorUsed: null,
                    Hook: LensGuards.ServableHook(note), LensClause: note?.FirstClause(), Mood: note?.Mood));
            }

            // The digest is the one surface whose picks were frozen days ago by a job that did not
            // record an artifact id, so the promise is attributed to whichever model is Active when
            // it is READ. That is a small inaccuracy and it is stated here rather than papered over;
            // with no Active artifact at all the digest is simply not logged, because an exposure
            // that cannot name the model that made the promise proves nothing.
            if (cards.Count > 0)
            {
                var artifactId = await db.ModelArtifacts
                    .Where(a => a.AccountId == account.Id && a.Status == ArtifactStatus.Active)
                    .OrderByDescending(a => a.Version)
                    .Select(a => (Guid?)a.Id)
                    .FirstOrDefaultAsync(ct);
                if (artifactId is { } activeArtifactId)
                {
                    await exposures.RecordAsync(
                        account.Id, ExposureSurface.Digest, feedSnapshotId: null, activeArtifactId, nowUtc,
                        cards.Select((c, i) => new ExposureCard(
                            c.TitleId, i, c.PredictedRating,
                            WhySentence: c.WhyThis, WhySource: whySources[i])).ToList(),
                        ct);
                }
            }

            return Results.Ok(new
            {
                weekStart = (DateOnly?)digest.WeekStart,
                generatedAt = (DateTime?)digest.GeneratedAt,
                degradedReason = DigestDegradedReason(digestItems),
                items = cards,
            });
        });
    }

    /// <summary>
    /// The reason line a digest card may carry, or null when the card must not be shown at all.
    /// <para>
    /// A release is "New this week". An arrival names a provider, and only one the card's own
    /// streaming strip (<paramref name="shown"/>) displays: the provider it arrived on when the
    /// strip still holds it, otherwise the strip's first — "Streaming on X" stays true either way,
    /// and never names something the reader cannot see.
    /// </para>
    /// <para>
    /// Null for an arrival with nothing behind it: no recorded provider means it was built before
    /// IR2S-02, when "arrived" meant "the availability cache was refreshed" (NCIS, 2003, "Just
    /// landed on your services"); an empty strip means the offer is no longer streaming now. In
    /// both the card's only reason for being in a "new this week" rail is gone.
    /// </para>
    /// </summary>
    public static string? ReasonCopy(DigestItem item, IReadOnlyList<FeedEndpoints.CardProvider> shown)
    {
        if (item.Reason != DigestReasons.NewOnProvider)
        {
            return DigestReasons.CopyFor(item.Reason);
        }

        if (string.IsNullOrWhiteSpace(item.Provider) || shown.Count == 0)
        {
            return null;
        }

        var named = shown.FirstOrDefault(p => string.Equals(p.Name, item.Provider.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? shown[0];
        return DigestReasons.CopyFor(item.Reason, named.Name);
    }

    /// <summary>
    /// The digest-level "the floor was relaxed" sentence, read back out of the per-item field the
    /// build writes it into (<see cref="DigestItem.DegradedReason"/>: identical on every item of a
    /// relaxed digest, null on an honest one). Taken from ANY item, not the first rendered card,
    /// because it describes the whole build: a relaxed digest whose first title has since left the
    /// catalogue is still relaxed. Blank is no reason. Dropping it here is what let Home title a
    /// rail of 6.5s "New &amp; high for you this week" under a 7 bar the build had already given up.
    /// </summary>
    public static string? DigestDegradedReason(IEnumerable<DigestItem> items) =>
        items.Select(i => i.DegradedReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

    /// <summary>
    /// A digest card's why-line and how it came to be, decided by ONE predicate so the logged source
    /// can never disagree with the rendered line. The line is the lens hook when one is cached for
    /// this reader — model text, written earlier and served from the lens cache, so
    /// <see cref="ExposureWhySource.Cache"/> — and otherwise the digest's own fixed reason copy,
    /// <see cref="ExposureWhySource.Digest"/>. An empty or whitespace hook is no hook: it used to
    /// render as a blank line while being logged as Digest. And the source used to be Digest for
    /// every card, which booked model-written hooks as deterministic copy and hid them from every
    /// accuracy query that separates model text from templates.
    /// </summary>
    public static (string Line, ExposureWhySource Source) DigestWhyLine(string? hook, string copy) =>
        string.IsNullOrWhiteSpace(hook) ? (copy, ExposureWhySource.Digest) : (hook, ExposureWhySource.Cache);
}
