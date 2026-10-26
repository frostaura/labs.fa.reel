using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using LlmCallMetrics = FrostAura.Reel.Infrastructure.Llm.LlmCallMetrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Extracts taste attributes via OpenRouter chat completions (model from OPENROUTER_MODEL,
/// founder-locked <c>openai/gpt-6-luna</c>). Mirrors fa.startup's OpenRouter conventions.
///
/// Two modes, picked at availability time:
///  • <b>live</b> — OPENROUTER_API_KEY present: one structured-JSON request per title.
///  • <b>stub</b> — no key, or TestHelpers:ModelStubMode=true: deterministic values derived
///    from the title text. Same input → same output, so tests and keyless dev are stable and
///    the rest of the pipeline (train, feed) exercises the real code path without spend.
///
/// <b>Never fabricate a scalar.</b> These seven numbers land in the feature vector and are
/// cached forever across every tenant, so a plausible-looking substitute for a field the model
/// did not actually answer is indistinguishable from a real judgement and permanently poisons
/// that title. A response missing a required scalar, carrying it as null / a string / a number
/// outside 0–1, or truncated mid-object, is a FAILED extraction: this adapter returns null and
/// the caller leaves the row un-Done so it can be retried. The old behaviour — substituting
/// 0.5 and letting the row be marked Done — is the exact bug this design exists to prevent.
/// </summary>
public class OpenRouterAttributeExtractor(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterAttributeExtractor> logger,
    // Optional by design: the typed HttpClient is transient and is constructed inside a scope in
    // every real code path, but the adapter must still be `new`-able in tests without a scope.
    IAccountContext? accountContext = null,
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null,
    // The per-purpose call counters (task 8); a DI singleton in production, none in a hand-built adapter.
    LlmCallMetrics? metrics = null)
    : ITitleAttributeExtractor, IClassifyingAttributeExtractor
{
    /// <summary>
    /// The shared transport — and for extraction a TRANSPORT-ONLY move (WS-1 task 2, FD-1 default).
    /// The purpose's options send no reasoning parameter, no transient retry and no 2× length retry;
    /// every request body is byte-identical to the pre-migration adapter's (pinned by
    /// <c>ExtractionRequestPinTests</c>), and the cache key — <see cref="ModelId"/> and
    /// <see cref="Version"/> — does not move, so nothing is re-extracted. What it gains is
    /// transport: a dropped body after a 200 is retried once, a 401/402/403 opens the provider
    /// breaker, and the per-purpose timeouts replace the 10-second resilience defaults.
    /// </summary>
    private readonly OpenRouterChatClient _chat = new(
        httpClient, usageRecorder, configuration, logger, accountContext, purposes, capabilities, health, gate, metrics);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The seven scalars, in the order the schema and the prompt declare them. Every one is
    /// required: a partial answer is a failed answer.
    /// </summary>
    private static readonly string[] RequiredScalars =
    [
        "darkness", "pacing", "complexity", "emotionalIntensity", "humor", "optimism", "ensembleVsSolo",
    ];

    /// <summary>
    /// The closed tone/era vocabularies now live in <see cref="Domain.Catalog.AttributeVocabulary"/>
    /// so the why-line templater can verify a stored Tone against the same list this extractor
    /// enforces on the provider. These aliases keep the extractor reading as before.
    /// </summary>
    private static readonly string[] ToneVocabulary = Domain.Catalog.AttributeVocabulary.Tones;

    private static readonly string[] EraVocabulary = Domain.Catalog.AttributeVocabulary.Eras;

    /// <summary>A scalar may overshoot the bounds by this much (float formatting) before it reads as a scale error.</summary>
    private const decimal RangeTolerance = 0.005m;

    private const int MaxThemes = 6;

    private const int MaxThemeChars = 60;

    /// <summary>TMDB overviews are normally well under this; the cap only bites on pathological rows.</summary>
    private const int MaxOverviewChars = 1200;

    private const int MaxCompactOverviewChars = 300;

    private const int MaxKeywords = 20;

    private const int MaxCompactKeywords = 8;

    /// <summary>Generous for a ~150-token object, so a normal answer can never hit the ceiling.</summary>
    private const int ResponseTokenBudget = 500;

    private const int CompactResponseTokenBudget = 900;

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    private bool StubMode => ApiKey is null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public bool IsAvailable => ApiKey is not null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    // The catalog's extraction rule is this property's old body verbatim (a blank OPENROUTER_MODEL
    // is used as-is) and takes no per-purpose override: the model is half the extraction key.
    public string ModelId => StubMode
        ? "stub/deterministic-v1"
        : _chat.Options(ApiUsagePurpose.ExtractAttributes).Model;

    /// <summary>
    /// The prompt generation stamped on every row this adapter writes. Bump it whenever a change
    /// here alters what a stored value MEANS, never for a change that only alters how the answer
    /// is obtained (a retry strategy, a token budget, a transport fix).
    /// <para>
    /// <b>v1 → v2 (2026-09-09).</b> The 2026-08-07 prompt closed the Tone and Era vocabularies and
    /// began enforcing them through <c>json_schema</c>. Rows written before that carry free-text
    /// labels: 2,312 pre-vocabulary rows hold <b>215 distinct Tone</b> and <b>97 distinct Era</b>
    /// values against vocabularies of 16 and 13. Those are not merely untidy — the why-line
    /// templater verifies a stored Tone against the closed list and silently drops anything else,
    /// so a pre-vocabulary row reads as "no tone" everywhere downstream while looking Done in the
    /// table. Only re-extraction fixes them, and until this constant existed nothing could ask
    /// for it: the handler hard-coded <c>ExtractorVersion = 1</c> and no sweep ever read it back.
    /// </para>
    /// </summary>
    public const int CurrentExtractorVersion = 2;

    public int Version => CurrentExtractorVersion;

    public async Task<IReadOnlyList<ExtractedTitleAttributes?>> ExtractAsync(
        IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
    {
        var outcomes = await ExtractOutcomesAsync(inputs, ct);
        return outcomes.Select(o => o.Attributes).ToArray();
    }

    /// <summary>
    /// The same requests as <see cref="ExtractAsync"/>, with the reason for every missing answer:
    /// <see cref="AttributeFailureClass.Transport"/> when no request for the title produced a
    /// completion (a dropped connection, a timeout, a 408/429/5xx, a refused key, an open breaker or
    /// a spent allowance), <see cref="AttributeFailureClass.Parse"/> when a completion came back
    /// unusable or the provider rejected the request itself. When EVERY title in the batch failed in
    /// transport it throws <see cref="AttributeExtractionTransportException"/>, so the enrich run
    /// stops instead of spending its next batch against the same outage (E-6, R1-47).
    /// </summary>
    public async Task<IReadOnlyList<AttributeExtractionOutcome>> ExtractClassifiedAsync(
        IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default)
    {
        var outcomes = await ExtractOutcomesAsync(inputs, ct);
        if (outcomes.Count > 0 && outcomes.All(o => o.Failure == AttributeFailureClass.Transport))
        {
            throw new AttributeExtractionTransportException(
                $"All {outcomes.Count} attribute extraction request(s) in the batch failed before producing a completion.");
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<AttributeExtractionOutcome>> ExtractOutcomesAsync(
        IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        if (StubMode)
        {
            return inputs.Select(i => AttributeExtractionOutcome.Success(Stub(i))).ToArray();
        }

        // Bounded concurrency (OPENROUTER_MAX_CONCURRENCY, default 4) — one title per request,
        // results written back in index order so the batch stays row-aligned for the caller.
        var maxConcurrency = Math.Max(1, configuration.GetValue("OPENROUTER_MAX_CONCURRENCY", 4));
        var results = new AttributeExtractionOutcome[inputs.Count];
        using var gate = new SemaphoreSlim(maxConcurrency);
        var tasks = inputs.Select(async (input, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = await ExtractOneAsync(input, ct);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        return results;
    }

    /// <summary>
    /// One title. At most two billable requests, and the second one only ever fires when it is
    /// materially different from the first — see <see cref="Repair"/>.
    /// </summary>
    private async Task<AttributeExtractionOutcome> ExtractOneAsync(TitleAttributeInput input, CancellationToken ct)
    {
        var first = await AttemptAsync(input, repairHint: null, compact: false, ct);
        if (first.Attributes is { } parsed)
        {
            return AttributeExtractionOutcome.Success(parsed);
        }

        var repair = Repair(first);
        if (repair is null)
        {
            // Transport / empty-body / rejected-request failures: nothing about a second identical
            // request makes it likelier to succeed inside this call. A transport failure is retried
            // on a LATER job run, backed off by time and never charged; an empty answer or a
            // rejected request spends one attempt, because the attempt budget is the only bound on
            // spend for a title the provider keeps answering badly.
            return AttributeExtractionOutcome.Failed(
                first.Failure == ExtractionFailure.Transport ? AttributeFailureClass.Transport : AttributeFailureClass.Parse);
        }

        logger.LogWarning(
            "Attribute extraction for {Title} ({TitleId}) came back {Failure} [{Detail}]; retrying once with a corrected prompt.",
            input.Name, input.TitleId, first.Failure, string.Join("; ", first.Problems));

        var second = await AttemptAsync(input, repair.Hint, repair.Compact, ct);
        if (second.Attributes is { } repaired)
        {
            return AttributeExtractionOutcome.Success(repaired);
        }

        logger.LogWarning(
            "Attribute extraction for {Title} ({TitleId}) failed after the repair pass ({Failure}: {Detail}). "
            + "No attributes written — the row stays un-Done rather than being filled with neutral guesses.",
            input.Name, input.TitleId, second.Failure, string.Join("; ", second.Problems));

        // The first answer was a paid completion that could not be used, so this title is charged
        // even when the repair request then died in transport.
        return AttributeExtractionOutcome.Failed(AttributeFailureClass.Parse);
    }

    /// <summary>
    /// Decides whether a second request is worth its money, and what has to change for it to be.
    /// Returns null when nothing meaningful can vary — resending a byte-identical prompt at
    /// temperature 0 is spend with no incremental chance of success.
    /// </summary>
    private static RepairPlan? Repair(AttemptOutcome outcome) => outcome.Failure switch
    {
        // The response was cut off mid-object. Shrink the input and widen the output budget —
        // a genuinely different request, not the same one again.
        ExtractionFailure.Truncated => new RepairPlan(
            "Your previous answer was cut off before the JSON object closed. Answer again, "
            + "much more briefly: at most three themes, at most three words each.",
            Compact: true),

        // Syntactically fine, semantically wrong. Naming the offending keys back to the model is
        // the one edit that reliably changes the outcome at temperature 0.
        ExtractionFailure.Malformed => new RepairPlan(
            "Your previous answer was rejected because of these fields: "
            + string.Join("; ", outcome.Problems)
            + ". Answer again with EVERY key present, every scalar a bare JSON number between 0.0 and 1.0 "
            + "(not a string, not null, never on a 0-10 or 0-100 scale), and tone/era taken verbatim from "
            + "the allowed lists.",
            Compact: false),

        _ => null,
    };

    private async Task<AttemptOutcome> AttemptAsync(
        TitleAttributeInput input, string? repairHint, bool compact, CancellationToken ct)
    {
        List<LlmMessage> messages = [LlmMessage.System(SystemPrompt), LlmMessage.User(UserPrompt(input, compact))];
        if (repairHint is not null)
        {
            messages.Add(LlmMessage.System(repairHint));
        }

        // The schema form always; the client sends it while the model accepts strict mode and
        // falls back to json_object (byte-identical to this adapter's old fallback) after a
        // rejection. The ceiling is passed explicitly so no purpose setting can move it.
        var result = await _chat.CompleteAsync(
            new LlmChatRequest(
                ApiUsagePurpose.ExtractAttributes,
                messages,
                ResponseFormat(strict: true),
                MaxTokens: compact ? CompactResponseTokenBudget : ResponseTokenBudget),
            ct);

        // Empty is checked BEFORE length, exactly as before: an empty cut-off is an empty answer.
        var answered = result.Failure is null or LlmCallFailure.Truncated or LlmCallFailure.EmptyAnswer;
        if (answered && string.IsNullOrWhiteSpace(result.Content))
        {
            return AttemptOutcome.Failed(ExtractionFailure.Empty, ["empty completion"]);
        }

        if (result.Failure == LlmCallFailure.Truncated)
        {
            // A "length" finish means the object was cut at a token boundary. Even when the
            // fragment happens to parse, its trailing fields are missing rather than answered.
            return AttemptOutcome.Failed(ExtractionFailure.Truncated, ["completion hit the token limit"]);
        }

        if (result.Failure is { } failure)
        {
            logger.LogWarning(
                "Attribute extraction {Failure} (HTTP {Status}) for {Title} ({TitleId}).",
                failure, result.HttpStatus, input.Name, input.TitleId);
            return AttemptOutcome.Failed(
                failure == LlmCallFailure.BadRequest ? ExtractionFailure.Rejected : ExtractionFailure.Transport,
                [result.HttpStatus is { } status ? $"HTTP {status}" : failure.ToString()]);
        }

        var (attributes, problems) = Parse(result.Content!);
        return attributes is null
            ? AttemptOutcome.Failed(ExtractionFailure.Malformed, problems)
            : new AttemptOutcome(attributes, ExtractionFailure.None, problems);
    }

    /// <summary>
    /// Strict structured output. <c>json_object</c> only promises the bytes parse as JSON — it
    /// says nothing about which keys exist or what type they are, which is how a missing scalar
    /// ever reached the parser in the first place. This moves "required + numeric + 0–1 + closed
    /// tone/era vocabulary" to the provider, where it is enforced rather than requested.
    /// </summary>
    private static object ResponseFormat(bool strict)
    {
        if (!strict)
        {
            return new { type = "json_object" };
        }

        var properties = new Dictionary<string, object>();
        foreach (var scalar in RequiredScalars)
        {
            properties[scalar] = new { type = "number", minimum = 0, maximum = 1 };
        }

        properties["tone"] = new { type = "string", @enum = ToneVocabulary };
        properties["era"] = new { type = "string", @enum = EraVocabulary };
        properties["themes"] = new
        {
            type = "array",
            minItems = 1,
            maxItems = MaxThemes,
            items = new { type = "string" },
        };

        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "title_taste_attributes",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    // OpenAI-style strict mode requires EVERY property to be listed as required.
                    required = properties.Keys.ToArray(),
                    properties,
                },
            },
        };
    }

    /// <summary>
    /// Parses a response into attributes, or into the list of reasons it is not usable. Returns
    /// null attributes if ANY required scalar is absent, non-numeric, or off-scale — there is no
    /// neutral default, because a default is a fabricated judgement that gets cached forever.
    /// tone/era are deliberately softer: an out-of-vocabulary label is dropped to null (never
    /// stored, so the taxonomy stays closed) but does not on its own sink the row, since neither
    /// feeds the feature vector.
    /// </summary>
    private static (ExtractedTitleAttributes? Attributes, IReadOnlyList<string> Problems) Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
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

            var fatal = new List<string>();
            var notes = new List<string>();
            var values = new decimal[RequiredScalars.Length];
            for (var i = 0; i < RequiredScalars.Length; i++)
            {
                values[i] = Scalar(root, RequiredScalars[i], fatal);
            }

            var tone = Vocabulary(root, "tone", ToneVocabulary, notes);
            var era = Vocabulary(root, "era", EraVocabulary, notes);
            var themes = Themes(root);

            // Only scalar problems are fatal; the vocabulary notes ride along for the log.
            if (fatal.Count > 0)
            {
                return (null, [.. fatal, .. notes]);
            }

            return (
                new ExtractedTitleAttributes(
                    values[0], values[1], values[2], values[3], values[4], values[5], values[6],
                    tone, era, themes, json),
                notes);
        }
    }

    private static decimal Scalar(JsonElement root, string name, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var el))
        {
            problems.Add($"{name} was absent");
            return 0m;
        }

        if (el.ValueKind != JsonValueKind.Number)
        {
            // Covers null, true/false, and the string-typed number ("0.8") that a json_object
            // response is perfectly entitled to return.
            problems.Add($"{name} was {el.ValueKind}, expected a bare number");
            return 0m;
        }

        if (!el.TryGetDecimal(out var raw))
        {
            problems.Add($"{name} was a number that is not representable as a decimal");
            return 0m;
        }

        // The killer case: a model answering on a 0-10 or 0-100 scale. Clamping would pin every
        // such title to exactly 1.0 and look entirely normal downstream, so an off-scale value is
        // treated as a wrong answer rather than quietly squashed into range.
        if (raw < -RangeTolerance || raw > 1m + RangeTolerance)
        {
            problems.Add($"{name} was {raw}, outside the 0-1 scale (wrong scale?)");
            return 0m;
        }

        return Math.Clamp(raw, 0m, 1m);
    }

    private static string? Vocabulary(JsonElement root, string name, string[] vocabulary, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = el.GetString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!vocabulary.Contains(value, StringComparer.Ordinal))
        {
            // Dropped, not stored: an invented label would otherwise widen a closed taxonomy by
            // one value per title until the column means nothing.
            problems.Add($"[non-fatal] {name} \"{value}\" is not in the closed vocabulary; stored as null");
            return null;
        }

        return value;
    }

    private static string[] Themes(JsonElement root)
    {
        if (!root.TryGetProperty("themes", out var t) || t.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Themes are hashed into feature buckets, so normalize before they get there: a stray
        // "  Serial Killer " and a whole sentence should not each claim their own bucket.
        return t.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim().ToLowerInvariant())
            .Where(s => s.Length is > 0 and <= MaxThemeChars)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxThemes)
            .ToArray();
    }

    /// <summary>
    /// Deterministic stand-in. Hashes the title's identity to a stable byte stream, then maps
    /// bytes to each scalar — so "Toy Story" always extracts the same vector. Light genre nudges
    /// keep the values plausible (horror darker, comedy funnier) without pretending to be real
    /// LLM judgment; it's a fixture, and the RawJson says so. Its tone/era values are members of
    /// the same closed vocabularies the live path is held to.
    /// </summary>
    private static ExtractedTitleAttributes Stub(TitleAttributeInput input)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes($"{input.Name}|{input.Year}|{input.MediaType}"));
        decimal Unit(int i) => Math.Round(seed[i % seed.Length] / 255m, 3);
        decimal Nudge(decimal v, decimal delta) => Math.Clamp(Math.Round(v + delta, 3), 0m, 1m);

        var genres = input.Genres.Select(g => g.ToLowerInvariant()).ToHashSet();
        var darkness = Nudge(Unit(0), genres.Contains("horror") || genres.Contains("thriller") ? 0.3m : 0m);
        var humor = Nudge(Unit(4), genres.Contains("comedy") ? 0.3m : 0m);
        var optimism = Nudge(Unit(5), genres.Contains("horror") ? -0.25m : 0m);

        var tone = darkness > 0.6m ? "brooding" : humor > 0.6m ? "playful" : "balanced";
        var era = input.Year switch
        {
            null => "unknown",
            >= 2015 => "contemporary",
            >= 1990 => "modern",
            _ => "period",
        };
        var themes = input.Keywords.Take(4).Concat(input.Genres.Take(2)).Distinct().Take(5).ToArray();

        var payload = JsonSerializer.Serialize(new { stub = true, model = "stub/deterministic-v1" }, JsonOptions);
        return new ExtractedTitleAttributes(
            darkness, Unit(1), Unit(2), Unit(3), humor, optimism, Unit(6), tone, era, themes, payload);
    }

    /// <summary>
    /// Every scalar carries both its endpoints. The old prompt anchored only pacing and
    /// ensembleVsSolo and said "0.0-1.0" once — leaving a model free to reason on a 0-10 scale
    /// for darkness, which the parser now rejects outright rather than clamping to 1.0.
    /// </summary>
    private static readonly string SystemPrompt =
        "You are a film and television taste analyst. Judge the WORK ITSELF, not its popularity or reception.\n" +
        "Return ONLY a JSON object with exactly these ten keys.\n" +
        "Seven scalars, each a bare JSON number on a 0.0-1.0 scale — NEVER 0-10, NEVER 0-100, never a string, never null:\n" +
        "  darkness            0.0 = light and safe, 1.0 = bleak, brutal, despairing\n" +
        "  pacing              0.0 = glacial slow-burn, 1.0 = relentless and frenetic\n" +
        "  complexity          0.0 = simple and linear, 1.0 = dense, demanding, many threads\n" +
        "  emotionalIntensity  0.0 = detached and cool, 1.0 = wrenching and overwhelming\n" +
        "  humor               0.0 = no humour at all, 1.0 = comic throughout\n" +
        "  optimism            0.0 = hopeless, 1.0 = hopeful and affirming\n" +
        "  ensembleVsSolo      0.0 = a single protagonist, 1.0 = a broad ensemble\n" +
        "tone: EXACTLY ONE value copied verbatim from this closed list: " + string.Join(", ", ToneVocabulary) + ".\n" +
        "era: the era the story is SET in, EXACTLY ONE value copied verbatim from this closed list: " +
        string.Join(", ", EraVocabulary) + ".\n" +
        "themes: 1 to " + MaxThemes + " short lowercase theme phrases, at most three words each.\n" +
        "Do not invent tone or era values outside the lists, and do not omit any key. " +
        "If you are unsure of a scalar, still give your best 0.0-1.0 estimate. Output strictly valid JSON.";

    private static string UserPrompt(TitleAttributeInput input, bool compact)
    {
        var sb = new StringBuilder();
        sb.Append(input.MediaType).Append(": ").Append(input.Name);
        if (input.Year is { } year)
        {
            sb.Append(" (").Append(year).Append(')');
        }

        if (input.Genres.Count > 0)
        {
            sb.Append("\nGenres: ").Append(string.Join(", ", input.Genres));
        }

        if (input.Keywords.Count > 0)
        {
            sb.Append("\nKeywords: ")
                .Append(string.Join(", ", input.Keywords.Take(compact ? MaxCompactKeywords : MaxKeywords)));
        }

        if (!string.IsNullOrWhiteSpace(input.Overview))
        {
            // A pathological overview is pure input cost and pushes the whole exchange toward the
            // context ceiling; the tone signal saturates long before the cap.
            var limit = compact ? MaxCompactOverviewChars : MaxOverviewChars;
            var overview = input.Overview!.Length > limit ? input.Overview[..limit] + "…" : input.Overview;
            sb.Append("\nOverview: ").Append(overview);
        }

        return sb.ToString();
    }

    private enum ExtractionFailure
    {
        None,

        /// <summary>
        /// No completion: network, timeout, a 408/429/5xx, an unreadable envelope, a refused key, an
        /// open breaker or a spent allowance. Not worth an identical retry here; never charged.
        /// </summary>
        Transport,

        /// <summary>
        /// The provider rejected the request itself (a 4xx other than 401/402/403/408/429). The same
        /// bytes get the same answer, so it is charged like an unusable completion.
        /// </summary>
        Rejected,

        /// <summary>200 OK with no completion text.</summary>
        Empty,

        /// <summary>The completion hit the token ceiling — the object is cut, not answered.</summary>
        Truncated,

        /// <summary>Parsed, but a required scalar is absent / non-numeric / off-scale.</summary>
        Malformed,
    }

    private sealed record RepairPlan(string Hint, bool Compact);

    private sealed record AttemptOutcome(
        ExtractedTitleAttributes? Attributes, ExtractionFailure Failure, IReadOnlyList<string> Problems)
    {
        public static AttemptOutcome Failed(ExtractionFailure failure, IReadOnlyList<string> problems) =>
            new(null, failure, problems);
    }
}
