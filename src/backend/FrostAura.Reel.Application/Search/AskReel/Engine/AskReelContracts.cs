using System.Globalization;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// What a message does to the conversation (§4.3). Persisted as its NAME on <c>SearchTurns.Act</c>;
/// a reader that meets a name it does not know reads <see cref="NewRequest"/> through
/// <see cref="TurnActs.Parse"/>, so an image rolled back past a newer act still reads every row.
/// </summary>
public enum TurnAct
{
    NewRequest,
    Refine,
    CritiqueSet,
    CritiqueItem,
    ItemQuestion,
    Chitchat,
}

/// <summary>The kind of answer a turn gave. Persisted as its name on <c>SearchTurns.Mode</c>.</summary>
public enum FinalMode
{
    Picks,
    Answer,
    Clarify,
    NoneFound,
}

/// <summary>
/// Where the turn's plan came from. Persisted as its name on <c>SearchTurns.PlanSource</c>; an
/// unknown name reads as <see cref="CarriedForward"/> — the reading that claims the least.
/// </summary>
public enum PlanSource
{
    Llm,
    LlmRetry,
    CarriedForward,
}

/// <summary>Name ↔ value conversions with the §5.0 rule-8 fallbacks.</summary>
public static class TurnActs
{
    public static TurnAct Parse(string? value) =>
        Enum.TryParse<TurnAct>(value, ignoreCase: false, out var act) && Enum.IsDefined(act) ? act : TurnAct.NewRequest;

    public static PlanSource ParseSource(string? value) =>
        Enum.TryParse<PlanSource>(value, ignoreCase: false, out var source) && Enum.IsDefined(source)
            ? source
            : PlanSource.CarriedForward;

    /// <summary>True for the acts that search the catalogue and put cards on screen.</summary>
    public static bool Searches(TurnAct act) =>
        act is TurnAct.NewRequest or TurnAct.Refine or TurnAct.CritiqueSet or TurnAct.CritiqueItem;
}

/// <summary>A card as the user and the planner both name it: answer <see cref="Turn"/>, position <see cref="Rank"/> (1-based).</summary>
public readonly record struct TurnHandle(int Turn, int Rank)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"T{Turn}.{Rank}");

    /// <summary>Reads "T2.3" (case-insensitive, surrounding space ignored); null for anything else.</summary>
    public static TurnHandle? Parse(string? text)
    {
        var s = text?.Trim();
        if (s is not { Length: >= 4 } || (s[0] != 'T' && s[0] != 't'))
        {
            return null;
        }

        var dot = s.IndexOf('.', StringComparison.Ordinal);
        return dot > 1
            && int.TryParse(s.AsSpan(1, dot - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var turn)
            && int.TryParse(s.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var rank)
            && turn > 0 && rank > 0
                ? new TurnHandle(turn, rank)
                : null;
    }
}

/// <summary>A title the conversation showed, under its handle.</summary>
public sealed record PresentedRef(Guid TitleId, TurnHandle Handle, string Name, int? Year, MediaType Media);

/// <summary>
/// The conversation's state after its newest turn (§4.3). Derived from the turn record and never
/// accepted from a client.
/// </summary>
/// <param name="UserNegations">The user's own words only; standing conditions are never stored here.</param>
/// <param name="LastPresented">The last three answers' cards, newest answer first, display order inside each.</param>
/// <param name="Version">The answer number the state was written after; a stored state is used only when it is the previous answer's.</param>
/// <param name="LegacyTmdbExclude">
/// No-repeat entries from rows older than <c>TitleId</c> that could not be translated through
/// <c>Titles</c> (§4.6): still never shown again, keyed on the TMDB id they were stored with.
/// </param>
/// <param name="ClarifiedOnce">A clarifying question has been asked in this conversation (at most one is).</param>
/// <param name="LegacyShown">Legacy no-repeat entries translated to a title through <c>Titles</c> by their TMDB id (§4.6).</param>
public sealed record ConversationState(
    int Version,
    HardConstraints Hard,
    SoftPreferences Soft,
    IReadOnlyList<string> UserNegations,
    IReadOnlyList<Guid> Rejected,
    IReadOnlyList<Guid> Liked,
    IReadOnlyList<PresentedRef> LastPresented,
    IReadOnlyList<long>? LegacyTmdbExclude = null,
    bool ClarifiedOnce = false,
    IReadOnlyList<Guid>? LegacyShown = null)
{
    /// <summary>A conversation that has said nothing yet.</summary>
    public static ConversationState Empty { get; } =
        new(0, HardConstraints.None, SoftPreferences.None, [], [], [], []);
}

