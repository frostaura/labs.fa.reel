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
/// </summary>
public static class ConceptSynonyms
{
    /// <summary>Stems that name one concept. Lower-case; matched as whole normalised phrases.</summary>
    public static readonly IReadOnlyList<string[]> Groups =
    [
        ["ninja", "shinobi", "kunoichi"],
        ["samurai", "ronin"],
        ["heist", "bank robbery", "robbery"],
        ["courtroom", "court case", "legal drama", "trial", "lawyer"],
        ["survival at sea", "lost at sea", "stranded at sea", "shipwreck", "castaway", "life raft", "adrift"],
        ["martial arts", "kung fu"],
        ["mafia", "gangster", "mob", "organized crime"],
        ["hitman", "assassin", "contract killer"],
        ["con artist", "con man", "grifter", "swindle"],
        ["kaiju", "giant monster"],
        ["spy", "spies", "espionage", "secret agent"],
        ["artificial intelligence", "ai", "robot", "android"],
        ["alien", "extraterrestrial", "alien invasion"],
        ["pandemic", "epidemic", "virus outbreak"],
        ["natural disaster", "disaster", "volcano", "earthquake", "tornado", "tsunami"],
        ["space", "outer space", "astronaut", "space station", "space travel", "spaceship", "space mission"],
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

        if (GenreWords.TryGetValue(phrase, out var whole))
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

            if (!GenreWords.TryGetValue(tokens[i], out var single))
            {
                return null;
            }

            slugs.AddRange(single);
        }

        return slugs.Count == 0 ? null : [.. slugs.Distinct(StringComparer.Ordinal)];
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
