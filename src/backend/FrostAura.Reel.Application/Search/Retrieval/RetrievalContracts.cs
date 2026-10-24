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
/// <param name="CatalogueLabels">Keyword labels from the vocabulary. Empty means "use <paramref name="UserWords"/> as the label".</param>
/// <param name="Genres">Genre slugs that also satisfy the concept.</param>
public sealed record ConceptTerm(string UserWords, IReadOnlyList<string> CatalogueLabels, IReadOnlyList<string> Genres);

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
public sealed record HardConstraints(
    IReadOnlyList<ConceptTerm> Concepts,
    IReadOnlyList<PersonConstraint> People,
    YearRange? Years,
    int? MaxRuntimeMinutes,
    MediaScope Media)
{
    /// <summary>No requirement at all beyond the standing conditions.</summary>
    public static HardConstraints None { get; } = new([], [], null, null, MediaScope.Any);

    /// <summary>True when the request names at least one concept — the case where a candidate needs topical evidence.</summary>
    public bool HasConcepts => Concepts.Count > 0;
}

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
    int Want);

/// <summary>
/// One retrieved title. Every candidate is eligible for the account at the moment it was retrieved.
/// </summary>
/// <param name="FusedScore">Reciprocal-rank fusion score (k=60) across the arms that found it; higher is better.</param>
/// <param name="Similarity">Cosine similarity from the semantic arm, when that arm found it.</param>
/// <param name="Evidence">
/// Why it matches the request, as <c>source:value</c> strings: <c>keyword:ninja</c>, <c>genre:horror</c>,
/// <c>person:Christopher Nolan/director</c>, <c>fts:overview</c>. A candidate for a request with
/// concepts always carries at least one <c>keyword:</c>, <c>genre:</c> or <c>fts:</c> entry.
/// </param>
public sealed record Candidate(Guid TitleId, double FusedScore, double? Similarity, IReadOnlyList<string> Evidence);

/// <summary>Elapsed milliseconds per retrieval stage, keyed by stage name (<c>facet</c>, <c>fts</c>, <c>knn</c>, <c>tmdb</c>, <c>supply</c>).</summary>
public sealed record StageTimings(IReadOnlyDictionary<string, long> ElapsedMs)
{
    /// <summary>No stage ran.</summary>
    public static StageTimings Empty { get; } = new(new Dictionary<string, long>());
}

/// <summary>The candidates for one query, the supply behind them, and where the time went.</summary>
public sealed record RetrievalResult(IReadOnlyList<Candidate> Candidates, SupplyReport Supply, StageTimings Timings);

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
/// and <c>Eligible = Unscored + BelowFloor + ClearingFloor</c>.</para>
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
    bool KnockoutsMeasured)
{
    /// <summary>Eligible, scored and at or above the For-you bar — the only titles that can become picks.</summary>
    public int ClearingFloor => Eligible - BelowFloor - Unscored;
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
    Task<SupplyReport> ReportAsync(Guid accountId, HardConstraints hard, CancellationToken ct);
}