/// <summary>A tool the planner may ask for (§4.4). The server runs it; no output goes back to the model except an answer's facts.</summary>
public enum AskTool
{
    SearchCatalog,
    SimilarTo,
    TitleFacts,
    Filmography,
    Supply,
    Conditions,
}

/// <summary>One requested tool, with the handle or name it is about when it is about one.</summary>
public sealed record ToolRequest(AskTool Tool, TurnHandle? Handle = null, string? Name = null);

/// <summary>
/// What a message changes. A <see cref="TurnAct.NewRequest"/> replaces the hard constraints and soft
/// preferences with what is here; every other search act merges it in.
/// </summary>
/// <param name="Concepts">Topics the user requires, in their words ("ninja").</param>
/// <param name="Genres">Genre slugs the user requires.</param>
/// <param name="Media">Null leaves the media as it was.</param>
/// <param name="Negations">What the user ruled out, in their words.</param>
/// <param name="ReferenceNames">Titles named as comparisons ("like John Wick").</param>
public sealed record ConversationStateDelta(
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> Genres,
    IReadOnlyList<PersonConstraint> People,
    YearRange? Years,
    int? MaxRuntimeMinutes,
    MediaScope? Media,
    IReadOnlyList<string> Moods,
    IReadOnlyList<AttributeDirection> Directions,
    IReadOnlyList<string> Negations,
    IReadOnlyList<string> ReferenceNames)
{
    public static ConversationStateDelta None { get; } = new([], [], [], null, null, null, [], [], [], []);
}

/// <summary>What a model call cost and how it ended, for the turn record.</summary>
public sealed record LlmCallMeta(string? Model, string? Effort, bool Truncated, int Attempts, long ElapsedMs, string? FailureCode = null)
{
    public static LlmCallMeta None { get; } = new(null, null, false, 0, 0);
}

/// <summary>The account's standing conditions, read-only, as the planner is shown them.</summary>
public sealed record StandingConditionsView(string? Summary);

/// <summary>Everything the planner reads (§4.5).</summary>
public sealed record PlanInput(
    string Message,
    ConversationState State,
    IReadOnlyList<ChatTurn> RecentTurns,
    StandingConditionsView Conditions,
    string TasteSummary,
    MediaScope MediaFilter);

/// <summary>Why a plan could not be produced, when it could not.</summary>
/// <param name="Refusal">True when the provider or the allowance refused before any work: the turn ends there.</param>
public sealed record PlanFailure(AgentFailureKind Kind, string? Code, bool Refusal);

/// <summary>
/// The planner's reading of one message (§4.3). <see cref="Failure"/> is set, and every other field
/// is empty, when no reading was produced; the engine then carries the state forward.
/// </summary>
/// <param name="Question">For an <see cref="TurnAct.ItemQuestion"/>: what is being asked, in the user's words.</param>
public sealed record PlanResult(
    TurnAct Act,
    ConversationStateDelta Delta,
    IReadOnlyList<TurnHandle> References,
    IReadOnlyList<ToolRequest> Tools,
    string? ClarifyQuestion,
    IReadOnlyList<string> ClarifyOptions,
    PlanSource Source,
    LlmCallMeta Meta,
    string? Question = null,
    PlanFailure? Failure = null)
{
    /// <summary>A plan that failed with <paramref name="failure"/>.</summary>
    public static PlanResult Failed(PlanFailure failure, LlmCallMeta meta) =>
        new(TurnAct.Refine, ConversationStateDelta.None, [], [], null, [], PlanSource.CarriedForward, meta, null, failure);
}

