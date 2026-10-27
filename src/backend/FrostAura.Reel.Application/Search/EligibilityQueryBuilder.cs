using System.Globalization;
using System.Linq.Expressions;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval;
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
/// exists), ExcludeKeyword, Language, MinCrowdRating and the MinYear/MaxYear window. An ExcludeGenre
/// also reaches the titles a TMDB taxonomy cannot tag with that genre — TV has no romance, horror,
/// history, thriller or music genre, and neither medium has anime or musical — through the stand-in
/// keywords and alias genres in <see cref="GenreStandIns"/>, so TV is judged as strictly as movies
/// as far as TMDB's keywords allow (their measured catch rate is recorded there). Under any
/// ExcludeGenre a title with no genres at all is unproven and ineligible (HREV2-RET-04). MaxMaturity is
/// the in-memory gate; MinPredictedRating is applied by the scoring surfaces, since it needs a model.
/// </summary>
public class EligibilityQueryBuilder(IReelDbContext db)
{
    /// <param name="ignoreIncludeGenreFilters">
    /// Drops the IncludeGenre allow-list only — every other rule still binds, ExcludeGenre above all.
    /// Set by the genre rails (<c>FeedRowContext.Genre</c>), where the rail's own genre is the user's
    /// explicit request in that moment and outranks a standing allow-list that does not list it:
    /// without this a "Comedy" rail for an account that allow-lists only horror could only ever be
    /// empty. An ExcludeGenre is NEVER waived (program FD-6's recommendation and default, which binds
    /// until the founder rules otherwise; it also follows from the founder-locked "exclusions are
    /// airtight on every surface"): the old
    /// <c>ignoreGenreFilters</c> dropped every genre rule, so the Adventure rail served Frozen II and
    /// Ice Age to an account that excludes animation and comedy — 20 of 96 rail cards broke a standing
    /// exclusion (F-2). A rail for an excluded genre is not offered at all (<c>FeedEndpoints</c>), so
    /// the only reason ever given for waiving exclusions — "the rail would be permanently empty" —
    /// never applied. Never set it on a general discovery surface.
    /// </param>
    /// <param name="suppressed">
    /// Content rules to leave OUT of the query, on top of <paramref name="ignoreIncludeGenreFilters"/>. This
    /// exists for the empty-feed diagnostic (<see cref="Ranking.FeedStarvationDiagnostic"/>), which
    /// answers "which single condition emptied the pool?" by re-counting with one rule dropped at a
    /// time. It must never be used to SERVE titles — a surface that suppresses a rule is showing the
    /// user content they told it not to. Discovery surfaces pass nothing here.
    /// </param>
    public IQueryable<Title> EligibleTitles(
        Guid accountId, bool ignoreIncludeGenreFilters = false, IReadOnlySet<FilterKind>? suppressed = null) =>
        WithoutFootprint(ContentFilteredTitles(accountId, ignoreIncludeGenreFilters, suppressed), accountId);

    /// <summary>
    /// <paramref name="query"/> without the titles the account has watched, rated, or set aside
    /// (NotInterested, SaveForLater, Dropped, unrevoked).
    ///
    /// <para>Written as <c>NOT IN</c> over each per-account id list rather than a correlated
    /// <c>NOT EXISTS</c>, on purpose. Postgres runs <c>NOT IN</c> as a hashed subplan — each list
    /// read once, one hash probe per title — whatever it estimates the content rules leave. With a
    /// correlated <c>NOT EXISTS</c> it plans an anti join from that estimate, and many exclusion
    /// rules multiply into an estimate of ONE surviving title: it then picked nested loops that
    /// re-scanned the watch list for each of 5,000 real survivors, 15 million comparisons, and the
    /// founder's draft count took 1.4 s instead of 59 ms (2026-10-24). Same rows either way: the
    /// title ids are never null.</para>
    /// </summary>
    private IQueryable<Title> WithoutFootprint(IQueryable<Title> query, Guid accountId)
    {
        var (watched, rated, setAside) = FootprintLists(accountId);
        return query.Where(t => !watched.Contains(t.Id) && !rated.Contains(t.Id) && !setAside.Contains(t.Id));
    }

