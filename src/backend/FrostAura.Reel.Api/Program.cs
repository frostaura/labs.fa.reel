using System.Globalization;
using System.Reflection;
using FrostAura.Reel.Api;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Middleware;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Infrastructure;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// ── Invariant culture, process-wide, before anything else runs ────────────────────────────────
// Nothing this service formats is localised: prompts sent to OpenRouter, TMDB and Trakt query
// strings, cache keys, SSE payloads, persisted insight text and job progress messages are all
// machine- or English-facing, and every one of them is read back or compared as an exact string.
// A host whose locale writes "4,196591281636216" instead of "4.196591281636216" therefore does
// not produce a differently-formatted product — it produces a broken one, silently, only in
// production. Directory.Build.props keeps InvariantGlobalization=false (ICU is still needed for
// culture-aware string comparison and for the language handling on the catalogue), so the
// default thread culture is what has to be pinned. Per-site CultureInfo.InvariantCulture stays
// in the formatting code as the real contract; this is the belt to that pair of braces, so a
// site nobody has audited yet cannot leak the host's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) runs this entry point
// under a mock server to read the endpoint table. That run must never load .env, open a database
// or start a background service: .env on a dev machine points at the live database.
var generatingOpenApiDocument = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

if (!generatingOpenApiDocument)
{
    DotEnv.Load();
}

var builder = WebApplication.CreateBuilder(args);

if (generatingOpenApiDocument)
{
    // Registration needs a connection string; nothing in a generation run ever opens it.
    builder.Configuration["ConnectionStrings:Postgres"] = "Host=localhost;Database=openapi_generation";
    // The document describes the production surface whatever the caller's shell exports — a
    // Development shell with QA_HELPERS_ENABLED would otherwise add the /api/dev routes to it.
    builder.Environment.EnvironmentName = Environments.Production;
    builder.Configuration["QA_HELPERS_ENABLED"] = "false";
    // Validated on host start like every other run, and nothing in a generation run signs anything.
    builder.Configuration["JWT_SECRET"] = "openapi-generation-placeholder-never-used-to-sign";
}

builder.Services.AddReelInfrastructure(builder.Configuration);
builder.Services.AddSingleton<SettingsUnlockTokenService>();
builder.Services.AddValidatedOptions<PreMigrationBackupOptions>();
builder.Services.AddOpenApi();

// ── Edge security (J-7, J-8, K-4) ────────────────────────────────────────────────────────────
// The client's scheme and address arrive as X-Forwarded-* from the gateway; they are believed only
// from TRUSTED_PROXY_NETWORKS (TrustedProxyOptions), so Request.IsHttps and the remote address are
// the real client's behind cloudflared → nginx and cannot be claimed by anyone else.
builder.Services.AddValidatedOptions<TrustedProxyOptions>();
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<TrustedProxyOptions>>((forwarded, trusted) =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var network in trusted.Value.Networks)
    {
        forwarded.KnownIPNetworks.Add(network);
    }
});
builder.Services.AddHsts(options =>
{
    // A year, this host only: frostaura.net carries other subdomains this API does not speak for.
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = false;
});
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddValidatedOptions<AskTurnLimitOptions>();
builder.Services.AddSingleton<AskTurnLimiter>();
builder.Services.AddSingleton<PinAttemptLimiter>();

// X-Request-Id in, X-Request-Id out, and the same value as every log line's RequestId (R1-36).
builder.Services.AddRequestIds();

if (generatingOpenApiDocument)
{
    builder.Services.RemoveAll<IHostedService>();
}

var corsOrigins = (builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
        .WithExposedHeaders(RequestId.HeaderName)));

var app = builder.Build();

// First, so everything after it — HSTS, the Secure-cookie rule, the loopback guard, the logs —
// sees the real client rather than the gateway.
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseCors();

app.UseMiddleware<AccountResolutionMiddleware>();
app.UseMiddleware<InboundLimits>();

