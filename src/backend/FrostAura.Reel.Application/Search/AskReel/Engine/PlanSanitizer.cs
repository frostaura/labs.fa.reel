namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The server's half of the planner's rules (§4.5): what a prompt asks for, this guarantees.
///
/// <list type="bullet">
/// <item><b>References</b> only to handles the conversation actually showed — a handle the model
/// invented resolves to nothing.</item>
/// <item><b>Negations</b> only from the user's words: a negation none of whose forms appears in the
/// message is dropped, and so is one that merely restates a standing condition (those are applied by
/// eligibility and never stored in the state).</item>
/// <item><b>Concepts and genres</b> never a standing condition copied from the planner's input
/// (an excluded genre, an avoided keyword, the allowed language) unless the user's words name it.</item>
/// <item><b>Concepts</b> never a negation cue, never the whole message echoed back, never filler or a
/// pointer ("the second one"), never a person or a title the plan names elsewhere, and never words that
/// only restate a genre the plan already requires ("romantic" beside the romance genre).</item>
/// <item><b>A pointer resolves to the rank the user named</b> ("the second one", "#3", "the last
/// one"): filled from the newest answer when the plan named no handle, moved to that rank when it named
/// one at another whose title the message does not name (R1-30).</item>
/// <item><b>At most one clarifying question per conversation.</b></item>
/// </list>
/// </summary>
public static class PlanSanitizer
{
    public static PlanResult Clean(PlanResult plan, string message, ConversationState state, FilterRuleSet standing)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(standing);
        if (plan.Failure is not null)
        {
            return plan;
        }

        var shown = state.LastPresented.Select(p => p.Handle).ToHashSet();
        var references = Pointed(plan.Act, plan.References.Where(shown.Contains).ToList(), message, state.LastPresented);

        var words = AskReelIntentSanitizer.Words(message);
        var standingValues = standing.ExcludeGenres.Concat(standing.ExcludeKeywords).ToList();
        bool Said(string term) =>
            AskReelIntentSanitizer.Words(term).All(w => words.Any(m => NegationTerms.EqualsAnyForm(m, w) || NegationTerms.EqualsAnyForm(w, m)));
        bool Standing(string term) => standingValues.Any(rule => NegationTerms.EqualsAnyForm(term, rule) || NegationTerms.EqualsAnyForm(rule, term));

        var delta = plan.Delta;
        var negations = delta.Negations.Where(n => Said(n) && !Standing(n)).ToList();
        var named = delta.People.Select(p => p.Name).Concat(delta.ReferenceNames).ToList();

