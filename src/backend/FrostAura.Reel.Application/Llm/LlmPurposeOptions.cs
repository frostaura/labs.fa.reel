using System.Globalization;
using FrostAura.Reel.Domain.Sync;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// Everything that bounds one model call for one purpose: which model, how hard it may think, how
/// many completion tokens it may spend, and how long and how often it may try.
///
/// <para><b>Why one record per purpose.</b> Every ceiling in this product was sized for
/// <c>gpt-5.4-mini</c> and hard-coded inside the adapter that used it. When the stack moved to a
/// reasoning model, hidden reasoning ate the 400-token interpret ceiling on 59% of calls and every
/// cross-reference batch at exactly 4,096 — and nothing could be re-sized without a code change in
/// five files. A purpose's budget is now one row read from one place.</para>
///
/// <para><paramref name="ReasoningEffort"/> is null where the request must carry no reasoning
/// parameter at all — attribute extraction, whose request body stays byte-identical until FD-1 so
/// no catalogue re-extraction is triggered. <paramref name="PerItemTokens"/> is the per-candidate
/// share of a batch call's completion budget (cross-reference); the call's ceiling is then
/// <c>MaxTokens + PerItemTokens × items</c>, so <paramref name="MaxTokens"/> is the fixed
/// allowance — reasoning and JSON envelope — that does not scale with the batch.</para>
/// </summary>
public sealed record LlmPurposeOptions(
    string Model,
    string? ReasoningEffort,
    int MaxTokens,
    int MaxRetries,
    TimeSpan AttemptTimeout,
    TimeSpan TotalTimeout,
    int PerItemTokens = 0,
    bool RetryOnLength = true)
{
    /// <summary>The completion ceiling for a call over <paramref name="items"/> batch entries.</summary>
    public int MaxTokensFor(int items) => MaxTokens + (PerItemTokens * Math.Max(0, items));
}

/// <summary>The reasoning-effort values OpenRouter's unified <c>reasoning.effort</c> parameter takes.</summary>
public static class ReasoningEfforts
{
    /// <summary>No hidden reasoning. The interactive default: a reply that thinks for 400 tokens
    /// before writing one sentence is the 52-second chat turn.</summary>
    public const string None = "none";

    public const string Minimal = "minimal";

    /// <summary>The fallback for a model that rejects <see cref="None"/>.</summary>
    public const string Low = "low";

    public const string Medium = "medium";

    public const string High = "high";

    /// <summary>Every value this build sends or accepts from configuration.</summary>
    public static readonly IReadOnlyList<string> All = [None, Minimal, Low, Medium, High];

    /// <summary>
    /// The effort to try when a model rejects <paramref name="effort"/>: none → low, and anything
    /// else → no reasoning parameter at all (null). Low is the documented fallback because
    /// "minimal" is not universally accepted either, and low is the cheapest value every reasoning
    /// model on the provider takes.
    /// </summary>
    public static string? FallbackFor(string effort) =>
        effort is None or Minimal ? Low : null;
}

/// <summary>Reads the options for a purpose.</summary>
public interface ILlmPurposeCatalog
{
    LlmPurposeOptions For(string purpose);
}

/// <summary>
/// The per-purpose defaults and their configuration overrides.
///
/// <para><b>Overrides.</b> Each field reads <c>LLM_&lt;PURPOSE&gt;_&lt;FIELD&gt;</c>, the purpose
/// upper-snake-cased (<c>LLM_ASK_PLAN_MAX_TOKENS</c>, <c>LLM_RERANK_FEED_EFFORT</c>,
/// <c>LLM_CROSS_REFERENCE_TOKENS_PER_CANDIDATE</c>), with fields <c>MODEL</c>, <c>EFFORT</c>,
/// <c>MAX_TOKENS</c>, <c>MAX_RETRIES</c>, <c>ATTEMPT_TIMEOUT_SECONDS</c>,
/// <c>TOTAL_TIMEOUT_SECONDS</c> and <c>TOKENS_PER_CANDIDATE</c>. Setting <c>EFFORT</c> to
/// <c>omit</c> sends no reasoning parameter. A blank value is absent.</para>
///
/// <para><b>Model resolution is the adapters' existing rule, moved, not changed (FD-1 default: no
/// model id changes).</b> The search family — interpret, the re-rank family, Ask v2 — reads
/// <c>REEL_SEARCH_MODEL</c>, then <c>OPENROUTER_MODEL</c>, then <c>openai/gpt-6-luna</c>; the
/// background writers read <c>OPENROUTER_MODEL</c> then luna; and extraction keeps its own,
/// slightly different rule exactly (a blank <c>OPENROUTER_MODEL</c> is NOT absent there), and takes
/// no per-purpose model override — its model is part of its cache key and of the catalogue's
/// extraction stamp, and FD-1 has not been answered.</para>
///
/// <para><b>The token defaults are the measured ones</b> (WS-1 task 3: p99 completion tokens at
/// effort none × 1.5, rounded up) where a measurement exists, and §4.11-derived ceilings where
/// no prompt exists yet to measure (ask-plan, ask-answer) or none was spent (lens-audit).</para>
/// </summary>
public sealed class LlmPurposeCatalog(IConfiguration configuration) : ILlmPurposeCatalog
{
    public const string DefaultModel = "openai/gpt-6-luna";

