using System.Globalization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// SupplyReport v0: where every title matching a request went — seen, saved or dismissed, held out
/// by a standing condition, or eligible (and then unscored, under the bar, or clearing it) — plus the
/// single conditions whose relaxation would unlock something.
///
/// <para>Why it exists: the founder asked for ninja films and every ninja film passing his twelve
/// conditions was one he had already seen. The product filled the screen with unrelated action
/// titles instead of saying so. An honest "none left" needs numbers that are true, and the only way
/// to make them true is to count through the same eligibility builder that serves titles, never a
/// second copy of the predicate.</para>
///
/// <para>It is <see cref="Ranking.FeedStarvationDiagnostic"/>'s subtractive method given a topic: the
/// diagnostic asks "which condition emptied the feed", this asks "which condition emptied THIS
/// request". The knockouts drop one rule VALUE at a time, as <see cref="FilterCostMeter"/> does,
/// because a chip relaxes one value, not a whole kind.</para>
///
/// <para>Two locks hold here. "Already seen" is a bucket, never a relaxation: the knockouts are
/// computed only over titles with no interaction footprint. And nothing here serves a title — the
/// relaxed rule sets are questions about the catalogue, never permission to show anything.</para>
/// </summary>
public sealed class SupplyReporter(IReelDbContext db, EligibilityQueryBuilder eligibility) : ISupplyReporter
{
    /// <summary>How many unlocked titles a knockout names.</summary>
    public const int KnockoutSampleSize = 5;

    /// <summary>Fewer titles than this clearing the bar is thin supply, where relaxations are always measured.</summary>
    public const int ThinSupply = 24;

    /// <summary>The largest topical population that later counts narrow by id rather than by predicate.</summary>
    public const int IdNarrowingLimit = 2_000;

    /// <summary>Every content kind, suppressed together to count the footprint-free population.</summary>
    private static readonly IReadOnlySet<FilterKind> EveryContentRule = Enum.GetValues<FilterKind>().ToHashSet();

    /// <inheritdoc />
    public Task<SupplyReport> ReportAsync(Guid accountId, HardConstraints hard, CancellationToken ct) =>
        ReportAsync(accountId, hard, CrowdVotes.MinRelaxVotes, ct);

    /// <summary>The report counted with an explicit vote floor (<c>0</c> counts every title).</summary>
    public Task<SupplyReport> ReportAsync(Guid accountId, HardConstraints hard, int minVotes, CancellationToken ct) =>
        ReportAsync(accountId, hard, minVotes, IdNarrowingLimit, ct);