        // A standing condition the model copied into what the user ASKED FOR becomes a requirement
        // the user never made: "samurai films" planned with genre animation (the planner input reads
        // "excludes: animation") counted "samurai animation movies", and "zombie movies" planned with
        // concept "english" (from "language en") replied "no zombie english movies" (HREV4-ASK-02).
        // The user's own words still win: "a comedy tonight" against an excluded comedy is a
        // collision the conflict frame names, not a copy.
        var standingAsked = StandingRequirementValues(standing);
        var genresSaid = GenresSaid(words);
        bool CopiedStanding(string term) =>
            !Said(term)
            && !(Retrieval.Concepts.ConceptSynonyms.GenresNamedBy(Retrieval.Concepts.ConceptResolver.Normalise(term)) is { Count: > 0 } slugs
                && slugs.Any(genresSaid.Contains))
            && standingAsked.Any(rule => NegationTerms.EqualsAnyForm(term, rule) || NegationTerms.EqualsAnyForm(rule, term));
        var genres = delta.Genres.Where(g => !CopiedStanding(g)).ToList();
        var concepts = delta.Concepts
            .Where(c => !CopiedStanding(c))
            .Where(c => !Rejected(c, message))
            .Where(c => !named.Any(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)))
            // A concept the same message also negates is resolved by where it stands in the message:
            // the latest mention decides (the keyless reading's rule, applied to the model's output).
            .Where(c => !negations.Any(n => NegationTerms.EqualsAnyForm(c, n)) || AskedLast(message, c))
            .ToList();
        negations = negations.Where(n => !concepts.Any(c => NegationTerms.EqualsAnyForm(c, n))).ToList();

        // A concept whose words only name genres the plan already requires is that requirement again,
        // and as a concept it also demanded the WORD as a tag: "romantic comedies" planned as romantic +
        // comedy + romance counted 145 titles tagged "romantic", while the same question planned as
        // comedy + romance counted every romantic comedy — two numbers for one question (HREV2-ASK-01).
        concepts = concepts.Where(c => !RestatesGenres(c, genres)).ToList();

        var media = UserMedia(delta.Media, message);

        var clarify = state.ClarifiedOnce ? null : plan.ClarifyQuestion;
        return plan with
        {
            References = references,
            Delta = delta with { Concepts = concepts, Genres = genres, Negations = negations, Media = media },
            ClarifyQuestion = clarify,
            ClarifyOptions = clarify is null ? [] : plan.ClarifyOptions.Take(3).ToList(),
        };
    }

    /// <summary>
    /// The media a plan searches, with the user's own media word outranking a plan that left it
    /// unset OR answered "any" (HREV1-ASK-02, HREV1-RET-04). The planner answered "any" for "samurai
    /// films", "submarine movies" and "Liam Neeson movies" — its input carries the app's media filter,
    /// which reads "any" when none is set — and the turns returned shows and counted them ("Of the 91
    /// samurai titles": 66 films and 25 shows). A model's explicit movie or tv stands; "any" is kept
    /// only where the user's words name both media or neither, ask for one IN ADDITION ("shows too",
    /// "include series") or rule one out without asking for the other. "Samurai, no tv shows" asks
    /// for films. Applied to every plan, a fallback plan included, since no model read those.
    /// </summary>
    public static Retrieval.MediaScope? UserMedia(Retrieval.MediaScope? planned, string? message)
    {
        if (planned is Retrieval.MediaScope.Movies or Retrieval.MediaScope.Shows)
        {
            return planned;
        }

        // "Show me samurai films": the verb "show" names no medium (AskReelCarry.WithoutShowVerb).
        var words = AskReelIntentSanitizer.ScopedWords(AskReelCarry.WithoutShowVerb(message));
        if (words.Any(w => AlsoCues.Contains(w.Word))
            || words.Zip(words.Skip(1)).Any(p => p.First.Word == "as" && p.Second.Word == "well"))
        {
            return planned;
        }

        var asked = Named(words.Where(w => !w.Negated).Select(w => w.Word));
        var ruledOut = Named(words.Where(w => w.Negated).Select(w => w.Word));
        return (asked, ruledOut) switch
        {
            (Retrieval.MediaScope.Movies, not Retrieval.MediaScope.Movies) => Retrieval.MediaScope.Movies,
            (Retrieval.MediaScope.Shows, not Retrieval.MediaScope.Shows) => Retrieval.MediaScope.Shows,
            (null, Retrieval.MediaScope.Shows) => Retrieval.MediaScope.Movies,
            (null, Retrieval.MediaScope.Movies) => Retrieval.MediaScope.Shows,
            _ => planned,
        };

        static Retrieval.MediaScope? Named(IEnumerable<string> said)
        {
            var media = AskReelCarry.MediaIn(said);
            return media.Count == 0 ? null : Retrieval.MediaScopes.From(media);
        }
    }

    /// <summary>
    /// Every value a standing condition holds that a plan could copy into a requirement: the excluded
    /// and only-these genres, the avoided keywords, and — when the account restricts language — each
    /// allowed language as its code and its English name ("en", "english").
    /// </summary>
    internal static IReadOnlyList<string> StandingRequirementValues(FilterRuleSet standing)
    {
        var values = standing.ExcludeGenres.Concat(standing.IncludeGenres).Concat(standing.ExcludeKeywords).ToList();
        if (!standing.AllLanguagesAllowed)
        {
            foreach (var code in standing.EffectiveLanguages)
            {
                values.Add(code);
                if (LanguageName(code) is { } name)
                {
                    values.Add(name);
                }
            }
        }

        return values;
    }

    /// <summary>
    /// The genres the user's own words name, one word or an adjacent pair at a time ("sci-fi",
    /// "science fiction"), so a model writing the genre in its catalogue form is still the user asking.
    /// </summary>
    private static HashSet<string> GenresSaid(IReadOnlyList<string> words)
    {
        var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < words.Count; i++)
        {
            foreach (var phrase in i + 1 < words.Count ? new[] { words[i], $"{words[i]} {words[i + 1]}" } : [words[i]])
            {
                if (Retrieval.Concepts.ConceptSynonyms.GenresNamedBy(phrase) is { } slugs)
                {
                    said.UnionWith(slugs);
                }
            }
        }

        return said;
    }

    /// <summary>The English name of a language code, lower-cased ("en" → "english"), or null when the code names none.</summary>
    private static string? LanguageName(string code)
    {
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(code.Trim());
            var name = culture.EnglishName.Split(' ', '(')[0].Trim().ToLowerInvariant();
            return name.Length == 0 || string.Equals(name, code.Trim(), StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="concept"/>'s words name only genres that <paramref name="genres"/> already holds.</summary>
    internal static bool RestatesGenres(string concept, IReadOnlyList<string> genres) =>
        Retrieval.Concepts.ConceptSynonyms.GenresNamedBy(Retrieval.Concepts.ConceptResolver.Normalise(concept)) is { Count: > 0 } named
        && named.All(g => genres.Any(planned => string.Equals(planned.Trim(), g, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Words that ask for a medium on top of what is already asked for: "shows too", "also series" (and "as well").</summary>
    private static readonly IReadOnlySet<string> AlsoCues = new HashSet<string>(StringComparer.Ordinal)
    {
        "too", "also", "include", "including", "both", "either", "plus",
    };

    /// <summary>
    /// The plan no model wrote, with the server's half of the pointer rules applied: a pointer at a
    /// card resolves to the rank the user named, exactly as for a model's plan (<see cref="Pointed"/>).
    /// </summary>
    public static PlanResult PointFallback(PlanResult plan, string message, ConversationState state)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        var shown = state.LastPresented.Select(p => p.Handle).ToHashSet();
        return plan with { References = Pointed(plan.Act, plan.References.Where(shown.Contains).ToList(), message, state.LastPresented) };
    }

    /// <summary>
    /// The position a search plan's message points at when the plan resolved it to no card — or null
    /// when it points at nothing or resolved. "More like the third one" with no third card the planner
    /// could name was searched anyway: the topic again, with the judge left to read "the third one"
    /// itself, and a why-line saying "as in the third title" about a card nobody showed (HREV1-ASK-03).
    /// A pointer the server cannot resolve is asked back, never searched.
    /// </summary>
    public static int? UnresolvedPointer(PlanResult plan, string message)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Act is TurnAct.Refine or TurnAct.CritiqueItem && plan.References.Count == 0
            ? AskReelPointer.Ordinal(message)
            : null;
    }

    /// <summary>The acts a pointer at a card belongs to; anything else keeps the plan's references as they are.</summary>
    private static readonly IReadOnlySet<TurnAct> PointingActs = new HashSet<TurnAct>
    {
        TurnAct.Refine, TurnAct.CritiqueItem, TurnAct.ItemQuestion,
    };

    /// <summary>
    /// Words that say the pointer is at an EARLIER answer ("the second one you showed me first time
    /// round", "the first one from before"). Which answer that is, is the model's to read from the
    /// handles; with none named, nothing is filled.
    /// </summary>
    private static readonly IReadOnlySet<string> EarlierCues = new HashSet<string>(StringComparer.Ordinal)
    {
        "earlier", "before", "previous", "previously", "originally", "round", "ago", "time",
    };

    /// <summary>
    /// The references with the user's pointer honoured (R1-30). "More like the second one" resolved
    /// once in six live runs: the planner — at effort none — mostly returned no handle at all, and the
    /// turn then searched the conversation's topic instead of near the card. The RANK is in the user's
    /// words, so it is never left to the model:
    /// <list type="bullet">
    /// <item>no handle named: the newest answer's card at that rank, unless the message points at an
    /// earlier answer (that choice is the model's) or the newest answer has no card there;</item>
    /// <item>exactly one handle named at another rank: the card at the named rank in the SAME answer,
    /// when that answer has one — the model's choice of answer stands, its miscount does not;</item>
    /// <item>exactly one handle whose card the message names by title: untouched. "Is Prometheus scarier
    /// than the first film?" points at the first card only as the other side of a comparison; the
    /// model reading Prometheus as the subject is right, and moving it would answer the wrong card;</item>
    /// <item>two or more handles, a message with no pointer, or an act a pointer does not belong to:
    /// untouched.</item>
    /// </list>
    /// </summary>
    internal static List<TurnHandle> Pointed(
        TurnAct act, List<TurnHandle> references, string message, IReadOnlyList<PresentedRef> presented)
    {
        if (!PointingActs.Contains(act) || references.Count > 1 || presented.Count == 0
            || AskReelPointer.Ordinal(message) is not { } position)
        {
            return references;
        }

        if (references.Count == 0 && AskReelIntentSanitizer.Words(message).Any(EarlierCues.Contains))
        {
            return references;
        }

        if (references.Count == 1 && presented.FirstOrDefault(p => p.Handle == references[0]) is { } chosen
            && NamesTitle(message, chosen.Name))
        {
            return references;
        }

        var answer = references.Count == 1 ? references[0].Turn : presented.Max(p => p.Handle.Turn);
        var ranks = presented.Where(p => p.Handle.Turn == answer).Select(p => p.Handle.Rank).ToList();
        if (ranks.Count == 0)
        {
            return references;
        }

        var rank = position == AskReelPointer.Last ? ranks.Max() : position;
        return ranks.Contains(rank) ? [new TurnHandle(answer, rank)] : references;
    }

    /// <summary>
    /// True when <paramref name="message"/> carries every word of <paramref name="title"/>, in order and
    /// adjacent ("is Prometheus scarier…"). Whole words only: "Aliens" does not name "Alien".
    /// </summary>
    internal static bool NamesTitle(string message, string title)
    {
        var name = AskReelIntentSanitizer.Words(title);
        var words = AskReelIntentSanitizer.Words(message);
        if (name.Count == 0 || name.Count > words.Count)
        {
            return false;
        }

        for (var start = 0; start + name.Count <= words.Count; start++)
        {
            var offset = start;
            if (name.Select((w, i) => (w, i)).All(n => string.Equals(words[offset + n.i], n.w, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The words a message of at most this many is allowed to be its own topic in.</summary>
    internal const int ShortTopicWords = 4;

    /// <summary>
    /// A concept is rejected by the v1 keyword rule (a negation cue, filler, a pointer, the whole
    /// message echoed) — except that a SHORT message which is exactly its topic keeps it: "survival at
    /// sea" is the request, not an echo of one. The echo rule was written for a sentence searched as a
    /// keyword ("something brooding and futuristic for tonight"); applied to a three-word topic it
    /// emptied the request, and an empty request judged sixteen random titles to a none-found.
    /// </summary>
    private static bool Rejected(string concept, string message)
    {
        if (!AskReelIntentSanitizer.RejectedKeyword(concept, message))
        {
            return false;
        }

        var words = AskReelIntentSanitizer.Words(concept);
        var whole = AskReelIntentSanitizer.Words(message);
        var isItsOwnTopic = whole.Count <= ShortTopicWords && words.SequenceEqual(whole, StringComparer.Ordinal);
        // Every other reason still applies: judged as if no message were there to echo.
        return !isItsOwnTopic || AskReelIntentSanitizer.RejectedKeyword(concept, string.Empty);
    }

    /// <summary>True when the keyless reading of <paramref name="message"/> ends with <paramref name="concept"/> asked FOR.</summary>
    private static bool AskedLast(string message, string concept)
    {
        var reading = AskReelIntentSanitizer.ReadKeyless(message);
        return reading.Positive.Any(p => NegationTerms.EqualsAnyForm(p, concept) || NegationTerms.EqualsAnyForm(concept, p));
    }
}
