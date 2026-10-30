using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// Which media a request is about. Persisted as a string wherever it is stored; a reader that meets
/// a value it does not know maps it to <see cref="Any"/> through <see cref="MediaScopes.Parse"/>,
/// so an image rolled back past a newer value still reads every row.
/// </summary>
public enum MediaScope
{
    Any,
    Movies,
    Shows,
}

/// <summary>Conversions for <see cref="MediaScope"/> that every reader shares.</summary>
public static class MediaScopes
{
    /// <summary>The scope a stored string names, or <see cref="MediaScope.Any"/> for anything unknown.</summary>
    public static MediaScope Parse(string? value) =>
        Enum.TryParse<MediaScope>(value, ignoreCase: true, out var scope) && Enum.IsDefined(scope)
            ? scope
            : MediaScope.Any;

    /// <summary>
    /// The scope a request's resolved media types describe (see <see cref="AskMediaTypes.Resolve"/>):
    /// none or both means <see cref="MediaScope.Any"/>.
    /// </summary>
    public static MediaScope From(IReadOnlyCollection<MediaType>? mediaTypes) =>
        mediaTypes switch
        {
            null or { Count: 0 } => MediaScope.Any,
            _ when mediaTypes.Contains(MediaType.Movie) && mediaTypes.Contains(MediaType.Show) => MediaScope.Any,
            _ when mediaTypes.Contains(MediaType.Movie) => MediaScope.Movies,
            _ => MediaScope.Shows,
        };
}

/// <summary>
/// One concept the user asked for, in their words, with the catalogue vocabulary it resolved to.
/// A title satisfies the concept when ANY of its TMDB keywords contains ANY of
/// <paramref name="CatalogueLabels"/> (containment, so "ninja" also matches "female ninja"), or when
/// it carries ANY of <paramref name="Genres"/>. Labels and genres are compared lower-cased; genres
/// are the catalogue's slugs ("science-fiction", not "Sci-Fi").
/// </summary>
/// <param name="UserWords">What the user said ("ninja movies" → "ninja"). Never a standing filter's value.</param>
/// <param name="CatalogueLabels">
/// The label STEMS the concept matches by containment from a word start ("ninja", "shinobi"). Empty
/// means "use <paramref name="UserWords"/> as the label".
/// </param>
/// <param name="Genres">Genre slugs that also satisfy the concept.</param>
/// <param name="ExactLabels">
/// Set by <see cref="Concepts.ConceptResolver"/>: every vocabulary label the stems expand to
/// ("ninja", "female ninja", "ninja warrior", "shinobi", "kunoichi"). When present, the keyword arm is
/// an indexed array overlap on exactly these labels instead of a regex over every title's labels —
/// the same titles as long as the vocabulary is current (it is re-read every few minutes). Null for
/// an unresolved concept, which keeps the containment regex.
/// </param>
/// <param name="StandInWords">
/// True when the caller had no words of the user's for the concept and <paramref name="UserWords"/>
/// holds the planner's keywords joined in their place (<see cref="IntentConstraints.FromIntent"/>
/// without user words: a conversation state rebuilt from a v1 reading, or a v1 topic no word of the
/// user's anchored). The stand-in still names the concept to the planner, embeds and discovers, but it
/// is not something the user SAID, so the name arm reads only its labels that are not curated synonyms
/// (<see cref="HardConstraintFilter.NameLabelsOf"/>): "samurai ronin" would otherwise say "ronin" and
/// count Ronin (1998) as a samurai film by its name (HREV3-RET-A). False — the user's own words — when a
/// stored state predates the field.
/// </param>
public sealed record ConceptTerm(
    string UserWords,
    IReadOnlyList<string> CatalogueLabels,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string>? ExactLabels = null,
    bool StandInWords = false);

/// <summary>A person the request is about, resolved to a catalogue person when possible.</summary>
/// <param name="Name">The name as the user said it; matched case-insensitively when <paramref name="PersonId"/> is null.</param>
/// <param name="PersonId">The resolved catalogue person, when known.</param>
/// <param name="Role">Which credit counts: director, cast, or any credited role.</param>
public sealed record PersonConstraint(string Name, Guid? PersonId, PersonRole Role);

/// <summary>An inclusive release-year window; either bound may be open.</summary>
public sealed record YearRange(int? From, int? To);

