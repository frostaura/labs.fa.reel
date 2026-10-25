namespace FrostAura.Reel.Domain.Ports;

/// <summary>One turn in an Ask Reel conversation.</summary>
public sealed record ChatTurn(string Role, string Text);

/// <summary>
/// The agent's response to a turn: a conversational reply + the resolved discovery intent.
///
/// <para><paramref name="Requested"/> is the FREE deterministic reading of the user's own words —
/// <see cref="ISearchQueryInterpreter.Heuristic"/> — carried alongside the model's answer rather
/// than discarded. It exists because <paramref name="Intent"/> is laundered by design: the interpret
/// prompt is told what the account permanently excludes, so a model that obeys removes the excluded
/// genre from its answer before returning it. Anything that needs to know what the turn ASKED FOR,
/// as opposed to what the pipeline was told to go and find, has to read a source the prompt cannot
/// touch. Null where a caller built a reply by hand; treat null as "no second reading".</para>
///
/// <para>Additive with a null default, so every existing construction site — the stubs in a dozen
/// tests included — stays valid.</para>
///
/// <para><paramref name="Degraded"/> is true when <paramref name="Reply"/> is NOT the model's own
/// sentence — every model attempt for the turn failed (or no model is configured) and the reply is
/// <see cref="DegradedReply"/>. It exists because the old fallback reply quoted the user's message
/// back inside a template ("Here are some action picks for '…'") and was then cached, persisted
/// and read back as history exactly like a real answer. A degraded reply must never be cached,
/// never enter history, and be shown as degraded. <paramref name="Failure"/> says why, for the
/// turn record and the per-purpose counters; it is null exactly when the reply is not degraded.</para>
/// </summary>
public sealed record AgentReply(
    string Reply, SearchIntent Intent, SearchIntent? Requested = null, bool Degraded = false,
    AgentFailureKind? Failure = null)
{
    /// <summary>
    /// The reply carried by a degraded turn. It claims nothing: no results, no genre reading, no
    /// echo of the user's words — the model did not answer, so nothing here may sound as if it had.
    /// </summary>
    public const string DegradedReply = "I couldn't think that one through just now. Try asking again in a moment.";

    /// <summary>
    /// The genres the user's own words asked for. Empty rather than null, because every caller
    /// treats "no second reading" and "it named no genre" the same way.
    /// </summary>
    public IReadOnlyList<string> RequestedGenres => Requested?.Genres ?? [];
}

/// <summary>
/// Why a model call produced nothing usable. Persisted as its NAME (a string), so a reader meeting
/// a value it does not know maps it to <see cref="Unknown"/> rather than failing — see
/// <see cref="LlmResultText"/>.
/// </summary>
public enum AgentFailureKind
{
    /// <summary>A stored value this build does not recognise. Never written by this build.</summary>
    Unknown,

    /// <summary>The completion hit its token ceiling (<c>finish_reason=length</c>).</summary>
    Truncated,

    /// <summary>The provider answered with no content.</summary>
    EmptyAnswer,

    /// <summary>The content was not the JSON object the prompt asked for, or carried no reply.</summary>
    MalformedAnswer,

    /// <summary>A transport failure or a retryable HTTP status (5xx, 429) after the transport's own retry.</summary>
    ProviderError,

    /// <summary>401, 402 or 403: the key, the credit or the permission is gone. Never retried.</summary>
    ProviderRefused,

    /// <summary>The transport's whole time budget ran out. Never retried: a retry doubles the wait.</summary>
    TimedOut,

    /// <summary>No model is configured (a keyless install); nothing was called.</summary>
    NoModel,

    /// <summary>
    /// The purpose's daily allowance could not admit the call, so nothing was sent. Budget is
    /// reserved BEFORE a call, which is why this is a refusal rather than an overshoot.
    /// </summary>
    BudgetSpent,

    /// <summary>
    /// The provider breaker is open (a recent 401/402/403, or the purpose is off for the day), so
    /// nothing was sent. Distinct from <see cref="ProviderRefused"/>, which is the call that
    /// tripped it.
    /// </summary>
    ProviderUnavailable,
}

/// <summary>
/// Where a value shown to the user came from. Assigned by the code that PRODUCED the value, and
/// never inferred from "the string is non-empty" — that inference is how a deterministic stub
/// sentence was labelled model output on thousands of append-only exposure rows.
/// Persisted as its name; an unknown stored name reads back as <see cref="Fallback"/>, never as
/// <see cref="Llm"/>.
/// </summary>
public enum ResultSource
{
    /// <summary>The model produced this value for this title.</summary>
    Llm,

    /// <summary>A deterministic catalogue fact (a keyword, a genre, a year) produced it.</summary>
    Facet,

    /// <summary>Nothing trustworthy produced it: a failure path or the deterministic stub.</summary>
    Fallback,
}

/// <summary>Whether a title is what the request asked for — independent of whether you would like it.</summary>
public enum Satisfies
{
    /// <summary>It plainly is what was asked for.</summary>
    Yes,

