using System.Net;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The in-process API harness: the real <c>Program</c> pipeline — real middleware, real endpoint
/// filters, real EF, real Postgres — with four specific things neutralised, each of which would
/// otherwise turn a test run into a bill or into a write against the founder's own database.
///
/// <para><b>The namespace is load-bearing.</b> CI's integration job filters on
/// <c>FullyQualifiedName~IntegrationTests</c>, so anything DB-backed that lives outside this
/// namespace silently never runs in CI. Do not move this type.</para>
///
/// <para><b>Hazard 1 — the real <c>.env</c> lands in the process.</b> <c>Program</c> calls
/// <see cref="Api.DotEnv.Load"/> before configuration builds, and there IS a real <c>.env</c> at
/// the repository root of a dev clone. <c>DotEnv</c> never overrides an already-set variable
/// (<c>DotEnv.cs:39</c>), which is the whole lever: the static constructor below blanks every
/// provider credential BEFORE the host builds, so the loader finds them set and skips them. Blank,
/// not absent — an absent variable would be re-populated from the file.</para>
///
/// <para><b>Hazard 2 — the model adapters would go live.</b> Blank keys already force the
/// OpenRouter adapters into their deterministic stub contract, but that is one condition away from
/// a live call, so <c>TestHelpers:ModelStubMode=true</c> is set as well. Belt and braces, and
/// <see cref="GuardAgainstLiveCredentials"/> asserts both are actually in force.</para>
///
/// <para><b>Hazard 3 — ten hosted services and one embedding provider.</b>
/// <c>DependencyInjection.cs:212-220</c> registers nine background services and <c>:55</c> a tenth
/// (<see cref="ApiUsageRecorder"/>); left in, the job runner, the Trakt delta poll, the nightly
/// reconcile and the catalogue growth loops all start doing real work against real APIs the moment
/// a client is created. All ten are removed. <see cref="ApiUsageRecorder"/> is deliberately still
/// registered as a singleton and as <c>IPendingApiUsage</c>, so <c>Record</c> still counts — the
/// ledger assertions read <c>PendingToday</c> rather than the table, because
/// <c>ApiUsageRecorder.FlushAsync</c> is <c>internal</c> and, with the hosted service gone, nothing
/// drains the counters to Postgres at all. Separately, the embeddings adapter has no stub mode of
/// its own (unlike every OpenRouter adapter), so <see cref="IEmbeddingProvider"/> is replaced with
/// <see cref="StubEmbeddingProvider"/> rather than merely being left keyless — keyless would make
/// it unavailable and silently route every Ask Reel turn down the lexical fallback.</para>
///
/// <para><b>Hazard 4 — the database.</b> The connection string comes from
/// <see cref="PostgresFixture"/>'s throwaway database, injected both as an environment variable
/// (again ahead of <c>DotEnv</c>, whose <c>.env</c> points at the founder's live <c>reel</c>) and
/// as configuration. The fixture has already dropped and re-migrated it by the time any test runs,
/// so <c>DatabaseInitializer.InitializeAsync</c> finds nothing pending and is a no-op — it stays
/// enabled so the harness is still correct against a database the fixture has not touched.</para>
///
/// <para><b>What is deliberately NOT mitigated: TMDB.</b> <c>ITmdbClient</c> is left real, so a
/// turn through this harness makes a genuine outbound request to <c>api.themoviedb.org</c> with a
/// blank bearer and gets a 401. That is free — TMDB bills nothing and the token is blank — and it
/// is not flaky, because being offline produces the same <c>HttpRequestException</c> the 401
/// produces. It is left in on purpose: stubbing the catalogue port by default would hand every
/// future test an empty corpus and let a discovery assertion pass for the wrong reason. A test
/// that wants an offline catalogue should swap the port itself via
/// <c>WithWebHostBuilder</c>.</para>
///
/// <para>One consequence is worth knowing before writing a test here: today that 401 escapes
/// <c>LiveSearchExpansionService.DiscoverCandidateIdsAsync</c> unguarded and is caught only by the
/// service's outermost handler, so a turn in this harness currently ends
/// <c>done { failed: true, reason: "search hit a snag — please try again" }</c> rather than on a
/// clean empty result. That is a real defect in the service, not in the harness, and it is already
/// on the plan (crash guards: wrap the discovery call the way the semantic arm is wrapped). Assert
/// on the wire's SHAPE here, not on <c>done.reason</c>, until it lands.</para>
/// </summary>
public sealed class ReelApiFactory : WebApplicationFactory<Program>
{
    /// <summary>The fixture's shared throwaway database — the harness every test class uses.</summary>
    public ReelApiFactory()
        : this(databaseConnectionString: null)
    {
    }

