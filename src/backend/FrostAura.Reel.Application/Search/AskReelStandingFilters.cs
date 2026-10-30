using System.Globalization;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The account's standing content rules, as the conversation needs to see them.
///
/// <para>Ask Reel used to resolve an intent in total ignorance of the exclusions the server would
/// then apply. So the model could cheerfully promise a genre the account has excluded,
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
public static partial class AskReelStandingFilters
{
    /// <summary>How many values of one kind are named before the summary stops listing them.</summary>
    private const int MaxListed = 8;

    /// <summary>
    /// The one-line summary handed to the interpret prompt, or <c>null</c> when the account has no
    /// standing rules at all. Short and mechanical on purpose: it rides in a prompt, so every extra
    /// clause is tokens on every turn for the rest of the account's life.
    ///
    /// <para><paramref name="maturityCeiling"/> is the account's maturity ceiling, which lives on the
    /// account's settings rather than in <see cref="FilterRuleSet"/> but is a condition all the same:
    /// the conditions editor shows it beside the rest, and every discovery surface applies it. Left
    /// out, an account whose only condition is a ceiling was told it had no conditions at all
    /// (R1-12).</para>
    /// </summary>
    public static string? Describe(FilterRuleSet rules, string? maturityCeiling = null)
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

        if (!string.IsNullOrWhiteSpace(maturityCeiling))
        {
            parts.Add($"max maturity {maturityCeiling.Trim()}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// The genres this turn asked for that the account has excluded — the collision the
    /// user is owed an explanation for. Compared case-insensitively because the model writes the
    /// intent and the editor writes the rows, and only one of them is normalised.
    ///
    /// <para><b><paramref name="requested"/> is what makes this reachable at all.</b> The other half
    /// of this feature DESCRIBES the standing rules to the interpret model, and a model that obeys
    /// that instruction stops asking for the excluded genre: "I want a comedy tonight" comes back as
    /// <c>genres: []</c>, <c>excludeGenres: ["comedy"]</c>. Intersecting the account's exclusions
    /// with <see cref="SearchIntent.Genres"/> alone therefore found the empty set on every turn, for
    /// every account — the better the prompt behaved, the less this rung could fire, and three
    /// independent finders reported the notice never once appearing. <paramref name="requested"/> is
    /// the interpreter's free, deterministic reading of the user's own WORDS, which no prompt can
    /// launder; the union of the two is what the turn actually asked for.</para>
    /// </summary>
    public static IReadOnlyList<string> CollidingGenres(
        FilterRuleSet rules, SearchIntent intent, SearchIntent? requested = null) =>
        Colliding(rules.ExcludeGenres, AskedFor(rawQuery: null, intent, requested));

    /// <summary>
    /// The words this turn asked for that the account AVOIDS — the same collision as
    /// <see cref="CollidingGenres"/>, one vocabulary down.
    ///
    /// <para>It exists because the excluded thing is not always a genre. "anime" and "musical" are
    /// not TMDB genres, so an account that never wants them holds them as avoid-keywords — and a
    /// finder asking for "an anime series" against exactly that rule got no notice, no excluded
    /// list and no server sentence, only whatever the model chose to say. A rule the pipeline will
    /// enforce is owed an explanation regardless of which list the editor happened to write it
    /// to.</para>
    ///
    /// <para>Matched on the WHOLE token, never as a substring: these are single words a user picked,
    /// and a substring rule would collide "war" with "warm".</para>
    /// </summary>
    public static IReadOnlyList<string> CollidingKeywords(
        FilterRuleSet rules, SearchIntent intent, SearchIntent? requested = null) =>
        Colliding(rules.ExcludeKeywords, AskedFor(rawQuery: null, intent, requested));

    /// <summary>
    /// True when the turn's release-year window cannot survive the account's standing one — "the
    /// 90s" against a "nothing before 2000" rule. Only a window that is wholly outside the standing
    /// one counts: a partial overlap still has titles to offer and needs no explanation.
    ///
    /// <para><paramref name="requested"/> for the reason given on <see cref="CollidingGenres"/>: the
    /// year half launders identically. Told "min year 2000", the model answers a 1990s request with
    /// <c>minYear: 2000</c>, and the window it hands back is inside the standing one by
    /// construction. EITHER window colliding is a collision — the model's reading is not evidence
    /// that the user did not ask.</para>
    ///
    /// <para><paramref name="rawQuery"/> closes A-12's second half: "older stuff is fine" against a
    /// "2000 onwards" rule names no year at all, so neither reading carries a window to compare — and
    /// the reply then promised "older shows from 2000 onward". An explicit request for OLDER titles
    /// ("older", "classic", "vintage", "oldies", "retro") collides with any standing lower bound,
    /// deterministically, because the bound is exactly what it asks past.</para>
    /// </summary>
    public static bool YearWindowCollides(
        FilterRuleSet rules, SearchIntent intent, SearchIntent? requested = null, string? rawQuery = null) =>
        WindowCollides(rules, intent)
        || (requested is not null && WindowCollides(rules, requested))
        || (rules.MinYear is not null && AsksForOlder(rawQuery));

    /// <summary>Words that ask for titles older than whatever is on offer.</summary>
    private static readonly IReadOnlySet<string> OlderCues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "older", "oldies", "classic", "classics", "vintage", "retro", "oldschool",
    };

    /// <summary>
    /// Words that, right before "classic", make it a judgement of quality rather than of age: "a
    /// modern classic" asks for nothing older (D11).
    /// </summary>
    private static readonly IReadOnlySet<string> NotOlderQualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "modern", "new", "newer", "recent", "future", "instant", "contemporary",
    };

