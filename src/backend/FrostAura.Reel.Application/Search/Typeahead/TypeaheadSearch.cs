using System.Text;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Typeahead;

/// <summary>How well a name matches what was typed. Lower is better; persisted nowhere.</summary>
public enum MatchTier
{
    /// <summary>The whole name, with or without a leading article ("godfather" → The Godfather).</summary>
    Exact = 0,

    /// <summary>The name starts with the term, with or without a leading article.</summary>
    Prefix = 1,

    /// <summary>The term starts a word inside the name ("nolan" → Christopher Nolan).</summary>
    WordStart = 2,

    /// <summary>Only a substring or a trigram look-alike ("heat" in Cheaters; "inceptoin").</summary>
    Fuzzy = 3,
}

/// <summary>One local title row.</summary>
public sealed record TypeaheadTitle(
    Guid TitleId,
    string MediaType,
    long? TmdbId,
    string Name,
    int? Year,
    string? PosterPath,
    bool IsFullyWatched,
    decimal? CrowdRating,
    int? CrowdVotes,
    MatchTier Tier);

/// <summary>One person row; <see cref="Affinity"/> is the account's own affinity with them, when it has one.</summary>
public sealed record TypeaheadPerson(Guid PersonId, string Name, string? KnownFor, string? ProfilePath, MatchTier Tier, decimal? Affinity);

/// <summary>The local leg: titles and people, the strong-match count that decides the global leg, and what the conditions hide.</summary>
/// <param name="StrongMatches">Title rows matching at <see cref="MatchTier.WordStart"/> or better — fuzzy junk never counts.</param>
/// <param name="HiddenByConditions">
/// Catalogue titles the term matches at a word start or better that the account's conditions (or
/// its maturity ceiling) keep out of the list — FD-5's default: hidden, and counted on a line.
/// </param>
public sealed record TypeaheadLocal(
    IReadOnlyList<TypeaheadTitle> Titles,
    IReadOnlyList<TypeaheadPerson> People,
    int StrongMatches,
    int HiddenByConditions);

/// <summary>One "beyond your catalogue" row — a TMDB lookup hit, never scored.</summary>
public sealed record GlobalTypeaheadRow(
    string Source,
    string MediaType,
    long TmdbId,
    string Name,
    int? Year,
    string? PosterPath,
    decimal? CrowdRating,
    decimal? PredictedRating);

/// <summary>The global leg: TMDB rows inside the account's conditions, and how many it hid.</summary>
public sealed record TypeaheadGlobal(IReadOnlyList<GlobalTypeaheadRow> Rows, int HiddenByConditions);

/// <summary>
/// The search box's lookup (program WS-3 task 10; C-8, C*3, G-4, G-11, J-20, G-22, G-5): find the title
/// or person the user is typing, canonical first.
///
/// <para><b>Tiers, not similarity.</b> Rows rank by <see cref="MatchTier"/> — exact, then prefix, then
/// word start, then fuzzy — and within a tier by how established the title is (crowd votes), so
/// "heat" puts Heat (1995) first instead of Cheaters, and "godfather" The Godfather (a leading article
/// is ignored for exact and prefix). Similarity only breaks ties among the fuzzy. The personal score
/// is no longer part of the lookup: it decided nothing a lookup should, and loading the model made
/// the leg slow, so the badge is fetched after the rows (<c>/typeahead/scores</c>).</para>
///
/// <para><b>Conditions (FD-5's default).</b> Excluded titles are hidden in BOTH legs — the local one
/// reads <see cref="EligibilityQueryBuilder.ContentFilteredTitles(Guid, bool, IReadOnlySet{Domain.Filters.FilterKind})"/>,
/// the global one judges every TMDB row by the same rules — and the number hidden is reported, so
/// "N matches hidden by your conditions" can be said instead of the lookup silently finding nothing.
/// Watched titles still appear (badged): a lookup answers "where is it", not "should you watch it".</para>
///
/// <para><b>Input is literal.</b> ILIKE metacharacters are escaped, so "%" finds titles containing a
/// percent sign, not every title.</para>
/// </summary>
public sealed partial class TypeaheadSearch(IReelDbContext db, EligibilityQueryBuilder eligibility)
{
    /// <summary>Title rows returned.</summary>
    public const int TitleLimit = 8;

