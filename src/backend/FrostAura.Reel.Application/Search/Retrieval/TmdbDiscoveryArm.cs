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
/// <param name="ExcludeGenres">Genres the USER rejected this turn — never a standing filter, which is read from the account.</param>
/// <param name="ExcludeConcepts">Concept phrases the user rejected this turn.</param>
/// <param name="Media">Which media to discover.</param>
/// <param name="Years">The request's own year window, intersected with the account's.</param>
/// <param name="MaxRuntimeMinutes">"Under 90 minutes".</param>
/// <param name="Message">The user's message, searched as a title only when <see cref="TitleShape"/> says it is one.</param>
/// <param name="Region">Release region for movie discover, or null.</param>
/// <param name="Want">Eligible titles wanted; paging stops once this many are in hand.</param>
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

/// <summary>
/// One TMDB result, with its standing in the account's eyes.
/// </summary>
/// <param name="CatalogueTitleId">The catalogue row for this TMDB id and medium, when one exists.</param>
/// <param name="Eligible">
/// True when the title would be served: in the catalogue, it is in <c>EligibilityQueryBuilder.EligibleTitles</c>
/// and inside the maturity ceiling; not yet in the catalogue (so it has no interaction footprint), it
/// passes every standing condition TMDB's list row can be checked against. Hydration re-checks it
/// through the builder before anything is shown.
/// </param>
public sealed record DiscoveredTitle(TmdbListItem Item, TmdbDiscoverySource Source, Guid? CatalogueTitleId, bool Eligible);

/// <summary>What one discovery call fetched, and how much of it the account can actually be shown.</summary>
/// <param name="Items">Every distinct item fetched, in fetch order (discover pages by popularity, then title search).</param>
/// <param name="Pages">Discover pages fetched across both media.</param>
/// <param name="TitleSearched">The text searched as a title, or null when the request was not title-shaped.</param>
/// <param name="TimedOut">True when the arm's deadline cut paging short; <see cref="Items"/> is what arrived in time.</param>
/// <param name="ElapsedMs">Wall time, keyword resolution included.</param>
public sealed record TmdbDiscoveryResult(
    IReadOnlyList<DiscoveredTitle> Items,
    int Pages,
    string? TitleSearched,
    bool TimedOut,
    long ElapsedMs)
{
    /// <summary>The items the account can be shown.</summary>
    public IReadOnlyList<DiscoveredTitle> EligibleItems => [.. Items.Where(i => i.Eligible)];

    /// <summary>Eligible items over items fetched — the waste measure (22.8% before standing options were sent).</summary>
    public double EligibleShare => Items.Count == 0 ? 0d : (double)Items.Count(i => i.Eligible) / Items.Count;
}

