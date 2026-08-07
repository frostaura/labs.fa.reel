using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Filters;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Answers the one question a blank feed must never leave unanswered: <i>which condition emptied
/// it?</i>
///
/// The founder-locked principle for this codebase is that an empty result is "we did not look hard
/// enough", never a verdict (see the relaxation ladder in <see cref="LiveSearchExpansionService"/>).
/// A feed that CAN still legitimately come out empty — the user really did ask for English
/// non-documentary titles from 2024 rated above 9 — therefore owes them a sentence saying so
/// instead of a blank page.
///
/// The method is subtractive and uses the ONE eligibility builder rather than re-deriving any
/// predicate: count the eligible pool as configured, then re-count it with each content rule
/// dropped in turn. The rule whose removal refills the pool is the one that emptied it. Only ever
/// runs on the already-broken path, so its handful of COUNT queries cost nothing in the normal case.
/// </summary>
public sealed class FeedStarvationDiagnostic(IReelDbContext db, EligibilityQueryBuilder eligibility)
{
    /// <summary>The content rules this can attribute an empty pool to, in the order they are reported.</summary>
    private static readonly (FilterKind Kind, string Label)[] Attributable =
    [
        (FilterKind.MinCrowdRating, "crowd-rating floor"),
        (FilterKind.MinYear, "release-year window"),
        (FilterKind.Language, "language filter"),
        (FilterKind.ExcludeGenre, "excluded genres"),
        (FilterKind.IncludeGenre, "included-genres allowlist"),
        (FilterKind.ExcludeKeyword, "excluded keywords"),
    ];

    /// <summary>
    /// A human sentence explaining an empty candidate pool, or null when the pool is not actually
    /// empty (in which case whatever emptied the FEED happened after retrieval and is the caller's
    /// to explain — typically the predicted-rating floor).
    /// </summary>
    public async Task<string?> ExplainEmptyPoolAsync(Guid accountId, CancellationToken ct)
    {
        var withFilters = await eligibility.EligibleTitles(accountId).CountAsync(ct);
        if (withFilters > 0)
        {
            return null;
        }

        // Nothing survives even with every content rule dropped ⇒ the catalogue itself is exhausted
        // for this account, which is a real (and rare) state worth naming as such rather than
        // blaming on a filter the user could relax to no effect.
        var everyRule = Attributable.Select(a => a.Kind).Append(FilterKind.MaxYear).ToHashSet();
        var unfiltered = await eligibility.EligibleTitles(accountId, suppressed: everyRule).CountAsync(ct);
        if (unfiltered == 0)
        {
            return "There is nothing left in the catalogue you have not already seen, rated or dismissed.";
        }

        var configured = await db.ContentFilters
            .Where(f => f.AccountId == accountId)
            .Select(f => f.Kind)
            .Distinct()
            .ToListAsync(ct);

        var culprits = new List<string>();
        foreach (var (kind, label) in Attributable)
        {
            // The year window is one user-facing control stored as two rows; suppressing either kind
            // drops both (see EligibilityQueryBuilder), so it is attributed once, under MinYear.
            var present = kind == FilterKind.MinYear
                ? configured.Contains(FilterKind.MinYear) || configured.Contains(FilterKind.MaxYear)
                : configured.Contains(kind);
            if (!present)
            {
                continue;
            }

            var without = await eligibility
                .EligibleTitles(accountId, suppressed: new HashSet<FilterKind> { kind })
                .CountAsync(ct);
            if (without > 0)
            {
                culprits.Add(label);
            }
        }

        // No single rule refills the pool ⇒ it took the combination, and naming one would be a lie.
        return culprits.Count switch
        {
            0 => $"Your content filters together leave nothing you have not already seen — {unfiltered} unseen titles are excluded by them.",
            1 => $"Your {culprits[0]} leaves nothing you have not already seen. Relax it to see picks again.",
            _ => $"Your {string.Join(" and ", culprits)} each leave nothing you have not already seen. Relax one to see picks again.",
        };
    }
}
