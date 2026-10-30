namespace FrostAura.Reel.Application.Search.Retrieval.Concepts;

/// <summary>
/// The small curated tables the concept resolver uses before it reaches for embeddings: synonym
/// groups (the words people use for one concept, against the labels TMDB uses for it) and the words
/// that name a genre. Containment alone takes "ninja" from 92 to 100 titles' worth of labels only when
/// "shinobi" and "kunoichi" are searched too; nothing in the vocabulary links them, so a person must.
///
/// <para><b>What belongs here.</b> A group is a set of stems that name the SAME concept, where a
/// title tagged with any of them answers a request for any other — never a looser association ("time
/// loop" is not "time travel"; "spy" is not "thriller"). Every member is matched through
/// <see cref="KeywordVocabulary.Expand"/>, so each stem reaches its labels the usual way ("lawyer"
/// reaches "lawyers" and "defense lawyer").</para>
///
/// <para><b>Keywords only.</b> A synonym is a claim about TMDB's tags, never about titles' names: the
/// name arm reads only the user's own words (<see cref="HardConstraintFilter.NameLabelsOf"/>), so
/// "ronin" finds the masterless-samurai tag and never Ronin (1998). A stem stops at a word end or an
/// inflection (<see cref="KeywordVocabulary.Reaches"/>), so a compound the stem no longer reaches
/// ("spacecraft", "witchcraft") is listed here when it names the same concept.</para>
/// </summary>
public static class ConceptSynonyms
{
    /// <summary>
    /// Stems that name one concept. Lower-case; matched as whole normalised phrases. The FIRST stem is
    /// the concept's own name and the rest are its synonyms (<see cref="IsSynonym"/>): when no words of
    /// the user's are known, the name arm reads only the name.
    /// </summary>
    public static readonly IReadOnlyList<string[]> Groups =
    [
        ["ninja", "shinobi", "kunoichi"],
        ["samurai", "ronin"],
        ["heist", "bank robbery", "robbery"],
        ["courtroom", "court case", "legal drama", "trial", "lawyer"],
        ["survival at sea", "lost at sea", "stranded at sea", "shipwreck", "castaway", "life raft", "adrift"],
        ["martial arts", "kung fu"],
        ["mafia", "gangster", "mob", "mobster", "organized crime"],
        ["hitman", "assassin", "contract killer"],
        ["con artist", "con man", "grifter", "swindle"],
        ["kaiju", "giant monster"],
        ["spy", "spies", "espionage", "secret agent"],
        ["artificial intelligence", "ai", "robot", "android"],
        ["alien", "extraterrestrial", "alien invasion"],
        ["pandemic", "epidemic", "virus outbreak"],
        ["natural disaster", "disaster", "volcano", "earthquake", "tornado", "tsunami"],
        ["space", "outer space", "astronaut", "space station", "space travel", "spaceship", "spacecraft", "space mission"],
        ["witch", "witchcraft"],
        ["superhero", "superpower", "super power"],
        ["ghost", "haunting", "haunted"],
        ["hacker", "hacking"],
        ["kidnapping", "abduction"],
        ["mountaineering", "mountaineer", "mountain climbing"],
        ["noir", "film noir", "neo-noir"],
        ["world war ii", "wwii", "ww2", "second world war"],
        ["boxing", "boxer"],
        ["werewolf", "werewolves"],
        ["vampire", "vampires"],
    ];

