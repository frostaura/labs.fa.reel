using FrostAura.Reel.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// The DI scope a cross-account background pass runs in (J-11, WS-4 task 10).
///
/// <para>A ticker that sweeps every account — the delta poll, the token refresh, the outbox drain,
/// the weekly digest scheduler, the catalogue schedulers, the boot-time repairs, the ratings
/// back-fill — has no account to pin: reading across accounts is what it is for. Outside a request
/// or a job execution nothing pins one either, so each of those passes used to run as an unscoped
/// read that only worked because <c>Tenancy:UnscopedReads</c> was still permissive. This says so
/// instead, so the pass keeps working when the default becomes <see cref="UnscopedReadMode.Throw"/>.</para>
///
/// <para>The handle ends with the scope: <see cref="AccountContext"/> is scoped, so disposing the
/// returned scope disposes the context that holds the system depth. Never use it for work done on
/// behalf of one account — pin that account instead.</para>
/// </summary>
internal static class SystemScopes
{
    /// <summary>A new DI scope whose <see cref="IAccountContext"/> is already in system scope.</summary>
    public static IServiceScope CreateSystemScope(this IServiceScopeFactory factory)
    {
        var scope = factory.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        return scope;
    }
}
