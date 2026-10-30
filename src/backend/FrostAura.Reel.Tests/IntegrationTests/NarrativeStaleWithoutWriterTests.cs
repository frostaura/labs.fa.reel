using System.Text.Json;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// A narrative written under an older prompt is reported stale whether or not a rewrite can be
/// queued (HREV6-DESK-01, open since HREV5-DESK-07). The read used to set <c>isStale</c> only on a
/// fingerprint or crowd-band move and treat the old prompt purely as a reason to queue; with no key
/// or an open OpenRouter breaker nothing was queued, so a v2 narrative reading "-0.18" as "below the
/// crowd" was served as current beside a tile saying "with the crowd".
///
/// <para>Every narrative here is seeded with the fingerprint and fact sheet the read itself will
/// recompute, so the ONLY thing that can make it stale is its prompt generation — the older
/// fixtures used a placeholder hash and were stale by fingerprint whatever the prompt said.</para>
///
/// <para>Its own class, and each case its own derived host, because one case swaps the writer and
/// another opens the host's process-wide provider breaker.</para>
/// </summary>
[Collection("postgres")]
public class NarrativeStaleWithoutWriterTests(PostgresFixture fixture, ReelApiFactory api)
    : IClassFixture<ReelApiFactory>
{
    private const int CurrentPrompt = OpenRouterLensWriter.CurrentNarrativePromptVersion;

    [Theory]
    [InlineData(CurrentPrompt - 1)]
    [InlineData(1)]
    public async Task An_older_prompt_with_no_writer_is_stale_and_queues_nothing(int promptVersion)
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var host = KeylessHost();
        var accountId = await api.SeedAccountAsync($"narrative-keyless-{Guid.NewGuid():N}");
        await SeedCurrentFingerprintAsync(host, accountId, promptVersion, editedText: null);

        var body = await ReadAsync(host, accountId);

        Assert.False(body.GetProperty("writerAvailable").GetBoolean());
        Assert.True(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(TasteNarrativeService.StaleOutdatedPrompt, body.GetProperty("staleReason").GetString());
        Assert.False(body.GetProperty("regenerating").GetBoolean());
        Assert.Equal(0, await NarrativeJobsAsync(accountId));
    }

    [Fact]
    public async Task An_older_prompt_behind_an_open_breaker_is_stale_and_queues_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var host = api.WithWebHostBuilder(_ => { });
        var health = host.Services.GetRequiredService<ProviderHealthService>();
        var accountId = await api.SeedAccountAsync($"narrative-breaker-stale-{Guid.NewGuid():N}");
        await SeedCurrentFingerprintAsync(host, accountId, CurrentPrompt - 1, editedText: null);

        health.ReportRefused(ApiProvider.OpenRouter, 402);
        try
        {
            var body = await ReadAsync(host, accountId);

            Assert.True(body.GetProperty("writerAvailable").GetBoolean());
            Assert.True(body.GetProperty("isStale").GetBoolean());
            Assert.Equal(TasteNarrativeService.StaleOutdatedPrompt, body.GetProperty("staleReason").GetString());
            Assert.False(body.GetProperty("regenerating").GetBoolean());
            Assert.Equal(0, await NarrativeJobsAsync(accountId));
        }
        finally
        {
            health.ReportAnswered(ApiProvider.OpenRouter);
        }
    }

    [Fact]
    public async Task A_current_prompt_with_no_writer_is_current()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The control: same seeding, same keyless host, current prompt — nothing is stale, so the
        // cases above are stale for the prompt alone and not for anything the seeding got wrong.
        await using var host = KeylessHost();
        var accountId = await api.SeedAccountAsync($"narrative-keyless-current-{Guid.NewGuid():N}");
        await SeedCurrentFingerprintAsync(host, accountId, CurrentPrompt, editedText: null);

        var body = await ReadAsync(host, accountId);

        Assert.False(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("staleReason").ValueKind);
    }

    [Fact]
    public async Task An_edited_narrative_over_an_older_prompt_is_the_users_words_and_current()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The shown text is the user's own correction, which no prompt generation wrote; the edit
        // wins everywhere, so it is not flagged as an older Reel's reading.
        await using var host = KeylessHost();
        var accountId = await api.SeedAccountAsync($"narrative-keyless-edited-{Guid.NewGuid():N}");
        await SeedCurrentFingerprintAsync(host, accountId, CurrentPrompt - 1, editedText: "I rate with the crowd.");

        var body = await ReadAsync(host, accountId);

        Assert.True(body.GetProperty("isEdited").GetBoolean());
        Assert.False(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("staleReason").ValueKind);
    }

    [Fact]
    public async Task A_moved_fingerprint_is_reported_as_taste_moved()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Taste moved takes precedence over the prompt: it is the claim about the person, and it
        // holds for an edited narrative too.
        await using var host = KeylessHost();
        var accountId = await api.SeedAccountAsync($"narrative-keyless-moved-{Guid.NewGuid():N}");
        await SeedCurrentFingerprintAsync(
            host, accountId, CurrentPrompt - 1, editedText: "My words.", profileHashOverride: "moved");

        var body = await ReadAsync(host, accountId);

        Assert.True(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(TasteNarrativeService.StaleTasteMoved, body.GetProperty("staleReason").GetString());
    }

    /// <summary>The harness with a writer that is not configured — no key, no stub mode.</summary>
    private WebApplicationFactory<Program> KeylessHost() =>
        api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILensWriter>();
            services.AddSingleton<ILensWriter, KeylessWriter>();
        }));

    /// <summary>
    /// Seeds a narrative carrying the fingerprint and fact sheet the read will recompute for this
    /// account, through the same builders the endpoint uses. The account is pinned first: tenancy
    /// fails closed, so the builders' account-scoped reads throw outside a pinned scope (HREV8-PLAT-01).
    /// </summary>
    private static async Task SeedCurrentFingerprintAsync(
        WebApplicationFactory<Program> host, Guid accountId, int promptVersion, string? editedText,
        string? profileHashOverride = null)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAccountContext>().SetAccount(accountId);
        var now = DateTime.UtcNow;
        var taste = await scope.ServiceProvider.GetRequiredService<FeatureVectorBuilder>()
            .BuildTasteStateAsync(accountId, now, CancellationToken.None);
        var built = await scope.ServiceProvider.GetRequiredService<TasteFactsBuilder>()
            .BuildAsync(accountId, taste, now, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        db.AccountTasteNarratives.Add(new AccountTasteNarrative
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            ProfileHash = profileHashOverride ?? built.ProfileHash,
            ProfileFingerprint = "{}",
            Text = "You rate below the crowd overall.",
            FactsJson = JsonSerializer.Serialize(built.Facts, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ModelId = "stub/deterministic-v1",
            PromptVersion = promptVersion,
            GeneratedAt = now,
            EditedText = editedText,
            EditedAt = editedText is null ? null : now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> ReadAsync(WebApplicationFactory<Program> host, Guid accountId)
    {
        var answer = await EdgeSecurityTests.SendAsync(host, "GET", "/api/taste/narrative", await AccessCookieAsync(accountId));
        Assert.Equal(200, answer.Status);
        using var document = JsonDocument.Parse(answer.Body);
        return document.RootElement.Clone();
    }

    private async Task<string> AccessCookieAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        return "reel_at=" + scope.ServiceProvider.GetRequiredService<Application.Auth.SessionService>()
            .MintAccessToken(account, DateTime.UtcNow);
    }

    private async Task<int> NarrativeJobsAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        return await db.SyncJobs.IgnoreQueryFilters()
            .CountAsync(j => j.AccountId == accountId && j.Kind == JobKind.BuildTasteNarrative);
    }

    /// <summary>A writer with no key and no stub mode: it reports unavailable and is never called.</summary>
    private sealed class KeylessWriter : ILensWriter
    {
        public bool IsAvailable => false;

        public string ModelId => "stub/deterministic-v1";

        public int LensPromptVersion => OpenRouterLensWriter.CurrentLensPromptVersion;

        public int NarrativePromptVersion => CurrentPrompt;

        public Task<NarrativeDraft?> WriteNarrativeAsync(TasteFacts facts, CancellationToken ct = default) =>
            throw new InvalidOperationException("An unavailable writer is never asked to write.");

        public Task<IReadOnlyList<LensDraft?>> WriteLensAsync(
            IReadOnlyList<LensTitleInput> titles, LensPersona persona, CancellationToken ct = default) =>
            throw new InvalidOperationException("An unavailable writer is never asked to write.");

        public Task<LensAuditScore?> JudgeAsync(
            LensTitleInput title, LensPersona persona, LensNote note, CancellationToken ct = default) =>
            throw new InvalidOperationException("An unavailable writer is never asked to judge.");
    }
}
