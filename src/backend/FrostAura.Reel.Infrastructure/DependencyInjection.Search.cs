using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The Ask Reel engine.</summary>
    private static IServiceCollection AddReelSearch(this IServiceCollection services)
    {
        services.AddScoped<Application.Search.LiveSearchExpansionService>();

        return services;
    }
}