    /// <summary>
    /// The report with an explicit id-narrowing limit, so tests can drive the predicate path (limit
    /// 0) on a small fixture and prove both paths count the same titles.
    /// </summary>
    internal async Task<SupplyReport> ReportAsync(
        Guid accountId, HardConstraints hard, int minVotes, int idNarrowingLimit, CancellationToken ct)
    {
        minVotes = Math.Max(0, minVotes);

        // The topic is resolved ONCE, to ids, and later counts narrow by primary key. Keyword
        // containment cannot use an index (yet), so re-evaluating it inside each of a dozen counts
        // made the founder's ninja report cost ~0.55 s; by id it is ~0.06 s. The vote floor is applied
        // here in memory through the badge's own resolution, and in SQL by the same rule below.
        var topical = await HardConstraintFilter.Apply(db.Titles, db, hard)
            .Select(t => new Title
            {
                Id = t.Id,
                TmdbVoteAverage = t.TmdbVoteAverage,
                TmdbVoteCount = t.TmdbVoteCount,
                TraktRating = t.TraktRating,
                TraktVotes = t.TraktVotes,
            })
            .ToListAsync(ct);
        var ids = topical.Where(t => CrowdVotes.Of(t) >= minVotes).Select(t => t.Id).ToList();

        // A broad request ("any movie") resolves to tens of thousands of ids, and shipping that array
        // into a dozen queries costs more than re-evaluating the predicate; past the limit the counts
        // narrow by the predicate itself. Both readings select the identical set.
        var narrowById = ids.Count <= idNarrowingLimit;
        IQueryable<Title> Population(IQueryable<Title> source) =>
            narrowById
                ? source.Where(t => ids.Contains(t.Id))
                : CrowdVotes.AtLeast(HardConstraintFilter.Apply(source, db, hard), minVotes);

        var matching = ids.Count;

        // Seen is the part of the footprint a reply may name as "you've seen": a watch (even partial)
        // or a rating. The rest of the footprint (saved, not interested, dropped) is derived by
        // subtraction from the builder's own footprint-free count, so a footprint kind added to the
        // builder later lands in SavedOrDismissed without this class learning about it.
        var seen = await Population(db.Titles)
            .Where(t =>
                db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == t.Id)
                || db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == t.Id))
            .CountAsync(ct);
        var footprintFree = await Population(eligibility.EligibleTitles(accountId, suppressed: EveryContentRule))
            .CountAsync(ct);

        var ceiling = await eligibility.MaturityCeilingAsync(accountId, ct);
        var eligibleIds = (await Population(eligibility.EligibleTitles(accountId))
                .Select(t => new { t.Id, t.Certification })
                .ToListAsync(ct))
            .Where(t => EligibilityQueryBuilder.PassesMaturity(new Title { Certification = t.Certification }, ceiling))
            .Select(t => t.Id)
            .ToHashSet();

        var rows = await db.ContentFilters
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);
        var floor = PredictedFloor(rows);

        var (belowFloor, unscored) = await SplitByScoreAsync(accountId, eligibleIds, floor, ct);

        // Relaxations are measured whenever the population is small enough to be cheap, and always
        // when the supply is thin — the case a relaxation offer exists for. A broad request with
        // plenty clearing the bar skips them: each knockout re-counts the whole catalogue there.
        var clearing = eligibleIds.Count - belowFloor - unscored;
        var measureKnockouts = narrowById || clearing < ThinSupply;
        var knockouts = measureKnockouts
            ? await KnockoutsAsync(accountId, rows, eligibleIds, ceiling, Population, ct)
            : [];

        return new SupplyReport(
            MatchingInCatalogue: matching,
            AlreadySeen: seen,
            SavedOrDismissed: matching - footprintFree - seen,
            ExcludedByConditions: footprintFree - eligibleIds.Count,
            Eligible: eligibleIds.Count,
            BelowFloor: belowFloor,
            Unscored: unscored,
            Knockouts: knockouts,
            LowVoteMatches: topical.Count - matching,
            MinVotes: minVotes,
            PredictedFloor: floor,
            KnockoutsMeasured: measureKnockouts);
    }

    /// <summary>
    /// The account's For-you bar, read exactly as Ask Reel reads it: clamped to 0–10, and zero (or a
    /// malformed value) meaning no bar.
    /// </summary>
    private static decimal? PredictedFloor(IReadOnlyList<ContentFilter> rows)
    {
        var raw = rows.FirstOrDefault(f => f.Kind == FilterKind.MinPredictedRating)?.Value;
        return raw is not null && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            && Math.Clamp(value, 0m, 10m) is var clamped && clamped > 0m
                ? clamped
                : null;
    }

    /// <summary>
    /// Splits the eligible set by the ACTIVE model's predictions. A title with no prediction from the
    /// active artifact is unscored even if an older artifact once scored it — the number a card would
    /// show comes from the active model, and a stale one would put a title on the wrong side of the
    /// bar. The bar is compared at badge precision (one decimal, away from zero), as
    /// <see cref="AskReel.AskReelGate.Clears"/> compares it, so "N under your 6.0 bar" is true of the badges.
    /// </summary>
    private async Task<(int BelowFloor, int Unscored)> SplitByScoreAsync(
        Guid accountId, IReadOnlySet<Guid> eligibleIds, decimal? floor, CancellationToken ct)
    {
        if (eligibleIds.Count == 0)
        {
            return (0, 0);
        }

        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (artifactId is not { } activeId)
        {
            return (0, eligibleIds.Count);
        }

        var ids = eligibleIds.ToList();
        var predictions = await db.TitleScores
            .Where(s => s.AccountId == accountId && s.ModelArtifactId == activeId && ids.Contains(s.TitleId))
            .Select(s => new { s.TitleId, s.PredictedRating })
            .ToListAsync(ct);
        var scored = predictions
            .GroupBy(s => s.TitleId)
            .ToDictionary(g => g.Key, g => g.First().PredictedRating);

        var below = floor is { } bar
            ? scored.Values.Count(p => decimal.Round(p, 1, MidpointRounding.AwayFromZero) < bar)
            : 0;
        return (below, eligibleIds.Count - scored.Count);
    }

    private async Task<IReadOnlyList<ConditionKnockout>> KnockoutsAsync(
        Guid accountId,
        IReadOnlyList<ContentFilter> rows,
        IReadOnlySet<Guid> eligibleIds,
        string? ceiling,
        Func<IQueryable<Title>, IQueryable<Title>> population,
        CancellationToken ct)
    {
        var rules = FilterRuleSet.FromRows(rows);
        var eligible = eligibleIds.ToList();
        var knockouts = new List<(int Order, ConditionKnockout Knockout)>();
        var order = 0;
        foreach (var rule in rules.Rules())
        {
            order++;
            // Projected rather than loaded: relaxing the language rule on a broad request can unlock
            // thousands of titles, and only the fields a maturity check and a sample need come back.
            var unlocked = (await population(eligibility.EligibleTitles(accountId, rules.Without(rule)))
                    .Where(t => !eligible.Contains(t.Id))
                    .Select(t => new Title
                    {
                        Id = t.Id,
                        Name = t.Name,
                        Year = t.Year,
                        ReleasedAt = t.ReleasedAt,
                        FirstAiredAt = t.FirstAiredAt,
                        MediaType = t.MediaType,
                        Certification = t.Certification,
                        TmdbVoteAverage = t.TmdbVoteAverage,
                        TmdbVoteCount = t.TmdbVoteCount,
                        TraktRating = t.TraktRating,
                        TraktVotes = t.TraktVotes,
                    })
                    .ToListAsync(ct))
                .Where(t => EligibilityQueryBuilder.PassesMaturity(t, ceiling))
                .ToList();
            if (unlocked.Count == 0)
            {
                continue;
            }

            var sample = unlocked
                .OrderByDescending(CrowdVotes.Of)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ThenBy(t => t.Id)
                .Take(KnockoutSampleSize)
                .Select(t => new TitleRef(t.Id, t.Name, t.Year ?? t.ReleasedAt?.Year ?? t.FirstAiredAt?.Year, t.MediaType))
                .ToList();
            knockouts.Add((order, new ConditionKnockout(
                rule.Kind, rule.Value, RowFor(rows, rule), LabelFor(rule), unlocked.Count, sample)));
        }

        return knockouts
            .OrderByDescending(k => k.Knockout.WouldUnlock)
            .ThenBy(k => k.Order)
            .Select(k => k.Knockout)
            .ToList();
    }

    /// <summary>
    /// The stored row a rule came from, so a chip can open the editor on it. The language allow-list
    /// is one rule over possibly several rows (the first is named) and is null when English-only is
    /// the no-rows default.
    /// </summary>
    private static Guid? RowFor(IReadOnlyList<ContentFilter> rows, FilterRule rule) =>
        rule.Kind switch
        {
            FilterKind.Language or FilterKind.MinCrowdRating or FilterKind.MinYear or FilterKind.MaxYear =>
                rows.FirstOrDefault(f => f.Kind == rule.Kind)?.Id,
            _ => rows.FirstOrDefault(f =>
                f.Kind == rule.Kind
                && string.Equals(f.Value.Trim(), rule.Value, StringComparison.OrdinalIgnoreCase))?.Id,
        };

    /// <summary>The rule in plain words, for a chip or a reply; values are printed as stored.</summary>
    internal static string LabelFor(FilterRule rule) => rule.Kind switch
    {
        FilterKind.ExcludeGenre => $"excluded genre: {rule.Value}",
        FilterKind.IncludeGenre => $"only these genres: {rule.Value}",
        FilterKind.ExcludeKeyword => $"excluded keyword: {rule.Value}",
        FilterKind.Language => rule.Value == FilterRuleSet.DefaultLanguage
            ? "English only"
            : $"languages: {rule.Value}",
        FilterKind.MinCrowdRating => $"crowd rating at least {rule.Value}",
        FilterKind.MinYear => $"released {rule.Value} or later",
        FilterKind.MaxYear => $"released {rule.Value} or earlier",
        _ => $"{rule.Kind}: {rule.Value}",
    };
}