// The document is generated at build time; at runtime it is served only to a developer.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapSettingsEndpoints();
app.MapSyncEndpoints();
app.MapFeedEndpoints();
app.MapTraktRecommendationEndpoints();
app.MapTitleEndpoints();
app.MapLensEndpoints();
app.MapReactionEndpoints();
app.MapPersonEndpoints();
app.MapSavedEndpoints();
app.MapSearchEndpoints();
app.MapTypeaheadEndpoints();
app.MapTasteEndpoints();
app.MapNarrativeEndpoints();
app.MapProviderEndpoints();
app.MapFilterEndpoints();
app.MapPreferenceTagEndpoints();
app.MapBillingEndpoints();
app.MapDigestEndpoints();
app.MapPushEndpoints();
app.MapBlendEndpoints();
app.MapMetricsEndpoints();
app.MapAccuracyEndpoints();
app.MapSseEndpoints();
app.MapObservabilityEndpoints();
app.MapDevEndpoints();

// Liveness, plus the commit this process was built from: the deploy job polls it and fails unless
// the SHA it just shipped is the one answering (A-17, K-9).
app.MapGet("/health", () => Results.Ok(new { status = "ok", commit = ReleaseInfo.Commit }));

app.MapGet("/health/ready", async (ReelDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    if (!canConnect)
    {
        return Results.Problem("Database unreachable", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
    return pending == 0
        ? Results.Ok(new { status = "ready" })
        : Results.Problem($"{pending} pending migration(s)", statusCode: StatusCodes.Status503ServiceUnavailable);
});

if (generatingOpenApiDocument)
{
    app.Run();
    return;
}

// A fatal error before app.Run() must END the process. On 2026-10-14 an unhandled startup
// exception left the process alive at 100% CPU and never listening, so `restart: always` had
// nothing to restart and the stack stayed down for over an hour. Exiting hands recovery to the
// container runtime.
try
{
    // Every ValidateOnStart options class, NOW: app.Run() would validate them too, but only after
    // the dump and the migrations below had already run against the database. A present-but-invalid
    // setting must stop the process before it changes anything (MOD-2).
    app.Services.GetService<IStartupValidator>()?.Validate();

    // What this process will actually spend, from the same budget objects the jobs read (K-5).
    using (var ceilingsScope = app.Services.CreateScope())
    {
        app.Logger.LogInformation("{Ceilings}", EffectiveCeilings.Describe(ceilingsScope.ServiceProvider, app.Configuration));
    }

    // An unencrypted key ring means a database dump decrypts every Trakt token (J-12). Not fatal —
    // the certificate is a founder action — but never silent outside Development.
    if (!app.Environment.IsDevelopment()
        && app.Services.GetRequiredService<IOptions<FrostAura.Reel.Infrastructure.Security.KeyRingOptions>>().Value.Certificate is null)
    {
        app.Logger.LogWarning(
            "The Data Protection key ring is stored unencrypted: set {CertPathKey} (docs/deployment.md#key-ring-certificate).",
            FrostAura.Reel.Infrastructure.Security.KeyRingOptions.CertPathKey);
    }

    // A dump before any pending migration, or no migration (K-1). No-op when nothing is pending.
    await PreMigrationBackup.EnsureAsync(app.Services);

    await DatabaseInitializer.InitializeAsync(app.Services);

    // Beside the migration step, and for the same reason it is here rather than in a hosted service:
    // both must finish BEFORE the server listens. A turn is written Pending before its stream opens and
    // stamped terminal after `done`, and both of those writers live in the request pipeline — so a
    // process killed mid-stream leaves a Pending row nothing in the replacement process would ever
    // reach. `/ask` refuses a new turn while one is Pending on the conversation, which turns that
    // leftover into a permanently wedged thread. This closes them all, once, while nothing can race it.
    await PendingTurnReconciliation.RunAsync(app.Services);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Startup failed before the server could listen; exiting so the container restarts.");
    Environment.Exit(1);
}

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
