using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Domain.Feed;

/// <summary>
/// The four outcome strings the <c>ExposureOutcomes</c> view emits. Kept as constants rather than
/// an enum because the value is produced by a SQL CASE ladder — the vocabulary lives in the view,
/// and these names exist so no consumer retypes a literal.
/// </summary>
public static class ExposureOutcomeKind
{
    /// <summary>The user rated or watched the title after seeing it. The only outcome that closes the loop.</summary>
    public const string Converted = "converted";

    /// <summary>The user queued it and has not finished with it yet.</summary>
    public const string Saved = "saved";

    /// <summary>The user said no to it.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>Shown, nothing has happened since. Not a failure — an open question.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// One exposure joined to whatever the user did AFTERWARDS — a read-only SQL view, not a table.
///
/// <para><b>Why a view and not a materialised projection.</b> The answer is a pure function of five
/// tables the system already keeps, and the volume is hundreds of rows per account, not millions. A
/// materialised copy would introduce a staleness class this codebase has already been bitten by —
/// the live refill in <c>FeedEndpoints</c> exists precisely because a materialised snapshot drifted
/// from the live eligibility rule. Materialise only when a real query is measurably slow, and then
/// as a cache with a rebuild job and a drift test.</para>
///
/// <para><b>Attribution is temporal, not client-supplied.</b> Roughly 2% of this product's ratings
/// are made in-app; the rest arrive from Trakt, Plex or Infuse where no client can hand back an
/// exposure id. So the join is <c>(AccountId, TitleId)</c> plus a strict time guard, and every leg
/// of it names <c>AccountId</c> in SQL rather than leaning on EF's global filter — background jobs
/// read rows before the account context is pinned, and a hand-written cross-tenant join there would
/// have no structural guard at all.</para>
/// </summary>
public class ExposureOutcome : IAccountScoped
{
    /// <summary>The exposure row this outcome describes.</summary>
    public Guid ExposureId { get; set; }

    public Guid AccountId { get; set; }

    public Guid TitleId { get; set; }

    public ExposureSurface Surface { get; set; }

    public int Rank { get; set; }

    public DateTime OccurredAt { get; set; }

    public decimal PredictedRating { get; set; }

    public Guid ModelArtifactId { get; set; }

    /// <summary>The rating the user later gave. Null unless a rating exists that POSTDATES the exposure.</summary>
    public short? ActualRating { get; set; }

    /// <summary><c>UserRating.FirstRatedAt</c> for that rating — immutable, so a re-rate cannot re-time the outcome.</summary>
    public DateTime? RatedAt { get; set; }

    /// <summary>When it was watched, if that happened after the exposure.</summary>
    public DateTime? WatchedAt { get; set; }

    /// <summary>When it was saved for later, if that happened after the exposure.</summary>
    public DateTime? SavedAt { get; set; }

    /// <summary>When it was marked not-interested, if that happened after the exposure.</summary>
    public DateTime? DismissedAt { get; set; }

    /// <summary>When the managed-list row closed — the "saved → watched" clock's end.</summary>
    public DateTime? ListClosedAt { get; set; }

    /// <summary>Why it closed: Watched or UserRemoved.</summary>
    public string? ListCloseReason { get; set; }

    /// <summary>One of <see cref="ExposureOutcomeKind"/>.</summary>
    public string Outcome { get; set; } = ExposureOutcomeKind.Pending;
}

/// <summary>
/// A2's cheap per-<c>(account, title, surface)</c> query shape, correctly demoted from
/// source-of-truth to a derived view over the append-only events. It answers "how often has this
/// been in front of me, how high did it ever get, and what did it ever promise" without any of the
/// resolution loss a stored counter would impose.
/// </summary>
public class TitleExposureRollup : IAccountScoped
{
    public Guid AccountId { get; set; }

    public Guid TitleId { get; set; }

    public ExposureSurface Surface { get; set; }

    public DateTime FirstShownAt { get; set; }

    public DateTime LastShownAt { get; set; }

    /// <summary>Distinct minute-buckets this title was served on this surface — not raw row count.</summary>
    public int ShowCount { get; set; }

    /// <summary>The best position it ever reached. Lower is better.</summary>
    public int MinRank { get; set; }

    public decimal MinPredicted { get; set; }

    public decimal MaxPredicted { get; set; }
}
