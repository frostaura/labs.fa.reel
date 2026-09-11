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
/// </summary>
public sealed record AgentReply(string Reply, SearchIntent Intent, SearchIntent? Requested = null)
{
    /// <summary>
    /// The genres the user's own words asked for. Empty rather than null, because every caller
    /// treats "no second reading" and "it named no genre" the same way.
    /// </summary>
    public IReadOnlyList<string> RequestedGenres => Requested?.Genres ?? [];
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

/// <summary>The model's hyper-personal verdict for one title: fit 0–1 + a one-line "why this fits you".</summary>
public sealed record RerankResult(Guid TitleId, double Fit, string Why);

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
}