    /// <summary>
    /// True when the title carries the account's interaction footprint — the exact complement of the
    /// footprint half of <see cref="EligibleTitles(Guid, bool, IReadOnlySet{FilterKind})"/>, built from
    /// the same three id lists (so it runs as the same hashed subplans). For single-pass counts that
    /// need the footprint as a column rather than a filter (<see cref="FilterCostMeter"/>).
    /// </summary>
    public Expression<Func<Title, bool>> Footprint(Guid accountId)
    {
        var (watched, rated, setAside) = FootprintLists(accountId);
        return t => watched.Contains(t.Id) || rated.Contains(t.Id) || setAside.Contains(t.Id);
    }

    /// <summary>
    /// True when the account has watched (even partly) or rated the title — the part of the footprint
    /// a reply may name as "you've seen". Same id lists, and so the same hashed subplans, as
    /// <see cref="Footprint"/>.
    /// </summary>
    public Expression<Func<Title, bool>> Seen(Guid accountId)
    {
        var (watched, rated, _) = FootprintLists(accountId);
        return t => watched.Contains(t.Id) || rated.Contains(t.Id);
    }

    private (IQueryable<Guid> Watched, IQueryable<Guid> Rated, IQueryable<Guid> SetAside) FootprintLists(Guid accountId)
    {
        var watched = db.WatchedTitles.Where(w => w.AccountId == accountId).Select(w => w.TitleId);
        var rated = db.UserRatings.Where(r => r.AccountId == accountId).Select(r => r.TitleId);
        var setAside = db.UserTitleReactions
            .Where(r => r.AccountId == accountId
                && (r.Kind == ReactionKind.NotInterested
                    || r.Kind == ReactionKind.SaveForLater
                    || r.Kind == ReactionKind.Dropped)
                && r.RevokedAt == null)
            .Select(r => r.TitleId);
        return (watched, rated, setAside);
    }