    /// <summary>
    /// Words that name a catalogue genre slug, including TMDB's fused TV genre names (program §4.7:
    /// "Sci-Fi &amp; Fantasy", "Action &amp; Adventure", "War &amp; Politics") and TV's "Kids".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> GenreWords =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["action"] = ["action"],
            ["adventure"] = ["adventure"],
            ["action & adventure"] = ["action", "adventure"],
            ["action and adventure"] = ["action", "adventure"],
            ["animation"] = ["animation"],
            ["animated"] = ["animation"],
            ["cartoon"] = ["animation"],
            ["anime"] = ["anime"],
            ["comedy"] = ["comedy"],
            ["comedies"] = ["comedy"],
            // A sitcom is a comedy series: read as a keyword stem, "sitcoms" counted the 312 shows TMDB
            // happens to tag "sitcom" where the comedy shelf the founder's exclusion empties holds 2,302.
            // The conflict detector already read it as comedy (SearchVocabulary); now the count does too.
            ["sitcom"] = ["comedy"],
            ["crime"] = ["crime"],
            ["documentary"] = ["documentary"],
            ["documentaries"] = ["documentary"],
            ["drama"] = ["drama"],
            ["dramas"] = ["drama"],
            ["family"] = ["family"],
            ["kids"] = ["family"],
            ["fantasy"] = ["fantasy"],
            ["history"] = ["history"],
            ["historical"] = ["history"],
            ["horror"] = ["horror"],
            ["music"] = ["music"],
            ["musical"] = ["musical"],
            ["musicals"] = ["musical"],
            ["mystery"] = ["mystery"],
            ["mysteries"] = ["mystery"],
            ["romance"] = ["romance"],
            ["romantic"] = ["romance"],
            ["science fiction"] = ["science-fiction"],
            ["science-fiction"] = ["science-fiction"],
            ["sci-fi"] = ["science-fiction"],
            ["sci fi"] = ["science-fiction"],
            ["scifi"] = ["science-fiction"],
            ["sci-fi & fantasy"] = ["science-fiction", "fantasy"],
            ["sci-fi and fantasy"] = ["science-fiction", "fantasy"],
            ["thriller"] = ["thriller"],
            ["thrillers"] = ["thriller"],
            ["suspense"] = ["thriller"],
            ["war"] = ["war"],
            ["war & politics"] = ["war"],
            ["war and politics"] = ["war"],
            ["western"] = ["western"],
            ["westerns"] = ["western"],
            ["reality"] = ["reality"],
            ["reality tv"] = ["reality"],
            ["soap"] = ["soap"],
            ["soap opera"] = ["soap"],
            ["talk show"] = ["talk-show"],
            ["talk-show"] = ["talk-show"],
            ["news"] = ["news"],
        };

    /// <summary>
    /// The genre slugs <paramref name="phrase"/> names when the WHOLE phrase is genre words — one
    /// <see cref="GenreWords"/> key ("comedy", "sci-fi &amp; fantasy") or several joined by spaces and
    /// "and"/"&amp;"/"or" ("romantic comedies", "comedy romance") — or null when any word is not a genre
    /// word ("romantic comedy film noir" is not). Lower-case, normalised input.
    /// </summary>
    public static IReadOnlyList<string>? GenresNamedBy(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return null;
        }

        if (GenreSlugsOf(phrase) is { } whole)
        {
            return whole;
        }

        var tokens = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var slugs = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "and" or "&" or "or")
            {
                continue;
            }

            // Two-word genre names first ("science fiction", "talk show", "soap opera").
            if (i + 1 < tokens.Length && GenreWords.TryGetValue($"{tokens[i]} {tokens[i + 1]}", out var pair))
            {
                slugs.AddRange(pair);
                i++;
                continue;
            }

            if (GenreSlugsOf(tokens[i]) is not { } single)
            {
                return null;
            }

            slugs.AddRange(single);
        }

        return slugs.Count == 0 ? null : [.. slugs.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The genre slugs <paramref name="word"/> names as one <see cref="GenreWords"/> key, or as the
    /// singular of one ("romances", "horrors", "fantasies") — null when it names none. A plural genre
    /// word read as a keyword stem reached every label containing the genre's name (HREV1-RET-06).
    /// Lower-case, normalised input.
    /// </summary>
    public static IReadOnlyList<string>? GenreSlugsOf(string word)
    {
        if (GenreWords.TryGetValue(word, out var slugs))
        {
            return slugs;
        }

        foreach (var singular in Singulars(word))
        {
            if (GenreWords.TryGetValue(singular, out slugs))
            {
                return slugs;
            }
        }

        return null;
    }

    /// <summary>
    /// The synonym group of <paramref name="stem"/> or of its singular ("heists" → the heist group,
    /// "robberies" → the same group), or just the stem. Retrying only the singular STEM kept "heist"
    /// and lost "bank robbery" and "robbery": 258 founder titles where "heist" finds 522.
    /// </summary>
    public static IEnumerable<string> GroupOfAnyForm(string stem)
    {
        foreach (var form in Singulars(stem).Prepend(stem))
        {
            foreach (var group in Groups)
            {
                if (group.Contains(form, StringComparer.Ordinal))
                {
                    return group;
                }
            }
        }

        return [stem];
    }

    /// <summary>The singular forms an English plural <paramref name="word"/> may have, most specific first.</summary>
    public static IEnumerable<string> Singulars(string word)
    {
        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
        {
            yield return word[..^3] + "y";
        }

        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length > 3)
        {
            yield return word[..^2];
        }

        if (word.EndsWith('s') && word.Length > 2)
        {
            yield return word[..^1];
        }
    }

    /// <summary>
    /// True when <paramref name="stem"/> (normalised), or a singular of it, is a curated synonym — a
    /// member of a group other than its first, naming stem ("ronin", "robbery", "volcano", "ai"). A
    /// plural of the name is the name ("vampires", "spies"), and a stem in no group is no synonym.
    /// </summary>
    public static bool IsSynonym(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var forms = Singulars(stem).Prepend(stem).ToList();
        foreach (var group in Groups)
        {
            if (forms.Contains(group[0], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return Groups.Any(group => forms.Any(form => group.Contains(form, StringComparer.Ordinal)));
    }

    /// <summary>The synonym group containing <paramref name="stem"/>, or just the stem.</summary>
    public static IEnumerable<string> GroupOf(string stem)
    {
        foreach (var group in Groups)
        {
            if (group.Contains(stem, StringComparer.Ordinal))
            {
                return group;
            }
        }

        return [stem];
    }
}
