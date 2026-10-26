using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ingestion;

/// <summary>What one back-fill batch did. <paramref name="Halted"/> means Trakt kept failing and the batch stopped early.</summary>
public sealed record TraktRatingsBackfillResult(int Asked, int Found, int Rated, int Failed, bool Halted);

/// <summary>
/// Gives catalogue titles Trakt's community rating (E-3), one rate-gated call per title on the
/// Backfill lane, resumable because each asked title is stamped.
///
/// <para><b>Why it exists.</b> Trakt's rating reached a title only through the user's own library,
/// so every training title had one (100%) and only 18% of the candidates the model scored did.
/// Without it, candidate predictions collapsed to a narrow band (SD 0.50, max 6.93) that never
/// reached the hero bar — train/serve skew that looked like a weak model. WS-5's acceptance needs
/// ≥ 80% Trakt coverage of scored candidates.</para>
///
/// <para><b>Order.</b> Titles the model has scored for any account first — they are where the skew
/// bites — then by TMDB vote count. A title is asked again only after <see cref="RecheckAfter"/>,
/// found or not.</para>
///
/// <para><b>Safety.</b> The lookup is public catalogue data and sends no user token, so it can never
/// rotate an account's single-use refresh token. A transport failure leaves the title unstamped for
/// the next pass; three in a row halt the batch so an outage is not hammered.</para>
/// </summary>
public class TraktRatingsBackfill(IReelDbContext db, ITraktClient trakt, ILogger<TraktRatingsBackfill> logger)
{
    /// <summary>A title is asked again this long after it was last asked.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(90);

    /// <summary>Consecutive failed calls that stop a batch.</summary>
    public const int HaltAfterConsecutiveFailures = 3;

    /// <summary>TMDB-backed titles with no Trakt rating, not asked inside <see cref="RecheckAfter"/>.</summary>
    public static IQueryable<Title> Pending(IReelDbContext db, DateTime nowUtc)
    {
        var recheckBefore = nowUtc - RecheckAfter;
        return db.Titles.Where(t => t.TmdbId != null
            && t.TraktRating == null
            && (t.TraktRatingCheckedAt == null || t.TraktRatingCheckedAt < recheckBefore));
    }

    /// <summary>
    /// Whether this stack holds a live Trakt connection — the structural guard that keeps the
    /// back-fill off every copied database: the lane and eval recipes null the tokens, and the eval
    /// stack's Trakt client refuses every call besides.
    /// </summary>
    public static Task<bool> HasLiveConnectionAsync(IReelDbContext db, CancellationToken ct) =>
        db.TraktConnections.AnyAsync(c => c.Status == ConnectionStatus.Active && c.AccessTokenEncrypted != "", ct);

    /// <summary>Scored titles (any account) and how many of them carry a Trakt rating — the acceptance measure.</summary>
    public static async Task<(int Scored, int WithTraktRating)> CoverageAsync(IReelDbContext db, CancellationToken ct)
    {
        var scored = db.Titles.Where(t => db.TitleScores.Any(s => s.TitleId == t.Id));
        return (await scored.CountAsync(ct), await scored.CountAsync(t => t.TraktRating != null, ct));
    }

    /// <summary>
    /// Writes what Trakt said onto <paramref name="title"/>: rating and votes when it has them, its
    /// ids when the row has none and <paramref name="mayAdoptTraktId"/> (no other row of the same
    /// kind holds that Trakt id), and the ask stamp either way.
    /// </summary>
    public static void Apply(Title title, TraktCatalogRating? found, bool mayAdoptTraktId, DateTime nowUtc)
    {
        title.TraktRatingCheckedAt = nowUtc;
        if (found is null)
        {
            return;
        }

        if (found.Rating is { } rating)
        {
            title.TraktRating = Math.Round(rating, 3);
            title.TraktVotes = found.Votes;
        }

        if (title.TraktId is null && mayAdoptTraktId)
        {
            title.TraktId = found.TraktId;
            if (string.IsNullOrEmpty(title.TraktSlug) && found.Slug is { } slug)
            {
                title.TraktSlug = slug;
            }
        }

        title.ImdbId ??= found.ImdbId;
    }

    /// <summary>Asks Trakt about up to <paramref name="size"/> pending titles and saves what it learned.</summary>
    public async Task<TraktRatingsBackfillResult> RunBatchAsync(int size, DateTime nowUtc, CancellationToken ct)
    {
        var titles = await Pending(db, nowUtc)
            .OrderByDescending(t => db.TitleScores.Any(s => s.TitleId == t.Id))
            .ThenByDescending(t => t.TmdbVoteCount)
            .ThenBy(t => t.Id)
            .Take(size)
            .ToListAsync(ct);

        var answers = new List<(Title Title, TraktCatalogRating? Found)>(titles.Count);
        int failed = 0, consecutive = 0;
        var halted = false;
        foreach (var title in titles)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                answers.Add((title, await trakt.GetCatalogRatingAsync(title.TmdbId!.Value, title.MediaType == MediaType.Movie, RatePriority.Backfill, ct)));
                consecutive = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failed++;
                if (++consecutive >= HaltAfterConsecutiveFailures)
                {
                    halted = true;
                    logger.LogWarning(ex, "Trakt ratings back-fill halted after {Count} consecutive failures.", consecutive);
                    break;
                }
            }
        }

        // A Trakt id joins the row only if no other row of the same kind already holds it: the
        // (MediaType, TraktId) index is unique, and a duplicate catalogue row is a defect for the
        // dedupe paths to fix, not something this back-fill may trip over.
        var candidateIds = answers.Where(a => a.Found is not null && a.Title.TraktId is null).Select(a => a.Found!.TraktId).Distinct().ToList();
        var held = (await db.Titles
                .Where(t => t.TraktId != null && candidateIds.Contains(t.TraktId.Value))
                .Select(t => new { t.MediaType, TraktId = t.TraktId!.Value })
                .ToListAsync(ct))
            .Select(x => (x.MediaType, x.TraktId))
            .ToHashSet();

        int found = 0, rated = 0;
        foreach (var (title, answer) in answers)
        {
            var mayAdopt = answer is not null && held.Add((title.MediaType, answer.TraktId));
            Apply(title, answer, mayAdopt, nowUtc);
            found += answer is null ? 0 : 1;
            rated += answer?.Rating is null ? 0 : 1;
        }

        await db.SaveChangesAsync(ct);
        return new TraktRatingsBackfillResult(answers.Count, found, rated, failed, halted);
    }
}
