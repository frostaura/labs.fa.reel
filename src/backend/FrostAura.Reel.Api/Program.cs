using System.Globalization;
using FrostAura.Reel.Api;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Api.Middleware;
using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Infrastructure;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReelInfrastructure(builder.Configuration);
builder.Services.AddSingleton<SettingsUnlockTokenService>();
builder.Services.AddOpenApi();

var corsOrigins = (builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.UseCors();

app.UseMiddleware<AccountResolutionMiddleware>();

app.MapOpenApi();

app.MapAuthEndpoints();
app.MapSettingsEndpoints();
app.MapSyncEndpoints();
app.MapFeedEndpoints();
app.MapTitleEndpoints();
app.MapLensEndpoints();
app.MapReactionEndpoints();
app.MapPersonEndpoints();
app.MapSavedEndpoints();
app.MapSearchEndpoints();
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
app.MapDevEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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

await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
