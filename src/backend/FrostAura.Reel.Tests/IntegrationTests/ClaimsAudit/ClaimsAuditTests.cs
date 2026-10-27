using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Typeahead;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.Fakes;
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

    /// <summary>
    /// The conditions scope sentence (conditionsCopy.ts): "…top-bar search. … anything else your conditions
    /// rule out is counted, not shown." The audit found it FALSE for the maturity ceiling, a condition the
    /// editor sets: the lookup's local leg drops an over-ceiling title and counts it, but the global leg
    /// judges a TMDB row by the content rules alone (<c>ContentFilteredTitles</c> for a catalogue title,
    /// <c>StandingDiscoverPlan.Admits</c> beyond it), neither of which reads the ceiling — so the same title
    /// comes straight back as a global row. This pins the leak; when WS-3 holds the global leg to the
    /// ceiling it fails here, and the register's verdict goes back for review.
    /// </summary>
    [Fact]
    public async Task The_top_bar_lookups_global_leg_shows_a_title_over_the_maturity_ceiling_that_the_local_leg_hides()
    {
        if (!fixture.Available)
        {
            return;
        }

        var token = "zq" + Guid.NewGuid().ToString("N")[..8];
        var tmdbId = Random.Shared.NextInt64(1, long.MaxValue);
        Guid accountId;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var account = new Account
            {
                Id = Guid.NewGuid(),
                TraktUserSlug = $"claims-maturity-{Guid.NewGuid():N}",
                Tier = AccountTier.Paid,
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                PipelineStageChangedAt = DateTime.UtcNow,
            };
            account.Settings.MaturityCeiling = "PG-13";
            db.Add(account);
            db.Add(new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = tmdbId,
                TraktSlug = string.Empty,
                Name = $"{token} After Dark",
                Language = "en",
                Genres = ["drama"],
                Year = 2010,
                Certification = "R",
                TmdbVoteAverage = 7m,
                TmdbVoteCount = 500,
                TmdbPopularity = 0m,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        await using var read = fixture.CreateContext(new AccountContext());
        var search = new TypeaheadSearch(read, new EligibilityQueryBuilder(read));
        var local = await search.LocalAsync(accountId, token, default);
        Assert.Empty(local.Titles);
        Assert.Equal(1, local.HiddenByConditions);

        // Vary one field: the same title, the same term, asked of the global leg.
        var tmdb = new SearchOnlyTmdbClient
        {
            MovieResults =
            [
                new TmdbListItem(tmdbId, true, $"{token} After Dark", "/p.jpg", null, "An overview.", 50m, 7m, 500,
                    new DateTime(2010, 5, 14, 0, 0, 0, DateTimeKind.Utc), [18], "en"),
            ],
        };
        var global = await search.GlobalAsync(accountId, tmdb, token, default);
        Assert.Equal([tmdbId], global.Rows.Select(r => r.TmdbId));
    }

    /// <summary>
    /// Ask Reel's conditions answer: "You have no standing conditions set, so every unseen title in the
    /// catalogue is open to you." The audit found it FALSE: it is said whenever the content rules describe
    /// to nothing, which is also the default English-only language rule (FilterRuleSet's DefaultLanguage —
    /// every non-English title is held out) and an account whose only conditions are a maturity ceiling or
    /// a "for you" bar (neither is in FilterRuleSet, so neither is described). This pins the sentence for
    /// the account with no rows at all; when WS-2 states the defaults, it fails here for review.
    /// </summary>
    [Fact]
    public void Asks_no_conditions_answer_is_given_while_the_default_language_rule_holds_titles_out()
    {
        var none = FilterRuleSet.FromRows([]);
        Assert.False(none.AllLanguagesAllowed);
        Assert.Equal([FilterRuleSet.DefaultLanguage], none.EffectiveLanguages);
        Assert.StartsWith("You have no standing conditions set", AskReelEngine.ConditionsAnswer(none, null), StringComparison.Ordinal);

        // Vary one field: with every language allowed the sentence is true — and the answer is word for
        // word the same, so it cannot tell the account it is true of from the one it is false for.
        var all = FilterRuleSet.Create(null, null, null, [FilterRuleSet.AllLanguages], null, null, null);
        Assert.True(all.AllLanguagesAllowed);
        Assert.Equal(AskReelEngine.ConditionsAnswer(all, null), AskReelEngine.ConditionsAnswer(none, null));
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
