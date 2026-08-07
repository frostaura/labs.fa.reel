using Microsoft.AspNetCore.DataProtection;

namespace FrostAura.Reel.Api.Security;

/// <summary>
/// Issues and validates the short-lived settings-unlock token that stands in for the raw PIN
/// after a successful <c>/api/settings/pin/verify</c>.
///
/// The token exists so the frontend never has to cache the PIN itself: the client holds an opaque,
/// account-bound, time-limited DataProtection payload instead, and a leaked token ages out on its
/// own within <see cref="Ttl"/> — a leaked raw PIN does not. Keys persist via the EF key ring that
/// DataProtection is already configured with, so tokens survive an API restart but die with the
/// key ring like every other protected secret.
/// </summary>
public sealed class SettingsUnlockTokenService
{
    /// <summary>How long one verified PIN entry stays good for. Mirrored by the frontend's badge.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ITimeLimitedDataProtector _protector;

    public SettingsUnlockTokenService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("FrostAura.Reel.SettingsUnlock.v1").ToTimeLimitedDataProtector();
    }

    /// <summary>Mints a token bound to <paramref name="accountId"/>, expiring after <see cref="Ttl"/>.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid accountId)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(Ttl);
        return (_protector.Protect(Payload(accountId), expiresAt), expiresAt);
    }

    /// <summary>
    /// True only for an untampered, unexpired token minted for this exact account. Any failure
    /// mode — garbage input, wrong key ring, expiry, another account's token — is false, never
    /// an exception: the caller turns false into a 423, and an attacker learns nothing extra.
    /// </summary>
    public bool Validate(string token, Guid accountId)
    {
        try
        {
            return _protector.Unprotect(token) == Payload(accountId);
        }
        catch
        {
            return false;
        }
    }

    private static string Payload(Guid accountId) => $"{accountId:N}:settings-pin";
}
