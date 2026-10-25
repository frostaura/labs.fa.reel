using System.Text.RegularExpressions;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// How an exclusion term is matched against a title, single-sourced so the standing (airtight) and
/// the requested (soft) halves of the negation contract can never disagree about what "trips" means.
///
/// <para><b>Plural and singular are the same term.</b> The live "no superheroes please" turn showed
/// eleven superhero titles as ordinary picks: the user typed the plural, the catalogue labels the
/// singular (<c>superhero</c>, 681 titles; <c>superheroes</c>, 0), and a substring test of the plural
/// against the singular label never fires. <see cref="Forms"/> generates both directions.</para>
///
/// <para><b>A term matches as a whole phrase, never as a substring.</b> A substring test let a
/// standing "music" exclusion trip every title tagged "musical", and "war" trip "warm". Matching is
/// case-insensitive at letter/digit boundaries, so "ghost" still matches the label "ghost story" and
/// "horror" still matches "supernatural horror" — the TV-horror coverage the containment rule
/// existed for — while "music" no longer matches "musical".</para>
///
/// <para><b>Generated forms match catalogue LABELS only, never prose.</b> Stripping a final "s" is
/// right for "ghosts" and wrong for "news": the generated "new" tripped every title whose overview
/// says "a new teacher arrives", and a standing exclusion is airtight, so it culled them silently
/// (D6). A label is a curated tag, where the singular of a plural is the same tag; a title's name or
/// overview is free prose, where it is a different word. So <see cref="MatchesPhrase"/> takes a
/// <c>literalOnly</c> switch for prose, and a short list of words that only look plural ("news",
/// "series", "riches") never generates a form at all.</para>
/// </summary>
public static class NegationTerms
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Words ending in "s" whose stripped form is a different word, or no plural at all. They match
    /// as typed and generate nothing.
    /// </summary>
    private static readonly IReadOnlySet<string> NotPlurals = new HashSet<string>(StringComparer.Ordinal)
    {
        "news", "series", "species", "riches", "means", "odds", "thanks", "politics", "physics",
        "mathematics", "economics", "ethics", "athletics", "gymnastics", "aerobics", "acrobatics",
        "electronics", "robotics", "genetics", "linguistics", "logistics", "statistics", "christmas",
        "texas", "paris", "atlas", "lens", "bus", "gas", "chaos",
        "cosmos", "pathos", "ethos", "diabetes", "measles", "mumps", "rabies", "herpes", "billiards",
        "headquarters", "crossroads", "barracks", "gallows", "whereabouts", "glasses", "always",
        "perhaps", "towards", "afterwards", "lots", "plus", "virus", "campus", "circus", "chorus",
        "genius", "status", "bonus", "census", "hippopotamus", "octopus", "cactus", "thesis", "crisis",
        "oasis", "analysis", "basis", "tennis", "iris", "axis", "boss", "process", "princess",
    };

    /// <summary>
    /// The term plus its plausible singular and plural spellings, lower-cased and de-duplicated.
    /// Over-generation is harmless here: a form that is not an English word ("superheroe") matches
    /// nothing at a word boundary, while a missing form costs a real exclusion.
    /// </summary>
    public static IReadOnlyList<string> Forms(string? term)
    {
        var raw = (term ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0)
        {
            return [];
        }

        var forms = new List<string> { raw };
        if (NotPlurals.Contains(raw))
        {
            return forms;
        }

        void Add(string form)
        {
            if (form.Length > 1 && !forms.Contains(form, StringComparer.Ordinal))
            {
                forms.Add(form);
            }
        }

        // Singular readings of a plural.
        if (raw.EndsWith("ies", StringComparison.Ordinal) && raw.Length > 4)
        {
            Add(string.Concat(raw.AsSpan(0, raw.Length - 3), "y"));
        }

        if (raw.EndsWith("es", StringComparison.Ordinal) && raw.Length > 3)
        {
            Add(raw[..^2]);
        }

        if (raw.EndsWith('s') && !raw.EndsWith("ss", StringComparison.Ordinal) && raw.Length > 2)
        {
            Add(raw[..^1]);
        }

        // Plural readings of a singular.
        if (!raw.EndsWith('s'))
        {
            if (raw.EndsWith('y') && raw.Length > 2 && !IsVowel(raw[^2]))
            {
                Add(string.Concat(raw.AsSpan(0, raw.Length - 1), "ies"));
            }

            Add(raw + "s");
            if (raw.EndsWith("ch", StringComparison.Ordinal) || raw.EndsWith("sh", StringComparison.Ordinal)
                || raw.EndsWith('x') || raw.EndsWith('z') || raw.EndsWith('o'))
            {
                Add(raw + "es");
            }
        }

        return forms;
    }

    /// <summary>
    /// True when any form of <paramref name="term"/> appears in <paramref name="text"/> as a whole
    /// phrase — bounded on both sides by something that is not a letter or a digit. With
    /// <paramref name="literalOnly"/>, only the term as typed: the setting for free prose (a name, an
    /// overview), where a generated singular is a different word.
    /// </summary>
    public static bool MatchesPhrase(string? text, string? term, bool literalOnly = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var forms = literalOnly
            ? (term ?? string.Empty).Trim().ToLowerInvariant() is { Length: > 0 } literal ? [literal] : []
            : Forms(term);
        foreach (var form in forms)
        {
            if (form.Length > 2 && Regex.IsMatch(
                    text,
                    $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(form)}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexBudget))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a catalogue genre slug IS the term (any form), compared case-insensitively.</summary>
    public static bool EqualsAnyForm(string? value, string? term)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        return Forms(term).Any(f => string.Equals(f, v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when any label in <paramref name="labels"/> carries the term as a whole phrase.</summary>
    public static bool AnyLabelMatches(IEnumerable<string>? labels, string? term) =>
        labels is not null && labels.Any(label => MatchesPhrase(label, term));

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';
}
