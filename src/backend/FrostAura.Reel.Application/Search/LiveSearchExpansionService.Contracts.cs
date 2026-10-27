using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The turn contract the endpoint and BOTH engines share — the emitter, the stored card, the outcome and the
/// turn's input. It outlives the v1 pipeline: at cutover (§4.13) the v1 partials go and this file stays.
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>An SSE event emitter — (eventName, payload). The endpoint writes it to the stream.</summary>
    public delegate Task Emit(string eventName, object payload);

    /// <summary>
    /// One card of the answer, as a row rather than as JSON. Built from the SAME projection as the
    /// <c>done.results</c> array and in the same order, so <see cref="Rank"/> is the position on
    /// screen, the stored rank and the exposure rank — one number, three readers, no chance of
    /// them disagreeing.
    /// </summary>
    /// <param name="TitleId">The title on the card.</param>
    /// <param name="TmdbId">Denormalised TMDB id, kept for readers that still key on it.</param>
    /// <param name="Rank">Index in the stored answer, zero-based: picks, then outside cards, then the trays.</param>
    /// <param name="OutsideRequest">The closest-but-outside reserve flag, as it was on the wire.</param>
    /// <param name="Similarity">Cosine to the ranking vector, rounded as rendered.</param>
    /// <param name="PredictedRating">The prediction the card carried, at the column's 2 dp.</param>
    /// <param name="LlmFit">The judge's fit, at the column's 3 dp — only where the MODEL produced it.</param>
    /// <param name="Blend">The server's ranking value.</param>
    /// <param name="Why">The why-line rendered, verbatim; null where none was shown.</param>
    /// <param name="WhySource">How that sentence was produced — an <c>ExposureWhySource</c> value.</param>
    /// <param name="BelowFloor">
    /// True for every card that is not a pick — the floor tray AND the near-miss tray — so an image
    /// that predates <paramref name="Tier"/> can never replay a tray card as a pick.
    /// </param>
    /// <param name="Tier">One of <c>SearchTurnCardTier</c>; null only from a caller that predates it.</param>
    /// <param name="Satisfies">The judge's request verdict (<c>yes</c>/<c>partial</c>/<c>no</c>), or null when unjudged.</param>
    /// <param name="FitSource">Where <paramref name="LlmFit"/> came from, by name; null where there is no fit.</param>
    /// <param name="SatisfiesEvidence">The v2 judge's evidence line for its verdict; null on v1 and where unjudged.</param>
    public readonly record struct TurnCard(
        Guid TitleId,
        long? TmdbId,
        int Rank,
        bool OutsideRequest,
        double? Similarity,
        decimal? PredictedRating,
        decimal? LlmFit,
        decimal Blend,
        string? Why,
        string WhySource,
        bool BelowFloor = false,
        string? Tier = null,
        string? Satisfies = null,
        string? FitSource = null,
        string? SatisfiesEvidence = null);

    /// <summary>
    /// What the turn was, handed back to whoever has a place to put it.
    ///
    /// <para>The service streams the answer and returns this; the ENDPOINT owns persistence,
    /// because only it knows which row this turn is and it must write on a scope that outlives the
    /// request's own tracked context. Nothing here is read back by the pipeline — a turn that is
    /// never stored behaves identically — so a persistence failure can never cost the user the
    /// answer they are already looking at.</para>
    /// </summary>
    /// <param name="AssistantText">The reply, exactly as emitted.</param>
    /// <param name="Reason">The <c>done.reason</c> clause list, exactly as emitted.</param>
    /// <param name="Failed">Whether the terminal frame admitted a crash.</param>
    /// <param name="FailureCode">The exception type or pipeline code behind <paramref name="Failed"/>.</param>
    /// <param name="Relaxation">How many levels discovery widened through.</param>
    /// <param name="FloorRelaxed">Always false since the floor stopped relaxing; kept for the column.</param>
    /// <param name="DiscoveredCount">How many candidates discovery returned, before eligibility.</param>
    /// <param name="IntentJson">The interpreted intent, serialised.</param>
    /// <param name="ModelArtifactId">The artifact behind the predictions on these cards.</param>
    /// <param name="ModelId">The chat model that produced the reply.</param>
    /// <param name="Cards">The answer, in stored order.</param>
    /// <param name="ConflictJson">The <c>conflict</c> frame this turn emitted, serialised, or null.</param>
    /// <param name="Mode">One of <c>SearchTurnMode</c>; null where the turn never reached a mode.</param>
    /// <param name="DegradedFlags">Comma-separated <c>SearchTurnDegraded</c> tokens, or null.</param>
    /// <param name="StageTimingsJson">The per-stage milliseconds, serialised.</param>
    /// <param name="ModelIdsJson">The models behind the turn by role, serialised.</param>
    /// <param name="ReasoningEffort">The effort the turn's model calls were made at.</param>
    /// <param name="Act">The v2 planner's act, by name; null on a v1 turn.</param>
    /// <param name="PlanSource">Where the v2 plan came from, by name; null on a v1 turn.</param>
    /// <param name="TruncationCount">How many of a v2 turn's calls finished on <c>length</c>; null on v1.</param>
    /// <param name="ConversationStateJson">The conversation's state after a v2 turn, for <c>SearchConversations.StateJson</c>.</param>
    public sealed record TurnOutcome(
        string? AssistantText,
        string? Reason,
        bool Failed,
        string? FailureCode,
        int? Relaxation,
        bool FloorRelaxed,
        int? DiscoveredCount,
        string? IntentJson,
        Guid? ModelArtifactId,
        string? ModelId,
        IReadOnlyList<TurnCard> Cards,
        string? ConflictJson = null,
        string? Mode = null,
        string? DegradedFlags = null,
        string? StageTimingsJson = null,
        string? ModelIdsJson = null,
        string? ReasoningEffort = null,
        string? Act = null,
        string? PlanSource = null,
        int? TruncationCount = null,
        string? ConversationStateJson = null)
    {
        /// <summary>A turn that answered without the full pipeline — an empty query, a fallback, a filmography.</summary>
        public static TurnOutcome Answered(string? assistantText, string? reason, IReadOnlyList<TurnCard>? cards = null) =>
            new(assistantText, reason, Failed: false, FailureCode: null, Relaxation: null, FloorRelaxed: false,
                DiscoveredCount: null, IntentJson: null, ModelArtifactId: null, ModelId: null, cards ?? []);

        /// <summary>
        /// A turn that fell over. <paramref name="failureCode"/> is what the row records.
        ///
        /// <para><paramref name="conflictJson"/> survives the crash for the same reason
        /// <paramref name="assistantText"/> does: both went out on the wire before it.
        /// <paramref name="cards"/> is non-empty only for a turn the CLIENT ended: the cards it had
        /// already been shown are recorded, so they enter history and the no-repeat set (B-3).</para>
        /// </summary>
        public static TurnOutcome Crashed(
            string? assistantText, string reason, string failureCode, string? conflictJson = null,
            IReadOnlyList<TurnCard>? cards = null, string? degradedFlags = null) =>
            new(assistantText, reason, Failed: true, failureCode, Relaxation: null, FloorRelaxed: false,
                DiscoveredCount: null, IntentJson: null, ModelArtifactId: null, ModelId: null, cards ?? [], conflictJson,
                DegradedFlags: degradedFlags);
    }

    /// <summary>
    /// Everything one Ask Reel turn is given — all of it derived by the endpoint from rows it wrote,
    /// never from the client.
    /// </summary>
    /// <param name="Message">The question.</param>
    /// <param name="History">The transcript the model reads, assistant lines carrying their shown handles.</param>
    /// <param name="ShownTitleIds">The conversation's no-repeat set, keyed on the title (B-16).</param>
    /// <param name="Presented">The last answers' cards with their handles, for reference resolution.</param>
    /// <param name="Region">The account's watch region.</param>
    /// <param name="MediaFilter">The explicit UI media filter; empty for none.</param>
    /// <param name="RerankPurpose">Which member of the re-rank family the judge bills to.</param>
    /// <param name="Debug">Whether the founder-only diagnostics ride the terminal frame.</param>
    /// <param name="SourceTurnId">The assistant row this turn is, for the exposure key.</param>
    /// <param name="Progress">Where the turn records what it has emitted, so an abort can persist it.</param>
    /// <param name="PriorIntent">The previous answer's reading, for what a silent follow-up inherits (v1).</param>
    /// <param name="ConversationId">The conversation, for the v2 engine's state.</param>
    /// <param name="AnswerNumber">This answer's number in the conversation, for its handles ("T{n}.{rank}").</param>
    /// <param name="PersistCards">Writes cards the moment they are emitted (v2), so a turn that dies keeps what it showed.</param>
    public sealed record AskTurnInput(
        string Message,
        IReadOnlyList<ChatTurn> History,
        IReadOnlyCollection<Guid> ShownTitleIds,
        IReadOnlyList<PresentedCard> Presented,
        string? Region,
        IReadOnlyList<MediaType> MediaFilter,
        string RerankPurpose,
        bool Debug = false,
        Guid? SourceTurnId = null,
        AskTurnProgress? Progress = null,
        SearchIntent? PriorIntent = null,
        Guid? ConversationId = null,
        int AnswerNumber = 1,
        Func<IReadOnlyList<TurnCard>, Task>? PersistCards = null);
}
