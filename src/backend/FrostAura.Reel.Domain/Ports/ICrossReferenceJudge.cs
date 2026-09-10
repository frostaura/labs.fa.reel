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
    /// Judge one batch. <paramref name="usagePurpose"/> is <c>cross-reference</c> for a serving run
    /// and <c>cross-reference-eval</c> for the backtest — deliberately different strings, so
    /// validating the instrument cannot consume the budget it is validating.
    /// </summary>
    Task<CrossReferenceBatchResult> JudgeAsync(
        IReadOnlyList<CrossReferenceCandidate> candidates,
        LensPersona persona,
        CrossReferenceRound round,
        string usagePurpose,
        CancellationToken ct = default);
}
