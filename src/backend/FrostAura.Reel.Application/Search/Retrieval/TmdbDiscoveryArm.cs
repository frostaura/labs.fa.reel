using System.Diagnostics;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>One TMDB discovery call for a request: what to look for, for whom, and how many eligible titles are wanted.</summary>
/// <param name="AccountId">Whose standing conditions shape the fetch and judge the results.</param>
/// <param name="Genres">Genre slugs the request asks for (OR-joined, as TMDB's <c>with_genres</c> pipe).</param>
/// <param name="Concepts">Concept phrases ("ninja", "time loop") resolved to TMDB keyword ids (OR-joined).</param>
/// <param name="ExcludeGenres">
/// Genres the USER rejected this turn — never a standing filter, which is read from the account. They
/// bind exactly as a standing ExcludeGenre does: sent with the fetch where the medium can name them
/// (stand-in keywords included), and judged on every result.
/// </param>
/// <param name="ExcludeConcepts">Concept phrases the user rejected this turn; judged as an ExcludeKeyword.</param>
/// <param name="Media">Which media to discover.</param>
/// <param name="Years">The request's own year window, intersected with the account's.</param>
/// <param name="MaxRuntimeMinutes">"Under 90 minutes".</param>
/// <param name="Message">The user's message, searched as a title only when <see cref="TitleShape"/> says it is one.</param>
/// <param name="Region">Release region for movie discover, or null.</param>
/// <param name="Want">Usable titles wanted; paging stops once this many are in hand.</param>
public sealed record TmdbDiscoveryRequest(
    Guid AccountId,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> ExcludeGenres,
    IReadOnlyList<string> ExcludeConcepts,
    MediaScope Media,
    YearRange? Years,
    int? MaxRuntimeMinutes,
    string? Message,
    string? Region,
    int Want,
    TmdbDiscoverSort Sort = TmdbDiscoverSort.Popularity,
    int MinVoteCount = TmdbDiscoveryArm.DefaultMinVoteCount);

/// <summary>Where a discovered item came from.</summary>
public enum TmdbDiscoverySource
{
    Discover,
    TitleSearch,
}

/// <summary>How far a discovered item has been judged against the turn's conditions.</summary>
public enum TmdbDiscoveryVerdict
{
    /// <summary>Fails a condition, or is a catalogue title the account has seen, rated or set aside.</summary>
    Ineligible,

    /// <summary>
    /// A catalogue title inside <c>EligibilityQueryBuilder.EligibleTitles</c> for the turn's conditions
    /// and inside the maturity ceiling — servable as it stands.
    /// </summary>
    Eligible,

    /// <summary>
    /// Not in the catalogue yet, and passes every condition a TMDB list row carries data for. A list
    /// row has no keywords and no certification, so stand-in keywords, keyword-label exclusions and
    /// the maturity ceiling are NOT checked: the caller hydrates it and re-judges it through the
    /// eligibility builder before it may be shown.
    /// </summary>
    Provisional,
}

/// <summary>One TMDB result, with its standing in the account's eyes.</summary>
/// <param name="CatalogueTitleId">The catalogue row for this TMDB id and medium, when one exists.</param>
public sealed record DiscoveredTitle(TmdbListItem Item, TmdbDiscoverySource Source, Guid? CatalogueTitleId, TmdbDiscoveryVerdict Verdict)
{
    /// <summary>True for a catalogue title the account can be shown as it stands.</summary>
    public bool Eligible => Verdict == TmdbDiscoveryVerdict.Eligible;

    /// <summary>True for a new title that must be hydrated and re-judged before it is shown.</summary>
    public bool Provisional => Verdict == TmdbDiscoveryVerdict.Provisional;
}

