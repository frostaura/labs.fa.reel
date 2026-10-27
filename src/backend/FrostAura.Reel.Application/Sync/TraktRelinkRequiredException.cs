namespace FrostAura.Reel.Application.Sync;

/// <summary>
/// The Trakt link cannot be used again until the user re-links it, so no retry and no refresh can
/// help: every job that meets this fails once, and the user is asked to reconnect. The two causes
/// are <see cref="TraktTokenUnreadableException"/> (a stored token this host can no longer decrypt)
/// and <see cref="TraktConnectionRevokedException"/> (a link already marked <c>Revoked</c>).
///
/// <para>Every message says "unauthorized" on purpose: it is what <c>JobFailureReason.Classify</c>
/// reads, so the failed job surfaces as the reconnect state rather than as a generic error.</para>
/// </summary>
public abstract class TraktRelinkRequiredException(Guid accountId, string message, Exception? innerException)
    : Exception(message, innerException)
{
    /// <summary>The account whose link needs re-linking.</summary>
    public Guid AccountId { get; } = accountId;
}
