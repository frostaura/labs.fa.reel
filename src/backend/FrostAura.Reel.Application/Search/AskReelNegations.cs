using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// What a turn is allowed to treat as a negation, and where each one came from.
///
/// <para><b>There are exactly two legitimate sources, and they behave differently.</b> A STANDING
/// exclusion is the account's own permanent rule, set in the conditions editor behind a PIN; it is
/// airtight, and a title that trips one may not reach the screen at all. A REQUESTED negation is
/// something the user typed in this turn — "no horror please" — and it is soft: it demotes a title
/// out of the picks, and the closest contradicting ones may still be shown as an explicitly-flagged
/// "outside your request" card when the clean set cannot fill a screen.</para>
///
/// <para><b>What is NOT a source: the interpret model's own inferences.</b>
/// <c>SearchIntent.ExcludeGenres</c> is a mixed bag — it carries the standing rules the prompt was
/// told about and dutifully echoed back, the negations the user actually typed, and whatever the
/// model decided on its own — and nothing downstream could tell the three apart. Two defects came
/// straight out of that. A standing exclusion arrived as a soft negation, so comedy titles appeared
/// in the outside channel underneath a notice stating that comedy can never appear. And an
/// inference the user never made was captioned "which you asked to skip": for "a 90s romantic
/// comedy", seven of eleven outside cards claimed the user had asked to skip horror, which they had
/// not — in this question or in their settings. A model's guess is not a user statement and must
/// not be quoted back as one, so an inferred exclusion that is neither standing nor typed simply
/// stops being a negation here. It still narrows the TMDB pull, where being wrong costs recall
/// rather than honesty.</para>
///
/// <para><paramref name="RequestedGenres"/> and <paramref name="RequestedKeywords"/> come from the
/// interpreter's free deterministic reading of the user's own words
/// (<see cref="ISearchQueryInterpreter.Heuristic"/>, carried on <see cref="AgentReply.Requested"/>),
/// because that is the one reading no prompt can launder.</para>
/// </summary>
/// <param name="StandingGenres">The account's permanent <c>ExcludeGenre</c> rules. Airtight.</param>
/// <param name="StandingKeywords">The account's permanent <c>ExcludeKeyword</c> rules. Airtight.</param>
/// <param name="RequestedGenres">Genres the user's own words ruled out in this turn. Soft.</param>
/// <param name="RequestedKeywords">Words the user's own words ruled out in this turn. Soft.</param>
public sealed record AskReelNegations(
    IReadOnlyList<string> StandingGenres,
    IReadOnlyList<string> StandingKeywords,
    IReadOnlyList<string> RequestedGenres,
    IReadOnlyList<string> RequestedKeywords)
{
    /// <summary>A turn with nothing ruled out, by anybody.</summary>
    public static readonly AskReelNegations None = new([], [], [], []);

    /// <summary>True when the account has a permanent rule that could cull a title outright.</summary>
    public bool HasStanding => StandingGenres.Count > 0 || StandingKeywords.Count > 0;

    /// <summary>True when the user's own words ruled something out in this turn.</summary>
    public bool HasRequested => RequestedGenres.Count > 0 || RequestedKeywords.Count > 0;

    /// <summary>
    /// The turn's negation model: the account's standing rules, plus whatever the user's own words
    /// ruled out. <paramref name="requested"/> is <see cref="AgentReply.Requested"/>; null means
    /// "no second reading", and the turn then carries standing rules only — which is the safe
    /// direction, because the half that can be lost is the soft half.
    /// </summary>
    public static AskReelNegations For(FilterRuleSet rules, SearchIntent? requested) =>
        new(
            rules.ExcludeGenres,
            rules.ExcludeKeywords,
            Clean(requested?.ExcludeGenres),
            Floored(Clean(requested?.ExcludeKeywords)));

    /// <summary>
    /// A turn with no standing rules whose user typed these negations — the "no horror please"
    /// shape, and the only shape most tests need.
    /// </summary>
    public static AskReelNegations FromRequest(
        IReadOnlyList<string>? genres = null, IReadOnlyList<string>? keywords = null) =>
        new([], [], Clean(genres), Floored(Clean(keywords)));

    /// <summary>
    /// A turn whose account's conditions exclude these — the airtight shape.
    /// </summary>
    public static AskReelNegations FromStanding(
        IReadOnlyList<string>? genres = null, IReadOnlyList<string>? keywords = null) =>
        new(Clean(genres), Clean(keywords), [], []);

    /// <summary>
    /// Blank and whitespace-only entries removed, because a stored filter row can legitimately be
    /// empty and an empty needle matches every haystack — which would cull the entire pool.
    /// </summary>
    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? values) =>
        values is null or { Count: 0 }
            ? []
            : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();

    /// <summary>
    /// The vocabulary floor, applied to the REQUESTED side only.
    ///
    /// <para>A requested negation is quoted straight back at the user — "one that matches ‘set’ —
    /// which you asked to skip" — so a token the product cannot honestly name is a caption it must
    /// not print. A live turn containing "nothing <b>set</b> in space" and "not … at <b>all</b>"
    /// demoted a crime/mystery/drama that answered the request exactly, out of the picks and into
    /// the reserve channel, for containing the word "set". The scan is floored at source; this is
    /// the same rule applied where the caption is actually built, so no other route into
    /// <see cref="RequestedKeywords"/> can reopen it.</para>
    ///
    /// <para><b>Standing rules are never floored.</b> They are rows the account wrote in the
    /// conditions editor behind a PIN. The product does not get to decide a user's own permanent
    /// rule was too generic to honour — and the sentence it carries ("which your conditions
    /// exclude") makes no claim about what they typed this turn.</para>
    /// </summary>
    private static IReadOnlyList<string> Floored(IReadOnlyList<string> values) =>
        values.Count == 0 ? values : values.Where(SearchVocabulary.IsNegatable).ToList();
}

/// <summary>
/// One exclusion a title actually trips, phrased for the outside-request caption, with the source
/// that decides how the sentence ends.
/// </summary>
/// <param name="Phrase">"a horror title", "one that matches ‘ghosts’".</param>
/// <param name="Standing">
/// True when the rule is the account's permanent one rather than something the user typed this
/// turn. The distinction reaches the screen: "which your conditions exclude" and "which you asked
/// to skip" are different claims about the same card, and the copy used to make both of them with
/// one sentence.
/// </param>
public readonly record struct NegationMatch(string Phrase, bool Standing);
