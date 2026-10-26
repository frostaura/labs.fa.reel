using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// WS-5 task 15 (J-11): the nightly reconcile and its start-up sweeps read every account on purpose,
/// so they say so with <see cref="IAccountContext.EnterSystemScope"/>. Under
/// <see cref="UnscopedReadMode.Throw"/> — where tenancy is heading — a sweep that forgot would throw
/// inside its own catch and log an error on every boot, leaving stale artifacts Active and untrusted
/// snapshots served.
/// </summary>
[Collection("postgres")]
public class NightlyReconcileSystemScopeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_start_up_sweeps_run_clean_when_tenancy_fails_closed()
    {
        if (!fixture.Available)
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddScoped<ReelDbContext>(sp => fixture.CreateContext(sp.GetRequiredService<IAccountContext>()));
        await using var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The two start-up sweeps run; the 03:00 loop does not.
                ["Background:NightlyReconcile"] = "false",
                ["Background:TrustPurge"] = "true",
            })
            .Build();
        var log = new RecordingLogger<NightlyReconcileService>();
        var service = new NightlyReconcileService(provider.GetRequiredService<IServiceScopeFactory>(), configuration, log);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Error);
        Assert.Contains(log.Lines, l => l.Message.Contains("NightlyReconcile disabled", StringComparison.Ordinal));
    }
}
