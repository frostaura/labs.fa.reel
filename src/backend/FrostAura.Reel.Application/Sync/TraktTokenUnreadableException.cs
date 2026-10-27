namespace FrostAura.Reel.Application.Sync;

/// <summary>
/// A stored Trakt secret (the access or the refresh token) can no longer be decrypted — the
/// Data Protection key ring that sealed it is gone or was rotated out without the old keys. No
/// retry and no refresh can recover it: only the user re-linking Trakt writes a token this host can
/// read. By the time this is thrown, <see cref="TraktTokenStore"/> has already marked the connection
/// <c>Revoked</c>, which is what stops the delta poll, the nightly reconcile and the token refresh
/// from picking it up again, and what puts "your Trakt link needs reconnecting" in front of the user.
/// </summary>
public sealed class TraktTokenUnreadableException(Guid accountId, string which, Exception innerException)
    : TraktRelinkRequiredException(
        accountId,
        $"Trakt link unauthorized: the stored {which} token for account {accountId} could not be decrypted; re-link required.",
        innerException);
