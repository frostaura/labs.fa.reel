using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Domain.Ports;

/// <summary>
/// One title this viewer has actually rated, sitting near a candidate in embedding space, with the
/// viewer's own score. The evidence the whole cross-reference idea rests on: the judge is never
/// asked "would someone like this", it is asked "given that this person gave <i>Sicario</i> a 10
/// and this is 0.83 away from it, what do you think".
/// </summary>
/// <param name="Name">The neighbour's title.</param>
/// <param name="Year">Release year, omitted from the prompt when unknown.</param>
/// <param name="Rating">The viewer's own 1–10 score. Never a predicted one.</param>
/// <param name="Similarity">Cosine similarity to the candidate, 0–1.</param>
/// <param name="TitleId">The neighbour's id, persisted with the verdict so the evidence is replayable.</param>
public sealed record CrossReferenceNeighbour(
    string Name, int? Year, short Rating, double Similarity, Guid TitleId);

/// <summary>
/// One candidate as the judge sees it. Every optional field is OMITTED from the rendered prompt
/// when null rather than zero-filled — the same rule the lens input builder follows, and for the
/// same reason: a zero is a claim, and "pacing 0.0" reads as "glacial" rather than "unmeasured".
///
/// <para><see cref="Neighbours"/> is empty when the title has no plot embedding. A candidate with
/// no embedding AND no attributes is not judged at all — a verdict on a name is not a verdict.</para>
/// </summary>
public sealed record CrossReferenceCandidate(
    Guid TitleId,
    string Name,
    int? Year,
    string MediaType,
    IReadOnlyList<string> Genres,
    decimal? Predicted,
    string? Tone,
    string? Era,
    IReadOnlyList<string> Themes,
    decimal? Darkness,
    decimal? Pacing,
    decimal? Complexity,
    decimal? EmotionalIntensity,
    decimal? Humor,
    decimal? Optimism,
    decimal? EnsembleVsSolo,
    string? Director,
    string? Writer,
    IReadOnlyList<string> TopCast,
    IReadOnlyList<CrossReferenceNeighbour> Neighbours,
    string? Overview);

/// <summary>
/// One entry the judge returned, already validated. Confidence is clamped to 0–1 and the
/// verdict × reason pair is legal — see <c>CrossReferenceReasons.IsLegal</c>.
/// </summary>
public sealed record CrossReferenceEntry(
    Guid TitleId, CrossReferenceVerdict Verdict, string Reason, double Confidence);

/// <summary>
/// One batch's answer plus what it cost. Tokens are carried out rather than only recorded on the
/// ledger because the backtest persists its own spend on <c>CrossReferenceEvaluation</c>, and a
/// number read back from a day-grained ledger cannot be attributed to one run.
/// </summary>
/// <param name="Entries">
/// One per candidate the judge answered about — never padded. <b>An omitted candidate gets no
/// row</b>: absence is what the resume anti-join reads as "still to do", so inventing an Unsure
/// for it would mark it done and it would never be looked at again.
/// </param>
/// <param name="Succeeded">
/// False when the transport failed, the body did not parse, or the completion was cut at the token
/// limit. Distinct from an empty <paramref name="Entries"/> on a successful call, which is a real
/// (and loud) answer: the judge said nothing about anything.
/// </param>
public sealed record CrossReferenceBatchResult(
    IReadOnlyList<CrossReferenceEntry> Entries,
    bool Succeeded,
    long? PromptTokens,
    long? CompletionTokens,
    int RejectedEntries);