    /// <summary>It matches part of the request, or only loosely.</summary>
    Partial,

    /// <summary>It is not what was asked for.</summary>
    No,
}

/// <summary>
/// The string forms of the LLM result enums, single-sourced so a writer and a reader can never
/// disagree, and so every reader has a defined answer for a value written by a later build.
/// </summary>
public static class LlmResultText
{
    /// <summary>The persisted name of a source.</summary>
    public static string Name(ResultSource source) => source.ToString();

    /// <summary>Reads a persisted source. Anything unrecognised is <see cref="ResultSource.Fallback"/>:
    /// an unknown provenance is untrusted provenance, and must never read back as model output.</summary>
    public static ResultSource ParseSource(string? value) =>
        ExactName<ResultSource>(value) ?? ResultSource.Fallback;

    /// <summary>Reads a persisted failure kind; anything unrecognised is <see cref="AgentFailureKind.Unknown"/>.</summary>
    public static AgentFailureKind ParseFailure(string? value) =>
        ExactName<AgentFailureKind>(value) ?? AgentFailureKind.Unknown;

    /// <summary>The wire and persisted form of a verdict: <c>yes</c>, <c>partial</c>, <c>no</c>.</summary>
    public static string Wire(Satisfies satisfies) => satisfies switch
    {
        Satisfies.Yes => "yes",
        Satisfies.Partial => "partial",
        _ => "no",
    };

    /// <summary>
    /// Reads a verdict from the model or from storage. Null for anything else — an unreadable
    /// verdict is UNJUDGED, which is not the same as <see cref="Satisfies.No"/> and must never be
    /// promoted to <see cref="Satisfies.Yes"/>.
    /// </summary>
    public static Satisfies? ParseSatisfies(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" => Satisfies.Yes,
        "partial" => Satisfies.Partial,
        "no" => Satisfies.No,
        _ => null,
    };

    /// <summary>
    /// A member by its exact persisted NAME only. <c>Enum.TryParse</c> alone also accepts digits
    /// ("0") and comma lists, which would let a stray number read back as <c>Llm</c>.
    /// </summary>
    private static T? ExactName<T>(string? value)
        where T : struct, Enum =>
        value is not null && Enum.TryParse<T>(value, ignoreCase: false, out var parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : null;
}

/// <summary>
/// The half of a candidate the rerank prompt used NOT to carry, and the reason its verdicts were
/// shallow: tone, era and themes (extracted per title by a paid model and then read by almost
/// nothing), the seven attribute scalars, the people, and the model's own top contributions — so
/// the judge can see WHY the recommender picked this and agree or disagree with a reason.
/// Null where the title has not been enriched, which is honest rather than zero-filled.
/// </summary>
public sealed record RerankTitleContext(
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
    IReadOnlyList<string> TopContributions);

/// <summary>A candidate handed to the per-movie re-rank (enough for the model to judge fit).</summary>
public sealed record RerankInput(
    Guid TitleId, string Name, int? Year, string MediaType, IReadOnlyList<string> Genres, string? Overview, decimal? Predicted,
    RerankTitleContext? Context = null);

/// <summary>
/// The verdict for one title: fit 0–1, a one-line "why this fits you", and — typed, never inferred
/// — where each of the two came from.
///
/// <para><paramref name="FitSource"/> and <paramref name="WhySource"/> say whether each value was
/// the model's. A voice-guard rejection keeps the model's fit (<c>FitSource=Llm</c>) and replaces
/// only the sentence (<c>WhySource=Fallback</c>); a failed batch, a skipped entry and the
/// deterministic stub are <c>Fallback</c> on both. Only <c>Llm</c>-sourced values may be cached,
/// debiased, preserved into a snapshot, or logged as model output.</para>
///
/// <para><b>Fit and Why are still non-nullable in this revision, deliberately.</b> The target
/// shape (WS-1 task 4) is <c>double? Fit</c> / <c>string? Why</c>, null on a failed batch; flipping
/// them here would break 19 compile sites in files other lanes own, so the flip lands with task 4
/// together with those call sites. Until then a Fallback-sourced value is the deterministic stub's,
/// and a consumer that must not show invented output reads the SOURCE, never the value.</para>
///
/// <para><paramref name="Satisfies"/> is the Ask-only request check — whether the title IS what the
/// user asked for, separate from whether they would like it. It is requested only on the
/// request-judging purposes (<c>rerank-ask</c>, <c>rerank-expand</c>); it is null on every feed
/// purpose, on every Fallback verdict, and wherever the model's verdict was missing or unreadable
/// (unjudged — never treated as <c>No</c>, never promoted to <c>Yes</c>).</para>
/// </summary>
public sealed record RerankResult(
    Guid TitleId, double Fit, ResultSource FitSource, string Why, ResultSource WhySource,
    Satisfies? Satisfies = null)
{
    /// <summary>
    /// A verdict the MODEL gave, fit and sentence both — the shape every test double that stands in
    /// for a working model builds. Production failure paths never use it; they state their sources.
    /// </summary>
    public RerankResult(Guid titleId, double fit, string why)
        : this(titleId, fit, ResultSource.Llm, why, ResultSource.Llm)
    {
    }

    /// <summary>
    /// The fit, only where the MODEL gave it; null on every fallback. The target shape of
    /// <see cref="Fit"/> itself (task 4) — available now so a consumer can migrate to it before the
    /// flip, and never mistake a fallback's ranking value for a judgement.
    /// </summary>
    public double? ModelFit => FitSource == ResultSource.Llm ? Fit : null;

    /// <summary>
    /// The why-line, only where the MODEL wrote it and it survived the voice guard; null otherwise.
    /// A fallback's <see cref="Why"/> is empty by construction — no sentence is ever invented.
    /// </summary>
    public string? ModelWhy => WhySource == ResultSource.Llm && Why is { Length: > 0 } ? Why : null;

    /// <summary>
    /// Whether this verdict may be cached, debiased, preserved into a snapshot or logged as model
    /// output: only a verdict whose fit came from the model. A fallback is recomputed every time.
    /// </summary>
    public bool Cacheable => FitSource == ResultSource.Llm;
}

/// <summary>
/// The conversational Ask Reel agent. Turns a chat turn (with history + the user's taste) into a
/// reply + a discovery intent, and judges each candidate's hyper-personal fit for a live re-rank.
/// Both honour the deterministic stub contract (keyless dev / tests) like the other OpenRouter
/// adapters — the stub always answers so the experience degrades, never breaks.
/// </summary>
public interface ISearchAgent
{
    bool IsAvailable { get; }