/// <summary>
/// What the user REQUIRES of a title, as opposed to what they would prefer. Every concept must be
/// satisfied (AND across concepts, OR inside one); every person must be credited. Standing
/// conditions never live here: they are the account's <c>ContentFilters</c>, applied by
/// <see cref="EligibilityQueryBuilder"/> inside every retrieval and every count.
/// </summary>
/// <param name="Languages">
/// Original languages the request requires ("korean thrillers" → Korean), set only by
/// <see cref="Concepts.ConceptResolver"/> from a concept whose words name a language
/// (<see cref="Concepts.LanguageWords"/>). A title satisfies it when its original language is one of
/// them — the same column the standing language rule reads, so a count and its relaxation describe
/// one shelf. Null (the stored, unresolved state) means none.
/// </param>
public sealed record HardConstraints(
    IReadOnlyList<ConceptTerm> Concepts,
    IReadOnlyList<PersonConstraint> People,
    YearRange? Years,
    int? MaxRuntimeMinutes,
    MediaScope Media,
    IReadOnlyList<LanguageTerm>? Languages = null)
{
    /// <summary>No requirement at all beyond the standing conditions.</summary>
    public static HardConstraints None { get; } = new([], [], null, null, MediaScope.Any);

    /// <summary>True when the request names at least one concept — the case where a candidate needs topical evidence.</summary>
    public bool HasConcepts => Concepts.Count > 0;

    /// <summary>The required languages, never null.</summary>
    public IReadOnlyList<LanguageTerm> LanguageTerms => Languages ?? [];

    /// <summary>
    /// True when the request names something countable — a concept, a person or a language — as opposed
    /// to a mood or an open request, whose "supply" would be the whole catalogue.
    /// </summary>
    public bool HasTopic => HasConcepts || People.Count > 0 || LanguageTerms.Count > 0;
}

/// <summary>A language the request requires, in the user's words, with its ISO 639-1 code as TMDB stores it.</summary>
/// <param name="UserWords">What the user said ("korean").</param>
/// <param name="Code">The original-language code a title carries ("ko").</param>
public sealed record LanguageTerm(string UserWords, string Code);

/// <summary>Which way the user wants an attribute pushed ("funnier", "less violent").</summary>
public enum AttributeTrend
{
    More,
    Less,
}

/// <summary>A soft steer on one title attribute; it can reorder, never exclude.</summary>
public sealed record AttributeDirection(string Attribute, AttributeTrend Trend);

/// <summary>What the user would prefer: moods, attribute steers and reference titles to search near.</summary>
public sealed record SoftPreferences(
    IReadOnlyList<string> Moods,
    IReadOnlyList<AttributeDirection> Directions,
    IReadOnlyList<Guid> ReferenceTitleIds)
{
    /// <summary>No preference.</summary>
    public static SoftPreferences None { get; } = new([], [], []);
}

/// <summary>One retrieval call: whose eligibility, what is required, what is preferred, what to skip, how many.</summary>
/// <param name="Exclude">Titles never to return this turn (already presented, rejected, the reference titles).</param>
/// <param name="Want">How many candidates the caller wants back; the retriever may return fewer, never more.</param>
public sealed record RetrievalQuery(
    Guid AccountId,
    HardConstraints Hard,
    SoftPreferences Soft,
    IReadOnlyCollection<Guid> Exclude,
    int Want)
{
    /// <summary>
    /// Genres the user rejected THIS turn, in their own words ("no fantasy"); applied as extra
    /// ExcludeGenre rules on top of the standing conditions, inside every arm. Never a standing rule.
    /// </summary>
    public IReadOnlyList<string> ExcludeGenres { get; init; } = [];

    /// <summary>Keywords the user rejected this turn ("no zombies"); applied as extra ExcludeKeyword rules inside every arm.</summary>
    public IReadOnlyList<string> ExcludeKeywords { get; init; } = [];

    /// <summary>
    /// Which arms run. Default: all four. A caller that already runs its own semantic and TMDB arms
    /// (v1's pipeline) asks for <see cref="RetrievalArms.Catalogue"/> so nothing is fetched twice.
    /// </summary>
    public RetrievalArms Arms { get; init; } = RetrievalArms.All;

    /// <summary>The user's message, searched on TMDB as a title only when it is shaped like one (<see cref="TitleShape"/>).</summary>
    public string? Message { get; init; }

    /// <summary>Release region for TMDB movie discovery, or null.</summary>
    public string? Region { get; init; }
}

