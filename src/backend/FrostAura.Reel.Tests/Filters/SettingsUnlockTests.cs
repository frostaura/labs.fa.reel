using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The settings-unlock token that replaced the client-cached raw PIN, plus the two endpoint gates
/// built on it. The rules pinned here: a token is bound to one account and one key ring, ages out
/// on its own, and never validates by exception; the filters gate accepts token OR legacy raw PIN;
/// and the destructive PIN-removal gate accepts ONLY the raw PIN — a live unlock token must never
/// be enough to remove the PIN it was minted from.
/// </summary>
public class SettingsUnlockTests
{
    private const string Purpose = "FrostAura.Reel.SettingsUnlock.v1";

    private static readonly Guid AccountId = Guid.NewGuid();

    private static SettingsUnlockTokenService CreateService(out IDataProtectionProvider provider)
    {
        provider = new EphemeralDataProtectionProvider();
        return new SettingsUnlockTokenService(provider);
    }

    private static Account PinnedAccount(string pin = "2468") => new()
    {
        Id = AccountId,
        Settings = new AccountSettings { SettingsPinHash = FilterEndpoints.HashPin(pin) },
    };

    private static DefaultHttpContext HttpWithHeaders(params (string Name, string Value)[] headers)
    {
        var http = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            http.Request.Headers[name] = value;
        }

        return http;
    }

    // ── Token service ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Issued_token_validates_for_its_own_account()
    {
        var service = CreateService(out _);
        var (token, expiresAt) = service.Issue(AccountId);

        Assert.True(service.Validate(token, AccountId));
        // ExpiresAt reflects the advertised TTL — the frontend badge counts down from this.
        var expectedExpiry = DateTimeOffset.UtcNow.Add(SettingsUnlockTokenService.Ttl);
        Assert.InRange(expiresAt, expectedExpiry.AddMinutes(-1), expectedExpiry.AddMinutes(1));
    }

    [Fact]
    public void Another_accounts_token_does_not_validate()
    {
        var service = CreateService(out _);
        var (token, _) = service.Issue(Guid.NewGuid());

        Assert.False(service.Validate(token, AccountId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("CfDJ8AAAAAAAAAAAAAAAAAAAAAA")] // plausible-looking DataProtection prefix, garbage body
    public void Garbage_never_validates_and_never_throws(string token)
    {
        var service = CreateService(out _);

        Assert.False(service.Validate(token, AccountId));
    }

    [Fact]
    public void A_token_from_a_different_key_ring_does_not_validate()
    {
        var service = CreateService(out _);
        var (foreignToken, _) = CreateService(out _).Issue(AccountId);

        Assert.False(service.Validate(foreignToken, AccountId));
    }

    [Fact]
    public async Task An_expired_token_does_not_validate()
    {
        // The service's TTL is fixed, so expiry is exercised by minting a token with the same
        // provider + purpose chain but a ~1ms lifetime and letting it lapse.
        var service = CreateService(out var provider);
        var protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        var token = protector.Protect($"{AccountId:N}:settings-pin", DateTimeOffset.UtcNow.AddMilliseconds(1));

        await Task.Delay(50);

        Assert.False(service.Validate(token, AccountId));
    }

    // ── PUT /api/settings/filters gate (token OR legacy raw PIN) ──────────────────────────

    [Fact]
    public async Task Filters_gate_is_open_when_no_pin_is_configured()
    {
        var service = CreateService(out _);
        var account = new Account { Id = AccountId, Settings = new AccountSettings() };

        Assert.True(await FilterEndpoints.VerifyUnlockAsync(account, HttpWithHeaders(), service));
    }

    [Fact]
    public async Task Filters_gate_accepts_a_valid_unlock_token()
    {
        var service = CreateService(out _);
        var (token, _) = service.Issue(AccountId);
        var http = HttpWithHeaders(("X-Settings-Unlock", token));

        Assert.True(await FilterEndpoints.VerifyUnlockAsync(PinnedAccount(), http, service));
    }

    [Fact]
    public async Task Filters_gate_rejects_a_garbage_token()
    {
        var service = CreateService(out _);
        var http = HttpWithHeaders(("X-Settings-Unlock", "garbage"));

        Assert.False(await FilterEndpoints.VerifyUnlockAsync(PinnedAccount(), http, service));
    }

    [Fact]
    public async Task Filters_gate_rejects_another_accounts_token()
    {
        var service = CreateService(out _);
        var (token, _) = service.Issue(Guid.NewGuid());
        var http = HttpWithHeaders(("X-Settings-Unlock", token));

        Assert.False(await FilterEndpoints.VerifyUnlockAsync(PinnedAccount(), http, service));
    }

    [Fact]
    public async Task Filters_gate_refuses_the_legacy_raw_pin_header()
    {
        var service = CreateService(out _);
        var http = HttpWithHeaders(("X-Settings-Pin", "2468"));

        Assert.False(await FilterEndpoints.VerifyUnlockAsync(PinnedAccount(), http, service));
    }

    [Fact]
    public async Task Filters_gate_locks_with_no_headers_or_a_wrong_pin()
    {
        var service = CreateService(out _);

        Assert.False(await FilterEndpoints.VerifyUnlockAsync(PinnedAccount(), HttpWithHeaders(), service));
        Assert.False(await FilterEndpoints.VerifyUnlockAsync(
            PinnedAccount(), HttpWithHeaders(("X-Settings-Pin", "9999")), service));
    }

    // ── DELETE /api/settings/pin gate (raw PIN ONLY — destructive, always re-prompts) ─────

    [Fact]
    public async Task Pin_removal_gate_rejects_an_unlock_token_even_a_valid_one()
    {
        var service = CreateService(out _);
        var account = PinnedAccount();
        var (token, _) = service.Issue(AccountId);

        // A live token in either header must not satisfy removal — only the raw PIN may.
        var tokenAsUnlock = HttpWithHeaders(("X-Settings-Unlock", token));
        var tokenAsPin = HttpWithHeaders(("X-Settings-Pin", token));

        Assert.False(await FilterEndpoints.VerifyPinHeaderAsync(account.Settings.SettingsPinHash, tokenAsUnlock));
        Assert.False(await FilterEndpoints.VerifyPinHeaderAsync(account.Settings.SettingsPinHash, tokenAsPin));
    }

    [Fact]
    public async Task Pin_removal_gate_accepts_the_raw_pin()
    {
        var account = PinnedAccount();
        var http = HttpWithHeaders(("X-Settings-Pin", "2468"));

        Assert.True(await FilterEndpoints.VerifyPinHeaderAsync(account.Settings.SettingsPinHash, http));
    }
}
