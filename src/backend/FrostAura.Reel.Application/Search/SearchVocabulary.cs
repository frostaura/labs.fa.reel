using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The words Ask Reel is allowed to act on, in ONE place: the genre aliases a person actually types
/// for a catalogue slug, and the closed vocabulary of concepts a negation may name.
///
/// <para><b>Why one table rather than two.</b> The alias list used to live privately inside
/// <c>OpenRouterSearchInterpreter</c>, so the collision rung — which intersects the account's
/// standing rules with everything the turn asked for — could not see it. "a 90s romantic comedy"
/// against standing exclusions on BOTH romance and comedy therefore named only comedy: the
/// heuristic mapped "romantic" to nothing at all, the model filed it under keywords, and the frame
/// intersected a vocabulary that never contained the word "romance". The prose in the very same
/// bubble said "your standing filters exclude romance and comedy". One table, read by the scanner
/// and by the collision, is what stops the two halves of one sentence disagreeing.</para>
///
/// <para><b>Why the negatable vocabulary is CLOSED.</b> The negation scanner accepted any token
/// that was not machinery, which made "nothing <b>set</b> in space" a soft exclusion on "set" and
/// "not … at <b>all</b>" one on "all" — both printed back to the user as "which you asked to skip",
/// and both matching a large share of every overview in the catalogue, so real answers were demoted
/// out of the picks by a word the user never rejected. A negation is therefore only a negation when
/// its object RESOLVES: to a genre this catalogue knows, or to a concept named here. Being closed
/// is the point — an unrecognised token costs one missed demotion, which is recoverable, where a
/// false one costs the product's honesty about what the user said, which is not.</para>
/// </summary>
public static class SearchVocabulary
{
    /// <summary>
    /// Words a person types for a catalogue genre, mapped to the slugs
    /// (<see cref="TmdbGenres.SlugToIds"/>) the pipeline can actually act on. One term may resolve
    /// to more than one slug — "romcom" is two genres, and reading it as one was half of F4-07.
    ///
    /// <para>Keys are the token as the tokenizer leaves it: it splits on hyphens and apostrophes,
    /// so "sci-fi" arrives as "sci" + "fi" and "rom-com" is normalised to "romance comedy" by
    /// <see cref="NormaliseCompounds"/> before it ever gets here.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Romance — the F4-07 gap. "romantic" resolved to nothing, so a standing ExcludeGenre on
            // romance could never collide with the word the user actually typed.
            ["romantic"] = ["romance"],
            ["romantics"] = ["romance"],
            ["romances"] = ["romance"],
            ["romcom"] = ["romance", "comedy"],
            ["romcoms"] = ["romance", "comedy"],

            // Science fiction. The slug is hyphenated and the tokenizer splits hyphens, so the
            // fragments carry the mapping.
            ["scifi"] = ["science-fiction"],
            ["sci"] = ["science-fiction"],
            ["fi"] = ["science-fiction"],
            ["scifis"] = ["science-fiction"],

            ["horrors"] = ["horror"],
            ["scary"] = ["horror"],
            ["spooky"] = ["horror"],
            ["frightening"] = ["horror"],

            ["funny"] = ["comedy"],
            // A-12: "like John Wick but FUNNIER" against a standing comedy exclusion emitted no
            // conflict frame while the reply itself said comedy was excluded. A comparative or
            // superlative of "funny" asks for comedy as plainly as the adjective does.
            ["funnier"] = ["comedy"],
            ["funniest"] = ["comedy"],
            ["hilarious"] = ["comedy"],
            ["comedies"] = ["comedy"],
            ["comedic"] = ["comedy"],
            ["humorous"] = ["comedy"],

            ["thrillers"] = ["thriller"],
            ["dramas"] = ["drama"],
            ["westerns"] = ["western"],
            ["mysteries"] = ["mystery"],
            ["fantasies"] = ["fantasy"],

