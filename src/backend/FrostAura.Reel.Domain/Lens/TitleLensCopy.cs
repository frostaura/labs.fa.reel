using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Lens;

/// <summary>Whether a generated lens is usable, was refused by a guard, or never arrived.</summary>
public enum LensStatus
{
    /// <summary>Generated, parsed and past every guard. The only status a surface renders.</summary>
    Done,

    /// <summary>A guard refused it — spoiler lexicon, voice leak, or a fact nothing could vouch for.
    /// Also set by the user's own "this spoiled it" report. Never rendered, never regenerated at the
    /// same (profile hash, prompt version): the row is the memory of the refusal.</summary>
    Rejected,

    /// <summary>The provider did not answer usably. Retryable on a later pass.</summary>
    Failed,
}

/// <summary>
/// One title, retold through one account's taste — the "through your lens" copy.
///
/// <para><b>The unique key is (AccountId, TitleId, ProfileHash, PromptVersion)</b> and every part of
/// it is deliberate. <see cref="ProfileHash"/> carries the account's taste fingerprint, including
/// the dislike-centroid version, so a title dismissed ten minutes ago cannot still be described as
/// something you will enjoy. It carries neither the active model artifact nor the prediction,
/// because the founder retrains most weeks: an artifact id in the key empties the whole cache every
/// retrain night, which is why the prose never states the number and the number is rendered from
/// the score at serve time instead. <see cref="PromptVersion"/> is the manual bump lever that
/// <c>TitleAttributes.ExtractorVersion</c> conspicuously lacked a reader for.</para>
///
/// <para>Unlike <c>TitleAttributes</c>, this is NOT shared across tenants — it is the opposite of
/// shared, it is the point. It is therefore <see cref="IAccountScoped"/> and carries the global
/// query filter every account-owned row does.</para>
/// </summary>
public class TitleLensCopy : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid TitleId { get; set; }

    /// <summary>The taste fingerprint this copy was written against. Shared with <see cref="AccountTasteNarrative"/>.</summary>
    public string ProfileHash { get; set; } = string.Empty;

    public int PromptVersion { get; set; }

    /// <summary>The structured lens object (jsonb) — see <c>LensNote</c> for the shape.</summary>
    public string LensJson { get; set; } = "{}";

    public string ModelId { get; set; } = string.Empty;

    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }

    public LensStatus Status { get; set; } = LensStatus.Done;

    /// <summary>Why a Rejected row was rejected — the guard's name or "user-reported-spoiler".</summary>
    public string? RejectionReason { get; set; }

    public DateTime GeneratedAt { get; set; }
}
