using System.Globalization;
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
/// whole raw sentence of a multi-word request, or when every word in it is an ordinal, a pronoun, a
/// conversational verb or a filler word (R1-04).</para>
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

    /// <summary>
    /// Words that carry no topic on their own — pronouns, auxiliaries, the verbs a person uses to talk
    /// ABOUT the conversation ("you showed me first time round"), and filler. A keyword made only of
    /// these and <see cref="PointerWords"/> is dropped (R1-04: the ninja ordinal turn read
    /// <c>["you", "showed", "round"]</c> as its topic and replied "9 unseen you movies"). A keyword that
    /// pairs one of them with a real topic word ("time travel", "found footage") is kept.
    /// </summary>
    private static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "i", "me", "my", "mine", "you", "your", "yours", "we", "us", "our", "they", "them", "their", "he",
        "him", "his", "she", "her", "it", "its", "this", "that", "these", "those", "one", "ones", "the", "a",
        "an", "and", "or", "of", "to", "in", "on", "at", "for", "with", "from", "by", "about", "as", "is",
        "are", "was", "were", "be", "been", "being", "am", "do", "does", "did", "have", "has", "had", "can",
        "could", "would", "should", "will", "shall", "may", "might", "must", "showed", "shown", "showing",
        "give", "gave", "given", "get", "got", "see", "saw", "seen", "watch", "watched", "want", "wanted",
        "like", "liked", "find", "found", "recommend", "recommended", "suggest", "suggested", "pick", "picks",
        "picked", "tell", "told", "said", "say", "mean", "meant", "again", "time", "times", "round", "once",
        "twice", "back", "please", "thanks", "thank", "some", "any", "something", "anything", "stuff",
        "thing", "things", "kind", "sort", "type", "lot", "lots", "bit", "just", "really", "very", "so",
        "too", "also", "then", "than", "now", "here", "there", "what", "which", "who", "how", "why", "when",
        "where", "let", "lets", "let's", "ok", "okay", "yes", "yeah", "maybe", "earlier", "before", "after",
    };

    /// <summary>
    /// Words that, after "none of" or "neither of", point at what was already shown — "none of the
    /// above", "none of those", "neither of them" — rather than name a topic.
    /// </summary>
    private static readonly IReadOnlySet<string> ResultReferents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "above", "those", "these", "them", "that", "ones", "previous", "last", "earlier", "results", "suggestions",
        "options", "picks",
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

        return words.All(w => PointerWords.Contains(w) || StopWords.Contains(w) || SearchVocabulary.IsFloorWord(w));
    }

    /// <summary>
    /// True when <paramref name="word"/> (lower-case) names no topic and no title on its own: a negation
    /// cue, a pointer, a media word, a pronoun, a conversational verb or filler.
    /// </summary>
    internal static bool IsFiller(string word) =>
        NegationCues.Contains(word) || PointerWords.Contains(word) || StopWords.Contains(word) || SearchVocabulary.IsFloorWord(word);

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
        var kept = Scoped(raw)
            .Where(w => !w.Negated && !SearchVocabulary.IsFloorWord(w.Word)
                        && !negated.Any(n => NegationTerms.EqualsAnyForm(w.Word, n)))
            .Select(w => w.Word);
        return string.Join(' ', kept);
    }

    /// <summary>
    /// A raw sentence read with no model at all — the keyless install's only reading (IREV1-SYS-04):
    /// the words it asks FOR, and the words it rules out. The search runs on the first and never on
    /// the second; the second is applied to whatever the search finds.
    ///
    /// <para>The keyless path used to search the raw sentence, so "no superheroes please" answered
    /// 17 titles every one of which carried the superhero keyword, and a question about a card on
    /// screen matched 14 unrelated titles on its stop words. Both halves are CONTENT words: not a
    /// pointer, a pronoun, a conversational verb or filler.</para>
    ///
    /// <para><b>The negated half is not floored by the vocabulary</b>, unlike a negation the model
    /// reads (<see cref="SearchVocabulary.IsNegatable"/>). That floor exists because a keyed turn
    /// quotes the word back in an outside-request caption and demotes an answer for it; a keyless
    /// turn has no caption and no answer to demote — a hit it drops is simply not shown. "superhero
    /// movies but no marvel" showed Marvel One-Shot when "marvel" met the floor and failed it.</para>
    /// </summary>
    public static KeylessReading ReadKeyless(string? raw)
    {
        var content = Scoped(raw)
            .Where(w => w.Word.Length > 2
                        && !StopWords.Contains(w.Word) && !PointerWords.Contains(w.Word)
                        && !SearchVocabulary.IsFloorWord(w.Word))
            .ToList();

        // The LATEST mention of a word decides it (IREV3-SYS-04). "those aren't ninja movies. I want
        // ninja movies" negates "ninja" in its first clause and asks for it in its second; reading the
        // negation as winning wherever it stood dropped the only word the user asked for, and the
        // turn answered that the message "only names what to skip". A word ruled out AFTER it was
        // asked for ("ninja movies, no ninja") is still ruled out.
        static bool Same(string a, string b) => NegationTerms.EqualsAnyForm(a, b) || NegationTerms.EqualsAnyForm(b, a);
        bool Superseded(int index) =>
            content.Skip(index + 1).Any(later => later.Negated != content[index].Negated && Same(later.Word, content[index].Word));

        var negated = content
            .Where((w, index) => w.Negated && !Superseded(index))
            .Select(w => w.Word)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var positive = content
            .Where((w, index) => !w.Negated && !Superseded(index))
            .Select(w => w.Word)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new KeylessReading(positive, negated);
    }

    /// <summary>
    /// The topics a raw sentence asks for, read with no model — its positive content words
    /// (<see cref="ReadKeyless"/>), kept as the PHRASES the user wrote them in: "Liam Neeson movies"
    /// is one topic, "liam neeson", and "time travel movies" is "time travel". A plan no model wrote
    /// used to search each word as a separate hard concept, so the dead-provider "time travel movies"
    /// searched "travel" and "Liam Neeson movies" required both "liam" AND "neeson" as keywords
    /// (HREV1-ASK-06). A phrase ends at a negation cue, a conjunction, punctuation, a media word or
    /// any word that is not content; it may start with one of <see cref="PhraseLeads"/>, the stop
    /// words that pair with a topic word into a topic of their own ("found footage").
    /// </summary>
    public static IReadOnlyList<string> KeylessPhrases(string? raw) => ReadKeylessRequest(raw, DateTime.UtcNow.Year).Phrases;

    /// <summary>
    /// A raw sentence read with no model into the request it states (HREV3-ASK-E): the topic PHRASES it
    /// asks for (<see cref="KeylessPhrases"/>), the MOODS it names, and the YEAR window it names — so a
    /// plan no model wrote never makes a mood or a year phrase into a topic every title must carry.
    ///
    /// <para><b>Why.</b> With the planner down, "tense heist movies from the last ten years" was searched
    /// as the hard topics "tense heist" and "ten years": no title carries either, the supply read 0, and
    /// the turn showed three unrelated titles. Read here, it is the topic "heist", the mood "tense" and
    /// the years <c>currentYear − 10</c> to <c>currentYear</c> — exactly what the planner would say.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Years</b>: "the last/past N years" (digits or words, "decade" = ten), "the 80s",
    /// "1990s", "since/after/from 2010", "before/until 2000", "in 2019", "2010 to 2015", or a bare year.
    /// The first window named wins; a negated one ("nothing from the 80s") is not a window.</item>
    /// <item><b>Moods</b>: a word or two-word phrase <see cref="Retrieval.Concepts.MoodLexicon"/> reads
    /// as a mood ("tense", "feel good", "slow burn") or one of the extractor's tones — never a word that
    /// names a genre ("romantic" is the romance genre and stays in the topic).</item>
    /// <item><b>Filler</b>: "good", "great", "best" and their like describe nothing and end a phrase.</item>
    /// </list>
    /// </summary>
    public static KeylessRequest ReadKeylessRequest(string? raw, int currentYear)
    {
        var positive = ReadKeyless(raw).Positive.ToHashSet(StringComparer.Ordinal);
        var stream = ScopedWithBreaks(raw).ToList();
        var consumed = new bool[stream.Count];

        Retrieval.YearRange? years = null;
        var yearWords = new List<string>();
        for (var i = 0; i < stream.Count && years is null; i++)
        {
            if (stream[i].Word is null || stream[i].Negated)
            {
                continue;
            }

            if (YearWindow(stream, i, currentYear) is { } window)
            {
                years = window.Range;
                for (var j = i; j < i + window.Length; j++)
                {
                    consumed[j] = true;
                    yearWords.Add(stream[j].Word!);
                }
            }
        }

        var moods = new List<string>();
        for (var i = 0; i < stream.Count; i++)
        {
            if (consumed[i] || stream[i].Word is not { } word || stream[i].Negated)
            {
                continue;
            }

            if (i + 1 < stream.Count && !consumed[i + 1] && stream[i + 1].Word is { } next && !stream[i + 1].Negated
                && IsMood($"{word} {next}"))
            {
                moods.Add($"{word} {next}");
                consumed[i] = consumed[i + 1] = true;
                i++;
            }
            else if (IsMood(word))
            {
                moods.Add(word);
                consumed[i] = true;
            }
            else if (FillerAdjectives.Contains(word))
            {
                consumed[i] = true;
            }
        }

        var phrases = new List<string>();
        var run = new List<string>();
        string? lead = null;
        for (var index = 0; index < stream.Count; index++)
        {
            var (word, negated) = stream[index];
            if (word is not null && !negated && !consumed[index] && positive.Contains(word))
            {
                if (run.Count == 0 && lead is not null)
                {
                    run.Add(lead);
                }

                lead = null;
                run.Add(word);
                continue;
            }

            Flush();
            lead = word is not null && !negated && !consumed[index] && PhraseLeads.Contains(word) ? word : null;
        }

        Flush();
        return new KeylessRequest(phrases, moods.Distinct(StringComparer.Ordinal).ToList(), years, yearWords);

        void Flush()
        {
            if (run.Count > 0)
            {
                var phrase = string.Join(' ', run);
                if (!phrases.Contains(phrase, StringComparer.Ordinal))
                {
                    phrases.Add(phrase);
                }

                run.Clear();
            }
        }
    }

    /// <summary>Words that praise or intensify without describing: never a topic, never a mood ("good heist movies" is "heist").</summary>
    private static readonly IReadOnlySet<string> FillerAdjectives = new HashSet<string>(StringComparer.Ordinal)
    {
        "good", "great", "best", "decent", "solid", "nice", "top", "awesome", "amazing", "excellent", "favourite", "favorite",
        "genuinely", "truly", "properly", "seriously",
    };

    /// <summary>Number words a relative year window may use ("the last ten years").</summary>
    private static readonly IReadOnlyDictionary<string, int> NumberWords = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["fifteen"] = 15,
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["couple"] = 2,
        ["few"] = 3,
    };

    /// <summary>True when <paramref name="words"/> is a mood the lexicon reads and not a genre word ("romantic").</summary>
    private static bool IsMood(string words) =>
        (Retrieval.Concepts.MoodLexicon.Words.Contains(words) || Retrieval.Concepts.MoodLexicon.KnownTones.Contains(words))
        && Retrieval.Concepts.ConceptSynonyms.GenreSlugsOf(words) is null;

    /// <summary>
    /// The year window that starts at <paramref name="start"/> in the scoped word stream, and how many
    /// entries it spans; null when none starts there. A window never runs across a clause break.
    /// </summary>
    private static (Retrieval.YearRange Range, int Length)? YearWindow(
        IReadOnlyList<(string? Word, bool Negated)> stream, int start, int currentYear)
    {
        string? At(int offset) =>
            start + offset < stream.Count && !stream[start + offset].Negated ? stream[start + offset].Word : null;

        var first = At(0)!;

        // "the last ten years", "past 5 years", "the last decade", "last couple of years".
        if (first is "last" or "past" or "previous")
        {
            var offset = 1;
            var count = 1;
            if (At(offset) is { } n && (NumberWords.TryGetValue(n, out var named) || int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out named)))
            {
                count = named;
                offset++;
                if (At(offset) == "of")
                {
                    offset++;
                }
            }

            var unit = At(offset);
            var span = unit switch
            {
                "year" or "years" => count,
                "decade" or "decades" => count * 10,
                _ => 0,
            };
            if (span is > 0 and <= 200)
            {
                // "last year" alone is the year before this one; a span of years runs up to now.
                var range = unit == "year" && offset == 1
                    ? new Retrieval.YearRange(currentYear - 1, currentYear - 1)
                    : new Retrieval.YearRange(currentYear - span, currentYear);
                return (range, offset + 1);
            }

            return null;
        }

        // "the 80s", "'90s", "1980s", "2000s".
        if (Decade(first, currentYear) is { } decade)
        {
            return (new Retrieval.YearRange(decade, decade + 9), 1);
        }

        // "since 2010", "after 2015", "before 2000", "until 1999", "in 2019", "from 2010 to 2015".
        if (first is "since" or "after" or "from" or "before" or "until" or "till" or "pre" or "in"
            && Year(At(1), currentYear) is { } bound)
        {
            if (first == "from" && At(2) is "to" or "until" or "till" && Year(At(3), currentYear) is { } to && to >= bound)
            {
                return (new Retrieval.YearRange(bound, to), 4);
            }

            var range = first switch
            {
                "since" or "from" => new Retrieval.YearRange(bound, null),
                "after" => new Retrieval.YearRange(bound + 1, null),
                "before" or "pre" => new Retrieval.YearRange(null, bound - 1),
                "until" or "till" => new Retrieval.YearRange(null, bound),
                _ => new Retrieval.YearRange(bound, bound),
            };
            return (range, 2);
        }

        // A bare year, or "2010 to 2015".
        if (Year(first, currentYear) is { } year)
        {
            if (At(1) is "to" or "until" or "till" && Year(At(2), currentYear) is { } to && to >= year)
            {
                return (new Retrieval.YearRange(year, to), 3);
            }

            return (new Retrieval.YearRange(year, year), 1);
        }

        return null;
    }

    /// <summary>A four-digit year from 1900 to next year, or null.</summary>
    private static int? Year(string? word, int currentYear) =>
        word is { Length: 4 } && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
        && year >= 1900 && year <= currentYear + 1
            ? year
            : null;

    /// <summary>The first year of a decade written "80s", "'80s", "80's", "1980s" or "2000s", or null.</summary>
    private static int? Decade(string word, int currentYear)
    {
        var digits = word.TrimStart('\'');
        if (digits.EndsWith("'s", StringComparison.Ordinal))
        {
            digits = digits[..^2];
        }
        else if (digits.EndsWith('s'))
        {
            digits = digits[..^1];
        }
        else
        {
            return null;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value % 10 != 0)
        {
            return null;
        }

        return digits.Length switch
        {
            4 when value >= 1900 && value <= currentYear => value,
            2 => 2000 + value <= currentYear ? 2000 + value : 1900 + value,
            _ => null,
        };
    }

    /// <summary>
    /// Stop words that start a topic when a content word follows: "time travel", "found footage".
    /// Only these — a pronoun or a request verb before a topic ("I want heist") never joins it.
    /// </summary>
    private static readonly IReadOnlySet<string> PhraseLeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "time", "found",
    };

    /// <summary>
    /// Every word of a raw sentence that is not a negation cue or a conjunction, with whether a cue
    /// governs it — media words, pointers and filler included, unlike <see cref="ReadKeyless"/>.
    /// What a deterministic rule reads when it needs to know whether a word was asked for or ruled
    /// out ("samurai, no tv shows").
    /// </summary>
    public static IReadOnlyList<(string Word, bool Negated)> ScopedWords(string? raw) => [.. Scoped(raw)];

    /// <summary>
    /// Every content word of a raw sentence with whether a negation cue governs it. A cue opens the
    /// scope; "but", "and", "with", a comma, a full stop or a semicolon closes it. The cues and those
    /// three conjunctions are never content themselves.
    ///
    /// <para>"None of the above (are ninja movies)" rejects the RESULTS, not the topic (IREV3-SYS-04):
    /// the acceptance follow-up "none of the above are ninja movies. I want ninja movies" asks for
    /// ninja films twice. So "none of" / "neither of" followed by a pointer at what was shown opens no
    /// scope, and the words that point are not content.</para>
    /// </summary>
    private static IEnumerable<(string Word, bool Negated)> Scoped(string? raw) =>
        ScopedWithBreaks(raw).Where(w => w.Word is not null).Select(w => (w.Word!, w.Negated));

    /// <summary>
    /// <see cref="Scoped"/>, with a null word wherever the sentence breaks — a cue, a conjunction, a
    /// rejection of the results, or punctuation that closes a clause — so phrases can be read off it.
    /// </summary>
    private static IEnumerable<(string? Word, bool Negated)> ScopedWithBreaks(string? raw)
    {
        var words = new List<(string Word, bool Closes)>();
        foreach (var token in (raw ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // A comma, a full stop or a semicolon closes the clause a negation cue opened.
            var closes = token.EndsWith(',') || token.EndsWith('.') || token.EndsWith(';');
            var tokenWords = Words(token);
            if (tokenWords.Count == 0)
            {
                if (closes && words.Count > 0)
                {
                    words[^1] = (words[^1].Word, true);
                }

                continue;
            }

            for (var i = 0; i < tokenWords.Count; i++)
            {
                words.Add((tokenWords[i], closes && i == tokenWords.Count - 1));
            }
        }

        var suppressing = false;
        for (var i = 0; i < words.Count; i++)
        {
            var closes = words[i].Closes;
            var rejected = RejectedResults(words, i);
            if (rejected > 0)
            {
                i += rejected - 1;
                closes = words[i].Closes;
                suppressing = false;
                yield return (null, false);
            }
            else if (NegationCues.Contains(words[i].Word))
            {
                suppressing = true;
                yield return (null, false);
            }
            else if (words[i].Word is "but" or "and" or "with")
            {
                suppressing = false;
                yield return (null, false);
            }
            else
            {
                yield return (words[i].Word, suppressing);
            }

            if (closes)
            {
                suppressing = false;
                yield return (null, false);
            }
        }
    }

    /// <summary>
    /// How many words starting at <paramref name="start"/> reject what was shown — "none of the
    /// above", "neither of those", "none of them were" — or 0 when the words there are not one. Only
    /// filler, pointers and the referring words are taken, up to the end of the clause, so the topic
    /// that follows ("are ninja movies") stays content.
    /// </summary>
    private static int RejectedResults(IReadOnlyList<(string Word, bool Closes)> words, int start)
    {
        if (words[start].Word is not ("none" or "neither")
            || words[start].Closes || start + 1 >= words.Count || words[start + 1].Word != "of")
        {
            return 0;
        }

        var referent = false;
        var end = start + 2;
        for (var j = start + 1; j < words.Count; j++)
        {
            var (word, closes) = words[j];
            if (j > start + 1 && !StopWords.Contains(word) && !PointerWords.Contains(word) && !ResultReferents.Contains(word))
            {
                break;
            }

            referent |= ResultReferents.Contains(word);
            end = j + 1;
            if (closes)
            {
                break;
            }
        }

        return referent ? end - start : 0;
    }
}

/// <summary>A sentence read without a model into the request it states (<see cref="AskReelIntentSanitizer.ReadKeylessRequest"/>).</summary>
/// <param name="Phrases">The topics it asks for, as the phrases it wrote them in.</param>
/// <param name="Moods">The moods it names: soft preferences, never topics.</param>
/// <param name="Years">The year window it names, or null.</param>
/// <param name="YearWords">The words that named the window ("last", "ten", "years").</param>
public sealed record KeylessRequest(
    IReadOnlyList<string> Phrases, IReadOnlyList<string> Moods, Retrieval.YearRange? Years, IReadOnlyList<string> YearWords);

/// <summary>A sentence read without a model: the words it asks for, and the words it rules out.</summary>
/// <param name="Positive">Content words no negation cue governs — what a keyword search may run on.</param>
/// <param name="Negated">Content words the user ruled out — applied to the hits, never searched.</param>
public sealed record KeylessReading(IReadOnlyList<string> Positive, IReadOnlyList<string> Negated);
