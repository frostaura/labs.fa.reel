using System.Globalization;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The account's standing content rules, as the conversation needs to see them.
///
/// <para>Ask Reel used to resolve an intent in total ignorance of the exclusions the server would
/// then apply. So the model could cheerfully promise a genre the account has permanently excluded,
/// the pipeline would honour the exclusion, and the turn came back empty with a reply that had just
/// said yes — a collision invisible to the model and unexplainable to the user. Two halves fix it:
/// the rules are DESCRIBED to the model before it answers, and any collision that survives is
/// STATED to the user before the results appear.</para>
///
/// <para><b>What this deliberately does not do is override anything.</b> An exclusion stays
/// airtight; naming the collision is worth most of the value of a per-turn override and costs no
/// policy change. Whether a user may say "ignore my filters just this once" is the founder's call,
/// not an engineer's — the PIN on the conditions editor exists for a reason.</para>
/// </summary>
public static class AskReelStandingFilters
{
    /// <summary>How many values of one kind are named before the summary stops listing them.</summary>
    private const int MaxListed = 8;

    /// <summary>
    /// The one-line summary handed to the interpret prompt, or <c>null</c> when the account has no
    /// standing rules at all. Short and mechanical on purpose: it rides in a prompt, so every extra
    /// clause is tokens on every turn for the rest of the account's life.
    /// </summary>
    public static string? Describe(FilterRuleSet rules)
    {
        var parts = new List<string>(6);
        if (rules.ExcludeGenres.Count > 0)
        {
            parts.Add($"excludes: {Listed(rules.ExcludeGenres)}");
        }

        if (rules.IncludeGenres.Count > 0)
        {
            parts.Add($"only: {Listed(rules.IncludeGenres)}");
        }

        if (rules.ExcludeKeywords.Count > 0)
        {
            parts.Add($"avoids: {Listed(rules.ExcludeKeywords)}");
        }

        if (rules.MinYear is { } minYear)
        {
            parts.Add(FormattableString.Invariant($"min year {minYear}"));
        }

        if (rules.MaxYear is { } maxYear)
        {
            parts.Add(FormattableString.Invariant($"max year {maxYear}"));
        }

        // Only a language the account CHOSE. With no rows at all the eligibility rule falls back to
        // English-only, but that is the product's baseline rather than this account's preference —
        // stating it would put a clause in every prompt, on every turn, for every account, forever.
        if (rules.Languages.Count > 0 && !rules.AllLanguagesAllowed)
        {
            parts.Add($"language {string.Join('/', rules.EffectiveLanguages)}");
        }

        if (rules.MinCrowdRating is { } crowd)
        {
            parts.Add(FormattableString.Invariant($"min crowd {crowd:0.0}"));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// The genres this turn asked for that the account has permanently excluded — the collision the
    /// user is owed an explanation for. Compared case-insensitively because the model writes the
    /// intent and the editor writes the rows, and only one of them is normalised.
    /// </summary>
    public static IReadOnlyList<string> CollidingGenres(FilterRuleSet rules, SearchIntent intent) =>
        rules.ExcludeGenres.Count == 0
            ? []
            : intent.Genres
                .Where(g => rules.ExcludeGenres.Contains(g, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>
    /// True when the turn's release-year window cannot survive the account's standing one — "the
    /// 90s" against a "nothing before 2000" rule. Only a window that is wholly outside the standing
    /// one counts: a partial overlap still has titles to offer and needs no explanation.
    /// </summary>
    public static bool YearWindowCollides(FilterRuleSet rules, SearchIntent intent) =>
        (rules.MinYear is { } standingMin && intent.MaxYear is { } askedMax && askedMax < standingMin)
        || (rules.MaxYear is { } standingMax && intent.MinYear is { } askedMin && askedMin > standingMax);

    /// <summary>
    /// What the user is told, in prose. Numbers about the POOL travel as raw JSON beside this so the
    /// client can render them how it likes; the only numbers here are the account's own year bounds,
    /// which are part of the sentence — and are invariant, like every number this backend formats.
    /// </summary>
    public static string Message(IReadOnlyList<string> excludedGenres, bool yearCollides, FilterRuleSet rules)
    {
        var parts = new List<string>(3);
        if (excludedGenres.Count > 0)
        {
            parts.Add($"Your conditions permanently exclude {Humanised(excludedGenres)}, so nothing you asked for there can appear.");
        }

        if (yearCollides)
        {
            parts.Add(rules.MinYear is { } min && rules.MaxYear is { } max
                ? FormattableString.Invariant($"Your conditions only allow titles from {min} to {max}, which is outside the years you asked for.")
                : rules.MinYear is { } onlyMin
                    ? FormattableString.Invariant($"Your conditions only allow titles from {onlyMin} onwards, which is outside the years you asked for.")
                    : FormattableString.Invariant($"Your conditions only allow titles up to {rules.MaxYear}, which is outside the years you asked for."));
        }

        parts.Add("You can change that under Conditions.");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Sorted, because this string reaches a prompt: row order out of Postgres is not guaranteed, and
    /// an unsorted list would make the same account's same rules produce two different prompts — and
    /// therefore two different answers — for no reason anybody could see.
    /// </summary>
    private static string Listed(IReadOnlyList<string> values)
    {
        var sorted = values.OrderBy(v => v, StringComparer.Ordinal).ToList();
        return sorted.Count <= MaxListed
            ? string.Join(", ", sorted)
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{string.Join(", ", sorted.Take(MaxListed))} +{sorted.Count - MaxListed} more");
    }

    /// <summary>"horror", "horror and comedy", "horror, comedy and musicals" — a list a person reads.</summary>
    private static string Humanised(IReadOnlyList<string> values) =>
        values.Count switch
        {
            1 => values[0],
            2 => $"{values[0]} and {values[1]}",
            _ => $"{string.Join(", ", values.Take(values.Count - 1))} and {values[^1]}",
        };
}