    /// <summary>
    /// Private, with <see cref="ForDatabase"/> as its door: xUnit refuses a class fixture with more than
    /// one public constructor.
    /// </summary>
    private ReelApiFactory(string? databaseConnectionString)
    {
        if (databaseConnectionString is not null)
        {
            if (PostgresFixtureGate.LiveTargetRefusal(databaseConnectionString) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            var shared = new Npgsql.NpgsqlConnectionStringBuilder(TestConnectionString);
            var own = new Npgsql.NpgsqlConnectionStringBuilder(databaseConnectionString);
            if (own.Host != shared.Host || own.Port != shared.Port || own.Database == shared.Database)
            {
                throw new InvalidOperationException(
                    "A database override must be a different database on the fixture's own eval server.");
            }
        }

        _databaseConnectionString = databaseConnectionString;
    }

    private readonly string? _databaseConnectionString;

    /// <summary>
    /// A harness whose EF context opens <paramref name="databaseConnectionString"/> instead of the shared
    /// fixture database: a test class that needs a catalogue nothing else writes into (the exclusion
    /// matrix, whose ranking surfaces would otherwise rank every other class's leftovers) owns a
    /// database of its own. It must name a different database on the same server as
    /// <see cref="PostgresFixtureGate.ConnectionStringVariable"/>'s and pass the live-target guard.
    /// </summary>
    public static ReelApiFactory ForDatabase(string databaseConnectionString) => new(databaseConnectionString);

    /// <summary>
    /// Every credential that must be blank for a test run to be incapable of spending money or
    /// touching a third party. <c>OPENAI_API_KEY</c> is here even though nothing reads it today:
    /// the embeddings adapter is OpenAI-shaped and a future one-line change to its key resolution
    /// must not silently re-open the hole.
    /// </summary>
    public static readonly string[] ProviderCredentialVariables =
    [
        "OPENROUTER_API_KEY",
        "OPENAI_API_KEY",
        "EMBEDDINGS_API_KEY",
        "TMDB_READ_ACCESS_TOKEN",
        "TRAKT_CLIENT_ID",
        "TRAKT_CLIENT_SECRET",
        "TRAKT_ACCESS_TOKEN",
        "TRAKT_REFRESH_TOKEN",
    ];

    /// <summary>
    /// A fixed signing secret so the harness can mint its own <c>reel_at</c> cookies without
    /// depending on a <c>.env</c> being present. Pre-set like the credentials, so <c>DotEnv</c>
    /// leaves it alone and the host and the tests agree on the key.
    /// </summary>
    private const string TestJwtSecret = "reel-integration-harness-signing-secret";

    /// <summary>
    /// Runs on first touch of this type — i.e. before any instance exists, and therefore before
    /// the host builds and before <c>Program</c> reaches <c>DotEnv.Load()</c>. That ordering is
    /// the entire mitigation for hazards 1 and 4; do not move this work into an instance
    /// constructor or into <see cref="ConfigureWebHost"/>.
    /// </summary>
    static ReelApiFactory()
    {
        foreach (var variable in ProviderCredentialVariables)
        {
            Environment.SetEnvironmentVariable(variable, string.Empty);
        }

        Environment.SetEnvironmentVariable("JWT_SECRET", TestJwtSecret);
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", TestConnectionString);
    }

    /// <summary>
    /// The fixture's throwaway database, read from the same environment variable
    /// <see cref="PostgresFixture"/> reads. Static because the static constructor needs it before
    /// any fixture instance is in hand — and it is the same value either way, since both come from
    /// <see cref="PostgresFixtureGate.ConnectionStringVariable"/>.
    /// </summary>
    private static string TestConnectionString =>
        Environment.GetEnvironmentVariable(PostgresFixtureGate.ConnectionStringVariable) ?? string.Empty;

    /// <summary>The ledger recorder the running host writes to — the money gate reads this.</summary>
    public ApiUsageRecorder UsageRecorder => Services.GetRequiredService<ApiUsageRecorder>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                // Deterministic answers from every OpenRouter adapter, with no key in reach.
                ["TestHelpers:ModelStubMode"] = "true",
                // Development + this flag is what gates POST /api/auth/dev/link and the founder
                // diagnostic surfaces; the harness turns it on so those routes are testable at all.
                ["QA_HELPERS_ENABLED"] = "true",
                ["ConnectionStrings:Postgres"] = TestConnectionString,
                ["JWT_SECRET"] = TestJwtSecret,
                // The fixture has already migrated; left true so the harness is also correct
                // against a database that arrives empty.
                ["Database:AutoMigrateOnStartup"] = "true",
            }));

        builder.ConfigureServices((context, services) =>
        {
            GuardAgainstLiveCredentials(context.Configuration);

            // All ten of them. Nothing in an endpoint test wants a job runner, a Trakt poll, a
            // nightly reconcile or a catalogue growth loop starting behind it.
            services.RemoveAll<IHostedService>();

            // The one adapter with no stub contract of its own.
            services.RemoveAll<IEmbeddingProvider>();
            services.AddSingleton<IEmbeddingProvider, StubEmbeddingProvider>();

            // A class that owns its database: every EF context the host builds opens it instead. The
            // registration is replaced whole (options and their configuration callbacks), so nothing
            // composes the shared connection string back in.
            if (_databaseConnectionString is { } own)
            {
                services.RemoveAll<DbContextOptions<ReelDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ReelDbContext>>();
                services.AddDbContext<ReelDbContext>(options => options.UseNpgsql(own, npgsql => npgsql.UseVector()));
            }
        });
    }

    /// <summary>
    /// Seeds a minimal linked account and returns an <see cref="HttpClient"/> already carrying its
    /// <c>reel_at</c> session cookie.
    /// </summary>
    public async Task<(Guid AccountId, HttpClient Client)> CreateSignedInAccountAsync(
        string slug, CancellationToken ct = default)
    {
        var accountId = await SeedAccountAsync(slug, ct);
        return (accountId, await CreateSignedInClientAsync(accountId, ct));
    }

    /// <summary>Inserts a linked account through the host's own DbContext and returns its id.</summary>
    public async Task<Guid> SeedAccountAsync(string slug, CancellationToken ct = default)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = slug,
            TraktUsername = slug,
            DisplayName = slug,
            Region = "ZA",
            Tier = AccountTier.Free,
            PipelineStage = PipelineStage.Linked,
            PipelineStageChangedAt = now,
            CreatedAt = now,
            LastSeenAt = now,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account.Id;
    }

    /// <summary>
    /// A signed-in client for an account that already exists.
    ///
    /// <para><b>Sign-in mechanism, and why.</b> The token is minted by the host's own
    /// <see cref="SessionService.MintAccessToken"/> and placed in a <c>reel_at</c> cookie — exactly
    /// the value <c>AccountResolutionMiddleware</c> validates, produced by exactly the code the
    /// Trakt OAuth callback runs. The alternative, driving
    /// <c>POST /api/auth/dev/link</c> under <c>QA_HELPERS_ENABLED</c>, cannot work here by
    /// construction: that route links <c>TRAKT_DEV_PROFILE</c> using <c>TRAKT_ACCESS_TOKEN</c> and
    /// <c>TRAKT_CLIENT_ID</c>, and hazard 1 has just blanked both. Minting keeps the harness
    /// hermetic and lets a test choose the account, the tier and the slug it wants.</para>
    /// </summary>
    public async Task<HttpClient> CreateSignedInClientAsync(Guid accountId, CancellationToken ct = default)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId, ct);
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var accessToken = sessions.MintAccessToken(account, DateTime.UtcNow);

        var cookies = new CookieContainerHandler();
        cookies.Container.Add(
            ClientOptions.BaseAddress,
            new Cookie("reel_at", accessToken, "/", ClientOptions.BaseAddress.Host));
        return CreateDefaultClient(cookies);
    }

    /// <summary>
    /// The assertion that makes hazard 1 provable rather than merely intended. It runs while the
    /// host is being configured — before a single request — and reads the SAME configuration the
    /// adapters will read, so it sees whatever <c>DotEnv</c> managed to land.
    ///
    /// <para>Remove the blanking in the static constructor and this throws with the offending
    /// variable named, instead of the suite quietly succeeding while a live key sits one
    /// <c>ModelStubMode</c> flag away from being spent. It also pins the database: a connection
    /// string that is not the fixture's throwaway one is a run about to write into whatever
    /// <c>.env</c> happened to point at.</para>
    /// </summary>
    internal static void GuardAgainstLiveCredentials(IConfiguration configuration)
    {
        var live = ProviderCredentialVariables
            .Where(variable => !string.IsNullOrEmpty(configuration[variable]) && !IsEvalTraktSelector(variable, configuration[variable]))
            .ToList();
        if (live.Count > 0)
        {
            throw new InvalidOperationException(
                $"ReelApiFactory would have run with LIVE provider credentials in configuration: " +
                $"{string.Join(", ", live)}. The static constructor must blank every one of them before " +
                "the host builds — DotEnv.Load() never overrides an already-set variable, and that is the " +
                "only thing keeping the repository's real .env out of this process.");
        }

        if (!configuration.GetValue("TestHelpers:ModelStubMode", false))
        {
            throw new InvalidOperationException(
                "ReelApiFactory requires TestHelpers:ModelStubMode=true. Without it the OpenRouter adapters " +
                "are one present key away from a live, billed call.");
        }

        var connectionString = configuration.GetConnectionString("Postgres");
        if (PostgresFixtureGate.LiveTargetRefusal(connectionString) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        if (string.IsNullOrWhiteSpace(TestConnectionString) || connectionString != TestConnectionString)
        {
            throw new InvalidOperationException(
                $"ReelApiFactory resolved a connection string that is not {PostgresFixtureGate.ConnectionStringVariable}'s " +
                "throwaway database. Refusing to build a host that would write into whatever .env points at.");
        }
    }

    /// <summary>
    /// <c>TRAKT_CLIENT_ID=eval-no-trakt</c> is the eval stack's selector for
    /// <see cref="Infrastructure.Eval.OfflineTraktClient"/>, not a credential: no Trakt application has
    /// that id, and it is what takes Trakt offline. It is the one non-blank value the guard lets
    /// through, so a test can boot the whole app the way the eval launchers do. Exact match only —
    /// any other client id is still refused as live.
    /// </summary>
    internal static bool IsEvalTraktSelector(string variable, string? value) =>
        variable == "TRAKT_CLIENT_ID"
        && string.Equals(value?.Trim(), Infrastructure.Eval.OfflineTraktClient.EvalClientId, StringComparison.OrdinalIgnoreCase);
}