    /// <summary>Person rows returned.</summary>
    public const int PersonLimit = 4;

    /// <summary>Global rows returned.</summary>
    public const int GlobalLimit = 6;

    /// <summary>The fewest characters a term needs.</summary>
    public const int MinTermLength = 2;

    /// <summary>The local leg for <paramref name="term"/>.</summary>
    public async Task<TypeaheadLocal> LocalAsync(Guid accountId, string term, CancellationToken ct)
    {
        var lower = term.Trim().ToLowerInvariant();
        if (lower.Length < MinTermLength)
        {
            return new TypeaheadLocal([], [], 0, 0);
        }

        var contains = $"%{EscapeLike(lower)}%";
        var prefix = $"{EscapeLike(lower)}%";
        var wordStart = WordStartPattern(lower);
        var fuzzy = AllowsFuzzy(lower);
        var maturityCeiling = await eligibility.MaturityCeilingAsync(accountId, ct);

        // Two characters are a word start being typed ("he", "go"), not a substring: matching them
        // anywhere reads ten thousand titles to rank ones nobody meant.
        var shortTerm = lower.Length < 3;
        var matched = await eligibility.ContentFilteredTitles(accountId)
            .Where(t => shortTerm
                ? Regex.IsMatch(t.Name, wordStart, RegexOptions.IgnoreCase)
                : EF.Functions.ILike(t.Name, contains, "\\") || (fuzzy && EF.Functions.TrigramsAreSimilar(t.Name, lower)))
            .Where(t => !db.UserTitleReactions.Any(r =>
                r.AccountId == accountId && r.TitleId == t.Id && r.Kind == ReactionKind.NotInterested && r.RevokedAt == null))
            .Select(t => new
            {
                Title = t,
                Tier = t.Name.ToLower() == lower || Regex.Replace(t.Name.ToLower(), ArticlePattern, string.Empty) == lower
                    ? 0
                    : EF.Functions.ILike(t.Name, prefix, "\\")
                        || EF.Functions.ILike(Regex.Replace(t.Name.ToLower(), ArticlePattern, string.Empty), prefix, "\\")
                        ? 1
                        : Regex.IsMatch(t.Name, wordStart, RegexOptions.IgnoreCase) ? 2 : 3,
                Similarity = EF.Functions.TrigramsSimilarity(t.Name, lower),
            })
            .OrderBy(x => x.Tier)
            .ThenByDescending(x => x.Title.TmdbVoteCount + x.Title.TraktVotes)
            .ThenByDescending(x => x.Similarity)
            .ThenBy(x => x.Title.Id)
            .Take(TitleLimit * 3)
            .AsNoTracking()
            .ToListAsync(ct);

        var withinCeiling = matched
            .Where(x => EligibilityQueryBuilder.PassesMaturity(x.Title, maturityCeiling))
            .Take(TitleLimit)
            .ToList();
        var ids = withinCeiling.Select(x => x.Title.Id).ToList();
        var watchedFull = (await db.WatchedTitles
                .Where(w => w.AccountId == accountId && ids.Contains(w.TitleId) && w.IsFullyWatched)
                .Select(w => w.TitleId)
                .ToListAsync(ct))
            .ToHashSet();

        var titles = withinCeiling
            .Select(x => new TypeaheadTitle(
                x.Title.Id,
                x.Title.MediaType.ToString(),
                x.Title.TmdbId,
                x.Title.Name,
                x.Title.Year,
                x.Title.PosterPath,
                watchedFull.Contains(x.Title.Id),
                CrowdRating.For(x.Title),
                CrowdRating.VotesFor(x.Title),
                (MatchTier)x.Tier))
            .ToList();

        // FD-5: what the conditions hide among the strong matches — every catalogue title the term
        // matches at a word start or better, minus those the conditions let through, plus any the
        // maturity ceiling dropped from the shortlist.
        // Not for a two-character term: "12 matches for 'he' hidden" informs nobody.
        var hidden = 0;
        if (!shortTerm)
        {
            var strongInCatalogue = await db.Titles
                .CountAsync(t => EF.Functions.ILike(t.Name, contains, "\\") && Regex.IsMatch(t.Name, wordStart, RegexOptions.IgnoreCase), ct);
            var strongAllowed = await eligibility.ContentFilteredTitles(accountId)
                .CountAsync(t => EF.Functions.ILike(t.Name, contains, "\\") && Regex.IsMatch(t.Name, wordStart, RegexOptions.IgnoreCase), ct);
            var overCeiling = matched.Count(x => x.Tier <= 2 && !EligibilityQueryBuilder.PassesMaturity(x.Title, maturityCeiling));
            hidden = Math.Max(0, strongInCatalogue - strongAllowed) + overCeiling;
        }

        // A person's surname is as much their name as their first name — "nolan" means Christopher
        // Nolan as surely as "keanu" means Keanu Reeves — so for people a word start anywhere is one
        // tier (Prefix), and the account's own affinity, then the catalogue's credits, decide inside it.
        var people = await db.Persons
            .Where(p => shortTerm
                ? Regex.IsMatch(p.Name, wordStart, RegexOptions.IgnoreCase)
                : EF.Functions.ILike(p.Name, contains, "\\") || (fuzzy && EF.Functions.TrigramsAreSimilar(p.Name, lower)))
            .Select(p => new
            {
                Person = p,
                Tier = p.Name.ToLower() == lower
                    ? 0
                    : Regex.IsMatch(p.Name, wordStart, RegexOptions.IgnoreCase) ? 1 : 3,
                Affinity = db.PersonAffinities
                    .Where(a => a.AccountId == accountId && a.PersonId == p.Id)
                    .Max(a => (decimal?)a.Affinity),
                Credits = db.TitleCredits.Count(c => c.PersonId == p.Id),
                Similarity = EF.Functions.TrigramsSimilarity(p.Name, lower),
            })
            .OrderBy(x => x.Tier)
            .ThenByDescending(x => x.Affinity != null)
            .ThenByDescending(x => x.Affinity)
            .ThenByDescending(x => x.Credits)
            .ThenByDescending(x => x.Similarity)
            .ThenBy(x => x.Person.Id)
            .Take(PersonLimit)
            .AsNoTracking()
            .ToListAsync(ct);

        return new TypeaheadLocal(
            titles,
            [.. people.Select(x => new TypeaheadPerson(
                x.Person.Id, x.Person.Name, x.Person.KnownForDepartment, x.Person.ProfilePath, (MatchTier)x.Tier, x.Affinity))],
            titles.Count(t => t.Tier <= MatchTier.WordStart),
            hidden);
    }