/// <summary>
/// The TMDB discovery arm: asks TMDB for what the request names, WITH the account's standing
/// conditions in the request rather than after it, pages until enough eligible titles are in hand
/// (at most <see cref="MaxPages"/> pages per medium), and searches by title only when the message is
/// shaped like one.
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
/// eligibility: every item is judged afterwards, and only a judged-eligible item is offered.</para>
///
/// <para>Measured on the founder's account against the 30 lane-C probes (2026-10-24, live TMDB, slot
/// copy of his data): the v1 call pulled 1,090 titles of which 232 (21.3%) were eligible catalogue
/// titles. This arm pulls 1,236, and 641 (51.9%) are eligible — 526 catalogue titles plus 115 new
/// ones that pass every condition. Of the catalogue titles it pulls and cannot offer, 91% are titles
/// he has already seen or rated, which no TMDB parameter can express.</para>
/// </summary>
public sealed class TmdbDiscoveryArm(
    ITmdbClient tmdb, IReelDbContext db, EligibilityQueryBuilder eligibility, IMemoryCache cache)
{
    /// <summary>
    /// The vote-count floor sent to TMDB: low enough to reach niche unseen titles (the ones a heavy
    /// watcher has left), because a hard high cutoff would silently hide new releases (NEW-2). The
    /// low-vote titles it admits are kept honest elsewhere — the crowd floor still applies, the badge
    /// and ranking read the vote-shrunk <see cref="CrowdRating"/>, and supply counts use
    /// <see cref="CrowdVotes.MinRelaxVotes"/>. Measured: raising it to 50 cut the eligible share from
    /// 51.9% to 46.8%, and to 200, to 38.8%.
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

        var rules = FilterRuleSet.FromRows(
            await db.ContentFilters.AsNoTracking().Where(f => f.AccountId == request.AccountId).ToListAsync(ct));
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
        try
        {
            var conceptIds = await ResolveKeywordIdsAsync(request.Concepts.Take(MaxPhrases), deadline.Token);
            var standingKeywordIds = await ResolveKeywordIdsAsync(rules.ExcludeKeywords, deadline.Token);
            var requestKeywordIds = await ResolveKeywordIdsAsync(request.ExcludeConcepts.Take(MaxPhrases), deadline.Token);

            var media = new List<Task<(List<TmdbListItem> Items, int Pages, bool TimedOut)>>();
            foreach (var movies in new[] { true, false }.Where(m => m ? wantMovies : wantShows))
            {
                var standIns = await StandInKeywordIdsAsync(rules, movies, deadline.Token);
                var excludeKeywords = standingKeywordIds.Concat(requestKeywordIds).Concat(standIns).Distinct().ToList();
                media.Add(PageAsync(request, rules, ceiling, movies, conceptIds, excludeKeywords, dbTurn, deadline.Token, ct));
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
            // The deadline fired during keyword resolution: nothing was fetched, and the turn goes on
            // without this arm rather than waiting for it.
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
                var (catalogueId, ok) = judged[(d.Item.IsMovie, d.Item.Id)];
                return new DiscoveredTitle(d.Item, d.Source, catalogueId, ok);
            })
            .ToList();

        return new TmdbDiscoveryResult(items, pages, titleQuery, timedOut, clock.ElapsedMilliseconds);
    }

    /// <summary>
    /// The text to search as a title, or null (<see cref="TitleShape"/>). The request's concepts and
    /// genres are its topic words; the one database question — does the message exactly name a
    /// catalogue title? — is asked only for an unquoted candidate.
    /// </summary>
    private async Task<string?> TitleQueryAsync(TmdbDiscoveryRequest request, CancellationToken ct)
    {
        var topicWords = request.Concepts.Concat(request.Genres).ToList();
        var namesTitle = false;
        if (TitleShape.Candidate(request.Message) is { Quoted: false } candidate)
        {
            var lowered = candidate.Text.ToLowerInvariant();
            namesTitle = await db.Titles.AsNoTracking().AnyAsync(t => t.Name.ToLower() == lowered, ct);
        }

        return TitleShape.Extract(request.Message, topicWords, namesTitle);
    }

    /// <summary>
    /// Pages one medium's discover until <see cref="TmdbDiscoveryRequest.Want"/> eligible items are in
    /// hand, the results run out, or <see cref="MaxPages"/> is reached. Eligibility is judged per page
    /// so the stop is on titles the account can be shown, not on titles fetched.
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
        var eligibleCount = 0;
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
                eligibleCount += judged.Values.Count(v => v.Eligible);
            }
            finally
            {
                dbTurn.Release();
            }

            if (batch.Count < TmdbPageSize || eligibleCount >= request.Want)
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
    /// Judges TMDB items against the account: a catalogue row through the eligibility builder (and the
    /// maturity ceiling), an item not yet in the catalogue through <see cref="StandingDiscoverPlan.Admits"/>.
    /// </summary>
    private async Task<Dictionary<(bool IsMovie, long Id), (Guid? CatalogueId, bool Eligible)>> JudgeAsync(
        Guid accountId, FilterRuleSet rules, string? ceiling, IReadOnlyList<TmdbListItem> items, CancellationToken ct)
    {
        var result = new Dictionary<(bool, long), (Guid?, bool)>();
        if (items.Count == 0)
        {
            return result;
        }

        var ids = items.Select(i => (long?)i.Id).Distinct().ToList();
        var known = await db.Titles.AsNoTracking()
            .Where(t => ids.Contains(t.TmdbId))
            .Select(t => new { t.Id, t.TmdbId, t.MediaType })
            .ToListAsync(ct);
        var eligibleRows = (await eligibility.EligibleTitles(accountId)
                .Where(t => ids.Contains(t.TmdbId))
                .Select(t => new { t.Id, t.Certification })
                .ToListAsync(ct))
            .Where(t => EligibilityQueryBuilder.PassesMaturity(new Title { Certification = t.Certification }, ceiling))
            .Select(t => t.Id)
            .ToHashSet();
        var unknownCertificationPasses = EligibilityQueryBuilder.PassesMaturity(new Title(), ceiling);

        foreach (var item in items)
        {
            var media = item.IsMovie ? MediaType.Movie : MediaType.Show;
            var row = known.FirstOrDefault(k => k.TmdbId == item.Id && k.MediaType == media);
            result[(item.IsMovie, item.Id)] = row is not null
                ? (row.Id, eligibleRows.Contains(row.Id))
                : (null, unknownCertificationPasses && StandingDiscoverPlan.Admits(rules, item));
        }

        return result;
    }

    /// <summary>
    /// The TMDB keyword ids that stand in for the account's excluded genres on a medium whose taxonomy
    /// cannot name them (TV: romance, horror, history, …; both: anime, musical). Each pattern's text is
    /// resolved as an exact TMDB keyword; a stem that is not itself a TMDB keyword resolves to nothing,
    /// and the eligibility check catches the rest after the fetch.
    /// </summary>
    private async Task<IReadOnlyList<int>> StandInKeywordIdsAsync(FilterRuleSet rules, bool movies, CancellationToken ct)
    {
        var media = movies ? MediaType.Movie : MediaType.Show;
        var phrases = rules.ExcludeGenres
            .Where(slug => GenreStandIns.MediaFor(slug).Contains(media) && GenreStandIns.Patterns.ContainsKey(slug))
            .SelectMany(slug => GenreStandIns.Patterns[slug].Select(p => p.Text))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return await ResolveKeywordIdsAsync(phrases, ct, exactOnly: true);
    }

    /// <summary>
    /// Resolves phrases to TMDB keyword ids, concurrently and cached per phrase: an exact name match
    /// wins, else (unless <paramref name="exactOnly"/>) TMDB's first result, matching the v1 resolver.
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

            var matches = await tmdb.SearchKeywordsAsync(phrase, ct);
            var exact = matches.FirstOrDefault(m => string.Equals(m.Name, phrase, StringComparison.OrdinalIgnoreCase));
            int? id = exact?.Id ?? (exactOnly ? null : matches.FirstOrDefault()?.Id);
            cache.Set(key, id, KeywordCacheWindow);
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
        // which is also science fiction, and a sci-fi request must not be emptied by it. Such a genre
        // is left to the eligibility check, which reads the slugs, not the fused id.
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
    /// True when a TMDB list row for a title NOT in the catalogue passes every standing condition the
    /// row carries data for — the same calls the eligibility builder makes: an unknown language is
    /// not English, an unrated title fails a crowd floor, an undated title passes a year window, a
    /// keyword rule matches the name or overview. Stand-in keywords cannot be checked on a list row;
    /// they were sent to TMDB as <c>without_keywords</c>, and hydration re-checks through the builder.
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