    /// <summary>The configuration value that sends no reasoning parameter for a purpose.</summary>
    public const string OmitEffort = "omit";

    private static readonly TimeSpan InteractiveAttempt = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InteractiveTotal = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan BackgroundAttempt = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BackgroundTotal = TimeSpan.FromSeconds(180);

    private enum ModelRule
    {
        Search,
        General,
        Extraction,
    }

    private sealed record Default(ModelRule Rule, LlmPurposeOptions Options);

    /// <summary>
    /// The defaults, model left blank — it is resolved from configuration per read.
    ///
    /// <para><b>Sized from WS-1 task 3's probe</b> (2026-10-24, gpt-6-luna at effort none, slot DB
    /// reel_ws1, 205 calls, reasoning tokens 0 on every call): each ceiling is the measured p99
    /// completion × 1.5, rounded up. Batch purposes are sized per item (p99 per item × 1.5) on top
    /// of a fixed envelope allowance, because luna's per-entry cost is what scales — a 9-title feed
    /// batch at p99 needed ~1,200 tokens, far past the old flat 900. p99 (p50) completion tokens:
    /// interpret 125 (91), interpreter 80 (68), feed batch of 9 1,194 (586; 133/item p99), page
    /// batch of 9 1,130 (592), Ask batch of 6 443 (412; 74/item), cross-reference batch of 10
    /// 928 (464; 93/item), lens 205 (163), narrative 239 (212). Against the gpt-5.4-mini ledger
    /// means: interpret 130, feed 368, page 424, Ask 366, cross-reference 1,490 per 60, lens 176,
    /// narrative 255 — at effort none luna is back in mini's range everywhere but the re-rank
    /// whys, which it writes about 1.6× longer.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Default> Defaults = new Dictionary<string, Default>(StringComparer.Ordinal)
    {
        // ── Ask Reel v1: the conversational interpret and the expansion interpreter ──
        [ApiUsagePurpose.SearchAgent] = Search(maxTokens: 200, InteractiveAttempt, InteractiveTotal),
        [ApiUsagePurpose.SearchInterpret] = Search(maxTokens: 150, InteractiveAttempt, InteractiveTotal),

        // ── The re-rank family: an envelope plus a per-title share ──
        // Feed curation is background work with nobody waiting, so it gets the longer clock: one
        // 9-title batch took 25 s at p99 in the probe, past the interactive attempt timeout.
        [ApiUsagePurpose.Rerank] = Search(maxTokens: 100, InteractiveAttempt, InteractiveTotal, perItem: 200),
        [ApiUsagePurpose.RerankFeed] = Search(maxTokens: 100, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90), perItem: 200),
        [ApiUsagePurpose.RerankPage] = Search(maxTokens: 100, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), perItem: 200),
        [ApiUsagePurpose.RerankAsk] = Search(maxTokens: 100, InteractiveAttempt, InteractiveTotal, perItem: 111),
        [ApiUsagePurpose.RerankExpand] = Search(maxTokens: 100, InteractiveAttempt, InteractiveTotal, perItem: 111),

        // ── Ask Reel v2 (§4.11 stage budgets). Plan and answer are not measurable until WS-2's
        // prompts exist and keep §4.11-derived ceilings; the judge uses the rerank-ask measurement,
        // its closest existing shape (a satisfies verdict + fit + why per title). ──
        [ApiUsagePurpose.AskPlan] = Search(maxTokens: 600, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(25)),
        [ApiUsagePurpose.AskJudge] = Search(maxTokens: 100, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), perItem: 111),
        [ApiUsagePurpose.AskAnswer] = Search(maxTokens: 300, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(25)),

        // ── Background writers ──
        // Cross-reference: a fixed allowance for the envelope (and any residual reasoning) plus a
        // per-candidate share, so the ceiling scales with the batch the way the answer does.
        [ApiUsagePurpose.CrossReference] = General(maxTokens: 256, BackgroundAttempt, BackgroundTotal, perItem: 140),
        [ApiUsagePurpose.CrossReferenceEval] = General(maxTokens: 256, BackgroundAttempt, BackgroundTotal, perItem: 140),
        [ApiUsagePurpose.Lens] = General(maxTokens: 350, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)),
        [ApiUsagePurpose.LensAudit] = General(maxTokens: 300, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)),
        [ApiUsagePurpose.TasteNarrative] = General(maxTokens: 400, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)),

        // ── Extraction: untouched until FD-1 ──
        // No reasoning parameter, the same 500-token ceiling, no transient retry (a 500 is retried
        // by a later job run, not by an identical immediate request) and no 2x length retry — the
        // extractor's own compact repair pass is its length strategy, and its first request must
        // stay byte-identical so no catalogue re-extraction is triggered.
        [ApiUsagePurpose.ExtractAttributes] = new(ModelRule.Extraction, new LlmPurposeOptions(
            string.Empty, ReasoningEffort: null, MaxTokens: 500, MaxRetries: 0,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), RetryOnLength: false)),
    };

    /// <summary>The purposes this catalog has defaults for — the ones a probe may measure.</summary>
    public static IReadOnlyCollection<string> KnownPurposes => (IReadOnlyCollection<string>)Defaults.Keys;

    public LlmPurposeOptions For(string purpose)
    {
        // A probe measures the purpose it names, at that purpose's own options.
        var basePurpose = purpose.StartsWith(ApiUsagePurpose.ProbePrefix, StringComparison.Ordinal)
            ? purpose[ApiUsagePurpose.ProbePrefix.Length..]
            : purpose;

        // An unknown purpose is bounded like the interactive family rather than left unbounded:
        // a new caller that forgot to register its purpose still cannot monologue.
        var def = Defaults.TryGetValue(basePurpose, out var d) ? d : Search(maxTokens: 900, InteractiveAttempt, InteractiveTotal);
        var prefix = "LLM_" + basePurpose.ToUpperInvariant().Replace('-', '_') + "_";
        var o = def.Options;

        var model = ResolveModel(def.Rule, prefix);
        var effort = o.ReasoningEffort;
        if (def.Rule != ModelRule.Extraction && Read(prefix + "EFFORT") is { } configuredEffort)
        {
            var normalized = configuredEffort.Trim().ToLowerInvariant();
            effort = normalized == OmitEffort ? null
                : ReasoningEfforts.All.Contains(normalized) ? normalized
                : effort;
        }

        return o with
        {
            Model = model,
            ReasoningEffort = effort,
            MaxTokens = PositiveInt(prefix + "MAX_TOKENS", o.MaxTokens),
            MaxRetries = Math.Clamp(PositiveInt(prefix + "MAX_RETRIES", o.MaxRetries, allowZero: true), 0, 3),
            AttemptTimeout = Seconds(prefix + "ATTEMPT_TIMEOUT_SECONDS", o.AttemptTimeout),
            TotalTimeout = Seconds(prefix + "TOTAL_TIMEOUT_SECONDS", o.TotalTimeout),
            PerItemTokens = PositiveInt(prefix + "TOKENS_PER_CANDIDATE", o.PerItemTokens, allowZero: true),
        };
    }

    private string ResolveModel(ModelRule rule, string prefix)
    {
        switch (rule)
        {
            case ModelRule.Extraction:
                // Verbatim from OpenRouterAttributeExtractor.ModelId — including the quirk that a
                // blank OPENROUTER_MODEL is used as-is. Changing it is FD-1's decision, not this one.
                return configuration["OPENROUTER_MODEL"] ?? DefaultModel;
            case ModelRule.Search:
                return Read(prefix + "MODEL") ?? Read("REEL_SEARCH_MODEL") ?? Read("OPENROUTER_MODEL") ?? DefaultModel;
            default:
                return Read(prefix + "MODEL") ?? Read("OPENROUTER_MODEL") ?? DefaultModel;
        }
    }

    // Empty env vars are "present but blank" to .NET config, which defeats ??; blank is absent here.
    private string? Read(string key) => configuration[key] is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private int PositiveInt(string key, int fallback, bool allowZero = false) =>
        Read(key) is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && (v > 0 || (allowZero && v == 0))
            ? v
            : fallback;

    private TimeSpan Seconds(string key, TimeSpan fallback) =>
        Read(key) is { } raw && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? TimeSpan.FromSeconds(v)
            : fallback;

    private static Default Search(int maxTokens, TimeSpan attempt, TimeSpan total, int perItem = 0) =>
        new(ModelRule.Search, new LlmPurposeOptions(
            string.Empty, ReasoningEfforts.None, maxTokens, MaxRetries: 1, attempt, total, perItem));

    private static Default General(int maxTokens, TimeSpan attempt, TimeSpan total, int perItem = 0) =>
        new(ModelRule.General, new LlmPurposeOptions(
            string.Empty, ReasoningEfforts.None, maxTokens, MaxRetries: 1, attempt, total, perItem));
}
