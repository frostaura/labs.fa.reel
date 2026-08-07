using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Turns a natural-language Ask Reel query into a TMDB discovery spec via OpenRouter chat
/// completions (REEL_SEARCH_MODEL, default <c>openai/gpt-5.4-mini</c>). Mirrors the attribute
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
    ILogger<OpenRouterSearchInterpreter> logger)
    : ISearchQueryInterpreter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    private bool StubMode => ApiKey is null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public bool IsAvailable => true; // the stub always answers

    public string ModelId => StubMode
        ? "stub/deterministic-v1"
        // Empty env vars are "present but blank" to .NET config, which defeats ?? — treat blank as absent.
        : configuration["REEL_SEARCH_MODEL"] is { Length: > 0 } m ? m
        : configuration["OPENROUTER_MODEL"] is { Length: > 0 } o ? o
        : "openai/gpt-5.4-mini";

    public async Task<SearchIntent> InterpretAsync(string query, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new SearchIntent([], [], [], string.Empty, null, null);
        }

        if (StubMode)
        {
            return Heuristic(trimmed);
        }

        try
        {
            usageRecorder.Record(ApiProvider.OpenRouter);
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = ModelId,
                    temperature = 0,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = trimmed },
                    },
                }, options: JsonOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var dto = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, ct);
            var content = dto?.Choices?.FirstOrDefault()?.Message?.Content;
            return string.IsNullOrWhiteSpace(content) ? Heuristic(trimmed) : Parse(content, trimmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Query interpretation failed for {Query}; falling back to heuristic.", trimmed);
            return Heuristic(trimmed);
        }
    }

    private static SearchIntent Parse(string json, string fallbackText)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return SearchIntentParser.FromJson(doc.RootElement, fallbackText, Heuristic(fallbackText));
        }
        catch (JsonException)
        {
            return Heuristic(fallbackText);
        }
    }

    /// <summary>
    /// Deterministic fallback: split the query, fold known genre words (and a few aliases) into
    /// genres, the rest into keywords. "shark horrors" → genres:[horror], keywords:[shark]. No
    /// network; same input → same output.
    /// </summary>
    private static SearchIntent Heuristic(string query)
    {
        var lower = query.ToLowerInvariant();
        var tokens = Tokenize(lower);
        var (minYear, maxYear) = ParseYearRange(lower);

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
            if (GenreAliases.TryGetValue(raw, out var aliasSlug))
            {
                Add(negated ? excludeGenres : genres, aliasSlug);
            }
            else if (TmdbGenres.SlugToIds.ContainsKey(raw))
            {
                Add(negated ? excludeGenres : genres, raw);
            }
            else if (TmdbGenres.SlugToIds.ContainsKey(token))
            {
                Add(negated ? excludeGenres : genres, token);
            }
            else if (raw.Length > 2 && !StopWords.Contains(raw))
            {
                var target = negated ? excludeKeywords : keywords;
                Add(target, token.Length > 2 ? token : raw);
                if (altToken is { Length: > 2 })
                {
                    Add(target, altToken);
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

    private static readonly IReadOnlyDictionary<string, string> GenreAliases = new Dictionary<string, string>
    {
        ["scifi"] = "science-fiction",
        ["sci"] = "science-fiction",
        ["fi"] = "science-fiction",
        ["horrors"] = "horror",
        ["scary"] = "horror",
        ["funny"] = "comedy",
        ["comedies"] = "comedy",
        ["romcom"] = "romance",
        ["thrillers"] = "thriller",
        ["docs"] = "documentary",
        ["doc"] = "documentary",
        ["animated"] = "animation",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "with", "for", "about", "like", "some", "any",
        "good", "best", "great", "really", "very", "more", "most", "kind", "stuff", "things",
        "something", "show", "me", "find", "want", "watch",
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
    /// Words that sit between a negator and its target without ending the negation —
    /// "don't WANT ghosts", "no MORE ghosts". They neither close the scope nor consume it.
    /// </summary>
    private static readonly HashSet<string> NegationFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "want", "wanted", "like", "liked", "see", "seen", "watch", "watching", "have", "any",
        "more", "much", "really", "into", "for", "the", "a", "an", "some", "too",
    };

    /// <summary>Words that open a negation ("less horror", "no ghosts", "I don't want ghosts").</summary>
    private static readonly HashSet<string> Negators = new(StringComparer.OrdinalIgnoreCase)
    {
        // "don" and "doesn" are what the tokenizer leaves of "don't"/"doesn't" — it splits on the
        // apostrophe, so the negator never survives as a whole word.
        "less", "fewer", "not", "no", "without", "except", "avoid", "hate", "don", "dont", "doesn",
    };

    /// <summary>
    /// Words that close a negation scope because the sentence has pivoted back to what the user
    /// DOES want — "no ghosts I want serial killers" must not negate "serial killers".
    /// </summary>
    private static readonly HashSet<string> PositivePivots = new(StringComparer.OrdinalIgnoreCase)
    {
        "but", "instead", "rather", "want", "prefer", "more", "just", "only", "give",
    };

    private const string SystemPrompt =
        "You convert a movie/TV search request into a JSON discovery spec. Return ONLY a JSON object with keys: " +
        "genres (array of slugs from EXACTLY this set: action, adventure, animation, comedy, crime, documentary, " +
        "drama, family, fantasy, history, horror, music, mystery, romance, science-fiction, thriller, war, western), " +
        "keywords (array of up to 5 lowercase concept phrases, e.g. \"shark\", \"serial killer\", \"heist\"), " +
        "people (array of {name, role} where role is \"director\", \"cast\", or \"any\" — for any named filmmaker or " +
        "actor, e.g. \"movies Christopher Nolan directs\" → people:[{\"name\":\"Christopher Nolan\",\"role\":\"director\"}]; " +
        "never put a person's name in keywords or freeText), " +
        "mediaTypes (array; any of \"movie\", \"tv\"; empty means both), minYear (integer or null), maxYear " +
        "(integer or null), excludeGenres (array of slugs from the same set), excludeKeywords (array of lowercase " +
        "phrases), and freeText (a concise literal-title search string, usually echoing the request). " +
        "Map intent to that genre set only; put anything specific (subjects, settings, vibes) into keywords. " +
        "NEGATION IS CRITICAL: anything the user says they do NOT want (\"no ghosts\", \"not horror\", \"less gore\", " +
        "\"anything but musicals\") goes in excludeGenres/excludeKeywords and MUST NOT appear in genres or keywords — " +
        "those are pull signals, so a rejected term placed there makes the search actively fetch the very thing the " +
        "user refused. Rejection lists can be long: put EVERY rejected item in the exclude arrays, not just the first few. " +
        "YEAR RANGES: a decade means both bounds — \"80s\" is minYear 1980 and maxYear 1989, \"from the 90s\" is 1990-1999. " +
        "\"before 2000\" sets only maxYear; \"after 2015\" sets only minYear. Do not infer a year from a title that merely " +
        "looks like one (\"1917\", \"300\", \"2001\"). Output strictly valid JSON.";

    private sealed record ChatResponse(ChatChoice[]? Choices);

    private sealed record ChatChoice(ChatMessage? Message);

    private sealed record ChatMessage(string? Content);
}