    /// <summary>
    /// <see cref="EligibleTitles(Guid, bool, IReadOnlySet{FilterKind})"/> rewound to a past instant:
    /// the interaction footprint is evaluated as it stood at <paramref name="asOf"/>, so a title the
    /// user watched, rated, saved or declined AFTER that moment counts as eligible then, exactly as
    /// it did on the day.
    ///
    /// <para><b>OFFLINE MEASUREMENT ONLY.</b> Nothing that serves titles may call this — it is
    /// deliberately a separate method rather than an optional parameter on the serving overload,
    /// because a nullable date threaded through the live predicate would change the SQL every
    /// surface in the product emits in order to serve a diagnostic that runs by hand. The serving
    /// path is byte-for-byte what it was.</para>
    ///
    /// <para>The CONTENT rules are read as they stand today, not as-of, and that is the honest call
    /// rather than a shortcut: <c>ContentFilter</c> rows carry a <c>CreatedAt</c> but are mutated
    /// and deleted in place, so there is no history to rewind to. A recall figure computed here
    /// therefore answers "under today's rules, would the arms have reached this title" — which is
    /// the question worth asking, since today's rules are the ones the next build will use.</para>
    ///
    /// <para>Two dating calls, both stated because they bound the answer. A watch counts as-of only
    /// when <c>LastWatchedAt</c> is known and falls on or before <paramref name="asOf"/>: an
    /// undated watch row reads as NOT-yet-watched, matching the year filter's "absence of a date is
    /// not evidence of the wrong date". And a rating counts by <see cref="UserRating.FirstRatedAt"/>
    /// rather than <c>RatedAt</c>, for the same reason <c>EvalHarness</c> splits on it — <c>RatedAt</c>
    /// is overwritten by every Trakt ratings walk, so a re-rate would drag a title backwards across
    /// the as-of line.</para>
    /// </summary>
    public IQueryable<Title> EligibleTitlesAsOf(Guid accountId, DateTime asOf, bool ignoreIncludeGenreFilters = false)
    {
        return ContentFilteredTitles(accountId, ignoreIncludeGenreFilters).Where(t =>
            !db.WatchedTitles.Any(w =>
                w.AccountId == accountId && w.TitleId == t.Id
                && w.LastWatchedAt != null && w.LastWatchedAt <= asOf)
            && !db.UserRatings.Any(r =>
                r.AccountId == accountId && r.TitleId == t.Id && r.FirstRatedAt <= asOf)
            && !db.UserTitleReactions.Any(r =>
                r.AccountId == accountId && r.TitleId == t.Id
                && (r.Kind == ReactionKind.NotInterested
                    || r.Kind == ReactionKind.SaveForLater
                    || r.Kind == ReactionKind.Dropped)
                && r.CreatedAt <= asOf
                && (r.RevokedAt == null || r.RevokedAt > asOf)));
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
    /// <param name="ignoreIncludeGenreFilters">See <see cref="EligibleTitles"/> — genre rails only.</param>
    /// <param name="suppressed">See <see cref="EligibleTitles"/> — the empty-feed diagnostic only.</param>
    public IQueryable<Title> ContentFilteredTitles(
        Guid accountId, bool ignoreIncludeGenreFilters = false, IReadOnlySet<FilterKind>? suppressed = null)
    {
        // Captured as locals so each arm's predicate is a constant EF can fold away, rather than a
        // set lookup it would have to translate.
        var skipExcludeGenre = suppressed?.Contains(FilterKind.ExcludeGenre) == true;
        var skipIncludeGenre = ignoreIncludeGenreFilters || suppressed?.Contains(FilterKind.IncludeGenre) == true;
        var skipKeyword = suppressed?.Contains(FilterKind.ExcludeKeyword) == true;
        var skipLanguage = suppressed?.Contains(FilterKind.Language) == true;
        var skipCrowd = suppressed?.Contains(FilterKind.MinCrowdRating) == true;
        var skipYears = suppressed?.Contains(FilterKind.MinYear) == true || suppressed?.Contains(FilterKind.MaxYear) == true;

        var query = db.Titles.Where(t =>
            skipExcludeGenre
            || !db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre && t.Genres.Contains(f.Value)));

        // The same genre exclusions, read through the TV (and no-id) taxonomy gaps: an excluded genre
        // TMDB cannot tag on a medium is recognised there by its stand-in keywords or alias genres
        // (GenreStandIns). Without this "no romance / horror / history" removed 3, 14 and 2 shows
        // from a catalogue of ~10,000 (H-7). One arm per known slug, each gated on an uncorrelated
        // EXISTS that Postgres evaluates once per query, so an account that excludes none of them
        // pays nothing for the keyword scan.
        if (!skipExcludeGenre)
        {
            query = WithoutStandIns(query, accountId);

            // A title with no genres at all cannot be shown to clear ANY genre exclusion, so under one
            // it fails closed (FilterBreaches.Untagged, HREV2-RET-04).
            query = query.Where(t =>
                t.Genres.Length > 0
                || !db.ContentFilters.Any(f => f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre));
        }

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

        // Crowd-rating floor. The resolution is the RAW TMDB-then-Trakt average, re-expressed here
        // inline because a method call (CrowdRating.ResolveRaw) cannot cross into SQL — keep the two
        // in step. The badge now shows the vote-shrunk score (CrowdRating.Resolve); the floor moves
        // to it only when the founder confirms FD-7, and until then the cost meter reports what the
        // pool would be under the shrunk floor (CrowdFloorMeasure.Shrunk on the draft overload).
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
        //
        // Convert.ToDecimal / Convert.ToInt32 are called here WITHOUT an IFormatProvider, and that is
        // deliberate rather than an oversight the culture sweep missed: EF Core translates only the
        // single-argument overloads, so passing CultureInfo.InvariantCulture makes the whole predicate
        // untranslatable and the query falls apart (verified 2026-09-10 — ~40 integration tests go red).
        // No host culture is involved either way: the conversion happens inside Postgres as a CAST,
        // never in .NET. The stored string shape is what makes that safe, and it is guaranteed by
        // ContentFilterRules.FormatRating / FormatYear, which are already invariant and are the only
        // sanctioned writers of these rows.
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
    /// The eligibility rule evaluated against an EXPLICIT rule set instead of the account's saved
    /// <c>ContentFilters</c> rows — the draft the conditions editor is currently showing.
    ///
    /// The interaction footprint half is unchanged and still reads the account's real rows: a draft
    /// changes what you are willing to be shown, never what you have already seen.
    /// </summary>
    public IQueryable<Title> EligibleTitles(Guid accountId, FilterRuleSet rules) =>
        EligibleTitles(accountId, rules, CrowdFloorMeasure.Raw);

    /// <summary>
    /// <see cref="EligibleTitles(Guid, FilterRuleSet)"/> with the crowd floor read by an explicit
    /// <paramref name="measure"/>. <see cref="CrowdFloorMeasure.Shrunk"/> is a QUESTION — "what would
    /// the pool be if the floor compared the vote-shrunk badge?" — asked by the cost meter before the
    /// founder decides FD-7. Nothing that serves titles passes it.
    /// </summary>
    public IQueryable<Title> EligibleTitles(Guid accountId, FilterRuleSet rules, CrowdFloorMeasure measure) =>
        WithoutFootprint(ContentFilteredTitles(rules, measure), accountId);

    /// <summary>
    /// The content-preference rules from an explicit <see cref="FilterRuleSet"/> rather than from the
    /// database. Same rules, same order, same edge calls as the persisted overload above — an unrated
    /// title fails a crowd floor, an undated title passes a year window, no language rows means
    /// English-only — expressed over in-memory values because the caller is costing conditions that
    /// have not been saved and therefore cannot be read back out of <c>ContentFilters</c>.
    ///
    /// This is the ONE sanctioned second phrasing of the content predicate, and it is pinned to the
    /// first by <c>FilterCostMeterTests.Draft_rules_match_the_persisted_rules_title_for_title</c>,
    /// which seeds a catalogue, saves the rules, and asserts both paths return the identical id set.
    /// If you change one arm here, change it there and let that test prove it. Anything that SERVES
    /// titles keeps using the persisted overload — a draft is a question, not a permission.
    /// </summary>
    public IQueryable<Title> ContentFilteredTitles(FilterRuleSet rules) =>
        ContentFilteredTitles(rules, CrowdFloorMeasure.Raw);

    /// <summary>
    /// <see cref="ContentFilteredTitles(FilterRuleSet)"/> with the crowd floor read by
    /// <paramref name="measure"/> — measurement only, see <see cref="EligibleTitles(Guid, FilterRuleSet, CrowdFloorMeasure)"/>.
    /// </summary>
    public IQueryable<Title> ContentFilteredTitles(FilterRuleSet rules, CrowdFloorMeasure measure)
    {
        var includeGenres = rules.IncludeGenres;
        var languages = rules.EffectiveLanguages;
        var allLanguages = rules.AllLanguagesAllowed;
        var crowdFloor = rules.MinCrowdRating;
        var minYear = rules.MinYear;
        var maxYear = rules.MaxYear;

        var query = db.Titles.AsQueryable();

        // Excluded genres and keywords are an AND of NOTs, so each one is its own Where — which is
        // exactly what "a title carrying ANY excluded value is out" means, and it keeps every arm a
        // plain translatable predicate over one captured constant.
        foreach (var genre in rules.ExcludeGenres)
        {
            var value = genre;
            query = query.Where(t => !t.Genres.Contains(value));

            // The taxonomy-gap half of the same exclusion, exactly as the persisted arm applies it.
            foreach (var alias in GenreStandIns.AliasesOf(value))
            {
                var aliasValue = alias;
                query = query.Where(t => !t.Genres.Contains(aliasValue));
            }

            if (GenreStandIns.CompositeOf(value) is { } composite)
            {
                query = query.Where(Not(composite));
            }

            if (GenreStandIns.Patterns.ContainsKey(value))
            {
                query = query.Where(Not(GenreStandIns.CarriesStandIn(value)));
            }
        }

        // Untagged titles fail closed under any genre exclusion, exactly as the persisted arm reads it.
        if (rules.ExcludeGenres.Count > 0)
        {
            query = query.Where(t => t.Genres.Length > 0);
        }

        // One phrasing with the cost meter's breach mask (FilterBreaches), so "breaks no rule" there
        // selects exactly this pool.
        foreach (var keyword in rules.ExcludeKeywords)
        {
            query = query.Where(FilterBreaches.Not(FilterBreaches.CarriesKeyword(keyword)));
        }

        // Include-genres are an allow-list when present: at least one must match.
        query = query.Where(t => includeGenres.Count == 0 || t.Genres.Any(g => includeGenres.Contains(g)));

        // Language allow-list, with the same two documented calls as the persisted arm: "*" opts every
        // language back in, and an unknown (null) language reads as not-English.
        query = query.Where(t =>
            allLanguages
            || (t.Language != null && languages.Contains(t.Language.ToLower())));

        // Crowd floor, resolved TMDB-then-Trakt inline exactly as above — a title with no crowd
        // rating from either source FAILS the floor. Under the Shrunk measure it compares the badge's
        // vote-shrunk score at badge precision instead, with the same "no rating fails" call.
        if (measure == CrowdFloorMeasure.Shrunk && crowdFloor is { } shrunkFloor)
        {
            query = query.Where(CrowdVotes.ShrunkAtLeast(shrunkFloor));
        }
        else
        {
            query = query.Where(t =>
                crowdFloor == null
                || (t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m
                        ? t.TmdbVoteAverage
                        : t.TraktRating != null && t.TraktRating > 0m ? t.TraktRating : null)
                    >= crowdFloor);
        }

        // Inclusive year window; a title with no year, release date or first-aired date PASSES.
        query = query.Where(t =>
            (minYear == null && maxYear == null)
            || (t.Year == null && t.ReleasedAt == null && t.FirstAiredAt == null)
            || ((t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year))
                    >= (minYear ?? int.MinValue)
                && (t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year))
                    <= (maxYear ?? int.MaxValue)));