/// <summary>The retrieval arms (program §4.7). Persisted nowhere; flags so a caller can pick a subset.</summary>
[Flags]
public enum RetrievalArms
{
    /// <summary>No arm: supply only.</summary>
    None = 0,

    /// <summary>Facet SQL over keyword labels, names and genres.</summary>
    Facet = 1,

    /// <summary>Full text over the <c>SearchDocument</c> tsvector and the themes document.</summary>
    FullText = 2,

    /// <summary>Semantic kNN on the positive concept text, with a 1.0 s embedding budget.</summary>
    Semantic = 4,

    /// <summary>TMDB discovery with the standing conditions in the request, 1.5 s deadline.</summary>
    Tmdb = 8,

    /// <summary>The two arms that read only the catalogue and need no network.</summary>
    Catalogue = Facet | FullText,

    /// <summary>Every arm.</summary>
    All = Facet | FullText | Semantic | Tmdb,
}

/// <summary>
/// A title TMDB discovery found that is not in the catalogue yet. It passed every condition a TMDB
/// list row carries data for, but NOT the stand-in keywords, keyword-label exclusions or maturity
/// ceiling: the caller hydrates it and re-judges it through <see cref="EligibilityQueryBuilder"/>
/// before it may be shown.
/// </summary>
public sealed record ProvisionalTitle(long TmdbId, MediaType Media, string Name, int? Year);

/// <summary>
/// One retrieved title. Every candidate is eligible for the account at the moment it was retrieved.
/// </summary>
/// <param name="FusedScore">Reciprocal-rank fusion score (k=60) across the arms that found it; higher is better.</param>
/// <param name="Similarity">Cosine similarity from the semantic arm, when that arm found it.</param>
/// <param name="Evidence">
/// Why it matches the request, as <c>source:value</c> strings: <c>keyword:ninja</c>, <c>genre:horror</c>,
/// <c>person:Christopher Nolan/director</c>, <c>name:ninja</c> (the label stands as a word in the
/// title's name), <c>fts:overview</c>. A candidate for a request with concepts always carries at
/// least one <c>keyword:</c>, <c>name:</c>, <c>genre:</c> or <c>fts:</c> entry.
/// </param>
public sealed record Candidate(Guid TitleId, double FusedScore, double? Similarity, IReadOnlyList<string> Evidence);

/// <summary>Elapsed milliseconds per retrieval stage, keyed by stage name (<c>facet</c>, <c>fts</c>, <c>knn</c>, <c>tmdb</c>, <c>supply</c>).</summary>
public sealed record StageTimings(IReadOnlyDictionary<string, long> ElapsedMs)
{
    /// <summary>No stage ran.</summary>
    public static StageTimings Empty { get; } = new(new Dictionary<string, long>());
}

/// <summary>The candidates for one query, the supply behind them, and where the time went.</summary>
public sealed record RetrievalResult(IReadOnlyList<Candidate> Candidates, SupplyReport Supply, StageTimings Timings)
{
    /// <summary>New titles TMDB discovery found that must be hydrated and re-judged before they are shown.</summary>
    public IReadOnlyList<ProvisionalTitle> Provisional { get; init; } = [];

    /// <summary>
    /// Arms that were asked for but contributed nothing because they timed out or failed
    /// (<c>semantic</c>, <c>tmdb</c>, <c>fulltext</c>); the turn fused without them (§4.10).
    /// </summary>
    public IReadOnlyList<string> SkippedArms { get; init; } = [];

    /// <summary>
    /// Candidates whose only evidence for the request's concepts is an overview hit
    /// (<see cref="FullTextHit.OnlyInProse"/>): leads for the judge, not matches. A reply counts them
    /// apart from <see cref="TaggedMatches"/>, so "3 shark films" never counts Sarah's Oil ("greedy oil
    /// sharks") as a shark film nobody checked (HREV5-RET-04).
    /// </summary>
    public int ProseLeads => Candidates.Count(FullTextHit.OnlyInProse);

    /// <summary>
    /// Candidates that are not <see cref="ProseLeads"/>: found by a tag, a name, a genre or an
    /// extracted theme — or, on a turn without a concept, every candidate.
    /// </summary>
    public int TaggedMatches => Candidates.Count - ProseLeads;
}

/// <summary>A title named in a reply or a sample, with just enough to print it.</summary>
public sealed record TitleRef(Guid TitleId, string Name, int? Year, MediaType Media);

