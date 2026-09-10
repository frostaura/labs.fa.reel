using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.CrossReference;

/// <summary>
/// One judge's answer about one candidate. Deliberately three values and no prose: the whole cost
/// case for judging the entire eligible pool rests on a closed vocabulary at ~45 completion tokens
/// per entry, and a free-text field would triple that and trip <c>finish_reason=length</c> at
/// batch 60.
/// </summary>
public enum CrossReferenceVerdict
{
    /// <summary>Worth the viewer's attention on the evidence shown. Never a promise, only a vote.</summary>
    Keep,

    /// <summary>Rule this out. The only verdict that ever bars a card from the hero, and only once
    /// the offline backtest has earned that right.</summary>
    Out,

    /// <summary>The evidence shown does not settle it. The correct answer far more often than it
    /// is comfortable, and the prompt says so — a judge that is never unsure is guessing.</summary>
    Unsure,
}

/// <summary>
/// Which pass produced a verdict. Part of the unique key, because two rounds over one title are
/// two observations and "two keeps" is the whole aggregation rule — a round-agnostic key would let
/// round 2 overwrite round 1 and leave nothing to count.
/// </summary>
public enum CrossReferenceRound
{
    /// <summary>The rule-out pass over the whole eligible pool, with the k nearest rated neighbours.</summary>
    First = 1,

    /// <summary>Survivors only, with different neighbours (ranks 4–6 plus one disliked) and a
    /// prompt that is looking for reasons to say no.</summary>
    Second = 2,
}

/// <summary>
/// The closed reason vocabulary. A string-constants class rather than an enum for the same reason
/// <c>ApiUsagePurpose</c> is: the column stays readable in ad-hoc SQL and a new reason does not
/// force a migration.
///
/// <para><b>The verdict × reason matrix is a parser rule, not a hope.</b> A schema can constrain
/// each field independently and cannot express "keep may not be justified by tone-mismatch"; that
/// pairing is checked in <c>CrossReferenceJudgeParser</c> and an illegal pair rejects exactly one
/// entry. See <see cref="IsLegal"/> for the matrix itself.</para>
/// </summary>
public static class CrossReferenceReasons
{
    /// <summary>The nearest titles this viewer rated are ones they scored highly.</summary>
    public const string NeighbourLoved = "neighbour-loved";

    /// <summary>The nearest titles this viewer rated are ones they scored low.</summary>
    public const string NeighbourDisliked = "neighbour-disliked";

    public const string ToneMatch = "tone-match";

    public const string ToneMismatch = "tone-mismatch";

    public const string PaceMismatch = "pace-mismatch";

    public const string ThemeMatch = "theme-match";

    public const string ThemeMismatch = "theme-mismatch";

    public const string GenreMismatch = "genre-mismatch";

    /// <summary>A format this viewer does not watch — the schedule-filler rule.</summary>
    public const string FormatMismatch = "format-mismatch";

    public const string CraftBelowBar = "craft-below-bar";

    /// <summary>The only legal reason for <see cref="CrossReferenceVerdict.Unsure"/>.</summary>
    public const string InsufficientSignal = "insufficient-signal";

    /// <summary>Every reason the schema admits, in the order the prompt lists them.</summary>
    public static readonly string[] All =
    [
        NeighbourLoved, NeighbourDisliked, ToneMatch, ToneMismatch, PaceMismatch,
        ThemeMatch, ThemeMismatch, GenreMismatch, FormatMismatch, CraftBelowBar, InsufficientSignal,
    ];

    /// <summary>
    /// The verdict × reason legality matrix.
    ///
    /// <para><c>keep</c> admits only the three positive reasons: a keep justified by
    /// "tone-mismatch" is not a keep with an odd label on it, it is an entry whose two fields
    /// disagree, and reading either half of it would be inventing which half to believe.
    /// <c>unsure</c> admits only <see cref="InsufficientSignal"/> — the other reasons are all
    /// statements the judge would have had to be sure of. <c>out</c> admits every negative reason
    /// and nothing positive.</para>
    /// </summary>
    public static bool IsLegal(CrossReferenceVerdict verdict, string reason) => verdict switch
    {
        CrossReferenceVerdict.Keep => reason is NeighbourLoved or ToneMatch or ThemeMatch,
        CrossReferenceVerdict.Unsure => reason is InsufficientSignal,
        CrossReferenceVerdict.Out => reason is NeighbourDisliked or ToneMismatch or PaceMismatch
            or ThemeMismatch or GenreMismatch or FormatMismatch or CraftBelowBar,
        _ => false,
    };
}

/// <summary>
/// One durable verdict: this account, this title, judged at this taste fingerprint by this prompt
/// generation in this round.
///
/// <para><b>The unique key is (AccountId, TitleId, ProfileHash, PromptVersion, Round)</b> and every
/// part earns its place. <see cref="Round"/> is in it because the aggregation rule counts
/// observations — "two keeps and no out" — and a key without the round makes round 2 an overwrite
/// of round 1 rather than a second look. <see cref="ProfileHash"/> is the same taste fingerprint
/// <c>TitleLensCopy</c> uses, so a title dismissed ten minutes ago cannot still be carrying a Keep
/// written against the taste that preceded the dismissal.</para>
///
/// <para><b><see cref="ModelArtifactId"/> is a dimension, not a key part</b>, for exactly the
/// reason <c>TasteProfileHash</c> already records for the lens cache: the founder retrains most
/// weeks, so an artifact in the key empties the whole store every retrain night. It is recorded
/// so "which model's predictions was the judge looking at" stays answerable, and nothing more.</para>
///
/// <para><b>A ProfileHash move demotes, it does not invalidate.</b> Rows at an older hash are
/// STALE — half strength, re-judged first — exactly like the lens's Fresh → Stale → None ladder.
/// Wholesale invalidation on every fingerprint step would re-judge ten thousand titles per step
/// and blow the ≤$0.25/account-month assumption on its own.</para>
///
/// <para><b>There is no Failed row.</b> A provider outage writes nothing: two thousand Failed rows
/// would be indistinguishable from two thousand judgements to the anti-join that decides what is
/// left to do, and would therefore suppress their own retry. Absence IS the retry signal.</para>
/// </summary>
public class TitleCrossReferenceVerdict : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid TitleId { get; set; }

    /// <summary>The taste fingerprint this verdict was judged against. Shared with <c>TitleLensCopy</c>.</summary>
    public string ProfileHash { get; set; } = string.Empty;

    public int PromptVersion { get; set; }

    public CrossReferenceRound Round { get; set; }

    public CrossReferenceVerdict Verdict { get; set; }

    /// <summary>One of <see cref="CrossReferenceReasons"/>, legal for <see cref="Verdict"/>.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>0–1, as the judge reported it. Three decimals: the Out threshold is 0.70 and a
    /// coarser column would put a quantisation step across it.</summary>
    public decimal Confidence { get; set; }

    /// <summary>
    /// The neighbours this verdict was shown, as a jsonb array of title ids. Kept so a surprising
    /// verdict can be re-read against the evidence it actually had rather than against today's
    /// nearest neighbours, which move as the account rates more.
    /// </summary>
    public string NeighbourIdsJson { get; set; } = "[]";

    public string ModelId { get; set; } = string.Empty;

    /// <summary>Which model artifact produced the predictions in the prompt. A dimension — see the
    /// class remarks for why it is deliberately not part of the key.</summary>
    public Guid? ModelArtifactId { get; set; }

    /// <summary>Which batch of the run this entry came back in. Diagnostic only.</summary>
    public int BatchIndex { get; set; }

    public DateTime GeneratedAt { get; set; }
}
