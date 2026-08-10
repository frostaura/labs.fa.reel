using FrostAura.Reel.Domain.Notifications;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebPush;

namespace FrostAura.Reel.Infrastructure.Notifications;

/// <summary>
/// Lazily provisions and caches the application's VAPID key pair. On first need the pair is
/// generated and persisted to <see cref="AppSecret"/> ("vapid:public" plain — it is public by
/// protocol; "vapid:private" wrapped via <see cref="ISecretProtector"/>), so every instance
/// and restart signs with the same keys — browsers bind a push subscription to the public key
/// that requested it, and rotating keys silently would orphan every subscription. Singleton;
/// resolves a scoped DbContext per initialization.
/// </summary>
public sealed class VapidKeyProvider(
    IServiceScopeFactory scopeFactory,
    ISecretProtector secretProtector,
    ILogger<VapidKeyProvider> logger)
{
    public const string PublicKeyName = "vapid:public";
    public const string PrivateKeyName = "vapid:private";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string PublicKey, string PrivateKey)? _keys;

    public async Task<(string PublicKey, string PrivateKey)> GetKeysAsync(CancellationToken ct)
    {
        if (_keys is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_keys is { } cachedAfterWait)
            {
                return cachedAfterWait;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();

            var stored = await db.AppSecrets
                .Where(s => s.Key == PublicKeyName || s.Key == PrivateKeyName)
                .ToDictionaryAsync(s => s.Key, ct);

            if (!stored.ContainsKey(PublicKeyName) || !stored.ContainsKey(PrivateKeyName))
            {
                var generated = VapidHelper.GenerateVapidKeys();
                db.AppSecrets.RemoveRange(stored.Values);
                db.AppSecrets.Add(new AppSecret { Key = PublicKeyName, ValueProtected = generated.PublicKey });
                db.AppSecrets.Add(new AppSecret
                {
                    Key = PrivateKeyName,
                    ValueProtected = secretProtector.Protect(generated.PrivateKey),
                });

                try
                {
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Generated and persisted a new VAPID key pair.");
                    _keys = (generated.PublicKey, generated.PrivateKey);
                    return _keys.Value;
                }
                catch (DbUpdateException)
                {
                    // Another instance won the provisioning race — use its keys, not ours.
                    db.ChangeTracker.Clear();
                    stored = await db.AppSecrets
                        .Where(s => s.Key == PublicKeyName || s.Key == PrivateKeyName)
                        .ToDictionaryAsync(s => s.Key, ct);
                }
            }

            _keys = (
                stored[PublicKeyName].ValueProtected,
                secretProtector.Unprotect(stored[PrivateKeyName].ValueProtected));
            return _keys.Value;
        }
        finally
        {
            _gate.Release();
        }
    }
}
