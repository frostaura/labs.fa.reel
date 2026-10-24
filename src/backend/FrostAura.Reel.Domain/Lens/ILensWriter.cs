namespace FrostAura.Reel.Domain.Lens;

/// <summary>Where an anchor candidate sits in the account's opinion. Rendered into the prompt verbatim.</summary>
public static class AnchorStance
{
    /// <summary>Rated highly.</summary>
    public const string Loved = "loved";

    /// <summary>Rated badly. An anchor may be something you disliked — that is still a comparison.</summary>
    public const string Disliked = "disliked";

    /// <summary>Declined from the feed without a rating. An opinion, and part of the library.</summary>
    public const string Declined = "declined";
}

/// <summary>
/// One title the lens is ALLOWED to name as an anchor, with the account's own verdict on it.
///
/// <para>The list of these is the anchor equivalent of the credits list: the model is shown
/// exactly the names it may use, so the downstream check verifies a promise rather than performs
/// a rescue. <see cref="Rating"/> is null for a declined title, which was never rated —
/// <see cref="Stance"/> carries the opinion instead.</para>
/// </summary>
public sealed record LensAnchorCandidate(string Name, int? Year, short? Rating, string Stance);

/// <summary>
/// The personalisation context every lens is written against: the account's effective taste
/// narrative (or, before one exists, the compact fallback line), the recently-declined list, and
/// the titles the copy may anchor against. The first three are separate fields rather than one
/// blob because the declined line changes on every reaction while the narrative changes on the
/// order of weeks, and the prompts frame them differently — one is who you are, the other is what
/// you just said no to.
///
/// <para><b><see cref="AnchorCandidates"/> exists because its absence was a hundred-percent
/// rejection rate.</b> The lens system prompt has always told the model to take anchor titles
/// "verbatim from the lists below" — and until this field there were no lists. The only titles in
/// the prompt were the ones inside <see cref="Declined"/>, so the model dutifully anchored on
/// those, and the verifier, which accepted only RATED titles, refused every note. A prompt that
/// names the permitted anchors is the fix; the guard is the backstop, not the policy.</para>
/// </summary>
public sealed record LensPersona(
    string Taste, string? Declined, IReadOnlyList<LensAnchorCandidate> AnchorCandidates)
{
    /// <summary>The two-field shape, for callers that have no anchor set to offer.</summary>
    public LensPersona(string taste, string? declined)
        : this(taste, declined, [])
    {
    }

    /// <summary>The compact-line shape the rerank prompts used before narratives existed.</summary>
    public static implicit operator LensPersona(string taste) => new(taste, null, []);
}

/// <summary>
/// One title as the lens writer sees it. Everything here already exists in the catalogue and has
/// already been paid for — <c>Tone</c>, <c>Era</c>, <c>Themes</c> and the seven scalars in
/// particular were extracted per title and then read by almost nothing.
///
/// <para><see cref="Predicted"/> and <see cref="PredictionError"/> ride the prompt as CONTEXT only:
/// they tell the model how confidently to pitch, and the model is forbidden from restating either.
/// Keeping the number out of the prose is what lets the cache key omit the model artifact and
/// survive a retrain.</para>
/// </summary>
public sealed record LensTitleInput(
    Guid TitleId,
    string Name,
    int? Year,
    string MediaType,
    int? RuntimeMinutes,
    IReadOnlyList<string> Genres,
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
    string? Overview,
    IReadOnlyList<string> Keywords,
    decimal? Predicted,
    decimal? PredictionError);

/// <summary>What the writer produced, with the provider's own token counts for the ledger.</summary>
public sealed record LensDraft(LensNote Note, string ModelId, long? PromptTokens, long? CompletionTokens);

/// <summary>Sentences plus token counts — the narrative writer's answer.</summary>
public sealed record NarrativeDraft(
    IReadOnlyList<string> Sentences, string ModelId, long? PromptTokens, long? CompletionTokens);

/// <summary>
/// Writes the two pieces of second-person copy the product is built on: the account's taste
/// narrative, and one title seen through it. Both honour the deterministic stub contract every
/// other OpenRouter adapter does (no key / <c>TestHelpers:ModelStubMode</c> ⇒ a stable answer
/// derived from the input), so keyless dev and the whole test suite exercise the real code path
/// without ever reaching the provider.
///
/// <para>Both return null rather than a plausible substitute when the provider does not answer
/// usably. That is the same rule the attribute extractor exists to enforce: a fabricated stand-in
/// is indistinguishable from a real judgement and gets cached.</para>
/// </summary>
public interface ILensWriter
{
    bool IsAvailable { get; }

    string ModelId { get; }

    /// <summary>The prompt generation for lens notes. Stamped on every row this writer produces.</summary>
    int LensPromptVersion { get; }

    /// <summary>The prompt generation for taste narratives.</summary>
    int NarrativePromptVersion { get; }

    /// <summary>
    /// The model AND reasoning effort lens notes are written at — <c>model|effort</c> — stored on
    /// every row as its <c>ModelId</c> and part of what makes a note current. A note written by a
    /// different model or at a different effort is a different generation: it is served as stale
    /// (old prose beats none) and rewritten, never mistaken for the current one. Defaults to the
    /// model id alone, for a writer that sends no effort.
    /// </summary>
    string LensCacheSalt => ModelId;

    /// <summary>The same salt for taste narratives (their own purpose, so their own overrides).</summary>
    string NarrativeCacheSalt => ModelId;

    /// <summary>6–10 second-person sentences drawn ONLY from <paramref name="facts"/>. Null on failure.</summary>
    Task<NarrativeDraft?> WriteNarrativeAsync(TasteFacts facts, CancellationToken ct = default);

    /// <summary>
    /// One lens note per input title, row-aligned to the input; an entry is null where that title
    /// could not be written. Batched because the budget loop above it works in batches.
    /// </summary>
    Task<IReadOnlyList<LensDraft?>> WriteLensAsync(
        IReadOnlyList<LensTitleInput> titles, LensPersona persona, CancellationToken ct = default);

    /// <summary>
    /// Scores an already-generated note against the founder-only audit rubric — specificity,
    /// grounding, spoiler-freeness, voice, each 0–1. Null when the judge did not answer; never
    /// invents a score, because a fabricated audit is worse than no audit.
    /// </summary>
    Task<LensAuditScore?> JudgeAsync(LensTitleInput title, LensPersona persona, LensNote note, CancellationToken ct = default);
}

/// <summary>The LLM judge's verdict on one generated note. Founder-only; never shown to a user.</summary>
/// <param name="PovSlips">
/// Every place the copy made the VIEWER a character in the plot — "the city that failed you", of a
/// city that failed HER — quoted, or null when there are none.
///
/// <para><b>A note rather than a fifth score, deliberately.</b> The slip is rare enough that folding
/// it into <see cref="Voice"/> would move a mean by less than the judge's own noise, and specific
/// enough that the offending phrase is worth more than any number. It stays out of
/// <see cref="Overall"/> for the same reason <see cref="Comment"/> does.</para>
/// </param>
public sealed record LensAuditScore(
    double Specificity,
    double Grounding,
    double SpoilerFreeness,
    double Voice,
    string? PovSlips,
    string? Comment)
{
    /// <summary>The unweighted mean — the single number a prompt-version comparison is read on.</summary>
    public double Overall => (Specificity + Grounding + SpoilerFreeness + Voice) / 4d;
}
