using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Writes the product's two pieces of second-person prose through OpenRouter (founder-locked
/// <c>openai/gpt-6-luna</c>): the account's taste narrative, and one title seen through it.
///
/// <para>Structurally a sibling of <see cref="OpenRouterAttributeExtractor"/> and follows the same
/// three rules, for the same reasons. <b>Strict <c>json_schema</c> where the provider supports
/// it</b>, with a permanent per-instance downgrade to <c>json_object</c> on rejection and the
/// parser as the backstop — prose instructions do not enforce a closed vocabulary, a schema does.
/// <b>One repair retry, and only when something material can change</b> — resending a
/// byte-identical prompt at temperature 0 is spend with no incremental chance of success.
/// <b>Never fabricate</b> — a response that does not parse or does not clear the guards returns
/// null so the caller leaves the row unwritten, rather than caching a plausible substitute that is
/// indistinguishable from a real answer.</para>
///
/// <para><b>Every number that reaches a prompt, a key or a JSON body is formatted with
/// <see cref="CultureInfo.InvariantCulture"/>.</b> This machine's locale renders 8.4 as "8,4" — a
/// decimal comma inside a comma-separated list, handed to a model asked to reason about the
/// figure. The host's locale has no business changing what the model reads.</para>
///
/// <para><b>Stub mode</b> (no key, or <c>TestHelpers:ModelStubMode</c>) is deterministic and
/// derived from the input, exactly as the extractor's is: the whole pipeline — job, guards,
/// persistence, surfaces — runs in tests and in keyless dev without a single billable call.</para>
/// </summary>
public class OpenRouterLensWriter(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterLensWriter> logger,
    // Optional by design, mirroring the other OpenRouter adapters: the typed HttpClient is
    // transient and is always constructed inside a scope in production, but the adapter must stay
    // `new`-able in a test with no scope.
    IAccountContext? accountContext = null)
    : ILensWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The narrative prompt generation. Bump when a change alters what a stored narrative MEANS —
    /// never for a retry strategy or a transport fix.
    ///
    /// <para>v2: the fact sheet leads with what the account WATCHES rather than what it rates
    /// highest, carries recent loves and dislikes, and labels sparse genres as sparse. A stored v1
    /// narrative was written from a fact sheet that could not distinguish twenty-six holiday films
    /// from eight hundred thrillers, so it means something different and must not be reused.</para>
    /// </summary>
    public const int CurrentNarrativePromptVersion = 2;

    /// <summary>
    /// The lens prompt generation, and half the lens cache key. Same bump rule.
    ///
    /// <para>v2: the prompt now NAMES the titles a note may anchor against. v1 told the model to
    /// take anchors "verbatim from the lists below" and supplied no lists, so its anchors were
    /// guesses and every note carrying one was refused. A v1 row is not a v2 row.</para>
    ///
    /// <para>v3: the length budgets asked for dropped about a tenth under the hard ceilings and the
    /// prompt now says the limit is a hard stop. The bump is what makes the fix visible: a v2 row
    /// was written against the old cap and may be sitting on screen ending mid-word, and
    /// <c>ResolveAsync</c> would keep serving it as fresh forever. Regeneration is lazy and
    /// budget-gated — the background job and the on-demand path pay for it a few notes at a
    /// time — so this is not a bulk re-spend.</para>
    ///
    /// <para><b>v4: the two dimensions the founder-only audit said v3 was failing.</b> Over a
    /// model-judged sample the v3 prompt scored 0.91 specificity and 0.95 grounding — it knew what
    /// it was talking about — against 0.57 voice and 0.63 spoiler-freeness. The judge's complaints
    /// were the same two sentences over and over: "third-person marketing-style copy" and "gives
    /// away the premise too explicitly". Both were prompt defects rather than model failures.</para>
    ///
    /// <para><i>Voice.</i> v3 asked for second person and then described <c>hook</c> as "the single
    /// reason to press play tonight", which is a brief for a poster line — and posters came back:
    /// "Jodie Foster turns grief into a tight revenge chase." v4 requires the hook to be said TO the
    /// viewer, bans a short list of marketing adjectives outright, and adds the POV rule the copy
    /// was missing: "you" is the viewer watching and is never a character in the plot. The live slip
    /// that named the rule — "the city that failed you", of a city that failed <i>her</i> — reads as
    /// a typo and is actually the model losing track of who it is talking to.</para>
    ///
    /// <para><i>Spoilers.</i> v3 said "first act only" and the judge read that as forbidding the
    /// premise itself, marking down notes for "revealing the childhood-memory premise". A pitch that
    /// will not say what the film is about is not spoiler-safe, it is useless. v4 states the line
    /// the product actually holds: the setup as printed in the official overview is fair game, and
    /// how it DEVELOPS never is. The judge rubric was corrected in the same pass, so the score and
    /// the prompt now mean the same thing — which is the only reason a v3→v4 mean is comparable.</para>
    ///
    /// <para>Bumped rather than edited in place for the usual reason: a v3 row is a third-person
    /// tagline that <c>ResolveAsync</c> would serve as fresh forever.</para>
    /// </summary>
    public const int CurrentLensPromptVersion = 4;

    /// <summary>Sentence bounds for a narrative — fewer reads as a stub, more reads as a list.</summary>
    private const int MinSentences = 6;

    private const int MaxSentences = 10;

    /// <summary>Roughly 10 sentences of prose plus JSON overhead, with room to spare.</summary>
    private const int NarrativeTokenBudget = 900;

    /// <summary>A full lens object is ~220 tokens; this is generous enough that a normal answer never truncates.</summary>
    private const int LensTokenBudget = 700;

    private const int AuditTokenBudget = 300;

    private const int MaxOverviewChars = 1200;

    private const int MaxKeywords = 20;

    private const int MaxCast = 3;

    /// <summary>See <see cref="OpenRouterAttributeExtractor"/>: written once, read from many.</summary>
    private volatile bool _strictSchemaSupported = true;

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    private bool StubMode => ApiKey is null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public bool IsAvailable => ApiKey is not null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public string ModelId => StubMode
        ? "stub/deterministic-v1"
        : configuration["OPENROUTER_MODEL"] is { Length: > 0 } m ? m : "openai/gpt-6-luna";

    public int LensPromptVersion => CurrentLensPromptVersion;

    public int NarrativePromptVersion => CurrentNarrativePromptVersion;

    // ── Taste narrative ────────────────────────────────────────────────────────────────────

    public async Task<NarrativeDraft?> WriteNarrativeAsync(TasteFacts facts, CancellationToken ct = default)
    {
        if (StubMode)
        {
            return new NarrativeDraft(StubNarrative(facts), ModelId, null, null);
        }

        var user = NarrativePrompt(facts);
        var first = await ChatAsync(NarrativeSystemPrompt, user, null, NarrativeTokenBudget,
            NarrativeSchema(), ApiUsagePurpose.TasteNarrative, ct);
        if (ParseNarrative(first.Content) is { } sentences)
        {
            return new NarrativeDraft(sentences, ModelId, first.Usage?.PromptTokens, first.Usage?.CompletionTokens);
        }

        if (first.Content is null)
        {
            // Transport or an empty body. A second identical request inside this call buys nothing;
            // the weekly cadence is the retry.
            return null;
        }

        logger.LogWarning("Taste narrative came back unusable; retrying once with a corrected prompt.");
        var second = await ChatAsync(
            NarrativeSystemPrompt,
            user,
            $"Your previous answer was rejected. Return ONLY {{\"sentences\":[...]}} with between "
                + $"{MinSentences} and {MaxSentences} complete second-person sentences and nothing else.",
            NarrativeTokenBudget,
            NarrativeSchema(),
            ApiUsagePurpose.TasteNarrative,
            ct);
        return ParseNarrative(second.Content) is { } repaired
            ? new NarrativeDraft(repaired, ModelId, second.Usage?.PromptTokens, second.Usage?.CompletionTokens)
            : null;
    }

    /// <summary>
    /// Sentences, or null. A narrative outside the sentence bounds is rejected rather than padded
    /// or truncated: three sentences is not a profile, and fifteen is a list.
    /// </summary>
    internal static IReadOnlyList<string>? ParseNarrative(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("sentences", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sentences = arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            return sentences.Count is >= MinSentences and <= MaxSentences ? sentences : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Lens copy ──────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LensDraft?>> WriteLensAsync(
        IReadOnlyList<LensTitleInput> titles, LensPersona persona, CancellationToken ct = default)
    {
        if (titles.Count == 0)
        {
            return [];
        }

        if (StubMode)
        {
            return titles.Select(t => (LensDraft?)new LensDraft(StubLens(t, persona), ModelId, null, null)).ToArray();
        }

        // One title per request, bounded concurrency, results written back in index order so the
        // batch stays row-aligned for the caller — the extractor's shape, and for its reason: a
        // single object per call is what makes strict schema enforcement meaningful per title.
        var maxConcurrency = Math.Max(1, configuration.GetValue("OPENROUTER_MAX_CONCURRENCY", 4));
        var results = new LensDraft?[titles.Count];
        using var gate = new SemaphoreSlim(maxConcurrency);
        await Task.WhenAll(titles.Select(async (title, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = await WriteOneLensAsync(title, persona, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        return results;
    }

    private async Task<LensDraft?> WriteOneLensAsync(LensTitleInput title, LensPersona persona, CancellationToken ct)
    {
        var user = LensPrompt(title, persona);
        var first = await ChatAsync(
            LensSystemPrompt, user, null, LensTokenBudget, LensSchema(), ApiUsagePurpose.Lens, ct);
        var (note, problems) = ParseLens(first.Content);
        if (note is not null)
        {
            return new LensDraft(note, ModelId, first.Usage?.PromptTokens, first.Usage?.CompletionTokens);
        }

        if (first.Content is null)
        {
            return null;
        }

        logger.LogWarning(
            "Lens for {Title} ({TitleId}) came back unusable [{Detail}]; retrying once with a corrected prompt.",
            title.Name, title.TitleId, string.Join("; ", problems));
        var second = await ChatAsync(
            LensSystemPrompt,
            user,
            "Your previous answer was rejected because of these fields: " + string.Join("; ", problems)
                + ". Answer again with every key present, the character limits respected, mood and "
                + "whenToWatch copied verbatim from the allowed lists, and no numbers anywhere in the prose.",
            LensTokenBudget,
            LensSchema(),
            ApiUsagePurpose.Lens,
            ct);
        var (repaired, stillWrong) = ParseLens(second.Content);
        if (repaired is null)
        {
            logger.LogWarning(
                "Lens for {Title} ({TitleId}) failed after the repair pass ({Detail}). Nothing written — the "
                + "surface falls back rather than showing invented copy.",
                title.Name, title.TitleId, string.Join("; ", stillWrong));
            return null;
        }

        return new LensDraft(repaired, ModelId, second.Usage?.PromptTokens, second.Usage?.CompletionTokens);
    }

    /// <summary>
    /// Parses a lens object, or the reasons it is unusable. Character limits are enforced HERE
    /// rather than trimmed, because a 600-character "lens" is not a long answer to the question
    /// asked — it is an answer to a different one, and silently cutting it mid-sentence ships the
    /// worst of both.
    /// </summary>
    internal static (LensNote? Note, IReadOnlyList<string> Problems) ParseLens(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (null, ["empty completion"]);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            return (null, [$"response was not valid JSON ({ex.Message})"]);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, [$"response root was {root.ValueKind}, expected an object"]);
            }

            var problems = new List<string>();
            var lens = Bounded(root, "lens", LensNote.MaxLensChars, required: true, problems);
            var hook = Bounded(root, "hook", LensNote.MaxHookChars, required: true, problems);
            var caution = Bounded(root, "caution", LensNote.MaxCautionChars, required: false, problems);
            var mood = Vocabulary(root, "mood", LensVocabulary.Moods, problems);
            var whenToWatch = Vocabulary(root, "whenToWatch", LensVocabulary.WhenToWatch, problems);
            var anchors = Anchors(root, problems);

            if (problems.Count > 0)
            {
                return (null, problems);
            }

            return (new LensNote(lens!, hook!, caution, anchors, mood!, whenToWatch!), []);
        }
    }

    private static string? Bounded(JsonElement root, string name, int max, bool required, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                problems.Add($"{name} was absent");
            }

            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{name} was {el.ValueKind}, expected a string");
            return null;
        }

        var value = el.GetString()!.Trim();
        if (value.Length == 0)
        {
            // An empty caution is the model declining to voice a reservation, which is allowed;
            // an empty lens or hook is a missing answer wearing a present key.
            if (required)
            {
                problems.Add($"{name} was empty");
            }

            return null;
        }

        if (value.Length > max)
        {
            problems.Add($"{name} was {value.Length} chars, over the {max} limit");
            return null;
        }

        return value;
    }

    private static string? Vocabulary(JsonElement root, string name, string[] vocabulary, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{name} was absent or not a string");
            return null;
        }

        var value = el.GetString()!.Trim().ToLowerInvariant();
        if (!vocabulary.Contains(value, StringComparer.Ordinal))
        {
            // Off-vocabulary is fatal here, unlike tone/era on a title: mood is what the
            // tonight-chips re-rank over, so an invented value is a card the chip can never find.
            problems.Add($"{name} \"{value}\" is not in the closed vocabulary");
            return null;
        }

        return value;
    }

    private static IReadOnlyList<LensAnchor> Anchors(JsonElement root, List<string> problems)
    {
        if (!root.TryGetProperty("anchors", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            // Anchors are genuinely optional: a title with nothing in the user's library to compare
            // it to should say nothing rather than reach.
            return [];
        }

        var anchors = new List<LensAnchor>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String
                || !el.TryGetProperty("relation", out var r) || r.ValueKind != JsonValueKind.String)
            {
                problems.Add("an anchor entry was not {title, relation}");
                return [];
            }

            var title = t.GetString()!.Trim();
            var relation = r.GetString()!.Trim();
            if (title.Length > 0 && relation.Length > 0)
            {
                anchors.Add(new LensAnchor(title, relation));
            }
        }

        return anchors.Take(LensNote.MaxAnchors).ToList();
    }

    // ── LLM judge (founder-only audit) ─────────────────────────────────────────────────────

    public async Task<LensAuditScore?> JudgeAsync(
        LensTitleInput title, LensPersona persona, LensNote note, CancellationToken ct = default)
    {
        if (StubMode)
        {
            // Deterministic but honest about being a fixture: derived from the note's own text so a
            // test can assert a stable number, with the comment saying exactly what it is.
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes(note.Lens + note.Hook));
            double Unit(int i) => Math.Round(0.5 + (seed[i % seed.Length] / 510d), 3);
            return new LensAuditScore(
                Unit(0), Unit(1), Unit(2), Unit(3), null, "stub/deterministic-v1 — not a real judgement");
        }

        var user = new StringBuilder()
            .Append("VIEWER\n").Append(persona.Taste).Append('\n')
            .Append("TITLE\n").Append(TitleBlock(title)).Append('\n')
            .Append("NOTE\n").Append(JsonSerializer.Serialize(note, JsonOptions))
            .ToString();
        var response = await ChatAsync(
            AuditSystemPrompt, user, null, AuditTokenBudget, AuditSchema(), ApiUsagePurpose.LensAudit, ct);
        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Content);
            var root = doc.RootElement;
            double? Score(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
                    && el.TryGetDouble(out var v) && v is >= 0 and <= 1
                    ? v
                    : null;

            // Every dimension or none: a partial rubric silently reweights the overall score.
            if (Score("specificity") is not { } specificity || Score("grounding") is not { } grounding
                || Score("spoilerFreeness") is not { } spoilerFree || Score("voice") is not { } voice)
            {
                return null;
            }

            string? Text(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    && el.GetString() is { Length: > 0 } value
                    ? value
                    : null;

            // povSlips is a NOTE, not a dimension — a judge that omits it has not returned a
            // partial rubric, so unlike the four scores its absence is not a reason to discard.
            return new LensAuditScore(
                specificity, grounding, spoilerFree, voice, Text("povSlips"), Text("comment"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Transport ──────────────────────────────────────────────────────────────────────────

    private sealed record ChatOutcome(string? Content, OpenRouterUsage? Usage);

    private async Task<ChatOutcome> ChatAsync(
        string system, string user, string? repairHint, int maxTokens, object schema, string purpose, CancellationToken ct)
    {
        var strict = _strictSchemaSupported;
        OpenRouterUsage? usage = null;
        try
        {
            var messages = new List<object>
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            };
            if (repairHint is not null)
            {
                messages.Add(new { role = "system", content = repairHint });
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = ModelId,
                    temperature = 0,
                    max_tokens = maxTokens,
                    response_format = strict ? schema : new { type = "json_object" },
                    messages = messages.ToArray(),
                }, options: JsonOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (strict && IsStrictSchemaRejection(response.StatusCode, body))
                {
                    _strictSchemaSupported = false;
                    logger.LogWarning(
                        "OpenRouter rejected strict json_schema for {Model} (HTTP {Status}); falling back to "
                        + "json_object for the remainder of this process. Field validation now rests on the parser.",
                        ModelId, (int)response.StatusCode);
                    return await ChatAsync(system, user, repairHint, maxTokens, schema, purpose, ct);
                }

                logger.LogWarning("Lens writer HTTP {Status} for purpose {Purpose}.", (int)response.StatusCode, purpose);
                return new ChatOutcome(null, null);
            }

            var dto = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, ct);
            usage = dto?.Usage;
            var choice = dto?.Choices?.FirstOrDefault();

            // A "length" finish means the object was cut at a token boundary. Even where the
            // fragment parses, its trailing fields are missing rather than answered.
            if (string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Lens writer completion hit the token limit for purpose {Purpose}.", purpose);
                return new ChatOutcome(null, usage);
            }

            return new ChatOutcome(choice?.Message?.Content, usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Lens writer call failed for purpose {Purpose}.", purpose);
            return new ChatOutcome(null, usage);
        }
        finally
        {
            // Recorded in a finally, AFTER the response: the counts live in the response body. Every
            // HTTP attempt is counted, including one that fails before a body exists — which then
            // reads as "unknown tokens" rather than "free".
            usageRecorder.Record(
                ApiProvider.OpenRouter, purpose, ModelId, accountContext?.AccountId,
                usage?.PromptTokens, usage?.CompletionTokens);
        }
    }

    private static bool IsStrictSchemaRejection(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
            or HttpStatusCode.NotImplemented or HttpStatusCode.NotFound))
        {
            return false;
        }

        return body.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
            || body.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || body.Contains("structured output", StringComparison.OrdinalIgnoreCase);
    }

    // ── Schemas ────────────────────────────────────────────────────────────────────────────

    internal static object NarrativeSchema() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "taste_narrative",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "sentences" },
                properties = new Dictionary<string, object>
                {
                    ["sentences"] = new
                    {
                        type = "array",
                        minItems = MinSentences,
                        maxItems = MaxSentences,
                        items = new { type = "string" },
                    },
                },
            },
        },
    };

    internal static object LensSchema() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "title_lens",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                // OpenAI-style strict mode requires EVERY property to be listed as required, so an
                // optional field is expressed as a nullable type rather than an absent key.
                required = new[] { "lens", "hook", "caution", "anchors", "mood", "whenToWatch" },
                properties = new Dictionary<string, object>
                {
                    ["lens"] = new { type = "string", maxLength = LensNote.RequestLensChars },
                    ["hook"] = new { type = "string", maxLength = LensNote.RequestHookChars },
                    ["caution"] = new { type = new[] { "string", "null" }, maxLength = LensNote.RequestCautionChars },
                    ["anchors"] = new
                    {
                        type = "array",
                        maxItems = LensNote.MaxAnchors,
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            required = new[] { "title", "relation" },
                            properties = new Dictionary<string, object>
                            {
                                ["title"] = new { type = "string" },
                                ["relation"] = new { type = "string" },
                            },
                        },
                    },
                    ["mood"] = new { type = "string", @enum = LensVocabulary.Moods },
                    ["whenToWatch"] = new { type = "string", @enum = LensVocabulary.WhenToWatch },
                },
            },
        },
    };

    internal static object AuditSchema() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "lens_audit",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                // Every property is listed in `required` because strict json_schema demands it; a
                // field that may legitimately be absent is expressed as a nullable TYPE instead.
                required = new[] { "specificity", "grounding", "spoilerFreeness", "voice", "povSlips", "comment" },
                properties = new Dictionary<string, object>
                {
                    ["specificity"] = new { type = "number", minimum = 0, maximum = 1 },
                    ["grounding"] = new { type = "number", minimum = 0, maximum = 1 },
                    ["spoilerFreeness"] = new { type = "number", minimum = 0, maximum = 1 },
                    ["voice"] = new { type = "number", minimum = 0, maximum = 1 },
                    ["povSlips"] = new { type = new[] { "string", "null" } },
                    ["comment"] = new { type = new[] { "string", "null" } },
                },
            },
        },
    };

    // ── Prompts ────────────────────────────────────────────────────────────────────────────

    private static readonly string NarrativeSystemPrompt =
        "You write a short, precise description of ONE person's film and television taste, addressed "
        + "directly to them as \"you\".\n"
        + "Return ONLY {\"sentences\": [...]} with between " + MinSentences + " and " + MaxSentences
        + " complete sentences.\n"
        + "HARD RULES:\n"
        + "  1. Use ONLY the facts given below. You may not name a single film, show or person that "
        + "does not appear in them. If you are tempted to add an example, do not.\n"
        + "  2. Second person throughout. Never \"they\", \"their\", \"them\", or \"the user\".\n"
        + "  3. No numbers, scores, percentages or counts in the prose. Say \"you rate crime higher "
        + "than almost anything\", never \"you rate crime 8.4\".\n"
        + "  4. Describe the person, not the data. \"You reach for something bleak and slow before "
        + "you reach for something loud\" — not \"your darkness attribute is high\".\n"
        + "  5. No marketing voice, no flattery, no \"you have great taste\". Say what is true, "
        + "including the awkward parts: what you avoid, where you disagree with the crowd, what you "
        + "have gone off lately.\n"
        + "  6. Each sentence carries its own idea. Do not open two sentences the same way.\n"
        + "  7. OPEN WITH VOLUME. The first sentence describes what they actually watch most — the "
        + "\"Mostly watches\" line — and how they rate it. What a person reaches for again and again "
        + "IS their taste; a high average over a handful of titles is not.\n"
        + "  8. A genre on the \"Watches rarely but rates well\" line may ONLY be described as "
        + "something they rate well WHEN they do watch it. Never call it a favourite, never let it "
        + "into the opening sentence, and never imply they seek it out.\n"
        + "  9. Name two or three of the recently loved titles, as recent. That is the sentence that "
        + "makes a profile recognisable rather than merely accurate.\n"
        + "  10. Never assert a preference the facts do not state. If the facts do not say it, you "
        + "do not know it — say less instead.\n"
        + "Output strictly valid JSON.";

    /// <summary>
    /// The lens brief, generation 4. Exposed to the golden-prompt tests because a prompt is the
    /// least reviewable artefact in the product: a rule that silently stops being stated degrades
    /// every note by a degree nobody attributes to the edit that caused it.
    /// </summary>
    internal static readonly string LensSystemPrompt =
        "You retell ONE film or show to ONE specific viewer, in their own taste vocabulary, speaking "
        + "to them as \"you\".\n"
        + "Return ONLY a JSON object with exactly these six keys:\n"
        + "  lens        <=" + LensNote.RequestLensChars + " chars. The premise, told the way THIS viewer "
        + "would want it told. Cite at least two specifics of the title and at least one thing about "
        + "the viewer.\n"
        + "  hook        <=" + LensNote.RequestHookChars + " chars. The single reason to press play tonight, "
        + "said TO the viewer and never as a tagline about the film. See rule 4.\n"
        + "  caution     <=" + LensNote.RequestCautionChars + " chars, or null. The honest reservation — the "
        + "thing about this title that might genuinely not land for this viewer. This is the one field "
        + "allowed to argue against the recommendation, and a real reservation is worth more than a "
        + "polite one. Use null only when you genuinely have none.\n"
        + "  anchors     up to 3 objects {title, relation}. Every title MUST be copied verbatim "
        + "from the ANCHORS list below — the exact characters, no year, no article added or removed. "
        + "relation is one clause saying how this title sits against it, and it must match the "
        + "viewer's stated verdict on that anchor. An empty array is a correct answer; an anchor "
        + "that is not on the list is not.\n"
        + "  mood        EXACTLY ONE of: " + string.Join(", ", LensVocabulary.Moods) + ".\n"
        + "  whenToWatch EXACTLY ONE of: " + string.Join(", ", LensVocabulary.WhenToWatch) + ".\n"
        + "HARD RULES:\n"
        + "  1. SPOILERS — THE PREMISE IS NOT A SPOILER. The setup as printed in the official "
        + "overview below is what the title is about, and restating it is exactly your job; a pitch "
        + "that will not say what the film is about is not safe, it is useless. What you may never "
        + "do is say how that premise DEVELOPS. No ending, no finale, no twist, no reveal, no death, "
        + "no late turn, nothing that happens after the setup, and no consequence of it. Every plot "
        + "fact must come from the overview and keywords given below and nothing else — if the "
        + "overview does not say it, you do not know it. Write \"sets up\", never \"reveals\".\n"
        + "  2. Never name a person who is not listed in this title's credits below, and never "
        + "name a film or show anywhere in the prose that is not this title or an entry on the "
        + "ANCHORS list.\n"
        + "  3. POINT OF VIEW. \"You\" is ALWAYS the viewer, sitting down to watch. \"You\" is never a "
        + "character, and nothing in the plot ever happens to \"you\". Characters stay in the third "
        + "person and keep their own pronouns: write \"the city that failed her\", never \"the city "
        + "that failed you\"; \"the men hunting him\", never \"the men hunting you\". Speak to the "
        + "viewer ABOUT the film; speak about the film THROUGH its characters.\n"
        + "  4. THE HOOK IS SAID TO THE VIEWER. It is second person, or an imperative aimed straight "
        + "at them — \"You get ...\", \"Expect ...\", \"Give this an evening if ...\". A third-person "
        + "tagline is refused outright: \"Jodie Foster turns grief into a tight revenge chase\" is a "
        + "poster line, not a hook.\n"
        + "  5. NO MARKETING VOICE. These are banned outright, in lens and hook alike: gripping, "
        + "must-see, unmissable, masterful, stunning, thrilling ride, edge of your seat, tour de "
        + "force, unforgettable. Plain, specific language only — name what is actually on screen, "
        + "the register, the pace, the pressure it keeps, rather than asserting how good it is. Do "
        + "not overstate the pacing. Every clause must be true of THIS title and no other.\n"
        + "  6. Never call the viewer \"they\", \"their\", \"them\" or \"the user\" — and avoid those "
        + "three pronouns entirely, even for characters. Name the character, or use he or she.\n"
        + "  7. NO NUMBERS ANYWHERE IN THE PROSE. A predicted rating may be given to you as context "
        + "so you know how confidently to pitch — never restate it, never allude to a score, a "
        + "percentage or a count.\n"
        + "  8. FINISH EVERY SENTENCE INSIDE ITS LIMIT. The limits above are hard: a field that "
        + "reaches one is cut off exactly there, mid-word if that is where it lands. Plan the last "
        + "clause and close it with a full stop. Say less rather than stopping short.\n"
        + "Output strictly valid JSON.";

    /// <summary>
    /// The judge's rubric, corrected in the same pass that shipped prompt v4 — and the correction is
    /// the reason a v3→v4 mean is readable at all.
    ///
    /// <para><b>spoilerFreeness was measuring the wrong thing.</b> v3's rubric said "first-act
    /// premise only" and the judge read the premise itself as the spoiler, marking notes down for
    /// "revealing the childhood-memory premise" — a premise printed on the poster. The rubric now
    /// states what the product actually holds: the official overview's setup is fair game, and only
    /// what comes AFTER it is a spoiler. A note that restates the overview and stops now scores 1.0
    /// where it used to score around 0.6.</para>
    ///
    /// <para><b><c>povSlips</c> is a note, not a score</b>, deliberately. The slip it catches — "the
    /// city that failed you", of a city that failed <i>her</i> — is rare enough that folding it into
    /// <c>voice</c> would move a mean by noise, and specific enough that the exact phrase is worth
    /// more than any number. It is also the one defect a deterministic guard could not be built for
    /// (see <c>LensGuards</c>), so this is where it gets caught.</para>
    /// </summary>
    private static readonly string AuditSystemPrompt =
        "You are auditing a piece of personalised film copy against a fixed rubric. Score each "
        + "dimension from 0.0 to 1.0 and return ONLY "
        + "{\"specificity\":n,\"grounding\":n,\"spoilerFreeness\":n,\"voice\":n,\"povSlips\":\"...\","
        + "\"comment\":\"...\"}.\n"
        + "  specificity     1.0 = every clause is true of this title and no other; 0.0 = generic filler.\n"
        + "  grounding       1.0 = every plot fact traces to the overview and every name to the credits; "
        + "0.0 = invented.\n"
        + "  spoilerFreeness THE OFFICIAL PREMISE IS NOT A SPOILER. The setup as printed in the "
        + "title's overview — who the characters are, what situation the story starts in, what the "
        + "first act sets up — is what the copy is FOR, and restating it must never cost a point, "
        + "however central it is to the film. 1.0 = says nothing the overview does not; 0.0 = names "
        + "an ending, a finale, a twist, a reveal, a death, or any way the premise later develops. "
        + "Score only what is past the setup.\n"
        + "  voice           1.0 = spoken TO the viewer in second person (the hook included, as \"You "
        + "get ...\" or an imperative), plain and specific, honest, no marketing adjectives and no "
        + "numbers; 0.0 = a third-person tagline, marketing register, flattery, or a quoted score.\n"
        + "povSlips names every place the copy makes the VIEWER a character in the plot — \"the city "
        + "that failed you\" where the city failed HER, \"the men hunting you\" where they hunt HIM. "
        + "Quote the offending phrase; null when there is none. \"You\" addressing the viewer as a "
        + "viewer is correct and is never a slip.\n"
        + "comment is one short sentence naming the worst problem, or null when there is none. "
        + "Be harsh; a lenient audit is worthless. Output strictly valid JSON.";

    internal static string NarrativePrompt(TasteFacts facts)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("FACTS ABOUT THIS VIEWER — use only these.\n");
        sb.Append("Ratings on record: ").Append(facts.RatingsCount.ToString(c))
            .Append(", averaging ").Append(facts.Affinity(facts.UserMean)).Append(" out of ten.\n");
        sb.Append("Split between films and series: ")
            .Append((facts.MovieRatedShare * 100m).ToString("0", c)).Append("% films.\n");

        // VOLUME FIRST, and it is first in the prompt for the same reason rule 7 puts it first in
        // the prose: the previous ordering opened on affinity alone, and an account that had rated
        // twenty-six holiday films among two thousand was told holiday sat "especially high".
        if (facts.MostWatchedGenres.Count > 0)
        {
            sb.Append("Mostly watches (by volume — this is what their taste actually is): ");
            sb.AppendJoin(" · ", facts.MostWatchedGenres.Select(g => string.Format(
                c,
                "{0}, {1} titles ({2:0}% of everything rated), rated {3:0.0}",
                g.Genre,
                g.Count,
                g.Share * 100m,
                g.Affinity)));
            sb.Append('\n');
        }

        if (facts.TopGenres.Count > 0)
        {
            sb.Append("Rates highest, among genres rated often enough to mean something: ");
            sb.AppendJoin(" · ", facts.TopGenres.Select(g =>
                string.Format(c, "{0} {1:0.0} over {2} titles", g.Genre, g.Affinity, g.Count)));
            sb.Append('\n');
        }

        // Named separately and labelled, because the model cannot infer "rarely" from a count it
        // has been told not to restate. Rule 8 is enforceable only because this line exists.
        if (facts.SparseButWellRated() is { Count: > 0 } sparse)
        {
            sb.Append("Watches rarely but rates well — say only \"when you do watch X you rate it "
                + "well\", never that they favour it: ");
            sb.AppendJoin(" · ", sparse.Select(g =>
                string.Format(c, "{0} ({1} titles)", g.Genre, g.Count)));
            sb.Append('\n');
        }

        if (facts.DriftingToward.Count > 0)
        {
            sb.Append("Moving toward lately: ");
            sb.AppendJoin(" · ", facts.DriftingToward.Select(d =>
                string.Format(c, "{0} ({1:+0.0;-0.0} against their all-time)", d.Genre, d.Delta)));
            sb.Append('\n');
        }

        if (facts.LovedAnchors.Count > 0)
        {
            sb.Append("Loves: ");
            sb.AppendJoin(", ", facts.LovedAnchors.Select(l =>
                l.Year is { } y ? string.Format(c, "{0} ({1})", l.Name, y) : l.Name));
            sb.Append('\n');
        }

        if (facts.RecentLoves.Count > 0)
        {
            sb.Append("Loved lately (name two or three of these): ");
            sb.AppendJoin(", ", facts.RecentLoves.Select(l =>
                l.Year is { } ry ? string.Format(c, "{0} ({1})", l.Name, ry) : l.Name));
            sb.Append('\n');
        }

        if (facts.RecentDislikes.Count > 0)
        {
            sb.Append("Rated badly lately: ");
            sb.AppendJoin(", ", facts.RecentDislikes.Select(l =>
                l.Year is { } dy ? string.Format(c, "{0} ({1})", l.Name, dy) : l.Name));
            sb.Append('\n');
        }

        if (facts.DeclinedLine is { Length: > 0 } declined)
        {
            sb.Append(declined).Append('\n');
        }

        if (facts.VerifiedPeople.Count > 0)
        {
            sb.Append("Craft they rate (verified over at least two titles each): ");
            sb.AppendJoin(" · ", facts.VerifiedPeople.Select(p =>
                string.Format(c, "{0} as {1}, {2:0.0} over {3}", p.Name, p.Role.ToLowerInvariant(), p.Average, p.TitleCount)));
            sb.Append('\n');
        }

        if (facts.Texture is { } t)
        {
            sb.Append(string.Format(
                c,
                "Texture of what they love (0-1 each, over {7} titles): darkness {0:0.00} · pacing {1:0.00} · "
                + "complexity {2:0.00} · emotional intensity {3:0.00} · humour {4:0.00} · optimism {5:0.00} · "
                + "ensemble-vs-lead {6:0.00}\n",
                t.Darkness, t.Pacing, t.Complexity, t.EmotionalIntensity, t.Humor, t.Optimism, t.EnsembleVsSolo,
                t.SampleSize));
        }

        sb.Append(string.Format(
            c, "Against the crowd overall: {0:+0.00;-0.00} rating points.\n", facts.Contrarian.Overall));
        if (facts.Contrarian.ByGenre.Count > 0)
        {
            sb.Append("Against the crowd by genre: ");
            sb.AppendJoin(" · ", facts.Contrarian.ByGenre.Select(o =>
                string.Format(c, "{0} {1:+0.00;-0.00}", o.Genre, o.Offset)));
            sb.Append('\n');
        }

        if (facts.PreferenceTags.Count > 0)
        {
            sb.Append("In their own words, things they are into: ")
                .Append(string.Join(", ", facts.PreferenceTags)).Append('\n');
        }

        return sb.ToString();
    }

    internal static string LensPrompt(LensTitleInput title, LensPersona persona)
    {
        var sb = new StringBuilder();
        sb.Append("THE VIEWER\n").Append(persona.Taste).Append('\n');
        if (persona.Declined is { Length: > 0 } declined)
        {
            sb.Append(declined).Append('\n');
        }

        sb.Append(AnchorBlock(persona.AnchorCandidates));
        sb.Append("\nTHIS TITLE\n").Append(TitleBlock(title));
        return sb.ToString();
    }

    /// <summary>
    /// The titles this note may anchor against, by name, year and the viewer's own verdict.
    ///
    /// <para><b>This block is the fix for a hundred-percent rejection rate.</b> The system prompt
    /// has always instructed the model to take anchor titles "verbatim from the lists below", and
    /// before this there were no lists — the only title names anywhere in a lens prompt were the
    /// ones inside the declined line. The model anchored on those, because they were the only
    /// names it had, and the guard refused every note for it. Naming the permitted anchors is the
    /// policy; the guard downstream is the backstop.</para>
    ///
    /// <para>The verdict rides each line because a relation clause has to know it. "You said this
    /// was not for you" and "you rated this as highly as anything" are both true anchors and the
    /// model cannot tell which applies from a bare name.</para>
    /// </summary>
    private static string AnchorBlock(IReadOnlyList<LensAnchorCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("\nANCHORS YOU MAY USE — these titles and NO OTHERS. Copy a name EXACTLY as written\n");
        sb.Append("here, with no year and no re-wording. If none of them genuinely relates to this\n");
        sb.Append("title, return an empty anchors array; an omitted anchor costs nothing and an\n");
        sb.Append("invented one is a memory the viewer never had.\n");
        sb.Append("The scores below are CONTEXT so you know how warmly to compare — like the\n");
        sb.Append("prediction, never restate one and never allude to it.\n");
        foreach (var candidate in candidates)
        {
            sb.Append("  ").Append(candidate.Name);
            if (candidate.Year is { } year)
            {
                sb.Append(" (").Append(year.ToString(c)).Append(')');
            }

            sb.Append(" — ").Append(candidate.Stance switch
            {
                AnchorStance.Loved => string.Format(c, "you rated it {0} out of ten", candidate.Rating ?? 0),
                AnchorStance.Disliked => string.Format(c, "you rated it only {0} out of ten", candidate.Rating ?? 0),
                _ => "you declined it from your feed without rating it",
            });
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string TitleBlock(LensTitleInput t)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(t.Name);
        if (t.Year is { } year)
        {
            sb.Append(" (").Append(year.ToString(c)).Append(')');
        }

        sb.Append(" · ").Append(t.MediaType.ToLowerInvariant());
        if (t.RuntimeMinutes is { } runtime)
        {
            sb.Append(" · ").Append(runtime.ToString(c)).Append(" min");
        }

        if (t.Genres.Count > 0)
        {
            sb.Append(" · ").Append(string.Join(", ", t.Genres));
        }

        sb.Append('\n');

        if (t.Tone is { Length: > 0 } || t.Era is { Length: > 0 } || t.Themes.Count > 0)
        {
            sb.Append("Tone: ").Append(t.Tone ?? "unknown")
                .Append(" · Era: ").Append(t.Era ?? "unknown");
            if (t.Themes.Count > 0)
            {
                sb.Append(" · Themes: ").Append(string.Join(", ", t.Themes));
            }

            sb.Append('\n');
        }

        // The seven scalars the catalogue was enriched with. This — and the tone/era/themes line
        // above — is where that extraction spend finally reaches something a person reads.
        if (t.Darkness is not null)
        {
            sb.Append(string.Format(
                c,
                "Texture (0-1): darkness {0:0.00} · pacing {1:0.00} · complexity {2:0.00} · emotional intensity "
                + "{3:0.00} · humour {4:0.00} · optimism {5:0.00} · ensemble-vs-lead {6:0.00}\n",
                t.Darkness, t.Pacing, t.Complexity, t.EmotionalIntensity, t.Humor, t.Optimism, t.EnsembleVsSolo));
        }

        var credits = new List<string>();
        if (t.Director is { Length: > 0 } director)
        {
            credits.Add("Director " + director);
        }

        if (t.Writer is { Length: > 0 } writer)
        {
            credits.Add("Writer " + writer);
        }

        if (t.TopCast.Count > 0)
        {
            credits.Add("Cast " + string.Join(", ", t.TopCast.Take(MaxCast)));
        }

        if (credits.Count > 0)
        {
            sb.Append(string.Join(" · ", credits)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(t.Overview))
        {
            var overview = t.Overview!.Length > MaxOverviewChars ? t.Overview[..MaxOverviewChars] + "…" : t.Overview;
            sb.Append("Overview: ").Append(overview).Append('\n');
        }

        if (t.Keywords.Count > 0)
        {
            sb.Append("Keywords: ").Append(string.Join(", ", t.Keywords.Take(MaxKeywords))).Append('\n');
        }

        if (t.Predicted is { } predicted)
        {
            // CONTEXT ONLY, and the prompt says so twice. Invariant culture: this machine renders
            // 8.4 as "8,4", and a decimal comma in a comma-separated line is a different number.
            sb.Append(string.Format(
                c,
                "CONTEXT, DO NOT RESTATE: the recommender predicts {0:0.0} out of ten for this viewer",
                predicted));
            if (t.PredictionError is { } error)
            {
                sb.Append(string.Format(c, ", typically wrong by about {0:0.0}", error));
            }

            sb.Append(". Pitch accordingly and never mention the number.\n");
        }

        return sb.ToString();
    }

    // ── Deterministic stubs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stand-in narrative built entirely from the supplied facts — so it passes the same
    /// grounding check the live path is held to, and a golden-prompt test exercises the whole
    /// pipeline rather than a shortcut around it. Exactly <see cref="MinSentences"/> sentences.
    /// </summary>
    internal static IReadOnlyList<string> StubNarrative(TasteFacts facts)
    {
        var c = CultureInfo.InvariantCulture;

        // Volume first, exactly as rule 7 requires of the live model — a stub that opens on
        // affinity would let the ordering defect back in through every test that uses it.
        var watched = facts.MostWatchedGenres.Take(3).Select(g => g.Genre).ToList();
        var sparse = facts.SparseButWellRated().Take(2).Select(g => g.Genre).ToList();
        var loved = facts.LovedAnchors.Take(2).Select(l => l.Name).ToList();
        var recent = facts.RecentLoves.Take(2).Select(l => l.Name).ToList();
        var people = facts.VerifiedPeople.Take(2).Select(p => p.Name).ToList();

        var sentences = new List<string>
        {
            watched.Count > 0
                ? $"You watch {string.Join(" and ", watched)} more than anything else, and you rate them steadily."
                : "You have not rated enough yet for a clear pattern to show.",
            loved.Count > 0
                ? $"When something lands for you it tends to look like {string.Join(" or ", loved)}."
                : "Nothing has landed hard enough yet to anchor the rest.",
            facts.Texture is { } t && t.Darkness > 0.5m
                ? "You are comfortable sitting with something bleak."
                : "You lean away from the bleakest end of the shelf.",
            facts.Texture is { Complexity: > 0.5m }
                ? "You would rather be made to work than be carried."
                : "You do not need a film to be difficult to be worth your evening.",
            facts.Contrarian.Overall > 0m
                ? "You are kinder than the crowd about the things you choose."
                : "You are harder on what you watch than most people are.",
            people.Count > 0
                ? $"You have a real track record with {string.Join(" and ", people)}."
                : "No single name has earned your trust yet.",
        };

        if (facts.MovieRatedShare > 0.6m)
        {
            sentences.Add("You rate films far more often than series.");
        }
        else if (facts.MovieRatedShare < 0.4m)
        {
            sentences.Add("You give series more of your attention than films.");
        }
        else
        {
            sentences.Add("You split your attention fairly evenly between films and series.");
        }

        if (recent.Count > 0)
        {
            sentences.Add($"Lately {string.Join(" and ", recent)} landed for you.");
        }

        if (sparse.Count > 0)
        {
            // Phrased as the qualified claim the prompt's rule 8 permits, and never as a favourite.
            sentences.Add($"When you do watch {string.Join(" or ", sparse)}, you rate it well.");
        }

        if (facts.DriftingToward.Count > 0)
        {
            sentences.Add(string.Format(
                c, "Lately you have been drifting toward {0}.", facts.DriftingToward[0].Genre));
        }

        return sentences.Take(MaxSentences).ToList();
    }

    /// <summary>
    /// The deterministic lens. Hashes the title's identity to a stable byte stream, then picks its
    /// mood and occasion from the closed vocabularies — so the same title always yields the same
    /// note and the whole guard/persistence/render chain is exercised without spend. Its prose is
    /// second-person, spoiler-free and number-free by construction, because a stub that trips the
    /// product's own guards would make every test a test of the stub.
    /// </summary>
    internal static LensNote StubLens(LensTitleInput t, LensPersona persona)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes($"{t.Name}|{t.Year}|{t.MediaType}|{persona.Taste.Length}"));
        var mood = LensVocabulary.Moods[seed[0] % LensVocabulary.Moods.Length];
        var when = LensVocabulary.WhenToWatch[seed[1] % LensVocabulary.WhenToWatch.Length];
        var genre = t.Genres.Count > 0 ? t.Genres[0] : "this kind of thing";
        var tone = t.Tone is { Length: > 0 } ? t.Tone : mood;

        var lens = $"A {tone} {genre} piece, and it sits close to the register you keep coming back to. "
            + $"You get the {(t.MediaType.Equals("Show", StringComparison.OrdinalIgnoreCase) ? "series" : "film")} "
            + "on its own terms rather than a version sanded down for everyone.";
        var hook = $"The {tone} {genre} you have been circling.";
        var caution = seed[2] % 3 == 0 ? null : $"It takes its time before it commits to being {tone}.";

        // Anchors are drawn from the candidates the persona actually offers, which is what the live
        // path is now instructed to do. A stub that returned no anchors left the entire anchor
        // chain — prompt, verifier, strip, persist — unexercised by every test in the suite, and
        // that is precisely the chain that failed in production.
        var anchors = persona.AnchorCandidates
            .Take(LensNote.MaxAnchors)
            .Select(a => new LensAnchor(a.Name, a.Stance switch
            {
                AnchorStance.Loved => "sits in the same register as this, and you went for it",
                AnchorStance.Disliked => "shares a lane with this, and it did not land for you",
                _ => "you passed on this one, and this is a gentler version of the same idea",
            }))
            .ToList();

        return new LensNote(
            lens.Length > LensNote.MaxLensChars ? lens[..LensNote.MaxLensChars] : lens,
            hook.Length > LensNote.MaxHookChars ? hook[..LensNote.MaxHookChars] : hook,
            caution,
            anchors,
            mood,
            when);
    }

    private sealed record ChatResponse(ChatChoice[]? Choices, OpenRouterUsage? Usage);

    private sealed record ChatChoice(
        ChatMessage? Message, [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatMessage(string? Content);
}

/// <summary>Small helpers that keep the invariant-culture formatting off the prompt-building line.</summary>
internal static class TasteFactsFormatting
{
    /// <summary>One rating value, one decimal place, invariant.</summary>
    public static string Affinity(this TasteFacts _, decimal value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);
}
