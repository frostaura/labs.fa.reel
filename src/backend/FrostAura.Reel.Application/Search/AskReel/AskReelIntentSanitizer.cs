using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The server's correction of a model-read intent before anything downstream acts on it.
///
/// <para><b>Positive keywords that are not positive.</b> A history-blind recovery path read
/// "no superheroes please" as the positive keyword <c>"no superheroes please"</c> and "more like the
/// second one" as the keywords <c>["second", "one"]</c>; both then fed TMDB keyword discovery and the
/// ranking vector, so the turn chased exactly what the user rejected or a word that names nothing
/// (A-3, A-9, M NEW-3). A keyword is dropped when it carries a negation cue as a word, when it is the
/// whole raw sentence of a multi-word request, or when every word in it is an ordinal or a filler
/// word.</para>
///
/// <para><b>Standing conditions copied into the exclusions.</b> The interpret prompt is shown the
/// account's standing conditions and restates them, unpredictably, as genre or keyword exclusions —
/// "comedy" under <c>excludeKeywords</c> on one turn and <c>excludeGenres</c> on the next (B-12, M
/// NEW-3). Standing conditions are applied by eligibility, airtight, on every channel; a copy of them
/// in the model's output only adds noise to the cache key and to the substring tests downstream, so
/// they are stripped here. Discovery re-adds the account's excluded GENRES itself, deterministically,
/// so the TMDB pull loses nothing.</para>
///
/// <para><b>Standing year bounds copied into the request.</b> The same restatement happens to the
/// year window: on the live "ninja movies" and "samurai films" turns the model wrote the account's
/// standing <c>MinYear=2000</c> into the intent (acceptance D2). Eligibility already applies the
/// standing bound, so the copy filtered nothing — but it became a HARD constraint of the request, so
/// the supply report counted only post-2000 titles ("Of the 43 …" against a catalogue truth of 62)
/// and the year rule, probably the largest relaxation, was never offered. A bound equal to the
/// standing one is dropped unless the user's own words name that year ("from 2000 on", "2000s",
/// "'00s").</para>
/// </summary>
public static class AskReelIntentSanitizer
{
    /// <summary>Words that turn what follows into a rejection, so a keyword carrying one is not a pull signal.</summary>
    private static readonly IReadOnlySet<string> NegationCues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "no", "not", "without", "never", "dont", "don't", "nothing", "none", "avoid", "except", "minus",
        "skip", "hate", "nor", "neither", "isnt", "isn't", "aren't", "arent",
    };

    /// <summary>Ordinals and pointers: words that refer to something on screen rather than name a topic.</summary>
    private static readonly IReadOnlySet<string> PointerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth",
        "last", "previous", "above", "below", "earlier", "latter", "former", "next", "other", "same",
        "more", "less", "movie", "movies", "film", "films", "show", "shows", "series", "title", "titles",
    };

    /// <summary>The intent with non-positive keywords removed and standing conditions stripped from its exclusions.</summary>
    public static SearchIntent Sanitize(SearchIntent intent, FilterRuleSet standing, string rawQuery)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(standing);

        var keywords = intent.Keywords.Where(k => !RejectedKeyword(k, rawQuery)).ToList();
        var standingValues = standing.ExcludeGenres.Concat(standing.ExcludeKeywords).ToList();
        bool IsStanding(string value) =>
            standingValues.Any(rule => NegationTerms.EqualsAnyForm(value, rule));

        return intent with
        {
            Keywords = keywords,
            ExcludeGenres = (intent.ExcludeGenres ?? []).Where(g => !IsStanding(g)).ToList(),
            ExcludeKeywords = (intent.ExcludeKeywords ?? []).Where(k => !IsStanding(k)).ToList(),
            MinYear = CopiedStandingYear(intent.MinYear, standing.MinYear, rawQuery) ? null : intent.MinYear,
            MaxYear = CopiedStandingYear(intent.MaxYear, standing.MaxYear, rawQuery) ? null : intent.MaxYear,
        };
    }

    /// <summary>
    /// Whether a model-supplied year bound is only the account's standing bound restated: equal to it,
    /// and not a year the user's own words name. Internal so the rule is pinned by unit tests.
    /// </summary>
    internal static bool CopiedStandingYear(int? asked, int? standing, string rawQuery)
    {
        if (asked is not { } year || standing != year)
        {
            return false;
        }

        var words = Words(rawQuery);
        var full = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (words.Any(w => w == full || w == full + "s" || w == full + "'s"))
        {
            return false;
        }

        // A decade in the user's shorthand ("90s", "'90s", "00s") names its first year.
        if (year % 10 == 0)
        {
            var decade = (year % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "s";
            if (words.Any(w => w.TrimStart('\'') == decade || w.TrimStart('\'') == decade[..^1] + "'s"))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a model-supplied positive keyword must be discarded. Internal so the rule is pinned by
    /// unit tests over the live failures that produced it.
    /// </summary>
    internal static bool RejectedKeyword(string? keyword, string rawQuery)
    {
        var words = Words(keyword);
        if (words.Count == 0)
        {
            return true;
        }

        if (words.Any(NegationCues.Contains))
        {
            return true;
        }

        var raw = Words(rawQuery);
        if (raw.Count > 2 && words.SequenceEqual(raw, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return words.All(w => PointerWords.Contains(w) || SearchVocabulary.IsFloorWord(w));
    }

    /// <summary>Lower-cased words, split on anything that is not a letter, a digit or an apostrophe.</summary>
    internal static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '\'' || c == '’')
            {
                current.Append(c == '’' ? '\'' : c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>
    /// The positive half of a raw sentence, for the one case where nothing structured survived:
    /// every negation cue, every word a cue governs up to the next comma or conjunction, every word
    /// the user negated, and every filler word removed. "no superheroes please" becomes "", so the
    /// ranking vector is never pulled towards the thing that was ruled out.
    /// </summary>
    public static string PositiveText(string? raw, IEnumerable<string> negatedTerms)
    {
        var negated = negatedTerms.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var kept = new List<string>();
        var suppressing = false;
        foreach (var token in (raw ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var word in Words(token))
            {
                if (NegationCues.Contains(word))
                {
                    suppressing = true;
                }
                else if (word is "but" or "and" or "with")
                {
                    suppressing = false;
                }
                else if (!suppressing && !SearchVocabulary.IsFloorWord(word)
                         && !negated.Any(n => NegationTerms.EqualsAnyForm(word, n)))
                {
                    kept.Add(word);
                }
            }

            // A comma, a full stop or a semicolon closes the clause a negation cue opened.
            if (token.EndsWith(',') || token.EndsWith('.') || token.EndsWith(';'))
            {
                suppressing = false;
            }
        }

        return string.Join(' ', kept);
    }
}
