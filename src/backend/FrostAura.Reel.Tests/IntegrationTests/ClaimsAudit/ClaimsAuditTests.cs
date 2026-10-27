using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests.ClaimsAudit;

/// <summary>
/// Pins for user-facing claims that no owner's test covered (program WS-7 task 9; the register is
/// <c>eval/claims/claims.json</c> and <c>scripts/eval/claims.mjs check</c> holds the tree to it). Each test
/// fails when the code stops doing what a line of copy says — or, for a claim the audit found FALSE, when
/// the code starts doing it, so the register's verdict is revisited rather than left stale.
/// </summary>
[Collection("postgres")]
public class ClaimsAuditTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private static readonly Assembly[] AppAssemblies =
    [
        typeof(BuildDigestJobHandler).Assembly, // Application
        typeof(Infrastructure.Persistence.ReelDbContext).Assembly,
        typeof(Program).Assembly, // Api
    ];

    /// <summary>
    /// PushOptIn: "One notification on Fridays when your "new &amp; high for you" picks land. Nothing else,
    /// ever." The only consumer of the push port is the weekly digest job; a second one makes the line false.
    /// </summary>
    [Fact]
    public void Only_the_weekly_digest_job_can_send_a_push_notification()
    {
        var consumers = AppAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && !typeof(IWebPushSender).IsAssignableFrom(t))
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(IWebPushSender)
                    || (p.ParameterType.IsGenericType && p.ParameterType.GetGenericArguments().Contains(typeof(IWebPushSender)))))
            .Select(t => t.FullName)
            .ToList();

        Assert.Equal([typeof(BuildDigestJobHandler).FullName], consumers);
    }

    /// <summary>
    /// The Settings receipt line: "Reel sends nothing to it — paid plans are switched off." Nothing in the
    /// app references a mail client, so nothing can send to the stored address.
    /// </summary>
    [Fact]
    public void Nothing_in_the_app_can_send_an_email()
    {
        // No mail library is referenced, and no app source names a sender. System.Net.Mail itself is
        // referenced for MailAddress, which SettingsEndpoints uses to validate the address's shape only.
        string[] libraries = ["MailKit", "MimeKit", "SendGrid", "Postmark", "Mailgun", "FluentEmail", "AWSSDK.SimpleEmail", "Resend"];
        var references = AppAssemblies.SelectMany(a => a.GetReferencedAssemblies()).Select(r => r.Name ?? string.Empty).ToList();
        Assert.DoesNotContain(references, name => libraries.Any(m => name.StartsWith(m, StringComparison.OrdinalIgnoreCase)));

        string[] senders = ["SmtpClient", "SendMailAsync", "IEmailSender", "MailMessage"];
        var backend = Path.Combine(RepoRoot(), "src", "backend");
        var offenders = new[] { "FrostAura.Reel.Api", "FrostAura.Reel.Application", "FrostAura.Reel.Infrastructure", "FrostAura.Reel.Domain" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(backend, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => senders.Any(sender => File.ReadAllText(file).Contains(sender, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// TasteNarrativePanel: "Reel reads this before every pick it makes for you." The audit found it FALSE:
    /// the model that scores every pick builds its features without the narrative. This pins that fact,
    /// so wiring the narrative into the features fails here and sends the register's verdict back for review.
    /// </summary>
    [Fact]
    public void The_scoring_model_never_reads_the_taste_narrative()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "backend", "FrostAura.Reel.Application", "Ml", "FeatureVectorBuilder.cs"));
        Assert.DoesNotContain("Narrative", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(FeatureVectorBuilder).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(PersonalisationContext));
    }

    /// <summary>
    /// RatePicker: "7 more ratings and Reel retrains tonight" / "That's enough — Reel retrains tonight". The
    /// status endpoint repeats the RETRAIN_MIN_NEW_RATINGS key and its default instead of asking RetrainGate;
    /// the copy is true only while the number the page counts down is the one the nightly gate applies.
    /// </summary>
    [Fact]
    public async Task The_retrain_threshold_the_status_reports_is_the_one_the_nightly_gate_applies()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await api.CreateSignedInAccountAsync($"claims-retrain-{Guid.NewGuid():N}");
        var status = await client.GetFromJsonAsync<JsonElement>("/api/sync/status");
        var reported = status.GetProperty("retrain").GetProperty("minNewRatings").GetInt32();

        using var scope = api.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<RetrainGate>();
        Assert.Equal(gate.Threshold, reported);
        Assert.Equal(RetrainPolicy.DefaultMinNewRatings, reported);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("fa.reel.slnx not found above the test assembly.");
    }
}
