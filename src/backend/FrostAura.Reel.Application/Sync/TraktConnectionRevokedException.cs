namespace FrostAura.Reel.Application.Sync;

/// <summary>
/// A token was asked for on a link already marked <c>Revoked</c> (Trakt rejected its refresh, or a
/// stored token became unreadable). The delta poll never queues such a link, but "Sync now" and a
/// job that was already queued still reach the token store; they fail once, as unauthorized, rather
/// than spending three attempts on an answer that cannot change (HREV1-PLAT-4).
/// </summary>
public sealed class TraktConnectionRevokedException(Guid accountId, Guid connectionId)
    : TraktRelinkRequiredException(
        accountId,
        $"Trakt link unauthorized: connection {connectionId} is revoked; re-link required.",
        null)
{
    /// <summary>The revoked connection.</summary>
    public Guid ConnectionId { get; } = connectionId;
}
