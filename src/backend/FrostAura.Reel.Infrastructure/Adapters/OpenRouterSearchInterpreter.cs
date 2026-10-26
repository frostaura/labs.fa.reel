using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Turns a natural-language Ask Reel query into a TMDB discovery spec via OpenRouter chat
/// completions (REEL_SEARCH_MODEL, default <c>openai/gpt-6-luna</c>). Mirrors the attribute
/// extractor's transport + stub contract:
///  • <b>live</b> — OPENROUTER_API_KEY present: one structured-JSON request.
///  • <b>stub</b> — no key / TestHelpers:ModelStubMode: a deterministic heuristic that splits the
///    query into known genre slugs + residual keywords, so keyless dev and tests still work and a
///    live model is a pure quality upgrade, never a correctness dependency.
/// </summary>
public class OpenRouterSearchInterpreter(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterSearchInterpreter> logger,
    // Optional by design: the typed HttpClient is transient and is constructed inside a scope in
    // every real code path, but the adapter must still be `new`-able in tests without a scope.
    IAccountContext? accountContext = null,
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null)
    : ISearchQueryInterpreter
{
    /// <summary>The shared transport (purpose <c>search-interpret</c>, which now carries a completion ceiling).</summary>
    private readonly OpenRouterChatClient _chat = new(
        httpClient, usageRecorder, configuration, logger, accountContext, purposes, capabilities, health, gate);

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    private bool StubMode => ApiKey is null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    // Honest availability: a real key, or an explicit test stub — the same gate the agent and the
    // attribute extractor apply. This used to answer an unconditional "true, the stub always
    // answers", which is the very bug the agent's own comment says it fixed: a keyless install
    // reported an LLM capability it did not have. Interpretation still degrades to the free
    // deterministic Heuristic when this is false; what changes is that callers can now tell.
    public bool IsAvailable => ApiKey is not null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public string ModelId => StubMode
        ? "stub/deterministic-v1"
        : _chat.Options(ApiUsagePurpose.SearchInterpret).Model;

    public Task<SearchIntent> InterpretAsync(
        string query, CancellationToken ct = default, string? standingFilters = null) =>
        InterpretAsync(query, [], ct, standingFilters);

    public async Task<SearchIntent> InterpretAsync(
        string query, IReadOnlyList<ChatTurn> history, CancellationToken ct = default, string? standingFilters = null)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0 || StubMode)
        {
            return Heuristic(trimmed);
        }

        // The shared client records every attempt on the ledger (a failed one included), bounds the
        // completion — this call used to have no max_tokens at all — and never throws for a provider
        // failure, so every failure lands on the free heuristic below.
        var completion = await _chat.CompleteAsync(
            new LlmChatRequest(
                ApiUsagePurpose.SearchInterpret,
                [LlmMessage.System(SystemPrompt), LlmMessage.User(BuildUserMessage(trimmed, standingFilters, history))]),
            ct);
        if (!completion.Succeeded)
        {
            logger.LogWarning(
                "Query interpretation failed ({Failure}) for {Query}; falling back to heuristic.", completion.Failure, trimmed);
            return HeuristicIntent(trimmed);
        }

        return Parse(completion.Content!, trimmed);
    }

    private static SearchIntent Parse(string json, string fallbackText)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return SearchIntentParser.FromJson(doc.RootElement, fallbackText, HeuristicIntent(fallbackText));
        }
        catch (JsonException)
        {
            return HeuristicIntent(fallbackText);
        }
    }

    /// <summary>
    /// The port's free, deterministic reading of a query. Public so a collaborator that only needs
    /// a FALLBACK intent — <c>OpenRouterSearchAgent</c> — can have one without paying for a second
    /// chat completion it will usually discard.
    /// </summary>
    public SearchIntent Heuristic(string query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        return trimmed.Length == 0
            ? new SearchIntent([], [], [], string.Empty, null, null)
            : HeuristicIntent(trimmed);
    }

    /// <summary>
    /// Deterministic fallback: split the query, fold known genre words (and a few aliases) into
    /// genres, the rest into keywords. "shark horrors" → genres:[horror], keywords:[shark]. No
    /// network; same input → same output.
    /// </summary>
    private static SearchIntent HeuristicIntent(string query)
    {
        // Compounds the tokenizer would destroy are rewritten first: it splits on hyphens, so
        // "rom-com" arrives as two fragments that mean nothing on their own.
        var lower = SearchVocabulary.NormaliseCompounds(query.ToLowerInvariant());
        var (minYear, maxYear) = ParseYearRange(lower);

        // The release window is read from the full phrase above; the phrase that carried it is then
        // removed before tokenizing, because a date is a BOUND, never a concept. Left in, "heist
        // movies from the 2010s" became keywords [heist, 2010]: the supply pulled every title tagged
        // "2010" and a degraded reply named its topic "heist or 2010" (IREV3-SYS-05).
        var tokens = Tokenize(StripYearPhrases(lower));

        var genres = new List<string>();
        var keywords = new List<string>();
        var media = new List<MediaType>();
        var excludeGenres = new List<string>();
        var excludeKeywords = new List<string>();

        // Negation scope. "less horror", "no ghosts", "I don't want ghosts" — without this every
        // rejected term became a positive pull signal and discovery fetched exactly what the user
        // had just ruled out. A negator opens the scope; it closes at a positive pivot ("but",
        // "I want", "more") or at a clause boundary that is not a continuation of the rejection
        // list (see the boundary rule below).
        //
        // This used to be a fixed three-token budget, which silently leaked the tail of any longer
        // rejection: "I don't want horror, gore, zombies or vampires" spent the budget on the first
        // three and then read "vampires" as a POSITIVE keyword — actively searching for the fourth
        // thing the user had just refused. The budget existed only because the tokenizer destroyed
        // commas, which made a rejection list and a new clause indistinguishable. Boundaries now
        // survive tokenization instead, so the scope can be governed by the sentence rather than
        // by a word count.
        var negating = false;
        var negatedContentSeen = false;
        var atBoundary = false;

        foreach (var raw in tokens)
        {
            if (raw == BoundaryToken)
            {
                atBoundary = true;
                continue;
            }

            if (atBoundary)
            {
                atBoundary = false;

                // A comma continues a rejection list only while the next word is itself content:
                // "no horror, gore, zombies" keeps negating, whereas "not scary, something funny"
                // has moved on to a fresh request and must not negate "funny". Filler and stop
                // words are the tell — a list of rejections does not need them.
                if (negating && !IsContentToken(raw))
                {
                    negating = false;
                    negatedContentSeen = false;
                }
            }

            if (Negators.Contains(raw))
            {
                negating = true;
                negatedContentSeen = false;
                continue;
            }

            // Media words are only ever a media constraint, and "tv" is short enough to be caught
            // by the noise filter below — so resolve them before anything else.
            if (raw is "movie" or "movies" or "film" or "films")
            {
                if (!media.Contains(MediaType.Movie)) media.Add(MediaType.Movie);
                continue;
            }

            if (raw is "tv" or "show" or "shows" or "series")
            {
                if (!media.Contains(MediaType.Show)) media.Add(MediaType.Show);
                continue;
            }

            // Filler between a negator and the thing being rejected ("don't WANT ghosts") is
            // transparent: it neither closes the scope nor spends it. Without this, "want" read as
            // a pivot and "I don't want ghosts" came out as a request FOR ghosts.
            // Only transparent BEFORE the negation lands on something. Once it has, the same word
            // is a pivot back to what the user does want — "don't want ghosts I WANT serial
            // killers" negates the ghosts and then stops.
            if (negating && !negatedContentSeen && NegationFiller.Contains(raw))
            {
                continue;
            }

            // A pivot only closes a negation that has actually landed on something — otherwise the
            // "want" in "don't want" would close the scope it belongs to.
            if (PositivePivots.Contains(raw))
            {
                if (!negating || negatedContentSeen)
                {
                    negating = false;
                    negatedContentSeen = false;
                }

                continue;
            }

            // Sub-3-character tokens are punctuation debris ("t" from "don't", a bare "i"), never
            // content — and must not consume the negation budget.
            if (raw.Length <= 2)
            {
                continue;
            }

            var negated = negating;
            if (negated)
            {
                negatedContentSeen = true;
            }
            // Crude singularise: "horrors" → "horror", "mysteries" → "mystery", "comedies" → "comedy".
            //
            // "-ies" is genuinely ambiguous without a lexicon: "mysteries" comes from "mystery"
            // (→ "y") but "zombies" comes from "zombie" (→ just drop the "s"). Applying the "y" rule
            // blindly produced "zomby", which then matched nothing — so "no zombies" was parsed as a
            // rejection and still failed to exclude a single zombie film. Both readings are kept and
            // the wrong one is harmless: keywords are OR-ed pull/exclusion signals, so a spare
            // near-miss token costs nothing while a missed exclusion is exactly the bug being fixed.
            var isIesPlural = raw.EndsWith("ies", StringComparison.Ordinal) && raw.Length > 4;
            var token = isIesPlural ? string.Concat(raw.AsSpan(0, raw.Length - 3), "y") : raw.TrimEnd('s');
            var altToken = isIesPlural ? raw[..^1] : null;

            // Genre words resolve through the shared table — alias, then slug, then the crude
            // singular — so the scanner and the collision rung agree about what "romantic" means.
            var slugs = SearchVocabulary.ResolveGenres(raw);
            if (slugs.Count == 0 && !string.Equals(token, raw, StringComparison.Ordinal))
            {
                slugs = SearchVocabulary.ResolveGenres(token);
            }

            if (slugs.Count > 0)
            {
                foreach (var slug in slugs)
                {
                    Add(negated ? excludeGenres : genres, slug);
                }
            }
            else if (negated)
            {
                // THE FLOOR. A negation is a negation only when its object resolves to something
                // this catalogue can name. The scanner used to accept any token that was not
                // machinery, so "nothing SET in space" and "not … at ALL" became soft exclusions on
                // "set" and "all" — captioned to the user as words they had asked to skip, and
                // matching a large share of every overview, so real answers were demoted out of the
                // picks by a rejection nobody made. An unresolvable token is dropped outright: it
                // does NOT fall through to the positive branch either, because searching for the
                // thing inside a rejection is the original defect negation exists to prevent.
                foreach (var keyword in NegatedKeywords(raw, token, altToken))
                {
                    Add(excludeKeywords, keyword);
                }
            }
            else if (raw.Length > 2 && !StopWords.Contains(raw))
            {
                Add(keywords, token.Length > 2 ? token : raw);
                if (altToken is { Length: > 2 })
                {
                    Add(keywords, altToken);
                }
            }
        }

        // A term on both sides is a negation that also read as positive earlier in the sentence
        // ("more thriller and less horror" once horror has been seen twice). Negation wins.
        genres.RemoveAll(excludeGenres.Contains);
        keywords.RemoveAll(excludeKeywords.Contains);

        // No positive concept words at all → treat the whole phrase as one keyword so discovery
        // still fires. Guarded on the negations too: a turn that is purely a rejection ("no ghosts")
        // must not turn that rejection into the search string.
        if (genres.Count == 0 && keywords.Count == 0 && excludeGenres.Count == 0 && excludeKeywords.Count == 0)
        {
            keywords.Add(lower);
        }

        return new SearchIntent(genres, keywords, media, query, minYear, maxYear, null, excludeGenres, excludeKeywords);

        static void Add(List<string> target, string value)
        {
            if (!target.Contains(value)) target.Add(value);
        }
    }

    /// <summary>
    /// The singular readings a negated token is tested against the vocabulary as, in preference
    /// order: the scanner's crude singular, its "-ies" alternative, and then every shorter reading
    /// <see cref="NegationTerms.Forms"/> generates from the RAW token. The crude singular alone only
    /// strips a final "s", so "superheroes" became "superheroe", "witches" became "witche", and the
    /// vocabulary — stored singular — recognised neither: "no superheroes please" produced no
    /// exclusion at all and requested negations are read only from here (IREV1-SYS-03). Only
    /// readings SHORTER than the raw token are offered, so the exclusion that lands is the
    /// vocabulary's own singular ("superhero"), never the plural the user typed or a mangled form.
    /// The generated readings are consulted only when neither crude reading landed, and the first
    /// one the vocabulary knows ends the search; later ones would only be spellings of the same
    /// concept.
    /// </summary>
    private static IReadOnlyList<string> NegatedKeywords(string raw, string token, string? altToken)
    {
        // The crude readings keep their historic behaviour: both "-ies" readings are offered, and
        // either that the vocabulary knows becomes an exclusion.
        var crude = new[] { token, altToken }
            .OfType<string>()
            .Where(candidate => candidate.Length > 2 && SearchVocabulary.IsNegatableKeyword(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (crude.Count > 0)
        {
            return crude;
        }

        var generated = NegationTerms.Forms(raw).FirstOrDefault(form =>
            form.Length > 2 && form.Length < raw.Length && SearchVocabulary.IsNegatableKeyword(form));
        return generated is null ? [] : [generated];
    }

    // The genre aliases moved to Application's SearchVocabulary, whole. They were private here, so
    // the collision rung could not read them — and "a 90s romantic comedy" named only comedy while
    // the prose in the same bubble named romance and comedy. One table, two readers.

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "with", "for", "about", "like", "some", "any",
        "good", "best", "great", "really", "very", "more", "most", "kind", "stuff", "things",
        "something", "show", "me", "find", "want", "watch",
        // "from" survives the year strip when it introduces a decade ("from the 2010s") and never
        // names a concept on its own; Ask Reel's sanitizer already treats it as a stop word.
        "from",
        // Negation machinery — these steer the parse, they are never content themselves.
        "less", "fewer", "not", "no", "don", "dont", "doesn", "without", "except", "avoid", "hate",
        "but", "instead", "rather",
    };

    /// <summary>Marker emitted by <see cref="Tokenize"/> where a clause ended, so negation scope can see it.</summary>
    private const string BoundaryToken = ",";

    private static readonly char[] WordSeparators = [' ', '/', '-', '&', '\'', '"', '(', ')', '\t'];

    private static readonly char[] ClauseSeparators = [',', ';', '.', '!', '?', ':'];

    /// <summary>
    /// Splits a query into content tokens, but keeps clause boundaries as a literal
    /// <see cref="BoundaryToken"/> rather than discarding them. Negation scope depends on them:
    /// once commas are gone, "no horror, gore, zombies" and "not scary, something funny" are the
    /// same token stream, and no word-count heuristic can tell a rejection list from a new request.
    /// </summary>
    private static List<string> Tokenize(string lower)
    {
        var result = new List<string>();
        var chunks = lower.Split(ClauseSeparators, StringSplitOptions.None);
        for (var i = 0; i < chunks.Length; i++)
        {
            result.AddRange(chunks[i].Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries));
            if (i < chunks.Length - 1)
            {
                result.Add(BoundaryToken);
            }
        }

        return result;
    }

    /// <summary>
    /// True when a token carries meaning of its own rather than steering the parse. Used to decide
    /// whether a clause boundary continues a rejection list or ends it.
    /// </summary>
    private static bool IsContentToken(string raw) =>
        raw.Length > 2
        && !StopWords.Contains(raw)
        && !NegationFiller.Contains(raw)
        && !PositivePivots.Contains(raw)
        && !Negators.Contains(raw);

    private static readonly IReadOnlyDictionary<string, int> DecadeWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["fifties"] = 1950,
        ["sixties"] = 1960,
        ["seventies"] = 1970,
        ["eighties"] = 1980,
        ["nineties"] = 1990,
        ["noughties"] = 2000,
    };

    /// <summary>
    /// Extracts an inclusive release-year window from natural phrasing — "80s sci-fi",
    /// "something from the nineties", "before 2000", "after 2015". A bare four-digit number is
    /// deliberately NOT treated as a year: titles like "1917", "300" and "2001" are far more often
    /// the thing being searched for than a date filter.
    /// </summary>
    internal static (int? MinYear, int? MaxYear) ParseYearRange(string lower)
    {
        foreach (var (word, start) in DecadeWords)
        {
            if (lower.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return (start, start + 9);
            }
        }

        var decade = Regex.Match(lower, @"\b(\d{2}|\d{4})s\b");
        if (decade.Success)
        {
            var raw = decade.Groups[1].Value;
            int start;
            if (raw.Length == 4)
            {
                start = int.Parse(raw, CultureInfo.InvariantCulture);
            }
            else
            {
                // Two-digit decades are read against the film era they realistically mean: "80s"
                // and "90s" are the 20th century, "00s"/"10s"/"20s" the 21st.
                var d = int.Parse(raw, CultureInfo.InvariantCulture);
                start = d >= 30 ? 1900 + d : 2000 + d;
            }

            start -= start % 10;
            return (start, start + 9);
        }

        var before = Regex.Match(lower, @"\b(?:before|earlier than|older than|up to|until|pre)\s+(\d{4})\b");
        var after = Regex.Match(lower, @"\b(?:after|since|from|newer than|later than|post)\s+(\d{4})\b");
        int? min = after.Success ? int.Parse(after.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        int? max = before.Success ? int.Parse(before.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        return (min, max);
    }

    /// <summary>
    /// The query with every phrase <see cref="ParseYearRange"/> reads as a release window blanked
    /// out: decade words ("nineties"), decade forms ("80s", "'90s", "2010s"), and a four-digit year
    /// together with the word that bounds it ("before 2000", "from 2015", "newer than 2010"). A
    /// BARE four-digit number survives, for the reason <see cref="ParseYearRange"/> gives — "1917"
    /// and "2001" are titles far more often than dates. Phrases are replaced by a space, never a
    /// clause separator, so the negation scope sees the same sentence boundaries it did before.
    /// </summary>
    internal static string StripYearPhrases(string lower)
    {
        var stripped = DecadeWordPattern.Replace(lower, " ");
        stripped = DecadeFormPattern.Replace(stripped, " ");
        return BoundedYearPattern.Replace(stripped, " ");
    }

    private static readonly Regex DecadeWordPattern = new(
        @"\b(?:" + string.Join("|", DecadeWords.Keys) + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DecadeFormPattern = new(
        @"\b(?:\d{2}|\d{4})s\b", RegexOptions.CultureInvariant);

    private static readonly Regex BoundedYearPattern = new(
        @"\b(?:before|earlier than|older than|up to|until|pre|after|since|from|newer than|later than|post)\s+\d{4}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Words that sit between a negator and its target without ending the negation —
    /// "don't WANT ghosts", "no MORE ghosts". They neither close the scope nor consume it.
    /// </summary>
    private static readonly HashSet<string> NegationFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "want", "wanted", "like", "liked", "see", "seen", "watch", "watching", "have", "any",
        "more", "much", "really", "into", "for", "the", "a", "an", "some", "too",
    };

    /// <summary>
    /// Words that open a negation ("less horror", "no ghosts", "I don't want ghosts").
    ///
    /// <para>"nothing", "none" and "never" were missing, which is how the live repro's "nothing set
    /// in space" ran on as a POSITIVE pull for the words inside the rejection. They open a scope
    /// now; the vocabulary floor below is what keeps that from becoming the opposite defect.</para>
    /// </summary>
    private static readonly HashSet<string> Negators = new(StringComparer.OrdinalIgnoreCase)
    {
        // "don" and "doesn" are what the tokenizer leaves of "don't"/"doesn't" — it splits on the
        // apostrophe, so the negator never survives as a whole word.
        "less", "fewer", "not", "no", "without", "except", "avoid", "hate", "don", "dont", "doesn",
        "nothing", "none", "never",
    };

    /// <summary>
    /// Words that close a negation scope because the sentence has pivoted back to what the user
    /// DOES want — "no ghosts I want serial killers" must not negate "serial killers".
    /// </summary>
    private static readonly HashSet<string> PositivePivots = new(StringComparer.OrdinalIgnoreCase)
    {
        "but", "instead", "rather", "want", "prefer", "more", "just", "only", "give",
    };

    /// <summary>
    /// The user message. Bare query when the account has no standing rules — which keeps every
    /// existing request byte-identical — and the query beneath the shared standing-filters line
    /// when it does. The line itself comes from <see cref="OpenRouterSearchAgent"/> so the two
    /// Ask Reel prompts cannot express the same rules in two different sentences: this one is the
    /// FALLBACK half of that pair, and the pair drifting is a defect this codebase has already
    /// paid for once.
    ///
    /// <para>The conversation rides the same way, and only when there is one: Ask Reel's recovery
    /// path hands it down so a reference ("the second one") or a negation from an earlier turn is
    /// resolved rather than read as a keyword. The instruction sits in the conversation header, not
    /// in the system prompt, so a request without history stays byte-identical.</para>
    /// </summary>
    internal static string BuildUserMessage(string query, string? standingFilters, IReadOnlyList<ChatTurn> history)
    {
        if (standingFilters is not { Length: > 0 } && history.Count == 0)
        {
            return query;
        }

        var sb = new System.Text.StringBuilder();
        if (standingFilters is { Length: > 0 } filters)
        {
            sb.Append(OpenRouterSearchAgent.StandingFiltersLine(filters));
        }

        if (history.Count > 0)
        {
            sb.Append("Conversation so far (resolve references like \"the second one\" against it, carry the user's ")
                .Append("negations forward unless they retract them, and never turn a reference or a negation into a ")
                .Append("keyword):\n");
            foreach (var turn in history.TakeLast(8))
            {
                sb.Append(turn.Role).Append(": ").Append(turn.Text).Append('\n');
            }
        }

        return sb.Append("Request: ").Append(query).ToString();
    }

    internal const string SystemPrompt =
        "You convert a movie/TV search request into a JSON discovery spec. Return ONLY a JSON object with keys: " +
        "genres (array of slugs from EXACTLY this set: action, adventure, animation, comedy, crime, documentary, " +
        "drama, family, fantasy, history, horror, music, mystery, romance, science-fiction, thriller, war, western), " +
        "keywords (array of up to 5 lowercase concept phrases, e.g. \"shark\", \"serial killer\", \"heist\"), " +
        "people (array of {name, role} where role is \"director\", \"cast\", or \"any\" — for any named filmmaker or " +
        "actor, e.g. \"movies Christopher Nolan directs\" → people:[{\"name\":\"Christopher Nolan\",\"role\":\"director\"}]; " +
        "never put a person's name in keywords or freeText), " +
        "mediaTypes (array; any of \"movie\", \"tv\"; empty means both), minYear (integer or null), maxYear " +
        "(integer or null), excludeGenres (array of slugs from the same set), excludeKeywords (array of lowercase " +
        "phrases), referenceTitles (array of movie/show titles the user names as COMPARISONS — \"like Blade Runner " +
        "but more emotional\" → referenceTitles:[\"Blade Runner\"]; extract only explicitly named titles, and keep " +
        "them OUT of keywords and freeText), maxRuntimeMinutes (integer or null — an upper runtime bound in " +
        "minutes: \"under 90 minutes\" → 90; null when no length constraint is given), " +
        "and freeText (a concise literal-title search string, usually echoing the request). " +
        "Map intent to that genre set only; put anything specific (subjects, settings, vibes) into keywords. " +
        "NEGATION IS CRITICAL: anything the user says they do NOT want (\"no ghosts\", \"not horror\", \"less gore\", " +
        "\"anything but musicals\") goes in excludeGenres/excludeKeywords and MUST NOT appear in genres or keywords — " +
        "those are pull signals, so a rejected term placed there makes the search actively fetch the very thing the " +
        "user refused. Rejection lists can be long: put EVERY rejected item in the exclude arrays, not just the first few. " +
        "YEAR RANGES: a decade means both bounds — \"80s\" is minYear 1980 and maxYear 1989, \"from the 90s\" is 1990-1999. " +
        "\"before 2000\" sets only maxYear; \"after 2015\" sets only minYear. Do not infer a year from a title that merely " +
        "looks like one (\"1917\", \"300\", \"2001\"). " +
        "STANDING FILTERS: where a standing-filters line is given, those are the account's own content rules and " +
        "they bound the answer space — never put an excluded genre or term in genres or keywords, even when the " +
        "request asks for it. Output strictly valid JSON.";
}
