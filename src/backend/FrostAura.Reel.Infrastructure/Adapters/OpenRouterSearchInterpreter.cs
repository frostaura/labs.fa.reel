using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
            return new SearchIntent([], [], [], string.Empty, null);
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
        var tokens = lower.Split([' ', ',', '.', '/', '-', '&', '\'', '"', '(', ')', '!', '?'], StringSplitOptions.RemoveEmptyEntries);

        var genres = new List<string>();
        var keywords = new List<string>();
        var media = new List<MediaType>();
        var excludeGenres = new List<string>();
        var excludeKeywords = new List<string>();

        // Negation scope. "less horror", "no ghosts", "I don't want ghosts" — without this every
        // rejected term became a positive pull signal and discovery fetched exactly what the user
        // had just ruled out. A negator flips the next few tokens; the scope closes at the next
        // positive pivot ("but", "i want", "more") or after NegationScope tokens, because natural
        // phrasing rarely negates further than that ("less horror, I want serial killers").
        var negateFor = 0;
        var negatedContentSeen = false;

        foreach (var raw in tokens)
        {
            if (Negators.Contains(raw))
            {
                negateFor = NegationScope;
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
            if (negateFor > 0 && !negatedContentSeen && NegationFiller.Contains(raw))
            {
                continue;
            }

            // A pivot only closes a negation that has actually landed on something — otherwise the
            // "want" in "don't want" would close the scope it belongs to.
            if (PositivePivots.Contains(raw))
            {
                if (negateFor == 0 || negatedContentSeen)
                {
                    negateFor = 0;
                }

                continue;
            }

            // Sub-3-character tokens are punctuation debris ("t" from "don't", a bare "i"), never
            // content — and must not consume the negation budget.
            if (raw.Length <= 2)
            {
                continue;
            }

            var negated = negateFor > 0;
            if (negated)
            {
                negateFor--;
                negatedContentSeen = true;
            }
            // Crude singularise: "horrors" → "horror", "mysteries" → "mystery", "comedies" → "comedy".
            var token = raw.EndsWith("ies", StringComparison.Ordinal) && raw.Length > 4
                ? string.Concat(raw.AsSpan(0, raw.Length - 3), "y")
                : raw.TrimEnd('s');
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
                Add(negated ? excludeKeywords : keywords, token.Length > 2 ? token : raw);
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

        return new SearchIntent(genres, keywords, media, query, null, null, excludeGenres, excludeKeywords);

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

    /// <summary>How many tokens a negator governs before the scope lapses.</summary>
    private const int NegationScope = 3;

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
        "mediaTypes (array; any of \"movie\", \"tv\"; empty means both), minYear (integer or null), and freeText " +
        "(a concise literal-title search string, usually echoing the request). Map intent to that genre set only; " +
        "put anything specific (subjects, settings, vibes) into keywords. Output strictly valid JSON.";

    private sealed record ChatResponse(ChatChoice[]? Choices);

    private sealed record ChatChoice(ChatMessage? Message);

    private sealed record ChatMessage(string? Content);
}
