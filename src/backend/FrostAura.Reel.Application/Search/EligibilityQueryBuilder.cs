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
/// lives on Saved, not back in the picks), not Dropped (founder report 2026-08-07: "I tried to
/// drop the show, and it says dropped, but it's still on my tonight's pick list" — Dropped was
/// scoped to Continue Watching, so a dropped title with no watch footprint stayed eligible and was
/// re-selected on every build, for ever; "I stopped watching this" now excludes it from every
/// suggestion surface, and it is reversible via the undrop DELETE), and inside the account's
/// content-preference rules.
/// Maturity ceilings compare certification ordinals in memory (certification systems don't order
/// in SQL) via <see cref="PassesMaturity"/>.
///
/// The content-preference rules applied here: ExcludeGenre, IncludeGenre (an allowlist when any row
/// exists), ExcludeKeyword, Language, MinCrowdRating and the MinYear/MaxYear window. MaxMaturity is
/// the in-memory gate; MinPredictedRating is applied by the scoring surfaces, since it needs a model.
/// </summary>
public class EligibilityQueryBuilder(IReelDbContext db)
{
    /// <param name="ignoreGenreFilters">
    /// Drops ExcludeGenre / IncludeGenre only — every other rule still binds. Set by the genre rails
    /// (<c>FeedRowContext.Genre</c>), where the rail's own genre is the user's explicit request in
    /// that moment and outranks their standing genre preference: without this a rail for a
    /// filtered-out genre can only ever be empty (founder, 2026-08-07: "the filters should cascade
    /// onto the genre specific suggestions too — of course in those cases the genre filters would be
    /// ignored"). Never set it on a general discovery surface.
    /// </param>
    /// <param name="suppressed">
    /// Content rules to leave OUT of the query, on top of <paramref name="ignoreGenreFilters"/>. This
    /// exists for the empty-feed diagnostic (<see cref="Ranking.FeedStarvationDiagnostic"/>), which
    /// answers "which single condition emptied the pool?" by re-counting with one rule dropped at a
    /// time. It must never be used to SERVE titles — a surface that suppresses a rule is showing the
    /// user content they told it not to. Discovery surfaces pass nothing here.
    /// </param>
    public IQueryable<Title> EligibleTitles(
        Guid accountId, bool ignoreGenreFilters = false, IReadOnlySet<FilterKind>? suppressed = null)
    {
        return ContentFilteredTitles(accountId, ignoreGenreFilters, suppressed).Where(t =>
            !db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == t.Id)
            && !db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == t.Id)
            && !db.UserTitleReactions.Any(r =>
                r.AccountId == accountId && r.TitleId == t.Id
                && (r.Kind == ReactionKind.NotInterested
                    || r.Kind == ReactionKind.SaveForLater
                    || r.Kind == ReactionKind.Dropped)
                && r.RevokedAt == null));
    }

    /// <summary>
    /// The eligible title-id set for <paramref name="accountId"/> computed WITHOUT the row-level
    /// tenancy filters, so a session pinned to one account can evaluate another account's
    /// eligibility (Blend needs both participants' sets). This is an EXCLUSION primitive only:
    /// it returns opaque title ids used to narrow a joint pool, and callers must never surface
    /// another tenant's interaction rows, ratings, or filters through it. Same rule as
    /// <see cref="EligibleTitles"/> — one builder, one predicate, two tenancy modes.
    /// </summary>
    public async Task<HashSet<Guid>> EligibleTitleIdsUnscopedAsync(Guid accountId, CancellationToken ct = default)
    {
        var ids = await EligibleTitles(accountId)
            .IgnoreQueryFilters()
            .Select(t => t.Id)
            .ToListAsync(ct);
        return [.. ids];
    }

    /// <summary>
    /// The subset of <paramref name="candidateTitleIds"/> that now carries an interaction footprint —
    /// the SAME rule <see cref="EligibleTitles"/> applies, expressed as a set lookup for surfaces
    /// holding titles that were already materialised (the feed snapshot) instead of a query to filter.
    ///
    /// It exists because the two used to be independent copies, and they drifted: the snapshot pruner
    /// unioned SaveForLater + NotInterested + watched + rated while <see cref="EligibleTitles"/> had
    /// its own list, so when Dropped joined the exclusions on 2026-08-07 only one of them learned
    /// about it. Anything that prunes a materialised feed calls this — do not write a second copy.
    /// </summary>
    public async Task<HashSet<Guid>> InteractedTitleIdsAsync(
        Guid accountId, IReadOnlyCollection<Guid> candidateTitleIds, CancellationToken ct = default)
    {
        if (candidateTitleIds.Count == 0)
        {
            return [];
        }

        var interacted = new HashSet<Guid>();
        interacted.UnionWith(await db.UserTitleReactions
            .Where(r => r.AccountId == accountId && r.RevokedAt == null
                && (r.Kind == ReactionKind.NotInterested
                    || r.Kind == ReactionKind.SaveForLater
                    || r.Kind == ReactionKind.Dropped)
                && candidateTitleIds.Contains(r.TitleId))
            .Select(r => r.TitleId).ToListAsync(ct));
        interacted.UnionWith(await db.WatchedTitles
            .Where(w => w.AccountId == accountId && candidateTitleIds.Contains(w.TitleId))
            .Select(w => w.TitleId).ToListAsync(ct));
        interacted.UnionWith(await db.UserRatings
            .Where(r => r.AccountId == accountId && candidateTitleIds.Contains(r.TitleId))
            .Select(r => r.TitleId).ToListAsync(ct));
        return interacted;
    }

    /// <summary>
    /// The content-preference rules alone — no watched/NotInterested logic. For surfaces that
    /// legitimately show watched titles (typeahead badges them) but must still honor every
    /// exclusion. Discovery surfaces use <see cref="EligibleTitles"/>, which composes this.
    /// </summary>
    /// <param name="ignoreGenreFilters">See <see cref="EligibleTitles"/> — genre rails only.</param>
    /// <param name="suppressed">See <see cref="EligibleTitles"/> — the empty-feed diagnostic only.</param>
    public IQueryable<Title> ContentFilteredTitles(
        Guid accountId, bool ignoreGenreFilters = false, IReadOnlySet<FilterKind>? suppressed = null)
    {
        // Captured as locals so each arm's predicate is a constant EF can fold away, rather than a
        // set lookup it would have to translate.
        var skipExcludeGenre = ignoreGenreFilters || suppressed?.Contains(FilterKind.ExcludeGenre) == true;
        var skipIncludeGenre = ignoreGenreFilters || suppressed?.Contains(FilterKind.IncludeGenre) == true;
        var skipKeyword = suppressed?.Contains(FilterKind.ExcludeKeyword) == true;
        var skipLanguage = suppressed?.Contains(FilterKind.Language) == true;
        var skipCrowd = suppressed?.Contains(FilterKind.MinCrowdRating) == true;
        var skipYears = suppressed?.Contains(FilterKind.MinYear) == true || suppressed?.Contains(FilterKind.MaxYear) == true;

        var query = db.Titles.Where(t =>
            skipExcludeGenre
            || !db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre && t.Genres.Contains(f.Value)));

        query = query.Where(t =>
            skipKeyword
            || !db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.ExcludeKeyword
                && (t.Name.ToLower().Contains(f.Value.ToLower())
                    || (t.Overview != null && t.Overview.ToLower().Contains(f.Value.ToLower()))
                    // A TMDB keyword phrase containing the filter word is a real match
                    // ("lgbt themes" ⊇ "lgbt"). The old reverse arm — filter.StartsWith(keyword) with a
                    // length≥4 guard — over-excluded: filter "warehouse" swallowed the unrelated "ware"
                    // keyword. Dropped; when filter and keyword are equal this forward Contains already
                    // matches, so the only thing lost was the fragile longer-form prefix.
                    || t.Keywords.Any(k => k.Contains(f.Value.ToLower())))));

        // Include-genres are an allowlist when present: at least one must match. This arm was already
        // live and correct before the 2026-08-07 conditions-editor work — verified against real
        // Postgres, not assumed; the earlier t.Genres.Any(g => ContentFilters.Any(…)) phrasing
        // translated fine. It is now written as ContentFilters.Any over t.Genres.Contains purely so it
        // is the mirror image of the ExcludeGenre arm above and reads as one rule with two signs.
        query = query.Where(t =>
            skipIncludeGenre
            || !db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.IncludeGenre)
            || db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.IncludeGenre && t.Genres.Contains(f.Value)));

        // Language allow-list. No rows configured ⇒ English-only by default (founder ask
        // 2026-06-30: "English only"). A sentinel "*" row opts every language back in. A title with
        // an unknown (null) language reads as not-English and is excluded unless "*" is set — the
        // founder asked for English, not "English plus anything we failed to tag".
        query = query.Where(t =>
            skipLanguage
            || db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.Language && f.Value == "*")
            || (t.Language != null
                && db.ContentFilters.Any(f =>
                    f.AccountId == accountId && f.Kind == FilterKind.Language && f.Value != "*" && f.Value == t.Language.ToLower()))
            || (!db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.Language)
                && t.Language == "en"));

        // Crowd-rating floor. The resolution is TMDB-then-Trakt, re-expressed here inline because a
        // method call (CrowdRating.Resolve) cannot cross into SQL — keep the two in step.
        //
        // A title with NO crowd rating from either source FAILS the floor. That is deliberate and it
        // mirrors PassesMaturity, where an unrated certification passes only when the ceiling is
        // permissive: a restriction the catalogue has no data for is not a licence to ignore the
        // restriction. It costs almost nothing in practice — TMDB covers ~100% of the live catalogue
        // (3,981 / 3,987 on 2026-08-07) — and it is the only reading under which "minimum 7.0"
        // actually means what it says.
        //
        // The floor is read through a scalar subquery whose CAST sits in the SELECT list, so Postgres
        // only ever casts rows that already matched Kind = MinCrowdRating. Language rows ("en") and
        // keyword rows are never fed to the numeric cast.
        query = query.Where(t =>
            skipCrowd
            || !db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.MinCrowdRating)
            || (t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m
                    ? t.TmdbVoteAverage
                    : t.TraktRating != null && t.TraktRating > 0m ? t.TraktRating : null)
                >= db.ContentFilters
                    .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinCrowdRating)
                    .Select(f => (decimal?)Convert.ToDecimal(f.Value))
                    .FirstOrDefault());

        // Inclusive release-year window. Year resolves Year → ReleasedAt → FirstAiredAt; a title with
        // none of the three PASSES, matching LiveSearchExpansionService.MatchesYear ("absence of a
        // date is not evidence of the wrong date"). Deliberately the opposite call to the crowd floor
        // above: a missing year is a metadata gap on an otherwise well-rated title, whereas a missing
        // rating is the very thing the floor is asserting about.
        query = query.Where(t =>
            skipYears
            || !db.ContentFilters.Any(f =>
                f.AccountId == accountId && (f.Kind == FilterKind.MinYear || f.Kind == FilterKind.MaxYear))
            || (t.Year == null && t.ReleasedAt == null && t.FirstAiredAt == null)
            || ((t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year))
                    >= (db.ContentFilters
                        .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MinYear)
                        .Select(f => (int?)Convert.ToInt32(f.Value))
                        .FirstOrDefault() ?? int.MinValue)
                && (t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year))
                    <= (db.ContentFilters
                        .Where(f => f.AccountId == accountId && f.Kind == FilterKind.MaxYear)
                        .Select(f => (int?)Convert.ToInt32(f.Value))
                        .FirstOrDefault() ?? int.MaxValue)));

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
