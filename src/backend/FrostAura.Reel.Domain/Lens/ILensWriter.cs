namespace FrostAura.Reel.Domain.Lens;

/// <summary>
/// The personalisation context every lens is written against: the account's effective taste
/// narrative (or, before one exists, the compact fallback line) plus the recently-declined list.
/// The two are separate fields rather than one blob because the declined line changes on every
/// reaction while the narrative changes on the order of weeks, and the prompts frame them
/// differently — one is who you are, the other is what you just said no to.
/// </summary>
public sealed record LensPersona(string Taste, string? Declined)
{
    /// <summary>The compact-line shape the rerank prompts used before narratives existed.</summary>
    public static implicit operator LensPersona(string taste) => new(taste, null);
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
public sealed record LensAuditScore(
    double Specificity, double Grounding, double SpoilerFreeness, double Voice, string? Comment)
{
    /// <summary>The unweighted mean — the single number a prompt-version comparison is read on.</summary>
    public double Overall => (Specificity + Grounding + SpoilerFreeness + Voice) / 4d;
}