/// <summary>What one discovery call fetched, and how much of it the account can actually be shown.</summary>
/// <param name="Items">Every distinct item fetched, in fetch order (discover pages, then title search).</param>
/// <param name="Pages">Discover pages fetched across both media.</param>
/// <param name="TitleSearched">The text searched as a title, or null when the request was not title-shaped.</param>
/// <param name="TimedOut">True when the arm's deadline cut the call short; <see cref="Items"/> is what arrived in time.</param>
/// <param name="ElapsedMs">Wall time, keyword resolution included.</param>
/// <param name="ConceptsUnresolved">
/// True when the request named concepts and none of them resolved to a TMDB keyword (TMDB has no such
/// keyword, or did not answer). Discover is then skipped: without a keyword it would return the
/// medium's popular titles, off-topic, marked as answers to the request.
/// </param>
public sealed record TmdbDiscoveryResult(
    IReadOnlyList<DiscoveredTitle> Items,
    int Pages,
    string? TitleSearched,
    bool TimedOut,
    long ElapsedMs,
    bool ConceptsUnresolved = false)
{
    /// <summary>The catalogue items the account can be shown as they stand.</summary>
    public IReadOnlyList<DiscoveredTitle> EligibleItems => [.. Items.Where(i => i.Eligible)];

    /// <summary>The new items to hydrate and re-judge before they can be shown.</summary>
    public IReadOnlyList<DiscoveredTitle> ProvisionalItems => [.. Items.Where(i => i.Provisional)];

    /// <summary>
    /// Eligible catalogue items over items fetched — the program's waste measure and the definition
    /// behind its gate (≥ 50%, from 22.8% before standing options were sent). Provisional items do
    /// not count: nothing has checked them against the stand-ins, keyword labels or maturity yet.
    /// </summary>
    public double EligibleShare => Items.Count == 0 ? 0d : (double)Items.Count(i => i.Eligible) / Items.Count;

    /// <summary>Eligible plus provisional items over items fetched: the upper bound once new titles are hydrated.</summary>
    public double UsableShare =>
        Items.Count == 0 ? 0d : (double)Items.Count(i => i.Verdict != TmdbDiscoveryVerdict.Ineligible) / Items.Count;
}