/// <summary>
/// What relaxing ONE standing condition would unlock for this request.
/// </summary>
/// <param name="Kind">The condition's kind, so a chip can open the Conditions editor on that rule.</param>
/// <param name="Value">The value the user would tap off: a genre slug, a threshold, or the language allow-list ("en").</param>
/// <param name="ContentFilterId">
/// The stored row behind the rule, or null when the rule is a default with no row (English-only
/// with no language rows configured). The language allow-list is one rule; its first row is named.
/// </param>
/// <param name="Label">The rule in plain words ("excluded genre: comedy").</param>
/// <param name="WouldUnlock">
/// Topical titles, with at least <see cref="SupplyReport.MinVotes"/> crowd votes and no interaction
/// footprint, that become eligible when this rule alone is relaxed. Never zero: a rule that unlocks
/// nothing is not offered.
/// </param>
/// <param name="Sample">Up to a handful of the unlocked titles, most-voted first.</param>
public sealed record ConditionKnockout(
    FilterKind Kind,
    string Value,
    Guid? ContentFilterId,
    string Label,
    int WouldUnlock,
    IReadOnlyList<TitleRef> Sample);

/// <summary>
/// The honest supply behind a request: how many titles in the catalogue match it, and where every
/// one of them went. It is computed from the account's real state through the one eligibility
/// builder, so a reply built from it cannot claim a number the catalogue does not hold.
///
/// <para><b>Population.</b> Every count is over the same population: titles that satisfy the
/// request's hard constraints AND carry at least <see cref="MinVotes"/> crowd votes (the
/// TMDB-then-Trakt count from <see cref="CrowdRating"/>). Low-vote titles are counted once, in
/// <see cref="LowVoteMatches"/>, and nowhere else, so a 2-vote curiosity never inflates "N left".</para>
///
/// <para><b>Partition.</b> The population splits exactly, each title in the first bucket it reaches:
/// <c>MatchingInCatalogue = AlreadySeen + SavedOrDismissed + ExcludedByConditions + Eligible</c>,
/// <c>Eligible = Unscored + BelowFloor + ClearingFloor</c>, and
/// <c>ClearingFloor = AlreadyShown + Offerable</c>.</para>
/// </summary>
/// <param name="MatchingInCatalogue">Titles satisfying the hard constraints, at or above the vote floor.</param>
/// <param name="AlreadySeen">Of those, watched (even partly) or rated. Never offered as a relaxation.</param>
/// <param name="SavedOrDismissed">Not seen, but saved for later, marked not interested or dropped.</param>
/// <param name="ExcludedByConditions">No footprint, but held out by a standing condition or the maturity ceiling.</param>
/// <param name="Eligible">No footprint and inside every standing condition.</param>
/// <param name="BelowFloor">Eligible and scored, but under the account's For-you bar at badge precision.</param>
/// <param name="Unscored">Eligible with no prediction from the active model.</param>
/// <param name="Knockouts">
/// Drop-one relaxations that unlock at least one title, biggest first. Empty either because no single
/// relaxation unlocks anything or because they were not measured; <see cref="KnockoutsMeasured"/> says which.
/// </param>
/// <param name="LowVoteMatches">Titles satisfying the hard constraints with fewer than <see cref="MinVotes"/> crowd votes; not split further.</param>
/// <param name="MinVotes">The vote floor the population was counted with.</param>
/// <param name="PredictedFloor">The For-you bar <see cref="BelowFloor"/> was measured against; null when none is set.</param>
/// <param name="KnockoutsMeasured">
/// False only for a broad request (a large population) with plenty clearing the bar, where measuring
/// relaxations would re-count the whole catalogue per rule and no relaxation is worth offering.
/// </param>
/// <param name="AlreadyShown">
/// Of <see cref="ClearingFloor"/>, the titles this conversation has already shown. They are still
/// eligible (nothing about them changed) but they are not offered again, so they are excluded from
/// <see cref="Offerable"/> and from every knockout's unlock count. Zero when the caller passed no
/// shown titles.
/// </param>
public sealed record SupplyReport(
    int MatchingInCatalogue,
    int AlreadySeen,
    int SavedOrDismissed,
    int ExcludedByConditions,
    int Eligible,
    int BelowFloor,
    int Unscored,
    IReadOnlyList<ConditionKnockout> Knockouts,
    int LowVoteMatches,
    int MinVotes,
    decimal? PredictedFloor,
    bool KnockoutsMeasured,
    int AlreadyShown = 0)
{
    /// <summary>Eligible, scored and at or above the For-you bar — the only titles that can become picks.</summary>
    public int ClearingFloor => Eligible - BelowFloor - Unscored;

    /// <summary>Clearing the bar and not yet shown in this conversation — the supply a reply may still offer.</summary>
    public int Offerable => ClearingFloor - AlreadyShown;
}