        return query;
    }

    /// <summary>
    /// <paramref name="query"/> without the titles an ExcludeGenre row catches through a taxonomy gap:
    /// for every slug <see cref="GenreStandIns"/> knows, "the account excludes this slug AND the title
    /// carries one of its alias genres or stand-in keywords on a medium whose taxonomy cannot tag it".
    /// </summary>
    private IQueryable<Title> WithoutStandIns(IQueryable<Title> query, Guid accountId)
    {
        foreach (var slug in GenreStandIns.Slugs)
        {
            var value = slug;
            Expression<Func<Title, bool>> excluded = t => db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre && f.Value == value);
            query = query.Where(Not(And(excluded, GenreStandIns.CarriesStandIn(value))));
        }

        foreach (var (slug, aliases) in GenreStandIns.Aliases)
        {
            var value = slug;
            var aliasValues = aliases;
            query = query.Where(t =>
                !(t.Genres.Any(g => aliasValues.Contains(g))
                    && db.ContentFilters.Any(f =>
                        f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre && f.Value == value)));
        }

        // Composite slugs (anime = animation in Japanese): "the account excludes this slug AND the
        // title is the combination", the same shape as the stand-ins above.
        foreach (var slug in GenreStandIns.CompositeSlugs)
        {
            var value = slug;
            Expression<Func<Title, bool>> excluded = t => db.ContentFilters.Any(f =>
                f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre && f.Value == value);
            query = query.Where(Not(And(excluded, GenreStandIns.CompositeOf(value)!)));
        }

        return query;
    }

    private static Expression<Func<Title, bool>> Not(Expression<Func<Title, bool>> predicate) =>
        Expression.Lambda<Func<Title, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static Expression<Func<Title, bool>> And(Expression<Func<Title, bool>> left, Expression<Func<Title, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterRebinder(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<Title, bool>>(Expression.AndAlso(left.Body, rightBody), parameter);
    }

    /// <summary>Rebinds one lambda's parameter onto another so two predicates share a single <c>t</c>.</summary>
    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
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
        ArgumentNullException.ThrowIfNull(title);
        return PassesMaturity(title.Certification, maturityCeiling);
    }

    /// <summary>
    /// The same gate on a bare certification — for a row that is not a catalogue title (a TMDB lookup
    /// hit carries none, so it passes only where an unrated title would) or a grouped count.
    /// </summary>
    public static bool PassesMaturity(string? certification, string? maturityCeiling)
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

        var ordinal = Ml.TasteMath.CertificationOrdinal(certification);
        // Unknown certifications pass only when the ceiling allows adult content — a kids'
        // profile must never see an unrated title by accident.
        return ordinal == 0 ? ceiling >= 4 : ordinal <= ceiling;
    }
}

/// <summary>Which number a crowd floor compares.</summary>
public enum CrowdFloorMeasure
{
    /// <summary>The raw TMDB-then-Trakt average — what every serving surface uses until FD-7.</summary>
    Raw,

    /// <summary>The vote-shrunk badge score — measurement only, for the cost meter.</summary>
    Shrunk,
}