/// <summary>
/// The TMDB discovery arm: asks TMDB for what the request names, WITH the account's standing
/// conditions and the turn's own rejections in the request rather than after it, pages until enough
/// usable titles are in hand (at most <see cref="MaxPages"/> pages per medium), and searches by title
/// only when the message is shaped like one.
///
/// <para>Why it exists (A-10, C-5): Ask Reel's discover call sent no language, no crowd floor and only
/// the exclusions the model happened to echo, fetched one page sorted by popularity, and searched the
/// whole sentence as a title. 30 probes pulled 1,090 titles of which 249 (22.8%) were eligible; the
/// ninja request pulled 23 and could show none. A rule applied after a fetch can only shrink the
/// pool; the same rule sent with the fetch changes what is in it.</para>
///
/// <para>What is projected into the fetch, read straight from <c>ContentFilters</c> through
/// <see cref="FilterRuleSet"/>: the language allow-list (TMDB takes one code — "*" or several
/// languages project to no constraint, and eligibility enforces the real rule), the crowd floor, the
/// year window (intersected with the request's), every excluded genre the medium's taxonomy can name,
/// and the excluded keywords plus the genre stand-ins (<see cref="GenreStandIns"/>) resolved to TMDB
/// keyword ids, so "no romance" also reaches TV through <c>without_keywords</c>. Nothing here relaxes
/// eligibility: every item is judged afterwards against the standing conditions AND the turn's
/// rejections (<see cref="FilterRuleSet.WithExclusions"/>), and only a catalogue title judged eligible
/// is <see cref="TmdbDiscoveryVerdict.Eligible"/>.</para>
///
/// <para>Measured by <c>TmdbDiscoveryShareProbe</c> (the 30 lane-C probes, the founder's slot data,
/// live TMDB) — see that harness for the figures and how to reproduce them. The share gate counts
/// eligible catalogue titles only. Most catalogue titles the arm pulls and cannot offer are titles he
/// has already seen or rated, which no TMDB parameter can express.</para>
/// </summary>
public sealed class TmdbDiscoveryArm(
    ITmdbClient tmdb, IReelDbContext db, EligibilityQueryBuilder eligibility, IMemoryCache cache)
{
    /// <summary>
    /// The vote-count floor sent to TMDB: low enough to reach niche unseen titles (the ones a heavy
    /// watcher has left), because a hard high cutoff would silently hide new releases (NEW-2). The
    /// low-vote titles it admits are kept honest elsewhere — the crowd floor still applies, the badge
    /// and ranking read the vote-shrunk <see cref="CrowdRating"/>, and supply counts use
    /// <see cref="CrowdVotes.MinRelaxVotes"/>. Measured on the probes: 50 and 200 both lowered the
    /// eligible share (see <c>TmdbDiscoveryShareProbe</c>).
    /// </summary>
    public const int DefaultMinVoteCount = 10;

    /// <summary>Discover pages per medium, at most (§4.7).</summary>
    public const int MaxPages = 3;

    /// <summary>Concept and exclusion phrases resolved per call; more would be a keyword-search storm.</summary>
    public const int MaxPhrases = 4;

    /// <summary>The arm's own deadline (§4.7): fusion proceeds without whatever has not arrived.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a phrase's TMDB keyword id is remembered. Keyword ids do not move.</summary>
    public static readonly TimeSpan KeywordCacheWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// How long TMDB's answer "there is no such keyword" is remembered — shorter than a hit, since
    /// TMDB adds keywords. A call TMDB did not answer (an error, a 429) is never remembered at all:
    /// one outage must not silently drop a topic, or a TV exclusion, for days.
    /// </summary>
    public static readonly TimeSpan KeywordMissCacheWindow = TimeSpan.FromHours(6);

    /// <summary>A full TMDB page; a shorter one is the last page there is.</summary>
    private const int TmdbPageSize = 20;

    /// <summary>Runs the arm under <see cref="DefaultTimeout"/>.</summary>
    public Task<TmdbDiscoveryResult> DiscoverAsync(TmdbDiscoveryRequest request, CancellationToken ct) =>
        DiscoverAsync(request, DefaultTimeout, ct);

    /// <summary>Runs the arm under an explicit deadline (tests and offline probes pass a long one).</summary>
    public async Task<TmdbDiscoveryResult> DiscoverAsync(TmdbDiscoveryRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var standing = FilterRuleSet.FromRows(
            await db.ContentFilters.AsNoTracking().Where(f => f.AccountId == request.AccountId).ToListAsync(ct));
        // The conditions this turn is judged by: the account's, plus what the user rejected this turn.
        var rules = standing.WithExclusions(request.ExcludeGenres, request.ExcludeConcepts.Take(MaxPhrases));
        var ceiling = await eligibility.MaturityCeilingAsync(request.AccountId, ct);

        var titleQuery = await TitleQueryAsync(request, ct);
        var wantMovies = request.Media != MediaScope.Shows;
        var wantShows = request.Media != MediaScope.Movies;

        // The media page concurrently against TMDB, but share this unit of work's DbContext, which
        // allows one operation at a time — so the eligibility judging between pages takes turns.
        using var dbTurn = new SemaphoreSlim(1, 1);
        var collected = new List<(TmdbListItem Item, TmdbDiscoverySource Source)>();
        var pages = 0;
        var timedOut = false;
        var conceptsUnresolved = false;
        try
        {
            // EVERY keyword id is resolved before any page starts, so a deadline that fires here
            // leaves no task running on the shared DbContext after this method returns.
            var concepts = ResolveKeywordIdsAsync(request.Concepts.Take(MaxPhrases), deadline.Token);
            var exclusions = ResolveKeywordIdsAsync(rules.ExcludeKeywords, deadline.Token);
            var movieStandIns = wantMovies ? StandInKeywordIdsAsync(rules, movies: true, deadline.Token) : Task.FromResult<IReadOnlyList<int>>([]);
            var showStandIns = wantShows ? StandInKeywordIdsAsync(rules, movies: false, deadline.Token) : Task.FromResult<IReadOnlyList<int>>([]);
            await Task.WhenAll(concepts, exclusions, movieStandIns, showStandIns);

            var conceptIds = await concepts;
            conceptsUnresolved = request.Concepts.Count > 0 && conceptIds.Count == 0;

            var media = new List<Task<(List<TmdbListItem> Items, int Pages, bool TimedOut)>>();
            if (!conceptsUnresolved)
            {
                foreach (var movies in new[] { true, false }.Where(m => m ? wantMovies : wantShows))
                {
                    var standIns = await (movies ? movieStandIns : showStandIns);
                    var excludeKeywords = (await exclusions).Concat(standIns).Distinct().ToList();
                    media.Add(PageAsync(request, rules, ceiling, movies, conceptIds, excludeKeywords, dbTurn, deadline.Token, ct));
                }
            }

            var titleSearches = titleQuery is null
                ? []
                : new[] { true, false }
                    .Where(m => m ? wantMovies : wantShows)
                    .Select(m => SearchAsync(m, titleQuery, deadline.Token, ct))
                    .ToList();

            foreach (var (mediumItems, mediumPages, mediumTimedOut) in await Task.WhenAll(media))
            {
                collected.AddRange(mediumItems.Select(i => (i, TmdbDiscoverySource.Discover)));
                pages += mediumPages;
                timedOut |= mediumTimedOut;
            }

            foreach (var (searchItems, searchTimedOut) in await Task.WhenAll(titleSearches))
            {
                collected.AddRange(searchItems.Select(i => (i, TmdbDiscoverySource.TitleSearch)));
                timedOut |= searchTimedOut;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The deadline fired during keyword resolution: nothing was fetched (no page had started),
            // and the turn goes on without this arm rather than waiting for it.
            timedOut = true;
        }

        var distinct = collected
            .GroupBy(c => (c.Item.IsMovie, c.Item.Id))
            .Select(g => g.First())
            .ToList();
        var judged = await JudgeAsync(request.AccountId, rules, ceiling, distinct.Select(d => d.Item).ToList(), ct);
        var items = distinct
            .Select(d =>
            {
                var (catalogueId, verdict) = judged[(d.Item.IsMovie, d.Item.Id)];
                return new DiscoveredTitle(d.Item, d.Source, catalogueId, verdict);
            })
            .ToList();

        return new TmdbDiscoveryResult(items, pages, titleQuery, timedOut, clock.ElapsedMilliseconds, conceptsUnresolved);
    }

    /// <summary>
    /// The text to search as a title, or null (<see cref="TitleShape"/>). The request's concepts and
    /// genres are its topic words; the one database question — does the whole message exactly name a
    /// catalogue title? — is asked before the shape rules, so a real title carrying a description
    /// word or a comma ("Scary Movie", "Monsters, Inc.") is still searched.
    /// </summary>
    private async Task<string?> TitleQueryAsync(TmdbDiscoveryRequest request, CancellationToken ct)
    {
        var topicWords = request.Concepts.Concat(request.Genres).ToList();
        var namesTitle = false;
        if (TitleShape.LookupText(request.Message) is { } lookup)
        {
            var lowered = lookup.ToLowerInvariant();
            namesTitle = await db.Titles.AsNoTracking().AnyAsync(t => t.Name.ToLower() == lowered, ct);
        }

        return TitleShape.Extract(request.Message, topicWords, namesTitle);
    }

    /// <summary>
    /// Pages one medium's discover until <see cref="TmdbDiscoveryRequest.Want"/> usable items (eligible
    /// or provisional) are in hand, the results run out, or <see cref="MaxPages"/> is reached.
    /// Eligibility is judged per page so the stop is on titles the account can use, not on titles
    /// fetched.
    /// </summary>
    private async Task<(List<TmdbListItem> Items, int Pages, bool TimedOut)> PageAsync(
        TmdbDiscoveryRequest request,
        FilterRuleSet rules,
        string? ceiling,
        bool movies,
        IReadOnlyList<int> conceptIds,
        IReadOnlyList<int> excludeKeywordIds,
        SemaphoreSlim dbTurn,
        CancellationToken deadline,
        CancellationToken ct)
    {
        var plan = StandingDiscoverPlan.For(rules, movies, request);
        var items = new List<TmdbListItem>();
        var usable = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            IReadOnlyList<TmdbListItem> batch;
            try
            {
                batch = await tmdb.DiscoverByConceptAsync(
                    movies,
                    plan.GenreIds,
                    conceptIds,
                    request.Region,
                    plan.ReleasedAfter,
                    page,
                    plan.ExcludeGenreIds,
                    excludeKeywordIds,
                    plan.ReleasedBefore,
                    request.MaxRuntimeMinutes,
                    plan.Options,
                    deadline);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (items, page - 1, true);
            }
            catch (HttpRequestException)
            {
                // A failed page ends this medium's paging; what already arrived still counts.
                return (items, page - 1, false);
            }

            items.AddRange(batch);
            await dbTurn.WaitAsync(ct);
            try
            {
                var judged = await JudgeAsync(request.AccountId, rules, ceiling, batch, ct);
                usable += judged.Values.Count(v => v.Verdict != TmdbDiscoveryVerdict.Ineligible);
            }
            finally
            {
                dbTurn.Release();
            }

            if (batch.Count < TmdbPageSize || usable >= request.Want)
            {
                return (items, page, false);
            }
        }

        return (items, MaxPages, false);
    }

    private async Task<(IReadOnlyList<TmdbListItem> Items, bool TimedOut)> SearchAsync(
        bool movies, string title, CancellationToken deadline, CancellationToken ct)
    {
        try
        {
            return (await tmdb.SearchTitlesAsync(movies, title, 1, deadline), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ([], true);
        }
        catch (HttpRequestException)
        {
            return ([], false);
        }
    }

    /// <summary>
    /// Judges TMDB items against the turn's conditions (<paramref name="rules"/>: standing plus the
    /// turn's rejections): a catalogue row through the eligibility builder (stand-ins, aliases,
    /// keyword labels, the interaction footprint) and the maturity ceiling; an item not yet in the
    /// catalogue through <see cref="StandingDiscoverPlan.Admits"/>, which can at most make it
    /// <see cref="TmdbDiscoveryVerdict.Provisional"/>.
    /// </summary>
    private async Task<Dictionary<(bool IsMovie, long Id), (Guid? CatalogueId, TmdbDiscoveryVerdict Verdict)>> JudgeAsync(
        Guid accountId, FilterRuleSet rules, string? ceiling, IReadOnlyList<TmdbListItem> items, CancellationToken ct)
    {
        var result = new Dictionary<(bool, long), (Guid?, TmdbDiscoveryVerdict)>();
        if (items.Count == 0)
        {
            return result;
        }

        var ids = items.Select(i => (long?)i.Id).Distinct().ToList();
        var known = await db.Titles.AsNoTracking()
            .Where(t => ids.Contains(t.TmdbId))
            .Select(t => new { t.Id, t.TmdbId, t.MediaType })
            .ToListAsync(ct);
        var eligibleRows = (await eligibility.EligibleTitles(accountId, rules)
                .Where(t => ids.Contains(t.TmdbId))
                .Select(t => new { t.Id, t.Certification })
                .ToListAsync(ct))
            .Where(t => EligibilityQueryBuilder.PassesMaturity(new Title { Certification = t.Certification }, ceiling))
            .Select(t => t.Id)
            .ToHashSet();

        foreach (var item in items)
        {
            var media = item.IsMovie ? MediaType.Movie : MediaType.Show;
            var row = known.FirstOrDefault(k => k.TmdbId == item.Id && k.MediaType == media);
            result[(item.IsMovie, item.Id)] = row is not null
                ? (row.Id, eligibleRows.Contains(row.Id) ? TmdbDiscoveryVerdict.Eligible : TmdbDiscoveryVerdict.Ineligible)
                : (null, StandingDiscoverPlan.Admits(rules, item) ? TmdbDiscoveryVerdict.Provisional : TmdbDiscoveryVerdict.Ineligible);
        }

        return result;
    }

    /// <summary>
    /// The TMDB keyword ids that stand in for the turn's excluded genres (standing and this turn's)
    /// on a medium whose taxonomy cannot name them (TV: romance, horror, history, …; both: anime,
    /// musical). Each pattern's text is resolved as an exact TMDB keyword; a stem that is not itself a
    /// TMDB keyword resolves to nothing, and the eligibility check catches the rest after the fetch.
    /// </summary>
    private Task<IReadOnlyList<int>> StandInKeywordIdsAsync(FilterRuleSet rules, bool movies, CancellationToken ct)
    {
        var media = movies ? MediaType.Movie : MediaType.Show;
        var phrases = rules.ExcludeGenres
            .Where(slug => GenreStandIns.MediaFor(slug).Contains(media) && GenreStandIns.Patterns.ContainsKey(slug))
            .SelectMany(slug => GenreStandIns.Patterns[slug].Select(p => p.Text))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ResolveKeywordIdsAsync(phrases, ct, exactOnly: true);
    }

    /// <summary>
    /// Resolves phrases to TMDB keyword ids, concurrently and cached per phrase: an exact name match
    /// wins, else (unless <paramref name="exactOnly"/>) TMDB's first result, matching the v1 resolver.
    /// A phrase TMDB did not answer for (a failed call) resolves to nothing for this call only.
    /// </summary>
    private async Task<IReadOnlyList<int>> ResolveKeywordIdsAsync(
        IEnumerable<string> phrases, CancellationToken ct, bool exactOnly = false)
    {
        var cleaned = phrases
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var resolved = await Task.WhenAll(cleaned.Select(async phrase =>
        {
            var key = $"tmdb-keyword:{(exactOnly ? "exact" : "first")}:{phrase}";
            if (cache.TryGetValue<int?>(key, out var hit))
            {
                return hit;
            }

            TmdbKeywordSearch search;
            try
            {
                search = await tmdb.SearchKeywordsCheckedAsync(phrase, ct);
            }
            catch (HttpRequestException)
            {
                return null;
            }

            if (!search.Answered)
            {
                return null;
            }

            var exact = search.Results.FirstOrDefault(m => string.Equals(m.Name, phrase, StringComparison.OrdinalIgnoreCase));
            int? id = exact?.Id ?? (exactOnly ? null : search.Results.FirstOrDefault()?.Id);
            cache.Set(key, id, id is null ? KeywordMissCacheWindow : KeywordCacheWindow);
            return id;
        }));
        return [.. resolved.OfType<int>().Distinct()];
    }
}


/// <summary>
/// The account's standing conditions (plus the request's own constraints) projected into one TMDB
/// discover call for one medium, and the in-memory check of a TMDB list row against the same
/// conditions. Pure, so every projection decision is pinned by a unit test.
/// </summary>
public sealed record StandingDiscoverPlan(
    IReadOnlyList<int> GenreIds,
    IReadOnlyList<int> ExcludeGenreIds,
    DateTime? ReleasedAfter,
    DateTime? ReleasedBefore,
    TmdbDiscoverOptions Options)
{
    /// <summary>The plan for one medium.</summary>
    public static StandingDiscoverPlan For(FilterRuleSet rules, bool movies, TmdbDiscoveryRequest request)
    {
        int? IdOf(string slug) =>
            TmdbGenres.SlugToIds.TryGetValue(slug, out var ids) ? (movies ? ids.Movie : ids.Tv) : null;

        var genreIds = request.Genres
            .Select(g => IdOf(g.Trim().ToLowerInvariant()))
            .OfType<int>()
            .Distinct()
            .ToList();

        // Standing exclusions first, then the user's own rejections — but never a fused TV id that
        // would also remove a genre the request ASKED for: excluding "fantasy" on TV means 10765,
        // which is also science fiction, and a sci-fi request must not be emptied at TMDB by it. Such
        // a rejection is NOT dropped: the arm judges every result against the standing rules plus the
        // turn's rejections (FilterRuleSet.WithExclusions), reading slugs rather than the fused id, so
        // a catalogue show stored as science-fiction alone survives, one tagged fantasy does not, and
        // a new 10765 show (both slugs on its list row) is ineligible.
        var excludeGenreIds = rules.ExcludeGenres
            .Concat(request.ExcludeGenres.Select(g => g.Trim().ToLowerInvariant()))
            .Select(IdOf)
            .OfType<int>()
            .Where(id => !genreIds.Contains(id))
            .Distinct()
            .ToList();

        // The tighter of the request's window and the account's.
        var from = Max(rules.MinYear, request.Years?.From);
        var to = Min(rules.MaxYear, request.Years?.To);
        DateTime? after = from is { } f ? new DateTime(f, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;
        DateTime? before = to is { } t ? new DateTime(t, 12, 31, 0, 0, 0, DateTimeKind.Utc) : null;

        // TMDB's with_original_language takes ONE code; "*" or several languages cannot be projected
        // without silently dropping the rest, so they project to no constraint.
        var languages = rules.EffectiveLanguages;
        var language = !rules.AllLanguagesAllowed && languages.Count == 1 ? languages[0] : null;

        return new StandingDiscoverPlan(
            genreIds,
            excludeGenreIds,
            after,
            before,
            new TmdbDiscoverOptions(
                OriginalLanguage: language,
                MinVoteAverage: rules.MinCrowdRating,
                MinVoteCount: request.MinVoteCount,
                PeopleIds: null,
                Priority: RatePriority.Interactive,
                Sort: request.Sort));
    }

    /// <summary>
    /// True when a TMDB list row for a title NOT in the catalogue passes the conditions the row
    /// carries data for — the same calls the eligibility builder makes: an unknown language is not
    /// English, an unrated title fails a crowd floor, an undated title passes a year window, an
    /// excluded genre (or its alias) on the row's genre ids fails, a keyword rule matches the name or
    /// overview. A list row has no keywords and no certification, so stand-in keywords, keyword-label
    /// exclusions and the maturity ceiling are NOT checked here: a pass makes the item
    /// <see cref="TmdbDiscoveryVerdict.Provisional"/>, never eligible, and the caller re-judges it
    /// through the eligibility builder once hydrated.
    /// </summary>
    public static bool Admits(FilterRuleSet rules, TmdbListItem item)
    {
        var language = item.OriginalLanguage?.Trim().ToLowerInvariant();
        if (!rules.AllLanguagesAllowed && (language is null || !rules.EffectiveLanguages.Contains(language)))
        {
            return false;
        }

        if (rules.MinCrowdRating is { } floor && (item.VoteAverage <= 0m || item.VoteAverage < floor))
        {
            return false;
        }

        if (item.ReleasedAt is { } released
            && ((rules.MinYear is { } min && released.Year < min) || (rules.MaxYear is { } max && released.Year > max)))
        {
            return false;
        }

        var slugs = TmdbGenres.MapToSlugs(item.IsMovie, item.GenreIds);
        var excluded = rules.ExcludeGenres
            .SelectMany(g => GenreStandIns.AliasesOf(g).Append(g))
            .ToHashSet(StringComparer.Ordinal);
        if (slugs.Any(excluded.Contains))
        {
            return false;
        }

        if (rules.IncludeGenres.Count > 0 && !slugs.Any(rules.IncludeGenres.Contains))
        {
            return false;
        }

        var name = item.Name.ToLowerInvariant();
        var overview = item.Overview?.ToLowerInvariant() ?? string.Empty;
        return !rules.ExcludeKeywords.Any(k => name.Contains(k, StringComparison.Ordinal) || overview.Contains(k, StringComparison.Ordinal));
    }

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
