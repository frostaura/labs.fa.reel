using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// THE single source of the locked eligibility rule + airtight content filters. Every surface
/// that proposes titles — feed build, hero, rows, typeahead, semantic search, similar-titles —
/// goes through this builder; a surface composing its own predicate is a bug by definition.
///
/// Eligible for DISCOVERY = zero interaction footprint (founder-locked 2026-06-12: "never show
/// things I've already seen"): no watch history at all — even partial; in-progress lives on the
/// continue-watching surface only — no rating (rated implies seen, including titles rated
/// without a logged play), not actively NotInterested, not already saved for later (founder
/// addition 2026-06-19: a title you've put on your watchlist is a decision already made — it
/// lives on Saved, not back in the picks), and inside the account's content-preference rules.
/// Maturity ceilings compare certification ordinals in memory (certification systems don't order
/// in SQL) via <see cref="PassesMaturity"/>.
/// </summary>
public class EligibilityQueryBuilder(IReelDbContext db)
{
    public IQueryable<Title> EligibleTitles(Guid accountId)
    {
        return ContentFilteredTitles(accountId).Where(t =>
            !db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == t.Id)
            && !db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == t.Id)
            && !db.UserTitleReactions.Any(r =>
                r.AccountId == accountId && r.TitleId == t.Id
                && (r.Kind == ReactionKind.NotInterested || r.Kind == ReactionKind.SaveForLater)
                && r.RevokedAt == null));
    }

    /// <summary>
    /// The content-preference rules alone — no watched/NotInterested logic. For surfaces that
    /// legitimately show watched titles (typeahead badges them) but must still honor every
    /// exclusion. Discovery surfaces use <see cref="EligibleTitles"/>, which composes this.
    /// </summary>
    public IQueryable<Title> ContentFilteredTitles(Guid accountId)
    {
        var query = db.Titles.Where(t =>
            !db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre && t.Genres.Contains(f.Value))
            && !db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.ExcludeKeyword
                && (t.Name.ToLower().Contains(f.Value.ToLower())
                    || (t.Overview != null && t.Overview.ToLower().Contains(f.Value.ToLower()))
                    // A TMDB keyword phrase containing the filter word is a real match
                    // ("lgbt themes" ⊇ "lgbt"). The old reverse arm — filter.StartsWith(keyword) with a
                    // length≥4 guard — over-excluded: filter "warehouse" swallowed the unrelated "ware"
                    // keyword. Dropped; when filter and keyword are equal this forward Contains already
                    // matches, so the only thing lost was the fragile longer-form prefix.
                    || t.Keywords.Any(k => k.Contains(f.Value.ToLower())))));

        // Include-genres are an allowlist when present: at least one must match.
        query = query.Where(t =>
            !db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.IncludeGenre)
            || t.Genres.Any(g => db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.IncludeGenre && f.Value == g)));

        // Language allow-list. No rows configured ⇒ English-only by default (founder ask
        // 2026-06-30: "English only"). A sentinel "*" row opts every language back in. A title with
        // an unknown (null) language reads as not-English and is excluded unless "*" is set — the
        // founder asked for English, not "English plus anything we failed to tag".
        query = query.Where(t =>
            db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.Language && f.Value == "*")
            || (t.Language != null
                && db.ContentFilters.Any(f =>
                    f.AccountId == accountId && f.Kind == FilterKind.Language && f.Value != "*" && f.Value == t.Language.ToLower()))
            || (!db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.Language)
                && t.Language == "en"));

        return query;
    }

    /// <summary>
    /// The account's certification ceiling (null = unrestricted) — the one lookup every search
    /// surface shares before applying <see cref="PassesMaturity"/>. Kept here (not re-queried per
    /// endpoint) so typeahead, /semantic, lexical, and Ask Reel all read the ceiling the same way;
    /// the feed path already threads <c>account.Settings.MaturityCeiling</c> from the loaded account.
    /// </summary>
    public async Task<string?> MaturityCeilingAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        return account?.Settings.MaturityCeiling;
    }

    /// <summary>
    /// Drops over-ceiling titles from an already-materialized sequence — the shared in-memory gate
    /// every search surface applies (certification ordinals don't order in SQL, so this can't push
    /// into the query). Callers over-fetch before calling so a restricted profile still lands enough
    /// survivors.
    /// </summary>
    public static IEnumerable<Title> WithinMaturity(IEnumerable<Title> titles, string? maturityCeiling) =>
        maturityCeiling is null ? titles : titles.Where(t => PassesMaturity(t, maturityCeiling));

    /// <summary>In-memory maturity gate — apply after materializing (certification ordering is app logic).</summary>
    public static bool PassesMaturity(Title title, string? maturityCeiling)
    {
        if (string.IsNullOrWhiteSpace(maturityCeiling))
        {
            return true;
        }

        var ceiling = Ml.TasteMath.CertificationOrdinal(maturityCeiling);
        if (ceiling <= 0)
        {
            return true;
        }

        var ordinal = Ml.TasteMath.CertificationOrdinal(title.Certification);
        // Unknown certifications pass only when the ceiling allows adult content — a kids'
        // profile must never see an unrated title by accident.
        return ordinal == 0 ? ceiling >= 4 : ordinal <= ceiling;
    }
}
