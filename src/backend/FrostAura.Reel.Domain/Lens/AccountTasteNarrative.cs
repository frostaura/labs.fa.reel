using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Lens;

/// <summary>
/// The account's taste, written out in the second person — 6 to 10 sentences the user can read,
/// disagree with and correct, and the personalisation context every downstream LLM call rides on.
///
/// <para><b>Why this exists.</b> Until it did, the entire personal channel into the most capable
/// model in the stack was one line — "favours thriller, drama; loves X, Y" — roughly twenty tokens
/// distilled from thousands of ratings. Genre affinity, drift, verified people, attribute texture,
/// contrarian offsets, preference tags and every dislike the account has ever registered were
/// computed, stored, and never spoken to the model. This row is that representation made explicit,
/// so it can be inspected instead of inferred.</para>
///
/// <para><b>Editable, and the edit wins.</b> <see cref="EditedText"/> is the user's own correction
/// and is NEVER overwritten by regeneration — a wrong inference must become a correction rather
/// than a silent standing bias. Regeneration writes <see cref="Text"/> and leaves the edit alone;
/// the explicit "regenerate" action is the only thing that clears it, and it is the user asking.</para>
///
/// <para><b><see cref="ProfileHash"/> is the invalidation key</b>, shared with
/// <see cref="TitleLensCopy"/>. It is deliberately coarse — affinities rounded to 0.5, the
/// attribute profile to 0.1 — so that an ordinary week of ratings does not churn every cached piece
/// of copy in the product. It deliberately does NOT include the active model artifact id: putting
/// the model's number in the key means every retrain night empties the whole lens cache, so the
/// prose stays about taste and the prediction is rendered separately at serve time.</para>
/// </summary>
public class AccountTasteNarrative : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>Monotonic per account — 1 for the first narrative ever written, then +1 per regeneration.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The taste fingerprint this text was written from (see <c>TasteProfileHash</c>). A narrative
    /// whose hash no longer matches the account's current taste is stale and owed a rewrite; it
    /// stays readable and usable in the meantime.
    /// </summary>
    public string ProfileHash { get; set; } = string.Empty;

    /// <summary>The generated narrative, sentences joined by a single space. Second person throughout.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The user's own rewrite. Wins over <see cref="Text"/> everywhere, and survives regeneration.</summary>
    public string? EditedText { get; set; }

    /// <summary>
    /// The structured inputs the text was written from (jsonb) — top genres and affinities,
    /// drifting-toward, loved anchors, the declined line, verified people with counts, texture
    /// scalars, contrarian notes, preference tags and the movie/TV share. Persisted because the
    /// fact-grounding check needs to prove after the fact that every title and person named in the
    /// prose was actually supplied, and because a narrative whose facts are unknowable is not
    /// auditable.
    /// </summary>
    public string FactsJson { get; set; } = "{}";

    /// <summary>How many whole-title ratings the facts were computed from — the "written from N ratings" line.</summary>
    public int RatingsCount { get; set; }

    public string ModelId { get; set; } = string.Empty;

    /// <summary>The prompt generation. Bump when a change here alters what the stored text MEANS.</summary>
    public int PromptVersion { get; set; }

    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }

    public DateTime GeneratedAt { get; set; }

    /// <summary>When the user last saved an edit; null while <see cref="EditedText"/> is null.</summary>
    public DateTime? EditedAt { get; set; }

    /// <summary>The narrative actually in force: the user's edit if they made one, else the generated text.</summary>
    public string Effective => string.IsNullOrWhiteSpace(EditedText) ? Text : EditedText!;
}
