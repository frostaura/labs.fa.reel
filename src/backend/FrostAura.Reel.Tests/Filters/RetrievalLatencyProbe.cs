using System.Diagnostics;
using System.Globalization;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit.Abstractions;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The measurements behind WS-3's latency acceptance, against a real account's data: the cost meter
/// (p95 ≤ 150 ms over 20 uncached drafts). Each figure quoted in the code comments and the lane
/// report can be reproduced from here rather than taken on trust.
///
/// <para><b>Opt-in only, never in CI.</b> It does nothing unless <c>REEL_RETRIEVAL_PROBE=1</c>, and
/// then needs <c>REEL_PROBE_CONNECTION_STRING</c> (a SLOT copy of an account's data — never the live
/// database) and optionally <c>REEL_PROBE_ACCOUNT_ID</c> (default: the account with the most
/// ratings). It reads and writes nothing, and calls no paid provider. Run with
/// <c>--filter FullyQualifiedName~RetrievalLatencyProbe --logger "console;verbosity=detailed"</c>,
/// with nothing else loading the machine (latency figures are only valid unloaded).</para>
/// </summary>
public class RetrievalLatencyProbe(ITestOutputHelper output)
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("REEL_PROBE_CONNECTION_STRING");

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("REEL_RETRIEVAL_PROBE") == "1" && !string.IsNullOrWhiteSpace(ConnectionString);

    [Fact]
    public async Task Cost_meter_p95_over_twenty_uncached_drafts()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, saved) = await AccountAsync();
        var drafts = new List<FilterRuleSet> { saved };
        foreach (var rule in saved.Rules())
        {
            drafts.Add(saved.Without(rule));
        }

        drafts.Add(saved.WithExclusions(["horror"], null));
        drafts.Add(saved.WithExclusions(["thriller", "war"], ["gore"]));
        drafts.Add(saved.WithExclusions(null, ["zombie", "shark"]));
        drafts.Add(FilterRuleSet.Create(saved.ExcludeGenres, ["drama", "crime"], saved.ExcludeKeywords, saved.Languages, saved.MinCrowdRating, saved.MinYear, saved.MaxYear));
        drafts = drafts.DistinctBy(d => d.CacheKey()).Take(20).ToList();

        // One warm-up so connection setup is not billed to the first draft.
        await MeasureAsync(accountId, saved);
        var elapsed = new List<double>();
        foreach (var draft in drafts)
        {
            var clock = Stopwatch.StartNew();
            var report = await MeasureAsync(accountId, draft);
            elapsed.Add(clock.Elapsed.TotalMilliseconds);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{clock.Elapsed.TotalMilliseconds,7:F1} ms  eligible {report.Eligible,5} of {report.Total}  rules {report.ByRule.Count}"));
        }

        output.WriteLine(Summary("cost meter", elapsed));
    }

    internal static string Summary(string what, IReadOnlyList<double> elapsed)
    {
        var sorted = elapsed.Order().ToList();
        double At(double q) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{what}: n={sorted.Count} p50 {At(0.5):F1} ms  p95 {At(0.95):F1} ms  max {sorted[^1]:F1} ms");
    }

    internal static ReelDbContext Context(Guid? accountId = null)
    {
        var context = new AccountContext();
        if (accountId is { } id)
        {
            context.SetAccount(id);
        }

        var options = new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(ConnectionString!, n => n.UseVector());
        if (Environment.GetEnvironmentVariable("REEL_PROBE_SQL") is { Length: > 0 } sqlFile)
        {
            // Writes every statement to a file for EXPLAIN ANALYZE; values are the probe's own drafts.
            options.LogTo(
                line => File.AppendAllText(sqlFile, line + Environment.NewLine),
                [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted]);
            options.EnableSensitiveDataLogging();
        }

        return new ReelDbContext(options.Options, context);
    }

    internal static async Task<(Guid AccountId, FilterRuleSet Saved)> AccountAsync()
    {
        await using var db = Context();
        var configured = Environment.GetEnvironmentVariable("REEL_PROBE_ACCOUNT_ID");
        var accountId = Guid.TryParse(configured, out var id)
            ? id
            : await db.UserRatings.GroupBy(r => r.AccountId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        var rows = await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync();
        return (accountId, FilterRuleSet.FromRows(rows));
    }

    private static async Task<FilterCostReport> MeasureAsync(Guid accountId, FilterRuleSet rules)
    {
        await using var db = Context(accountId);
        var context = new AccountContext();
        context.SetAccount(accountId);
        var meter = new FilterCostMeter(db, new EligibilityQueryBuilder(db), context, new MemoryCache(new MemoryCacheOptions()));
        return await meter.MeasureAsync(rules);
    }
}