/// <summary>
/// Everything about a batch that is a property of the PROMPT GENERATION rather than of the
/// candidates — carried in one record so adding a v3 input does not re-shape the port again.
///
/// <para><b>Every field but the version is optional and is OMITTED from the prompt when null</b>,
/// the same rule the candidate line follows: an affinity line reading "genres you rate highest:"
/// with nothing after it, or a base rate of 0%, are both claims about the viewer rather than
/// admissions that nobody measured.</para>
/// </summary>
/// <param name="PromptVersion">
/// Which prompt generation to render and validate against. It is also part of the verdict key, so
/// two generations judging the same title are two rows rather than an overwrite — which is exactly
/// what makes a v1-versus-v2 comparison possible on one account's history.
/// </param>
/// <param name="GenreAffinity">
/// The account's as-of genre affinity line — top genres by shrunken mean with the mean itself, and
/// the bottom few. <b>As-of only</b>: the stored taste narrative was written from every rating the
/// account has ever made, holdout included, and handing it to a backtest is the answer key.
/// </param>
/// <param name="LovedShare">
/// The share of this viewer's rated titles scored ≥ 8, 0–1. Rendered as the base rate a calibrated
/// confidence is supposed to be anchored on.
/// </param>
/// <param name="DislikedShare">The share scored ≤ 4, 0–1.</param>
public sealed record CrossReferenceJudgeOptions(
    int PromptVersion,
    string? GenreAffinity = null,
    decimal? LovedShare = null,
    decimal? DislikedShare = null);

/// <summary>
/// The cross-reference judge — its own port rather than a method on <c>ISearchAgent</c>.
///
/// <para><b>Why not reuse the search agent.</b> <c>ISearchAgent.RerankBatchAsync</c> transports
/// with <c>response_format: json_object</c> and no <c>max_tokens</c> at all, which is survivable
/// for a nine-title rail and is not survivable for a sixty-title batch whose whole design depends
/// on a closed enum and a bounded completion. This port carries a strict <c>json_schema</c>, a
/// computed token ceiling, and a <c>finish_reason=length</c> discard — none of which can be added
/// to the rerank path without changing what every existing rail costs.</para>
///
/// <para><b>Stub mode</b> (no key, or <c>TestHelpers:ModelStubMode</c>) answers deterministically
/// from the candidate's own predicted rating and its neighbour mean, exactly as the other
/// OpenRouter adapters derive their stubs from their inputs: the job, the aggregator, the
/// persistence and the backtest all run in tests and in keyless dev without a billable call.</para>
/// </summary>
public interface ICrossReferenceJudge
{
    bool IsAvailable { get; }

    string ModelId { get; }

    /// <summary>
    /// The prompt generation, and part of the verdict key. Bump when a change alters what a stored
    /// verdict MEANS — never for a transport fix or a retry strategy. A bump makes every stored
    /// verdict a different key rather than invalidating anything, so the old rows stay readable and
    /// the new ones are earned one batch at a time under the daily ceiling.
    /// </summary>
    int PromptVersion { get; }

    /// <summary>
    /// Judge one batch at this adapter's current prompt generation. <paramref name="usagePurpose"/>
    /// is <c>cross-reference</c> for a serving run and <c>cross-reference-eval</c> for the backtest
    /// — deliberately different strings, so validating the instrument cannot consume the budget it
    /// is validating.
    /// </summary>
    Task<CrossReferenceBatchResult> JudgeAsync(
        IReadOnlyList<CrossReferenceCandidate> candidates,
        LensPersona persona,
        CrossReferenceRound round,
        string usagePurpose,
        CancellationToken ct = default)
        => JudgeAsync(
            candidates, persona, round, usagePurpose, new CrossReferenceJudgeOptions(PromptVersion), ct);

    /// <summary>
    /// Judge one batch at an EXPLICIT prompt generation.
    ///
    /// <para>The serving job never calls this — it takes the overload above and rides
    /// <see cref="PromptVersion"/>, so a generation reaches production by being made current and
    /// not by a caller choosing one. This exists for the backtest, which has to be able to measure
    /// a generation that is not yet current: the whole point of the instrument is that a prompt
    /// change is a measured decision rather than a hopeful edit.</para>
    /// </summary>
    Task<CrossReferenceBatchResult> JudgeAsync(
        IReadOnlyList<CrossReferenceCandidate> candidates,
        LensPersona persona,
        CrossReferenceRound round,
        string usagePurpose,
        CrossReferenceJudgeOptions options,
        CancellationToken ct = default);
}
