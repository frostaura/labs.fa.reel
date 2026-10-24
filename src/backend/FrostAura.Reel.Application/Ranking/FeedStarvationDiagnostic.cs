using System.Globalization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
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
    /// <summary>
    /// The content rules this can attribute an empty pool to, in the order they are reported, and
    /// whether the label is plural ("Your excluded genres leave…", "Your language filter leaves…").
    /// </summary>
    private static readonly (FilterKind Kind, string Label, bool Plural)[] Attributable =
    [
        (FilterKind.MinCrowdRating, "crowd-rating floor", false),
        (FilterKind.MinYear, "release-year window", false),
        (FilterKind.Language, "language filter", false),
        (FilterKind.ExcludeGenre, "excluded genres", true),
        (FilterKind.IncludeGenre, "included-genres allowlist", false),
        (FilterKind.ExcludeKeyword, "excluded keywords", true),
    ];

    /// <summary>
    /// A human sentence explaining an empty candidate pool, or null when the pool is not actually
    /// empty (in which case whatever emptied the FEED happened after retrieval and is the caller's
    /// to explain — typically the predicted-rating floor).
    /// </summary>
    public async Task<string?> ExplainEmptyPoolAsync(Guid accountId, CancellationToken ct)
    {
        if (await eligibility.EligibleTitles(accountId).CountAsync(ct) > 0)
        {
            return null;
        }

        // Nothing survives even with every content rule dropped ⇒ the catalogue itself is exhausted
        // for this account, which is a real (and rare) state worth naming as such rather than
        // blaming on a filter the user could relax to no effect.
        var unfiltered = await eligibility.EligibleTitles(accountId, suppressed: EveryRule).CountAsync(ct);
        if (unfiltered == 0)
        {
            return "There is nothing left in the catalogue you have not already seen, rated or dismissed.";
        }

        return await NameTheCulpritsAsync(accountId, source => source, unfiltered, "nothing", ct);
    }

    /// <summary>
    /// <see cref="ExplainEmptyPoolAsync(Guid, CancellationToken)"/> for ONE request's titles: a genre
    /// rail, an Ask Reel concept. The topic narrows every count — the pool as configured, the pool
    /// with every rule dropped, and each drop-one recount — so the sentence names the condition that
    /// emptied THIS request, not the whole feed. Null when the topical pool is not empty.
    ///
    /// <para>Counts use the topic's crowd-vote floor (<see cref="StarvationTopic.MinVotes"/>), so a
    /// 2-vote curiosity never decides whether a request is "empty". Every sentence stays TRUE about
    /// the catalogue and the user, which is why the floor is checked separately from existence: a
    /// topic whose only unseen matches have too few votes says exactly that, never "the catalogue has
    /// none" and never "you have seen them all".</para>
    ///
    /// <para>"Already seen" is never offered as something to relax: when even dropping every content
    /// rule leaves nothing, the sentence says why and names no condition. Only content rules are ever
    /// named as culprits.</para>
    /// </summary>
    public async Task<string?> ExplainEmptyPoolAsync(Guid accountId, StarvationTopic topic, CancellationToken ct)
    {
        IQueryable<Title> Counted(IQueryable<Title> source) => CrowdVotes.AtLeast(topic.Match(source), topic.MinVotes);

        if (await Counted(eligibility.EligibleTitles(accountId)).CountAsync(ct) > 0)
        {
            return null;
        }

        var unfiltered = await Counted(eligibility.EligibleTitles(accountId, suppressed: EveryRule)).CountAsync(ct);
        if (unfiltered > 0)
        {
            return await NameTheCulpritsAsync(accountId, Counted, unfiltered, $"no {topic.Label} titles", ct);
        }

        // Nothing countable survives with every content rule dropped. Three different truths, told
        // apart without the vote floor: the catalogue has no such title at all; it has unseen ones,
        // but none with enough votes to judge; or every one of them is seen, rated or dismissed.
        if (!await topic.Match(db.Titles).AnyAsync(ct))
        {
            return $"The catalogue has no {topic.Label} titles.";
        }

        var unseen = await topic.Match(eligibility.EligibleTitles(accountId, suppressed: EveryRule)).CountAsync(ct);
        if (unseen > 0)
        {
            var votes = string.Create(CultureInfo.InvariantCulture, $"{topic.MinVotes}");
            return unseen == 1
                ? $"The one {topic.Label} title you have not seen has fewer than {votes} crowd votes — too few to judge it."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"All {unseen} {topic.Label} titles you have not seen have fewer than {votes} crowd votes — too few to judge them.");
        }

        return $"Every {topic.Label} title in the catalogue is one you have already seen, rated or dismissed.";
    }

    /// <summary>
    /// The topic for a request's hard constraints, labelled for the reply. <paramref name="label"/>
    /// is a noun modifier that reads before "titles" — "ninja", "time loop", "Adventure" — never a
    /// phrase that already ends in "films" or "titles".
    /// </summary>
    public StarvationTopic TopicFor(HardConstraints hard, string label, int minVotes = CrowdVotes.MinRelaxVotes) =>
        new(source => HardConstraintFilter.Apply(source, db, hard), label, minVotes);

    private static readonly IReadOnlySet<FilterKind> EveryRule =
        Attributable.Select(a => a.Kind).Append(FilterKind.MaxYear).ToHashSet();

    /// <summary>
    /// The pool is empty, and dropping every content rule refills it with <paramref name="unfiltered"/>
    /// titles: name the single rules whose removal alone refills it, or say it took the combination.
    /// </summary>
    private async Task<string> NameTheCulpritsAsync(
        Guid accountId, Func<IQueryable<Title>, IQueryable<Title>> counted, int unfiltered, string subject, CancellationToken ct)
    {
        var configured = await db.ContentFilters
            .Where(f => f.AccountId == accountId)
            .Select(f => f.Kind)
            .Distinct()
            .ToListAsync(ct);

        var culprits = new List<(string Label, bool Plural)>();
        foreach (var (kind, label, plural) in Attributable)
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

            var without = await counted(eligibility
                    .EligibleTitles(accountId, suppressed: new HashSet<FilterKind> { kind }))
                .CountAsync(ct);
            if (without > 0)
            {
                culprits.Add((label, plural));
            }
        }

        // No single rule refills the pool ⇒ it took the combination, and naming one would be a lie.
        var count = string.Create(CultureInfo.InvariantCulture, $"{unfiltered}");
        return culprits switch
        {
            [] => $"Your content filters together leave {subject} you have not already seen — {count} unseen titles are excluded by them.",
            [var (label, plural)] =>
                $"Your {label} {(plural ? "leave" : "leaves")} {subject} you have not already seen. Relax {(plural ? "them" : "it")} to see picks again.",
            _ => $"Your {string.Join(" and ", culprits.Select(c => c.Label))} each leave {subject} you have not already seen. Relax one to see picks again.",
        };
    }
}

/// <summary>
/// One request's titles, for the empty-pool diagnostic: how to match them (without any vote floor),
/// the noun the reply names them by, and the crowd votes a title needs before it counts.
/// </summary>
/// <param name="Match">Narrows a title query to the request.</param>
/// <param name="Label">A noun modifier read before "titles": "ninja", "time loop", "Adventure".</param>
/// <param name="MinVotes">Crowd votes a title needs to count toward the request's supply.</param>
public sealed record StarvationTopic(Func<IQueryable<Title>, IQueryable<Title>> Match, string Label, int MinVotes = CrowdVotes.MinRelaxVotes);
