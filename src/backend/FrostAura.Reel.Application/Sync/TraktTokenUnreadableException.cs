namespace FrostAura.Reel.Application.Sync;

/// <summary>
/// A stored Trakt secret (the access or the refresh token) can no longer be decrypted — the
/// Data Protection key ring that sealed it is gone or was rotated out without the old keys. No
/// retry and no refresh can recover it: only the user re-linking Trakt writes a token this host can
/// read. By the time this is thrown, <see cref="TraktTokenStore"/> has already marked the connection
/// <c>Revoked</c>, which is what stops the delta poll, the nightly reconcile and the token refresh
/// from picking it up again, and what puts "your Trakt link needs reconnecting" in front of the user.
///
/// <para>The message says "unauthorized" on purpose: it is what <c>JobFailureReason.Classify</c>
/// reads, so the failed job surfaces as the same reconnect state a revoked token does.</para>
/// </summary>
public sealed class TraktTokenUnreadableException(Guid accountId, string which, Exception innerException)
    : Exception(
        $"Trakt link unauthorized: the stored {which} token for account {accountId} could not be decrypted; re-link required.",
        innerException)
{
    /// <summary>The account whose connection was marked <c>Revoked</c>.</summary>
    public Guid AccountId { get; } = accountId;
}
