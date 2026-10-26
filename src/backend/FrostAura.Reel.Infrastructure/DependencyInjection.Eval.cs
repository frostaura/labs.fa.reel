using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Eval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>
    /// The eval stack's isolation (WS-7, program §4.12): when <c>TRAKT_CLIENT_ID</c> is
    /// <see cref="OfflineTraktClient.EvalClientId"/>, every <see cref="ITraktClient"/> registration is
    /// replaced by <see cref="OfflineTraktClient"/>, so no Trakt call leaves the process. Any other
    /// client id leaves the registrations untouched. Called LAST from
    /// <see cref="AddReelInfrastructure"/>, after the Trakt adapter is registered, so the replacement wins.
    /// </summary>
    internal static IServiceCollection AddReelEvalIsolation(this IServiceCollection services, IConfiguration configuration)
    {
        if (!string.Equals(configuration["TRAKT_CLIENT_ID"]?.Trim(), OfflineTraktClient.EvalClientId, StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        services.RemoveAll<ITraktClient>();
        services.AddSingleton<OfflineTraktClient>();
        services.AddSingleton<ITraktClient>(provider => provider.GetRequiredService<OfflineTraktClient>());
        return services;
    }
}