    /// <summary>
    /// The global leg: one TMDB movie search and one show search, unioned, poster-less rows dropped,
    /// popularity-ordered, kept only where the row's own name carries the term at a word start or
    /// better (accents folded) — and judged by the account's conditions like the local leg (FD-5): a row
    /// for a catalogue title passes only if the catalogue title does; any other row by
    /// <see cref="StandingDiscoverPlan.Admits"/>. TMDB being down yields an empty list, never an error.
    /// </summary>
    public async Task<TypeaheadGlobal> GlobalAsync(Guid accountId, ITmdbClient tmdb, string term, CancellationToken ct)
    {
        var trimmed = term.Trim();
        if (trimmed.Length < MinTermLength)
        {
            return new TypeaheadGlobal([], 0);
        }

        IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem> hits;
        try
        {
            var batches = await Task.WhenAll(
                tmdb.SearchTitlesAsync(true, trimmed, 1, ct),
                tmdb.SearchTitlesAsync(false, trimmed, 1, ct));
            hits = Union(batches);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lookup beyond the catalogue is a bonus, never a dependency.
            return new TypeaheadGlobal([], 0);
        }

        if (hits.Count == 0)
        {
            return new TypeaheadGlobal([], 0);
        }

        var rows = await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync(ct);
        var rules = FilterRuleSet.FromRows(rows);
        var tmdbIds = hits.Select(h => (long?)h.Id).ToList();
        var known = await db.Titles
            .Where(t => t.TmdbId != null && tmdbIds.Contains(t.TmdbId))
            .Select(t => new { t.Id, t.TmdbId, t.MediaType })
            .ToListAsync(ct);
        var knownIds = known.Select(k => k.Id).ToList();
        var allowedKnown = (await eligibility.ContentFilteredTitles(accountId)
                .Where(t => knownIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var catalogue = known.ToDictionary(k => (k.MediaType == MediaType.Movie, k.TmdbId!.Value), k => k.Id);

        var kept = new List<GlobalTypeaheadRow>();
        var hidden = 0;
        foreach (var hit in hits)
        {
            // TMDB's search is loose (it matches alternative titles and cast); a row whose own name
            // does not carry the term at a word start is not what the user is looking up, and is
            // neither shown nor counted as hidden.
            if (TierOf(hit.Name, trimmed) > MatchTier.WordStart)
            {
                continue;
            }

            var inCatalogue = catalogue.TryGetValue((hit.IsMovie, hit.Id), out var titleId);
            var passes = inCatalogue ? allowedKnown.Contains(titleId) : StandingDiscoverPlan.Admits(rules, hit);
            if (!passes)
            {
                // A catalogue title the conditions hide was already counted by the local leg (its
                // name carries the term at a word start, the same bar); only titles beyond the
                // catalogue add to the count, so one title is never counted twice.
                if (!inCatalogue)
                {
                    hidden++;
                }

                continue;
            }

            if (kept.Count < GlobalLimit)
            {
                kept.Add(new GlobalTypeaheadRow(
                    "tmdb",
                    hit.IsMovie ? "movie" : "show",
                    hit.Id,
                    hit.Name,
                    hit.ReleasedAt?.Year,
                    hit.PosterPath,
                    hit.VoteAverage == 0m ? null : hit.VoteAverage,
                    null));
            }
        }

        return new TypeaheadGlobal(kept, hidden);
    }

    /// <summary>
    /// The movie and show search pages as one list: deduplicated by (medium, id), poster-less rows
    /// dropped (a bare text row in a poster list reads as broken), most popular first.
    /// </summary>
    public static IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem> Union(IEnumerable<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> batches) =>
        [.. batches
            .SelectMany(b => b)
            .GroupBy(i => (i.IsMovie, i.Id))
            .Select(g => g.First())
            .Where(i => !string.IsNullOrEmpty(i.PosterPath))
            .OrderByDescending(i => i.Popularity)];

    /// <summary>The in-memory twin of the SQL tier, for tests and for re-ranking.</summary>
    public static MatchTier TierOf(string? name, string term)
    {
        // Accents folded on both sides: "amelie" looks up Amélie.
        var lower = Fold(term.Trim().ToLowerInvariant());
        var n = Fold((name ?? string.Empty).ToLowerInvariant());
        var stripped = Article().Replace(n, string.Empty);
        if (n == lower || stripped == lower)
        {
            return MatchTier.Exact;
        }

        if (n.StartsWith(lower, StringComparison.Ordinal) || stripped.StartsWith(lower, StringComparison.Ordinal))
        {
            return MatchTier.Prefix;
        }

        return Regex.IsMatch(n, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(lower)}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            ? MatchTier.WordStart
            : MatchTier.Fuzzy;
    }

    /// <summary>The text with combining marks removed ("amélie" → "amelie").</summary>
    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                folded.Append(c);
            }
        }

        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// True when a term has enough letters and digits for trigram look-alikes to mean anything (three).
    /// pg_trgm ignores everything else, so "50%" would otherwise fuzzily match every title with "50"
    /// in it and a literal search would not be literal.
    /// </summary>
    public static bool AllowsFuzzy(string term) => term.Count(char.IsLetterOrDigit) >= 3 && term.All(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '-' or '\'' or '.' or ':' or '&' or ',' or '!' or '?');

    /// <summary>
    /// <paramref name="term"/> with ILIKE's metacharacters escaped by backslash (the escape character
    /// every query here passes), so the term always matches literally.
    /// </summary>
    public static string EscapeLike(string term)
    {
        var escaped = new StringBuilder(term.Length + 4);
        foreach (var c in term)
        {
            if (c is '\\' or '%' or '_')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>The Postgres (ARE) pattern for "the term starts a word", metacharacters escaped.</summary>
    internal static string WordStartPattern(string lower)
    {
        var escaped = new StringBuilder(lower.Length * 2);
        foreach (var c in lower)
        {
            if (c is '\\' or '^' or '$' or '.' or '|' or '?' or '*' or '+' or '(' or ')' or '[' or ']' or '{' or '}')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return $@"\m{escaped}";
    }

    /// <summary>A leading article, dropped for exact and prefix matching.</summary>
    private const string ArticlePattern = "^(the|a|an) ";

    [GeneratedRegex(ArticlePattern, RegexOptions.CultureInvariant)]
    private static partial Regex Article();
}