    /// <summary>Whether the raw sentence asks for older titles, by a whole word.</summary>
    internal static bool AsksForOlder(string? rawQuery)
    {
        var tokens = Tokens(rawQuery).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!OlderCues.Contains(tokens[i]))
            {
                continue;
            }

            var isClassic = tokens[i].StartsWith("classic", StringComparison.OrdinalIgnoreCase);
            if (isClassic && i > 0 && NotOlderQualifiers.Contains(tokens[i - 1]))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool WindowCollides(FilterRuleSet rules, SearchIntent intent) =>
        (rules.MinYear is { } standingMin && intent.MaxYear is { } askedMax && askedMax < standingMin)
        || (rules.MaxYear is { } standingMax && intent.MinYear is { } askedMin && askedMin > standingMax);

    /// <summary>
    /// Everything this turn asked for, as one vocabulary: the model's genres and keywords, the
    /// heuristic's genres and keywords, and the raw sentence's own word tokens — in that order, so
    /// the intent's casing wins where two readings carry the same term and the same turn always
    /// names its collisions in the same order.
    ///
    /// <para><b>One vocabulary, deliberately, not one per list.</b> The collision used to intersect
    /// the account's excluded GENRES with the turn's genres and the account's excluded KEYWORDS with
    /// the turn's keywords. That assumes the conditions editor and the interpreter agree about which
    /// vocabulary a word belongs to, and they do not: the founder holds "anime" as an excluded
    /// genre, "an anime series" is read by the heuristic as a keyword, and a turn asking for exactly
    /// the thing the account excludes therefore emitted no conflict frame at all —
    /// while the model's own prose in the same stream said the filters excluded anime. Which list
    /// the editor happened to write a rule to is an implementation detail of the editor; the user
    /// asked for a word, and the pipeline is about to enforce a rule against it.</para>
    ///
    /// <para><paramref name="rawQuery"/> is the last resort and it is worth its cost: the heuristic
    /// drops stop words and the model drops whatever the prompt told it to, so a word both of them
    /// discarded can still be the one the rule names. Tokens are whole words — never substrings,
    /// which would collide "war" with "warm" — and sub-3-character tokens are dropped as punctuation
    /// debris, exactly as the heuristic's own tokenizer does.</para>
    ///
    /// <para><b>Every reading is then expanded through <see cref="SearchVocabulary"/>'s genre
    /// aliases</b>, which is what closes F4-07. "a 90s romantic comedy" against standing exclusions
    /// on BOTH romance and comedy named only comedy: nothing in any of the three readings ever
    /// spelled the word "romance", because the user had written "romantic" — while the prose in the
    /// same bubble said "your standing filters exclude romance and comedy". The alias table is a
    /// fact about English, not about which reading found the word, so it belongs to all three.</para>
    /// </summary>
    public static IReadOnlyList<string> AskedFor(string? rawQuery, SearchIntent intent, SearchIntent? requested) =>
        SearchVocabulary.WithGenreSynonyms(
            intent.Genres
                .Concat(intent.Keywords)
                .Concat(requested?.Genres ?? [])
                .Concat(requested?.Keywords ?? [])
                .Concat(Tokens(rawQuery))
                // A genre no single word names ("chick flicks", "love story"): HREV5-ASK-02.
                .Concat(SearchVocabulary.GenresOfPhrases(AskReel.AskReelIntentSanitizer.Words(rawQuery)))
                .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// What the user's own WORDS ask for, read exactly as the conflict detector reads them
    /// (<see cref="AskedFor"/> with no intent): the raw tokens and the genres their synonyms name
    /// ("funny" is comedy, "romantic" is romance). The sanitiser keeps a standing value the planner
    /// returned only when these words ask for it (HREV5-ASK-02), so the plan and the conflict frame can
    /// never disagree about whether the user asked for an excluded genre.
    /// </summary>
    public static IReadOnlyList<string> AskedForInWords(string? rawQuery) => AskedFor(rawQuery, NoIntent, null);

    private static readonly SearchIntent NoIntent = new([], [], [], string.Empty, null, null);

    /// <summary>
    /// The intersection of one standing exclusion list with <see cref="AskedFor"/>. Whole-token,
    /// case-insensitive; a multi-word rule ("true crime") is matched as a phrase against the joined
    /// vocabulary instead, because no single token can carry it.
    ///
    /// <para><b>Which list a term is reported under is decided by which list the RULE lives in</b>,
    /// never by which bucket the interpreter happened to put the word in. An account holding
    /// <c>anime</c> as an <c>ExcludeGenre</c> gets it back under <c>excludedGenres</c> even though
    /// the heuristic reads "an anime series" as a keyword, and an account holding <c>ghosts</c> as
    /// an <c>ExcludeKeyword</c> gets it back under <c>excludedKeywords</c>. The frame describes the
    /// account's own rules, and a client that keys the two lists to different affordances — a
    /// genre chip and a term chip lead to different rows in the conditions editor — must be told
    /// the truth about where to send the user.</para>
    ///
    /// <para>The result is ORDINAL-SORTED. Row order out of Postgres is not guaranteed, so a turn
    /// that collides with two rules would otherwise name them in one order on the live frame and
    /// another on the next resume, for no reason anybody could see.</para>
    /// </summary>
    public static IReadOnlyList<string> Colliding(IReadOnlyList<string> excluded, IReadOnlyList<string> askedFor)
    {
        if (excluded.Count == 0 || askedFor.Count == 0)
        {
            return [];
        }

        var single = askedFor.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var phrase = string.Join(' ', askedFor);
        return excluded
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Select(rule => rule.Trim())
            .Where(rule => single.Contains(rule)
                || (rule.Contains(' ') && phrase.Contains(rule, StringComparison.OrdinalIgnoreCase)))
            // The spelling the user or the intent used, where there is one — the rule row's casing
            // is the editor's, and the sentence around it is the turn's.
            .Select(rule => askedFor.FirstOrDefault(
                v => string.Equals(v, rule, StringComparison.OrdinalIgnoreCase)) ?? rule)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The languages <paramref name="asked"/> names that the account's language rule does not allow,
    /// by name ("Korean"), in the order asked. Empty when every language is allowed.
    /// </summary>
    public static IReadOnlyList<string> CollidingLanguages(FilterRuleSet rules, IReadOnlyList<Retrieval.LanguageTerm> asked)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(asked);
        if (asked.Count == 0 || rules.AllLanguagesAllowed)
        {
            return [];
        }

        var allowed = rules.EffectiveLanguages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return asked
            .Where(l => !allowed.Contains(l.Code))
            .Select(l => Retrieval.Concepts.LanguageWords.NameOf(l.Code))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The crowd rating the raw sentence asks to stay under ("rated under 5", "a rating below 4.5",
    /// "scored less than 6"), or null when it asks for none.
    /// </summary>
    internal static decimal? AskedCrowdCeiling(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return null;
        }

        var match = CrowdCeiling().Match(rawQuery);
        return match.Success && decimal.TryParse(match.Groups["n"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// True when the turn asks for titles rated under a number the account's crowd floor does not
    /// reach below — "so-bad-it's-good movies rated under 5" against "crowd rating at least 6.0" can only
    /// ever come back empty, and the user is owed the reason.
    /// </summary>
    public static bool CrowdFloorCollides(FilterRuleSet rules, string? rawQuery) =>
        rules.MinCrowdRating is { } floor && AskedCrowdCeiling(rawQuery) is { } ceiling && ceiling <= floor;

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:rated|rating|ratings|scored|scores?|scoring)\s+(?:of\s+)?(?:under|below|less\s+than|lower\s+than|<)\s*(?<n>\d+(?:\.\d+)?)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CrowdCeiling();

    /// <summary>
    /// The collision message with the language and crowd-floor halves (<see cref="CollidingLanguages"/>,
    /// <see cref="CrowdFloorCollides"/>) added to <see cref="Message(IReadOnlyList{string}, IReadOnlyList{string}, bool, FilterRuleSet)"/>'s.
    /// </summary>
    public static string Message(
        IReadOnlyList<string> excludedGenres, IReadOnlyList<string> excludedKeywords, bool yearCollides,
        FilterRuleSet rules, IReadOnlyList<string> languages, bool crowdCollides)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(languages);
        var parts = new List<string>(3);
        if (languages.Count > 0)
        {
            var allowed = rules.EffectiveLanguages.Select(Retrieval.Concepts.LanguageWords.NameOf).ToList();
            parts.Add($"Your conditions only allow titles in {Humanised(allowed)}, so nothing in {HumanisedOr([.. languages])} can appear.");
        }

        if (crowdCollides && rules.MinCrowdRating is { } floor)
        {
            parts.Add(FormattableString.Invariant($"Your conditions only allow titles with a crowd rating of at least {floor:0.0}, so nothing rated under that can appear."));
        }

        if (excludedGenres.Count + excludedKeywords.Count > 0 || yearCollides || parts.Count == 0)
        {
            var rest = Message(excludedGenres, excludedKeywords, yearCollides, rules);
            return parts.Count == 0 ? rest : $"{string.Join(" ", parts)} {rest}";
        }

        parts.Add("You can change that under Conditions.");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The collision message when nothing the turn SAID names a condition, but the conditions hold out
    /// every unseen title that matches it — named by the relaxations that would bring some back.
    /// </summary>
    public static string HeldOutMessage(IReadOnlyList<string> ruleLabels)
    {
        ArgumentNullException.ThrowIfNull(ruleLabels);
        return $"Your conditions hold out every unseen title that matches ({Humanised(ruleLabels)}). You can change that under Conditions.";
    }

    /// <summary>Whole word tokens of a raw query, lowercased, punctuation-split, debris dropped.</summary>
    private static IEnumerable<string> Tokens(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            yield break;
        }

        foreach (var token in rawQuery.ToLowerInvariant().Split(
                     rawQuery.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 2)
            {
                yield return token;
            }
        }
    }

    /// <summary>
    /// What the user is told, in prose. Numbers about the POOL travel as raw JSON beside this so the
    /// client can render them how it likes; the only numbers here are the account's own year bounds,
    /// which are part of the sentence — and are invariant, like every number this backend formats.
    /// </summary>
    public static string Message(
        IReadOnlyList<string> excludedGenres, IReadOnlyList<string> excludedKeywords, bool yearCollides,
        FilterRuleSet rules)
    {
        var parts = new List<string>(3);
        var excluded = excludedGenres.Concat(excludedKeywords).ToList();
        if (excluded.Count > 0)
        {
            // Never "permanently" (R1-29): a condition is editable behind the settings PIN, and Ask
            // Reel itself offers to relax one — the sentence below says where.
            parts.Add($"Your conditions exclude {Humanised(excluded)}, so nothing you asked for there can appear.");
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
    /// The reply, qualified by what the account's conditions are about to do to it — appended
    /// BEFORE the reply is emitted or persisted, so what the user reads and what the transcript
    /// stores are the same sentence.
    ///
    /// <para><b>Why the reply needs qualifying at all.</b> It is written by the interpret model
    /// before a single candidate exists, and nothing afterwards reconciles it — so a turn opened
    /// "here are gritty 90s action picks" and then rendered seven post-2000 titles under a notice
    /// saying the 90s were unavailable. Moving the reply after discovery would fix the honesty and
    /// cost the turn its only early frame; stating the consequence up front costs nothing and is
    /// true at the moment it is written, because an exclusion is airtight.</para>
    ///
    /// <para>Deterministic on purpose: it is built from the same collision data as the
    /// <c>conflict</c> frame, so the two can never disagree, and it does not depend on the model
    /// choosing to be candid — which it demonstrably did not, writing "I'll keep it genuinely funny"
    /// on one turn and naming the exclusion on the next.</para>
    /// </summary>
    public static string Qualify(
        string reply, IReadOnlyList<string> excludedGenres, IReadOnlyList<string> excludedKeywords,
        bool yearCollides, FilterRuleSet rules)
    {
        var excluded = excludedGenres.Concat(excludedKeywords).ToList();
        var clauses = new List<string>(2);
        if (excluded.Count > 0)
        {
            clauses.Add($"no {HumanisedOr(excluded)}");
        }

        // A reply that already names the bound ("older shows from 2000 onward") is not told it again:
        // the live conflict turn read "…from 2000 onward — under your conditions that means titles
        // from 2000 on."
        if (yearCollides && !NamesYearBound(reply, rules))
        {
            clauses.Add(rules.MinYear is { } min && rules.MaxYear is { } max
                ? FormattableString.Invariant($"titles between {min} and {max}")
                : rules.MinYear is { } onlyMin
                    ? FormattableString.Invariant($"titles from {onlyMin} on")
                    : FormattableString.Invariant($"titles up to {rules.MaxYear}"));
        }

        if (clauses.Count == 0)
        {
            return reply;
        }

        // The clause opens with an em dash, so a reply that already ended its sentence would read
        // "…picks. — under your conditions". One trailing full stop is absorbed; a question mark or
        // an exclamation is left alone, because removing either changes what the reply said.
        var stem = reply.TrimEnd();
        if (stem.EndsWith('.'))
        {
            stem = stem[..^1].TrimEnd();
        }

        return $"{stem} — under your conditions that means {string.Join(" and ", clauses)}.";
    }

    /// <summary>Whether the reply names every standing year bound, as a whole number.</summary>
    internal static bool NamesYearBound(string reply, FilterRuleSet rules)
    {
        var tokens = Tokens(reply).ToHashSet(StringComparer.Ordinal);
        bool Named(int? year) =>
            year is not { } y || tokens.Contains(y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (rules.MinYear is not null || rules.MaxYear is not null) && Named(rules.MinYear) && Named(rules.MaxYear);
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
    private static string Humanised(IReadOnlyList<string> values) => Joined(values, "and");

    /// <summary>
    /// The same list under a "no" — "no horror or comedy". <c>and</c> would read as a conjunction of
    /// two separate refusals rather than one list of them ("no horror and comedy" is ambiguous about
    /// whether a horror comedy is the only thing ruled out).
    /// </summary>
    private static string HumanisedOr(IReadOnlyList<string> values) => Joined(values, "or");

    private static string Joined(IReadOnlyList<string> values, string conjunction) =>
        values.Count switch
        {
            1 => values[0],
            2 => $"{values[0]} {conjunction} {values[1]}",
            _ => $"{string.Join(", ", values.Take(values.Count - 1))} {conjunction} {values[^1]}",
        };
}
