using System.Text.Json;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// What <c>GET /api/dev/lens-audit</c> reports about REFUSED notes.
///
/// <para><b>Why the refusals are the report.</b> The audit exists so prompt versions can be
/// compared, and the deterministic half of that comparison — which notes the guards threw out and
/// why — is worth more than any of the model-scored means, because it is the only number on the
/// report that is not itself a model output. During the founder's outage every on-demand note was
/// refused for the same reason by the same guard over the same four titles, and a report that
/// showed the reason WITHOUT the offending title would have said "invented-anchor: 2" and led
/// nowhere. The token is the diagnosis.</para>
/// </summary>
[Collection("postgres")]
public class LensAuditReportTests(PostgresFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private sealed class NoopScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("No flush expected here.");
    }

    private static IConfiguration StubConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TestHelpers:ModelStubMode"] = "true" })
            .Build();

    [Fact]
    public async Task Refused_notes_appear_in_the_report_with_their_reason_and_the_token_behind_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAsync();
        await using var db = fixture.CreateContext(Scoped(accountId));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = StubConfig();
        var writer = new OpenRouterLensWriter(
            new HttpClient { BaseAddress = new Uri("http://stub.invalid/") },
            new ApiUsageRecorder(new NoopScopeFactory(), NullLogger<ApiUsageRecorder>.Instance),
            configuration,
            NullLogger<OpenRouterLensWriter>.Instance);
        var audit = new LensAudit(
            db,
            writer,
            new LensInputBuilder(db, new RatingCalibrationService(db, cache)),
            new PersonalisationContext(db, new DislikeSignal(db, cache)));

        var taste = await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, Now, default);
        // score:false — the deterministic half is the half under test, and paying a judge to agree
        // with a guard is spend for nothing.
        var report = await audit.RunAsync(accountId, taste, sampleSize: 50, score: false, default);

        Assert.Equal(3, report.Rows.Count);
        Assert.Equal(2, report.RejectedByGuards);

        var refused = report.Rows.Single(r => r.RejectionKind == LensRejectionReasons.InventedAnchor);
        Assert.Equal("invented-anchor:Frontera", refused.RejectionReason);
        Assert.Equal("Frontera", refused.RejectionToken);
        // The prose is on the row too: reading fifty generations yourself is the check that
        // actually matters, and a refused generation is one you especially want to read.
        Assert.NotNull(refused.Lens);
        Assert.Equal(["Frontera"], refused.Anchors);

        // Grouped by failure class, so a prompt edit that trades one class for another is visible
        // rather than hidden inside an unchanged total.
        var byReason = report.RejectedByReason.ToDictionary(r => r.Reason, StringComparer.Ordinal);
        Assert.Equal(1, byReason[LensRejectionReasons.InventedAnchor].Count);
        Assert.Equal(["Frontera"], byReason[LensRejectionReasons.InventedAnchor].Tokens);
        Assert.Equal(1, byReason[LensRejectionReasons.Spoiler].Count);
        // A refusal that carries no token reports none rather than inventing one.
        Assert.Empty(byReason[LensRejectionReasons.Spoiler].Tokens);
    }

    [Fact]
    public void A_reason_written_before_tokens_existed_is_still_readable()
    {
        // Rows persisted by the previous build carry a bare reason. They must group by class, not
        // become an unreadable row on a report whose whole job is comparing across versions.
        Assert.Equal(("invented-anchor", null), LensAudit.SplitReason("invented-anchor"));
        Assert.Equal(("invented-anchor", "Frontera"), LensAudit.SplitReason("invented-anchor:Frontera"));
        Assert.Equal((null, null), LensAudit.SplitReason(null));
    }

    private async Task<Guid> SeedAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var accountId = Guid.NewGuid();
        db.Accounts.Add(new Account
        {
            Id = accountId,
            TraktUserSlug = $"audit-{accountId:N}",
            TraktUsername = "audit",
            DisplayName = "Audit",
            CreatedAt = Now,
            LastSeenAt = Now,
        });

        Guid Title(string name)
        {
            var title = new FrostAura.Reel.Domain.Catalog.Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"audit-{Guid.NewGuid():N}",
                TmdbId = 600_000 + Random.Shared.Next(1_000_000),
                Name = name,
                Year = 2015,
                Genres = ["crime"],
                CreatedAt = Now,
            };
            db.Titles.Add(title);
            return title.Id;
        }

        void Copy(Guid titleId, LensStatus status, string? reason, IReadOnlyList<string> anchors)
        {
            var note = new LensNote(
                "A cold, procedural piece that keeps its pressure low and constant.",
                "Quiet and relentless.",
                null,
                anchors.Select(a => new LensAnchor(a, "sits in the same register")).ToList(),
                "tense",
                "tonight-alone");
            db.TitleLensCopies.Add(new TitleLensCopy
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ProfileHash = "abc123abc123",
                PromptVersion = OpenRouterLensWriter.CurrentLensPromptVersion,
                LensJson = JsonSerializer.Serialize(note, JsonOptions),
                ModelId = "stub/deterministic-v1",
                Status = status,
                RejectionReason = reason,
                GeneratedAt = Now,
            });
        }

        Copy(Title("Kept"), LensStatus.Done, null, []);
        Copy(
            Title("Refused For An Anchor"),
            LensStatus.Rejected,
            LensRejectionReasons.WithToken(LensRejectionReasons.InventedAnchor, "Frontera"),
            ["Frontera"]);
        Copy(Title("Refused For A Spoiler"), LensStatus.Rejected, LensRejectionReasons.Spoiler, []);

        await db.SaveChangesAsync();
        return accountId;
    }
}