    string ModelId { get; }

    /// <summary>
    /// Reply + intent for a conversational turn, aware of prior turns and the shown titles.
    ///
    /// <para><paramref name="standingFilters"/> is a one-line, human-readable summary of the
    /// account's standing content filters ("never horror; nothing before 1990"), or null where the
    /// account has none. It exists because the agent used to resolve an intent in total ignorance
    /// of the exclusions the server would then apply, so a turn could confidently promise a genre
    /// the account has permanently excluded and come back empty — the collision was invisible to
    /// the model and unexplainable to the user. The port carries it; the prompt half and the
    /// computation half land separately.</para>
    ///
    /// <para><b>Why it sits AFTER <paramref name="ct"/>,</b> which is otherwise wrong: the existing
    /// production caller passes its CancellationToken POSITIONALLY, so an optional parameter
    /// inserted before it would silently rebind that argument and fail to compile. The alternative
    /// was editing a file this change deliberately does not own. Trailing is the honest cost of a
    /// signature-only change.</para>
    /// </summary>
    Task<AgentReply> InterpretAsync(
        IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
        CancellationToken ct = default, string? standingFilters = null);

    /// <summary>
    /// Score each candidate's fit for this user + query (batched). Row-aligned to the input.
    /// <paramref name="anchorName"/> is the rail's anchor title when the row has one (a
    /// "because you loved" / "more like this" rail) — the agent is then asked to name the concrete
    /// connection between each candidate and that anchor. Null for un-anchored rows and Ask Reel,
    /// whose anchor is the user's request itself.
    ///
    /// <para><paramref name="usagePurpose"/> is which member of the re-rank family this call bills
    /// to — one of <c>ApiUsagePurpose.RerankFeed / RerankPage / RerankAsk / RerankExpand</c>. It
    /// exists because a single <c>rerank</c> string written by four callers made "what does a turn
    /// cost" unanswerable on a day-grained ledger, and every per-surface figure derived from it was
    /// wrong. It defaults to the LEGACY <c>rerank</c> so a caller that forgets degrades to
    /// "counted, unattributed" rather than to "free" — the family ceiling counts that string
    /// too.</para>
    /// </summary>
    Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
        IReadOnlyList<RerankInput> titles, string query, Lens.LensPersona persona, string? anchorName = null,
        string usagePurpose = Sync.ApiUsagePurpose.Rerank, CancellationToken ct = default);

    /// <summary>
    /// The salt every cache key built from this agent's output must carry: a result-format
    /// version, the model and the reasoning effort the purpose runs at. A change to any of the three
    /// changes what a cached verdict means, so it must miss rather than serve.
    ///
    /// <para>A default member so every test double keeps compiling; the production adapter
    /// overrides it with the effort it actually sends.</para>
    /// </summary>
    string CacheSalt(string usagePurpose) => string.Concat(RerankCacheVersion.Current, ":", ModelId);
}

/// <summary>
/// The version every re-rank result cache key carries. Bumped to 2 when fallback verdicts stopped
/// carrying an invented sentence and a predicted-rating fit: an entry written under 1 may hold the
/// old "A {genre} pick aligned with your taste." stub, and must never be served again.
/// </summary>
public static class RerankCacheVersion
{
    public const string Current = "rr2";
}