            ["docs"] = ["documentary"],
            ["doc"] = ["documentary"],
            ["docos"] = ["documentary"],
            ["documentaries"] = ["documentary"],
            // HREV5-ASK-02: the planner reads these as the genre — "sitcoms" is comedy on TV,
            // "docuseries" a documentary — and the sanitiser, reading the user's words through this
            // table, then stripped the genre as a standing condition the model had copied. The
            // conflict frame never fired and the request opened up to The Wire and Stranger Things.
            ["sitcom"] = ["comedy"],
            ["sitcoms"] = ["comedy"],
            ["docuseries"] = ["documentary"],
            ["docu"] = ["documentary"],
            ["docus"] = ["documentary"],

            ["animated"] = ["animation"],
            ["animations"] = ["animation"],
        };

    /// <summary>
    /// Two-word phrases that name a genre outright though neither word does on its own: "love" is a
    /// floor word and "chick" names nothing, but "love story" and "chick flick" are romance. Read by
    /// the conflict detector (<see cref="AskReelStandingFilters.AskedFor"/>), the plan sanitiser and
    /// the model-less reader alike, so the three can never disagree about whether the user asked for
    /// a genre (HREV5-ASK-02: "chick flicks" planned as romance was stripped as a copied condition).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> GenrePhrases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["love story"] = ["romance"],
            ["love stories"] = ["romance"],
            ["chick flick"] = ["romance"],
            ["chick flicks"] = ["romance"],
            ["rom com"] = ["romance", "comedy"],
            ["rom coms"] = ["romance", "comedy"],
        };

    /// <summary>
    /// The genre slugs <paramref name="words"/> (lower-cased, in order) name by a two-word
    /// <see cref="GenrePhrases"/> entry, in the order they appear, each once.
    /// </summary>
    public static IReadOnlyList<string> GenresOfPhrases(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var genres = new List<string>();
        for (var i = 0; i + 1 < words.Count; i++)
        {
            if (GenrePhrases.TryGetValue($"{words[i]} {words[i + 1]}", out var slugs))
            {
                genres.AddRange(slugs.Where(g => !genres.Contains(g, StringComparer.Ordinal)));
            }
        }

        return genres;
    }

    /// <summary>
    /// The concepts a turn's own words may rule out. Deliberately closed, deliberately concrete:
    /// every entry is a thing a person says they do not want to watch, and every one of them is
    /// specific enough to print in "one that matches ‘…’" without lying.
    ///
    /// <para>Stored singular where the scanner's crude singulariser produces a singular, and in
    /// both readings where it does not — "zombies" is reduced to "zomby" AND "zombie", and only the
    /// second is a word.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> NegatableKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "addiction", "alien", "anime", "apocalypse", "cannibal", "cartoon", "clown", "cult",
            "demon", "dinosaur", "dragon", "drug", "dubbed", "dystopia", "exorcism", "footage",
            "ghost", "gore", "gory", "haunted", "haunting", "hero", "incest", "jumpscare", "killer",
            "monster", "murder",
            "musical", "mutant", "nudity", "paranormal", "pirate", "possession", "prequel",
            "rape", "reboot", "remake", "robot", "satanic", "scare", "sequel", "shark", "slasher",
            "snake", "spider", "subtitle", "subtitled", "suicide", "superhero", "supernatural",
            "torture", "vampire", "violence", "werewolf", "witch", "zombie",
        };

    /// <summary>
    /// Tokens that steer a sentence rather than carry it. A negation scope running over one of
    /// these must never turn it into an exclusion — this is the floor R3-01 asked for, and "set",
    /// "all" and "at" are the three tokens that were measured escaping it.
    /// </summary>
    private static readonly IReadOnlySet<string> FloorWords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "all", "any", "anything", "anyone", "set", "sets", "setting", "kind", "kinds", "sort",
            "sorts", "type", "types", "thing", "things", "stuff", "bit", "lot", "lots", "one",
            "ones", "part", "parts", "way", "ways", "time", "times", "mind", "minds", "really",
            "quite", "very", "too", "much", "many", "few", "such", "that", "this", "those",
            "these", "there", "here", "when", "where", "which", "what", "who", "whom", "while",
            "with", "without", "into", "onto", "from", "about", "around", "over", "under",
            "again", "still", "just", "only", "even", "also", "please", "thanks", "and", "but",
            "for", "the", "watch", "watching", "seen", "see", "like", "liked", "love", "want",
            "something", "someone", "anywhere", "everything", "nothing", "none", "never",
        };

    /// <summary>
    /// The catalogue slugs a token names, or an empty list when it names none. Alias first, then
    /// the slug table on the raw token, then on its crude singular — the same three readings the
    /// scanner used to do inline, so moving them here changed no answer.
    /// </summary>
    public static IReadOnlyList<string> ResolveGenres(string? token)
    {
        var raw = (token ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return [];
        }

        if (Aliases.TryGetValue(raw, out var aliased))
        {
            return aliased;
        }

        if (TmdbGenres.SlugToIds.ContainsKey(raw.ToLowerInvariant()))
        {
            return [raw.ToLowerInvariant()];
        }

        var singular = Singularise(raw.ToLowerInvariant());
        if (!string.Equals(singular, raw, StringComparison.OrdinalIgnoreCase))
        {
            if (Aliases.TryGetValue(singular, out var aliasedSingular))
            {
                return aliasedSingular;
            }

            if (TmdbGenres.SlugToIds.ContainsKey(singular))
            {
                return [singular];
            }
        }

        return [];
    }

    /// <summary>
    /// True when a token names a concept a turn's own words are allowed to rule out. Every singular
    /// and plural reading is tried (<see cref="AskReel.NegationTerms.Forms"/>), because only one of
    /// them is ever a word: "zombies" reduces to "zomby" under the "-y" rule and to "zombie" under
    /// the plain one, and "superheroes" reduces to "superheroe" under the "-s" rule — which is how
    /// "no superheroes please" stopped being a negation at all and showed eleven superhero titles
    /// as picks (A-9).
    /// </summary>
    public static bool IsNegatableKeyword(string? token)
    {
        var raw = (token ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length <= 2)
        {
            return false;
        }

        return AskReel.NegationTerms.Forms(raw).Any(NegatableKeywords.Contains);
    }

    /// <summary>
    /// True when a token may become a negation at all — a genre this catalogue knows, or a concept
    /// the vocabulary names. Everything else is dropped rather than demoted, because the caption
    /// quotes the token verbatim and a word too generic to caption is too generic to negate.
    /// </summary>
    public static bool IsNegatable(string? token) =>
        !IsFloorWord(token) && (ResolveGenres(token).Count > 0 || IsNegatableKeyword(token));

    /// <summary>True for the function words a negation scope must run straight over.</summary>
    public static bool IsFloorWord(string? token) =>
        token is { Length: > 0 } && FloorWords.Contains(token.Trim());

    /// <summary>
    /// Every term a turn asked for, plus the catalogue slugs those terms resolve to. This is what
    /// makes the collision rung see "romance" in a sentence whose only spelling of it was
    /// "romantic". Input order is preserved and the expansions follow, so a turn always names its
    /// collisions in the same order.
    /// </summary>
    public static IReadOnlyList<string> WithGenreSynonyms(IEnumerable<string> terms)
    {
        var result = new List<string>();
        var expansions = new List<string>();
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            result.Add(term);
            expansions.AddRange(ResolveGenres(term));
        }

        result.AddRange(expansions);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Compounds the tokenizer would otherwise destroy, rewritten before it runs. "rom-com" and
    /// "rom com" split into two fragments that mean nothing on their own, and reading "romcom" as a
    /// single genre loses half of what the user asked for.
    /// </summary>
    public static string NormaliseCompounds(string lower) =>
        System.Text.RegularExpressions.Regex.Replace(
            lower, @"\brom[\s-]?coms?\b", "romance comedy",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The scanner's crude singularise, shared so the vocabulary is checked against exactly the
    /// token the scanner will store. "-ies" is genuinely ambiguous without a lexicon ("mysteries"
    /// → "mystery", "zombies" → "zombie"), so the caller checks both readings and the wrong one
    /// simply fails to resolve.
    /// </summary>
    private static string Singularise(string raw) =>
        raw.EndsWith("ies", StringComparison.Ordinal) && raw.Length > 4
            ? string.Concat(raw.AsSpan(0, raw.Length - 3), "y")
            : raw.TrimEnd('s');
}