/// <summary>The planner (§4.3): one call, strict schema, effort none.</summary>
public interface IAskReelPlanner
{
    bool IsAvailable { get; }

    string ModelId { get; }

    Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct);
}

/// <summary>One title as the judge sees it (§4.5).</summary>
public sealed record JudgeTitle(
    Guid TitleId,
    string Name,
    int? Year,
    MediaType Media,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    string? Overview,
    IReadOnlyList<string> Themes,
    string? Tone);

/// <summary>One judge call's worth of work.</summary>
/// <param name="Request">The request in its conversation: the message plus the state it refines.</param>
/// <param name="Persona">One line about the viewer.</param>
public sealed record JudgeInput(Guid AccountId, string Request, string Persona, IReadOnlyList<JudgeTitle> Titles);

/// <summary>The judge's ruling on one title (§4.3). A title the model did not rule on carries <see cref="ResultSource.Fallback"/> sources.</summary>
public sealed record JudgeVerdict(
    Guid TitleId,
    Satisfies Satisfies,
    double? Fit,
    string? Why,
    string? Evidence,
    ResultSource FitSource,
    ResultSource WhySource)
{
    /// <summary>True when a model actually ruled on the request for this title.</summary>
    public bool Judged => FitSource == ResultSource.Llm;

    /// <summary>The verdict for a title no model ruled on.</summary>
    public static JudgeVerdict Unjudged(Guid titleId) =>
        new(titleId, Satisfies.No, null, null, null, ResultSource.Fallback, ResultSource.Fallback);
}

/// <summary>One batch's verdicts from the model, or why there are none.</summary>
public sealed record JudgeBatchResult(IReadOnlyList<JudgeVerdict> Verdicts, LlmCallMeta Meta, bool Failed);

/// <summary>The model side of the judge: one batch, one call (the adapter).</summary>
public interface IRequestJudgeModel
{
    bool IsAvailable { get; }

    string ModelId { get; }

    /// <summary>The cache salt: model, effort and prompt version (§4.5).</summary>
    string CacheSalt { get; }

    Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct);
}

/// <summary>
/// The judge (§4.3): every title in <see cref="JudgeInput.Titles"/> gets exactly one verdict, yielded
/// as its batch returns. Batch membership is <see cref="RequestJudge.Batches"/>, so a caller can tell
/// when a batch is complete and emit it.
/// </summary>
public interface IRequestJudge
{
    IAsyncEnumerable<JudgeVerdict> JudgeAsync(JudgeInput input, CancellationToken ct);
}

/// <summary>What the server knows about one title, for an item question (§4.4 <c>title_facts</c>).</summary>
/// <param name="Seen">The account has watched or rated it (the footprint flag).</param>
public sealed record TitleFactSheet(
    Guid TitleId,
    string Name,
    int? Year,
    MediaType Media,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Keywords,
    string? Overview,
    int? RuntimeMinutes,
    string? Certification,
    IReadOnlyList<string> Directors,
    IReadOnlyList<string> Cast,
    IReadOnlyList<string> Themes,
    string? Tone,
    bool Seen);

/// <summary>The fact lookup (§4.4).</summary>
public interface ITitleFacts
{
    Task<TitleFactSheet?> GetAsync(Guid accountId, Guid titleId, CancellationToken ct);
}

/// <summary>An item question and the facts it may be answered from.</summary>
public sealed record AnswerInput(string Question, TitleFactSheet Facts);

/// <summary>The answer, or why there is none.</summary>
public sealed record AnswerResult(string? Text, LlmCallMeta Meta, AgentFailureKind? Failure);

/// <summary>The ItemQuestion answerer (§4.5): at most two sentences, claiming nothing beyond the facts.</summary>
public interface IAskReelAnswerer
{
    bool IsAvailable { get; }

    string ModelId { get; }

    Task<AnswerResult> AnswerAsync(AnswerInput input, CancellationToken ct);
}
