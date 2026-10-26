using System.Net;
using System.Net.Http.Json;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Eval;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// R1-38, whole app: the eval stack's Trakt isolation is wired into the real composition root, not
/// only into a hand-built service collection. The host is booted through <c>Program</c> exactly as the
/// eval launchers boot it — <c>TRAKT_CLIENT_ID=eval-no-trakt</c> in configuration and nothing else — so
/// this fails if <c>AddReelInfrastructure</c> stops calling <c>AddReelEvalIsolation</c>, or calls it
/// before the real Trakt adapter is registered (the real adapter would then win).
/// </summary>
[Collection("postgres")]
public class EvalTraktIsolationWiringTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public void The_eval_client_id_boots_the_app_with_Trakt_offline()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var factory = WithTraktClientId(OfflineTraktClient.EvalClientId);

        var trakt = factory.Services.GetRequiredService<ITraktClient>();

        Assert.True(
            trakt is OfflineTraktClient,
            $"With TRAKT_CLIENT_ID={OfflineTraktClient.EvalClientId} the app resolved {trakt.GetType().Name}, so Trakt is live on the eval stack. " +
            "AddReelInfrastructure must call services.AddReelEvalIsolation(configuration) after AddReelData() (CR-1).");
        Assert.Same(factory.Services.GetRequiredService<OfflineTraktClient>(), trakt);
    }

    [Fact]
    public async Task A_sign_in_on_the_eval_stack_is_refused_inside_the_process()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The eval descriptor's default redirect; /trakt/start refuses to build an authorize URL without one.
        using var factory = WithTraktClientId(OfflineTraktClient.EvalClientId, redirectUri: "http://localhost:5173/auth/trakt/callback");
        var offline = factory.Services.GetService<OfflineTraktClient>();
        if (offline is null)
        {
            Assert.Fail("OfflineTraktClient is not registered in the running app (CR-1). Refusing to send a request that would reach api.trakt.tv.");
            return;
        }

        using var client = factory.CreateClient();
        var start = await client.PostAsync("/api/auth/trakt/start", content: null);
        start.EnsureSuccessStatusCode();
        var state = (await start.Content.ReadFromJsonAsync<StartBody>())!.State;

        var callback = await client.PostAsJsonAsync("/api/auth/trakt/callback", new { code = "eval-code", state });

        // The code exchange is the first Trakt call of a sign-in. Refused locally, it fails the way an
        // unreachable Trakt fails, so the endpoint answers its usual "try again" 400 and links no account.
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Equal(1, offline.Refused);
    }

    [Fact]
    public void A_blank_client_id_keeps_the_real_adapter()
    {
        if (!fixture.Available)
        {
            return;
        }

        // How every other test, and every non-eval stack, boots.
        using var factory = WithTraktClientId(string.Empty);

        Assert.IsType<TraktClient>(factory.Services.GetRequiredService<ITraktClient>());
        Assert.Null(factory.Services.GetService<OfflineTraktClient>());
    }

    [Theory]
    [InlineData("eval-no-trakt", true)]
    [InlineData(" EVAL-NO-TRAKT ", true)]
    [InlineData("eval-no-trakt-2", false)]
    [InlineData("a1b2c3-real-trakt-client", false)]
    public void The_harness_guard_lets_through_only_the_eval_selector(string clientId, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TRAKT_CLIENT_ID"] = clientId,
            ["TestHelpers:ModelStubMode"] = "true",
        }).Build();

        Assert.Equal(allowed, ReelApiFactory.IsEvalTraktSelector("TRAKT_CLIENT_ID", clientId));
        // The selector is honoured for the Trakt client id only; the same value under a secret's name is live.
        Assert.False(ReelApiFactory.IsEvalTraktSelector("TRAKT_CLIENT_SECRET", clientId));

        // The guard checks credentials first, so a refusal that names TRAKT_CLIENT_ID is that check, not
        // the database check behind it (this configuration has no connection string at all).
        var refusal = Assert.Throws<InvalidOperationException>(() => ReelApiFactory.GuardAgainstLiveCredentials(configuration));
        Assert.Equal(!allowed, refusal.Message.Contains("TRAKT_CLIENT_ID", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>UseSetting</c>, not <c>ConfigureAppConfiguration</c>: under minimal hosting an app-configuration
    /// callback lands only when the host builds, after <c>Program</c> has already run
    /// <c>AddReelInfrastructure</c>, which reads the client id at registration time. A host setting is
    /// in <c>builder.Configuration</c> from the start, as the eval launchers' environment variable is.
    /// </summary>
    private WebApplicationFactory<Program> WithTraktClientId(string clientId, string? redirectUri = null) =>
        api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TRAKT_CLIENT_ID", clientId);
            if (redirectUri is not null)
            {
                builder.UseSetting("TRAKT_REDIRECT_URI", redirectUri);
            }
        });

    private sealed record StartBody(string AuthorizeUrl, string State);
}