/// <summary>
/// The retrieval tool Ask Reel's executor calls (<c>search_catalog</c> and <c>similar_to</c>).
/// Eligibility is applied inside: nothing it returns has an interaction footprint or breaks a
/// standing condition, for movies and TV alike.
/// </summary>
public interface IAskReelRetriever
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery q, CancellationToken ct);
}

/// <summary>
/// The supply tool (<c>supply(hard)</c>): counts only, no candidates. Answers "how many X are
/// left?" and feeds the NoneFound reply.
/// </summary>
public interface ISupplyReporter
{
    /// <summary>The supply behind <paramref name="hard"/>, with nothing yet shown.</summary>
    Task<SupplyReport> ReportAsync(Guid accountId, HardConstraints hard, CancellationToken ct) =>
        ReportAsync(accountId, hard, [], ct);

    /// <summary>
    /// The supply behind <paramref name="hard"/>, with <paramref name="alreadyShown"/> (the titles
    /// this conversation has shown) counted in <see cref="SupplyReport.AlreadyShown"/> and excluded
    /// from what is offered.
    /// </summary>
    Task<SupplyReport> ReportAsync(
        Guid accountId, HardConstraints hard, IReadOnlyCollection<Guid> alreadyShown, CancellationToken ct);

    /// <summary>
    /// The supply as a surface that waives the account's include-genre rules counts it: a genre rail
    /// (FD-6) passes <see cref="IncludeGenreRules.Waived"/>, so the N in "your conditions hide N" is
    /// what the rail itself hides. Exclude-genre rules are never waived.
    /// </summary>
    Task<SupplyReport> ReportAsync(
        Guid accountId,
        HardConstraints hard,
        IReadOnlyCollection<Guid> alreadyShown,
        IncludeGenreRules includeGenre,
        CancellationToken ct);

    /// <summary>
    /// The supply behind <paramref name="hard"/> with the turn's own rejections taken OUT OF THE
    /// TOPIC (HREV4-ASK-01): a title the user ruled out is not one they asked for, so it is in no
    /// bucket — not matching, not seen, not held out — and no knockout counts or names it. "Ninja movies
    /// but no turtles" was counted as the plain ninja shelf, and offered a relaxation whose sample
    /// was Turtles Forever. Matched exactly as retrieval matches them (name, overview or tag for a
    /// keyword; the genre, its aliases and stand-ins for a genre).
    /// </summary>
    Task<SupplyReport> ReportAsync(
        Guid accountId,
        HardConstraints hard,
        IReadOnlyCollection<Guid> alreadyShown,
        TurnExclusions exclusions,
        CancellationToken ct);
}

/// <summary>
/// The user's own rejections for one turn, in catalogue form ("no turtles" → keyword "turtles", "no
/// comedy" → genre "comedy"): the same extra exclusions <see cref="RetrievalQuery.ExcludeGenres"/> and
/// <see cref="RetrievalQuery.ExcludeKeywords"/> carry into retrieval. Never a standing rule, never a
/// relaxation.
/// </summary>
public sealed record TurnExclusions(IReadOnlyList<string> Genres, IReadOnlyList<string> Keywords)
{
    /// <summary>No rejection this turn.</summary>
    public static TurnExclusions None { get; } = new([], []);

    /// <summary>True when the turn rejected nothing.</summary>
    public bool IsEmpty => Genres.Count == 0 && Keywords.Count == 0;

    /// <summary>The rejections a retrieval query carries.</summary>
    public static TurnExclusions Of(RetrievalQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        return new TurnExclusions(q.ExcludeGenres, q.ExcludeKeywords);
    }
}

/// <summary>
/// Whether a count applies the account's include-genre rules. Never persisted; a surface that waives
/// them (genre rails, FD-6) says so explicitly.
/// </summary>
public enum IncludeGenreRules
{
    /// <summary>Every standing condition applies (Ask Reel, the feed's main grid).</summary>
    Applied,

    /// <summary>Include-genre rules are left out; exclude-genre and every other rule still apply.</summary>
    Waived,
}
