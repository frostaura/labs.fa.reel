using System.Globalization;
using System.Text;
using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Judges a batch of candidates against the viewer and against the titles the viewer has actually
/// rated, through OpenRouter — the cross-reference rounds.
///
/// <para>Structurally a sibling of <see cref="OpenRouterLensWriter"/> and follows the same
/// transport rules for the same reasons: <b>strict <c>json_schema</c> where the provider supports
/// it</b>, with a permanent per-instance downgrade to <c>json_object</c> on rejection and the
/// parser as the backstop; usage recorded in a <c>finally</c> so a call that fails after the
/// request reads as "unknown tokens" rather than "free"; and <b>never fabricate</b> — an entry that
/// does not parse or breaks the verdict × reason matrix is dropped, and a candidate the model
/// omitted gets no row at all.</para>
///
/// <para><b>Why an omitted candidate must not be filled in.</b> The job has no cursor: its resume
/// state is an anti-join against the verdict table, so "no row" is exactly what "still to do"
/// means. Inventing an Unsure for a skipped candidate would mark it done and it would never be
/// looked at again — the opposite of what a missing answer should cause.</para>
///
/// <para><b><c>max_tokens</c> is computed, and <c>finish_reason=length</c> discards the batch.</b>
/// The ceiling is the purpose's fixed allowance plus a per-candidate share (both in
/// <see cref="LlmPurposeCatalog"/>, measured at reasoning effort none). The old 64 × n + 256 was
/// sized for a model that does not reason: on gpt-6-luna every batch since 2026-10-14 finished at
/// exactly 4,096 with zero verdicts, because hidden reasoning spent the ceiling — and splitting the
/// batch cannot help when the ceiling scales with it. A completion cut at a token boundary has
/// missing entries rather than wrong ones, and those missing entries are indistinguishable from a
/// deliberate omission — so the whole batch is discarded rather than half-recorded, after the
/// shared client's one retry at twice the ceiling.</para>
///
/// <para><b>Every number that reaches the prompt is
/// <see cref="CultureInfo.InvariantCulture"/>-formatted.</b> This machine renders 8.4 as "8,4" — a
/// decimal comma inside a pipe-and-comma delimited line handed to a model asked to reason about the
/// figure. The host's locale has no business changing what the model reads.</para>
///
/// <para><b>Stub mode</b> (no key, or <c>TestHelpers:ModelStubMode</c>) is deterministic and derived
/// from the input — see <see cref="StubVerdict"/> for the rule — so the whole pipeline runs in tests
/// and in keyless dev without a billable call.</para>
/// </summary>
public class OpenRouterCrossReferenceJudge(
    HttpClient httpClient,
    ApiUsageRecorder usageRecorder,
    IConfiguration configuration,
    ILogger<OpenRouterCrossReferenceJudge> logger,
    // Optional by design, mirroring the other OpenRouter adapters: the typed HttpClient is
    // transient and is always constructed inside a scope in production, but the adapter must stay
    // `new`-able in a test with no scope.
    IAccountContext? accountContext = null,
    ILlmPurposeCatalog? purposes = null,
    ModelCapabilities? capabilities = null,
    IProviderHealth? health = null,
    ILlmCallGate? gate = null,
    // The per-purpose call counters (task 8); a DI singleton in production, none in a hand-built adapter.
    LlmCallMetrics? metrics = null)
    : ICrossReferenceJudge
{
    /// <summary>The shared transport: strict schema, effort, ceilings, retries, breaker, budget and ledger.</summary>
    private readonly OpenRouterChatClient _chat = new(
        httpClient, usageRecorder, configuration, logger, accountContext, purposes, capabilities, health, gate, metrics);

    /// <summary>
    /// The prompt generation, and part of the verdict key.
    ///
    /// <para>v1: the first pass. Round 1 rules candidates OUT and is told in as many words that
    /// <c>unsure</c> is the correct answer far more often than it is comfortable; round 2 looks
    /// only for reasons to say no. Bump when a change alters what a stored verdict MEANS.</para>
    ///
    /// <para><b>2 since 2026-09-11, because it was measured and v1 was not better.</b> A
    /// generation becomes current by being MEASURED against the one it replaces, never by being
    /// written. On the founder's holdout, five split origins pooled: v1 judged 1,307 titles at AUC
    /// 0.611 / Spearman 0.128 with keeps later averaging 6.53 against 6.21 for outs; v2 judged
    /// 1,194 at AUC 0.655 / Spearman 0.190 with keeps averaging 6.83 against 6.16, keep → loved
    /// 33.2% and keep → disliked 8.1% against base rates of roughly 23% and 15%. v2's keep is the
    /// better-separated verdict on every one of those axes, so v2 is what the job writes.</para>
    ///
    /// <para><b>Moving this constant does not rewrite a single stored row.</b> The version is part
    /// of the verdict key, so v1 rows stay readable under their own key and are simply no longer
    /// current: nothing reads them into the feed, the resume anti-join treats their titles as
    /// unjudged at v2, and they are re-judged in the ordinary course rather than deleted.</para>
    /// </summary>
    public const int CurrentPromptVersion = CrossReferencePromptVersions.Second;

    /// <summary>
    /// The second prompt generation — an experiment, selectable by the backtest and by nothing
    /// else.
    ///
    /// <para><b>Every difference from v1 answers something the 2026-09-10 live run measured</b>
    /// (five origins, 1,305 judged: false-rule-out 12.0%, true-rule-out 10.1%, unsure 33%). A
    /// false-rule-out rate that matches the true-rule-out rate is a verdict uncorrelated with the
    /// outcome, and a third of the pool answering "insufficient signal" says the judge frequently
    /// had nothing it could use. So v2 (a) leads each candidate line with the neighbour evidence,
    /// the viewer's own score first, and shows five neighbours rather than three — including up to
    /// two low-rated ones, so a negative comparison is visible whenever one exists rather than only
    /// when it happens to be among the three nearest; (b) states the account's genre affinities with
    /// the pool each rests on, so genre-level evidence is explicit instead of implied by a
    /// twenty-token taste line; (c) anchors confidence on the viewer's own base rates and says an
    /// <c>out</c> should be rarer than their dislike share; and (d) asks for a neighbour reason
    /// where the verdict is neighbour-based, adding <c>neighbour-mixed</c> so "the evidence
    /// disagrees with itself" stops being recorded as "I was shown nothing".</para>
    /// </summary>
    public const int SecondPromptVersion = CrossReferencePromptVersions.Second;

    /// <summary>
    /// The generation the argument-light <c>BuildUserPrompt</c> / <c>ResultsSchema</c> /
    /// <c>ParseResults</c> overloads render, and the ONLY thing those overloads are for: pinning v1
    /// in the golden tests.
    ///
    /// <para>Deliberately a separate constant from <see cref="CurrentPromptVersion"/> and
    /// deliberately frozen at 1. A stored v1 verdict has to keep meaning what it meant, so the
    /// tests that pin v1's bytes must not follow the serving default around; and no production path
    /// reaches these overloads — <c>JudgeAsync</c> names its version on every call, because the
    /// version is a persisted key part and a prompt built at a version other than the one written
    /// into the row would be a verdict nothing could ever reproduce.</para>
    /// </summary>
    internal const int GoldenPinnedVersion = CrossReferencePromptVersions.First;

    /// <summary>
    /// Overview characters in the prompt. 200, not the lens writer's 1,200 and not the 300 an
    /// earlier draft assumed: no prose is written back from this call, so the overview is here to
    /// disambiguate a title, not to be summarised.
    /// </summary>
    private const int MaxOverviewChars = 200;

    /// <summary>Cast names shown. Three is the same ceiling the rerank context uses.</summary>
    private const int MaxCast = 3;

    /// <summary>Themes shown per candidate.</summary>
    private const int MaxThemes = 4;

    private string? ApiKey => configuration["OPENROUTER_API_KEY"] is { Length: > 0 } key ? key : null;

    private bool StubMode => ApiKey is null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public bool IsAvailable => ApiKey is not null || configuration.GetValue("TestHelpers:ModelStubMode", false);

    public string ModelId => StubMode
        ? "stub/deterministic-v1"
        : _chat.Options(ApiUsagePurpose.CrossReference).Model;

    /// <summary>The model-and-effort a verdict is written under — what the cross-run breaker compares runs on.</summary>
    public string RunKey => StubMode ? ModelId : _chat.CacheSalt(ApiUsagePurpose.CrossReference);

    public int PromptVersion => CurrentPromptVersion;

    // ── Prompts ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Round 1. The instruction that matters most is the last one: a judge that is never unsure is
    /// guessing, and a guess recorded as a verdict is worse than no verdict because the aggregate
    /// rule will count it.
    /// </summary>
    internal const string RoundOneSystemPrompt =
        """
        You are cross-referencing a list of candidate films and shows against ONE viewer.

        Your only job is to rule candidates OUT. You are not recommending anything and you are not
        ranking anything.

        Answer "out" when: the nearest titles they rated are ones they scored low; the tone, pace or
        theme runs against the viewer description; or the format is one they do not watch.

        Answer "keep" only on the evidence given — a nearest title they scored highly, a tone that
        matches what the description says they seek out, or a theme they return to. Never keep
        something because it is acclaimed, popular or well made.

        Answer "unsure" for everything else. Unsure is the correct answer far more often than it is
        comfortable. A judge that is never unsure is guessing.

        Schedule-filler formats — long-running panel shows, daytime talk, competition-reality strips
        and similar — are "out" with reason "format-mismatch" unless the viewer description says
        otherwise.

        Never rule something out for being obscure, old, or in another language. None of those is a
        reason.

        Spread your confidence across the range. Do not answer 0.9 for everything.

        Return EXACTLY ONE entry per candidate id you were given, and no prose.
        """;

    /// <summary>
    /// Round 2. Survivors only, different neighbours, and an explicitly adversarial framing — the
    /// second keep is what makes a Confirmed Keep mean anything, so it has to be hard to get.
    /// </summary>
    internal const string RoundTwoSystemPrompt =
        """
        You are cross-referencing a list of candidate films and shows against ONE viewer.

        These candidates already survived a first pass, and you are looking at DIFFERENT nearby
        titles this viewer rated than that pass did. You are now looking specifically for reasons
        this viewer would NOT enjoy them.

        Answer "out" when: the nearest titles they rated are ones they scored low; the tone, pace or
        theme runs against the viewer description; or the format is one they do not watch.

        Answer "keep" only where the new evidence independently supports it. A second keep is a real
        endorsement and should be rare.

        Answer "unsure" for everything else. Unsure is the correct answer far more often than it is
        comfortable.

        Schedule-filler formats — long-running panel shows, daytime talk, competition-reality strips
        and similar — are "out" with reason "format-mismatch" unless the viewer description says
        otherwise.

        Never rule something out for being obscure, old, or in another language. None of those is a
        reason.

        Spread your confidence across the range. Do not answer 0.9 for everything.

        Return EXACTLY ONE entry per candidate id you were given, and no prose.
        """;

    // ── Prompts, v2 ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Round 1, generation 2. Everything v1 says, plus the four changes the live result asked for:
    /// read the neighbours FIRST, use the genre lines, name a neighbour reason when the verdict is
    /// neighbour-based, and treat confidence as a probability anchored on this viewer's own base
    /// rates.
    ///
    /// <para>The base-rate sentence is appended by <see cref="BuildSystemPrompt"/> rather than
    /// living here, because it carries two of the viewer's own numbers and is OMITTED entirely when
    /// they are unknown — a "you dislike about 0% of what you finish" is a claim, not a default.</para>
    /// </summary>
    internal const string RoundOneSystemPromptV2 =
        """
        You are cross-referencing a list of candidate films and shows against ONE viewer.

        Your only job is to rule candidates OUT. You are not recommending anything and you are not
        ranking anything.

        Each candidate line LEADS with the strongest evidence there is: the titles this viewer has
        actually rated that sit nearest it, with the viewer's own score first. Read those before
        anything else on the line. The GENRES lines above the candidates give this viewer's own
        average score per genre and the number of titles each average rests on.

        Answer "out" when: the nearest titles they rated are ones they scored low; the tone, pace or
        theme runs against the viewer description; or the format is one they do not watch.

        Answer "keep" only on the evidence given — a nearest title they scored highly, a tone that
        matches what the description says they seek out, or a theme they return to. Never keep
        something because it is acclaimed, popular or well made.

        Answer "unsure" for everything else. Unsure is the correct answer far more often than it is
        comfortable. A judge that is never unsure is guessing.

        Where your verdict rests on the nearby rated titles rather than on the description, say so
        in the reason: "neighbour-loved" where those titles are ones this viewer scored highly,
        "neighbour-disliked" where they scored them low, and "neighbour-mixed" where the nearest
        ones point both ways. "insufficient-signal" means you were shown nothing usable at all, and
        is not the same answer as "the evidence disagrees with itself".

        Schedule-filler formats — long-running panel shows, daytime talk, competition-reality strips
        and similar — are "out" with reason "format-mismatch" unless the viewer description says
        otherwise.

        Never rule something out for being obscure, old, or in another language. None of those is a
        reason.

        Your confidence is a probability, not an emphasis: 0.9 means you expect to be right nine
        times in ten. Spread your confidence across the range, and reserve the top of it for
        candidates whose nearby rated titles agree with each other.

        Return EXACTLY ONE entry per candidate id you were given, and no prose.
        """;

    /// <summary>
    /// Round 2, generation 2. The v2 instructions with v1's adversarial round-2 framing: different
    /// neighbours, and a second keep that has to be earned.
    /// </summary>
    internal const string RoundTwoSystemPromptV2 =
        """
        You are cross-referencing a list of candidate films and shows against ONE viewer.

        These candidates already survived a first pass, and you are looking at DIFFERENT nearby
        titles this viewer rated than that pass did. You are now looking specifically for reasons
        this viewer would NOT enjoy them.

        Each candidate line LEADS with the strongest evidence there is: the titles this viewer has
        actually rated that sit nearest it, with the viewer's own score first. Read those before
        anything else on the line. The GENRES lines above the candidates give this viewer's own
        average score per genre and the number of titles each average rests on.

        Answer "out" when: the nearest titles they rated are ones they scored low; the tone, pace or
        theme runs against the viewer description; or the format is one they do not watch.

        Answer "keep" only where the new evidence independently supports it. A second keep is a real
        endorsement and should be rare.

        Answer "unsure" for everything else. Unsure is the correct answer far more often than it is
        comfortable.

        Where your verdict rests on the nearby rated titles rather than on the description, say so
        in the reason: "neighbour-loved" where those titles are ones this viewer scored highly,
        "neighbour-disliked" where they scored them low, and "neighbour-mixed" where the nearest
        ones point both ways. "insufficient-signal" means you were shown nothing usable at all, and
        is not the same answer as "the evidence disagrees with itself".

        Schedule-filler formats — long-running panel shows, daytime talk, competition-reality strips
        and similar — are "out" with reason "format-mismatch" unless the viewer description says
        otherwise.

        Never rule something out for being obscure, old, or in another language. None of those is a
        reason.

        Your confidence is a probability, not an emphasis: 0.9 means you expect to be right nine
        times in ten. Spread your confidence across the range, and reserve the top of it for
        candidates whose nearby rated titles agree with each other.

        Return EXACTLY ONE entry per candidate id you were given, and no prose.
        """;

    /// <summary>
    /// The system message for a round at a prompt generation.
    ///
    /// <para><b>v1 is returned byte-identical</b> — the constants, untouched, with nothing appended.
    /// Its stored verdicts are keyed on version 1 and a v1 row has to keep meaning what it meant, so
    /// the golden test that pins those two strings must never need editing to accommodate a later
    /// generation.</para>
    ///
    /// <para>v2 appends the base-rate sentence when, and only when, both shares are known. The house
    /// rule about omission over zero-filling applies to a system prompt exactly as it applies to a
    /// candidate line: an unknown base rate rendered as 0% is a statement about the viewer that
    /// nobody measured.</para>
    /// </summary>
    internal static string BuildSystemPrompt(CrossReferenceRound round, CrossReferenceJudgeOptions options)
    {
        if (options.PromptVersion < SecondPromptVersion)
        {
            return round == CrossReferenceRound.First ? RoundOneSystemPrompt : RoundTwoSystemPrompt;
        }

        var body = round == CrossReferenceRound.First ? RoundOneSystemPromptV2 : RoundTwoSystemPromptV2;
        if (options.LovedShare is not { } loved || options.DislikedShare is not { } disliked)
        {
            return body;
        }

        return body + "\n\n" + string.Create(
            CultureInfo.InvariantCulture,
            $"This viewer rates about {loved * 100m:0}% of what they finish 8 or higher and "
            + $"{disliked * 100m:0}% 4 or lower. An \"out\" should be rarer than that dislike share "
            + $"unless the evidence is strong.");
    }

    /// <summary>
    /// The user message: the viewer, then one line per candidate.
    ///
    /// <para><b>Fields are OMITTED when missing, never zero-filled.</b> The same rule the lens input
    /// builder follows: "pacing 0.0" is a claim that the thing is glacial, not an admission that
    /// nobody measured it. A candidate with no embedding has no <c>nearest you rated</c> clause at
    /// all rather than an empty one.</para>
    ///
    /// <para><b>Order is shuffled per batch</b> via <see cref="RerankOrder"/>, which is
    /// deterministic on the batch's own contents — LLM judges systematically score later items
    /// lower, and listing candidates in model-rank order would have the judge reinforce the ranking
    /// it is supposed to second-guess. Deterministic so a golden test means something.</para>
    /// </summary>
    internal static string BuildUserPrompt(
        IReadOnlyList<CrossReferenceCandidate> candidates, LensPersona persona, CrossReferenceRound round)
        => BuildUserPrompt(candidates, persona, round, new CrossReferenceJudgeOptions(GoldenPinnedVersion));

    /// <summary>
    /// The user message at an explicit prompt generation.
    ///
    /// <para><b>v2's two differences.</b> The viewer block carries the genre affinity line — the
    /// account's own average per genre with the pool size beside it, omitted entirely when there is
    /// nothing nameable. And each candidate line LEADS with the neighbour evidence rather than
    /// ending with it, the viewer's own score first: the 2026-09-10 run put that clause last, after
    /// seven scalars and a cast list, and produced an <c>out</c> verdict uncorrelated with the
    /// outcome while a third of the pool came back "insufficient signal". Position is not a subtle
    /// lever on a sixty-line prompt.</para>
    /// </summary>
    internal static string BuildUserPrompt(
        IReadOnlyList<CrossReferenceCandidate> candidates,
        LensPersona persona,
        CrossReferenceRound round,
        CrossReferenceJudgeOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("THE VIEWER\n").Append(persona.Taste.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(persona.Declined))
        {
            sb.Append(persona.Declined.Trim()).Append('\n');
        }

        if (options.PromptVersion >= SecondPromptVersion && !string.IsNullOrWhiteSpace(options.GenreAffinity))
        {
            sb.Append(options.GenreAffinity.Trim()).Append('\n');
        }

        sb.Append("\nCANDIDATES\n");
        foreach (var candidate in Order(candidates, round))
        {
            AppendCandidate(sb, candidate, options.PromptVersion);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The batch in its prompt order. Reuses the re-ranker's shuffle so the two surfaces cannot
    /// drift; the round is the "anchor" so the same batch judged twice is placed differently.
    /// </summary>
    internal static IReadOnlyList<CrossReferenceCandidate> Order(
        IReadOnlyList<CrossReferenceCandidate> candidates, CrossReferenceRound round)
    {
        var byId = candidates.ToDictionary(c => c.TitleId);
        var proxies = candidates
            .Select(c => new RerankInput(c.TitleId, c.Name, c.Year, c.MediaType, c.Genres, null, c.Predicted))
            .ToList();
        return RerankOrder
            .Shuffle(proxies, "cross-reference", round.ToString())
            .Select(p => byId[p.TitleId])
            .ToList();
    }

    private static void AppendCandidate(StringBuilder sb, CrossReferenceCandidate c, int promptVersion)
    {
        var neighbourFirst = promptVersion >= SecondPromptVersion;
        sb.Append(c.TitleId.ToString("D", CultureInfo.InvariantCulture));
        if (neighbourFirst)
        {
            // The id stays first because it is the key the model has to echo back, and a line whose
            // first field moved between generations would make every parser rule version-dependent
            // for nothing. The evidence comes immediately after it, ahead of the name.
            AppendNeighbours(sb, c, neighbourFirst: true);
        }

        sb.Append(" | ").Append(c.Name);
        if (c.Year is { } year)
        {
            sb.Append(' ').Append(string.Create(CultureInfo.InvariantCulture, $"({year})"));
        }

        sb.Append(" | ").Append(c.MediaType.ToLowerInvariant());
        if (c.Genres.Count > 0)
        {
            sb.Append(" | ").Append(string.Join(", ", c.Genres));
        }

        if (c.Predicted is { } predicted)
        {
            sb.Append(" | predicted ").Append(string.Create(CultureInfo.InvariantCulture, $"{predicted:0.0}"));
        }

        var texture = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Tone))
        {
            texture.Add(c.Tone);
        }

        if (!string.IsNullOrWhiteSpace(c.Era))
        {
            texture.Add(c.Era);
        }

        if (c.Themes.Count > 0)
        {
            texture.Add(string.Join("/", c.Themes.Take(MaxThemes)));
        }

        if (texture.Count > 0)
        {
            sb.Append(" | ").Append(string.Join(" · ", texture));
        }

        var scalars = new List<string>();
        Scalar(scalars, "dark", c.Darkness);
        Scalar(scalars, "pace", c.Pacing);
        Scalar(scalars, "complex", c.Complexity);
        Scalar(scalars, "intense", c.EmotionalIntensity);
        Scalar(scalars, "humor", c.Humor);
        Scalar(scalars, "optimism", c.Optimism);
        Scalar(scalars, "ensemble", c.EnsembleVsSolo);
        if (scalars.Count > 0)
        {
            sb.Append(" | ").Append(string.Join(' ', scalars));
        }

        var people = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Director))
        {
            people.Add("dir " + c.Director);
        }

        if (!string.IsNullOrWhiteSpace(c.Writer))
        {
            people.Add("wr " + c.Writer);
        }

        if (c.TopCast.Count > 0)
        {
            people.Add(string.Join(", ", c.TopCast.Take(MaxCast)));
        }

        if (people.Count > 0)
        {
            sb.Append(" | ").Append(string.Join(" · ", people));
        }

        if (!neighbourFirst)
        {
            AppendNeighbours(sb, c, neighbourFirst: false);
        }

        if (!string.IsNullOrWhiteSpace(c.Overview))
        {
            var overview = c.Overview.Trim();
            sb.Append(" | ").Append(overview.Length <= MaxOverviewChars ? overview : overview[..MaxOverviewChars]);
        }

        sb.Append('\n');
    }

    /// <summary>
    /// The neighbour clause. <b>Rating AND similarity, both</b> — "you 9" alone cannot distinguish a
    /// neighbour at 0.82 from one at 0.31, and those are different claims (the v5
    /// <c>knownPeopleFraction</c> lesson).
    ///
    /// <para>v2 puts the viewer's own SCORE first inside each item — "you 3/10 · Argylle (2024) ·
    /// sim 0.64" — because the score is the thing being reasoned from and the title is only how it
    /// is identified. v1's ordering is preserved exactly.</para>
    ///
    /// <para>Absent, not empty, when there are no neighbours: an empty clause with a label on it is
    /// a claim that the model looked and found nothing.</para>
    /// </summary>
    private static void AppendNeighbours(StringBuilder sb, CrossReferenceCandidate c, bool neighbourFirst)
    {
        if (c.Neighbours.Count == 0)
        {
            return;
        }

        var clause = c.Neighbours.Select(n =>
        {
            var year = n.Year is { } y ? string.Create(CultureInfo.InvariantCulture, $" ({y})") : string.Empty;
            return neighbourFirst
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"you {n.Rating}/10 · {n.Name}{year} · sim {n.Similarity:0.00}")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{n.Name}{year} you {n.Rating} · sim {n.Similarity:0.00}");
        });

        sb.Append(" | nearest you rated: ").Append(string.Join("; ", clause));
    }

    private static void Scalar(List<string> into, string name, decimal? value)
    {
        if (value is { } v)
        {
            into.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {v:0.00}"));
        }
    }

    // ── Schema ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The strict response schema. Every property is required and
    /// <c>additionalProperties: false</c>, so a model that wants to explain itself cannot.
    ///
    /// <para><b>There is deliberately no free-text reason field.</b> A one-line justification per
    /// entry would roughly triple completion tokens — from ~45 to ~140 — which at batch 60 means
    /// 8,400 completion tokens where the ceiling is 4,096, i.e. <c>finish_reason=length</c> and a
    /// discarded batch every time. The reason vocabulary is closed for cost, and the cost is the
    /// reason the whole eligible pool can be judged at all.</para>
    /// </summary>
    internal static object ResultsSchema(int batchSize) => ResultsSchema(batchSize, GoldenPinnedVersion);

    /// <summary>
    /// The schema for a prompt generation. The reason enum is
    /// <c>CrossReferenceReasons.ForVersion</c>'s list and NOT one growing set: the verdict table is
    /// keyed on prompt version and rows are read back long after, so a v1 row carrying a reason v1
    /// was never offered would be unexplainable rather than richer.
    /// </summary>
    internal static object ResultsSchema(int batchSize, int promptVersion) => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "cross_reference_verdicts",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "results" },
                properties = new Dictionary<string, object>
                {
                    ["results"] = new
                    {
                        type = "array",
                        maxItems = batchSize,
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            required = new[] { "titleId", "verdict", "reason", "confidence" },
                            properties = new Dictionary<string, object>
                            {
                                ["titleId"] = new { type = "string", minLength = 36, maxLength = 36 },
                                ["verdict"] = new { type = "string", @enum = new[] { "keep", "out", "unsure" } },
                                ["reason"] = new
                                {
                                    type = "string",
                                    @enum = CrossReferenceReasons.ForVersion(promptVersion),
                                },
                                ["confidence"] = new { type = "number", minimum = 0, maximum = 1 },
                            },
                        },
                    },
                },
            },
        },
    };

    // ── Judging ────────────────────────────────────────────────────────────────────────────

    public async Task<CrossReferenceBatchResult> JudgeAsync(
        IReadOnlyList<CrossReferenceCandidate> candidates,
        LensPersona persona,
        CrossReferenceRound round,
        string usagePurpose,
        CrossReferenceJudgeOptions options,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            return new CrossReferenceBatchResult([], Succeeded: true, null, null, 0);
        }

        if (StubMode)
        {
            return new CrossReferenceBatchResult(
                candidates.Select(StubVerdict).ToList(), Succeeded: true, null, null, 0);
        }

        var system = BuildSystemPrompt(round, options);
        var user = BuildUserPrompt(candidates, persona, round, options);

        var outcome = await _chat.CompleteAsync(
            new LlmChatRequest(
                usagePurpose,
                [LlmMessage.System(system), LlmMessage.User(user)],
                ResultsSchema(candidates.Count, options.PromptVersion),
                Items: candidates.Count),
            ct);
        if (!outcome.Succeeded)
        {
            // No rows, not Failed rows. Absence is the retry signal; a Failed row would look like a
            // judgement to the resume anti-join and suppress its own retry.
            logger.LogWarning(
                "Cross-reference judge produced no verdicts for purpose {Purpose} ({Failure}, {Count} candidates, ceiling {MaxTokens}).",
                usagePurpose, outcome.Failure, candidates.Count, outcome.MaxTokens);
            return new CrossReferenceBatchResult(
                [], Succeeded: false, outcome.PromptTokens, outcome.CompletionTokens, 0, outcome.AgentFailure);
        }

        var (entries, rejected) = ParseResults(
            outcome.Content!, candidates.Select(c => c.TitleId).ToHashSet(), options.PromptVersion);
        if (rejected > 0)
        {
            logger.LogWarning(
                "Cross-reference discarded {Rejected} malformed entr(ies) of {Count} for purpose {Purpose}; "
                + "those candidates keep no row and are re-judged on the next pass.",
                rejected, candidates.Count, usagePurpose);
        }

        return new CrossReferenceBatchResult(
            entries, Succeeded: true, outcome.PromptTokens, outcome.CompletionTokens, rejected);
    }

    /// <summary>
    /// Reads the batch's entries, rejecting bad ones ONE AT A TIME.
    ///
    /// <para>Every accessor is kind-checked and each entry is wrapped, because a batch-wide catch
    /// costs sixty verdicts for one bad one — the exact defect the re-ranker's
    /// <c>TryReadEntry</c> was written to fix, where a <c>titleId</c> returned as a JSON number
    /// threw out of <c>GetString()</c> and silently cost every other title in the batch.</para>
    ///
    /// <para>Five rejection classes, each costing exactly one entry: an id that was not in this
    /// batch (the model inventing or remembering a title), a duplicate id (two verdicts for one
    /// candidate is not a verdict), a non-string <c>titleId</c>, an illegal verdict × reason pair,
    /// and a confidence outside <c>[-0.01, 1.01]</c> — the last of which means the model scored on
    /// another scale entirely, and clamping would pin every such entry to 1.0 and flatten exactly
    /// the signal the field exists to carry.</para>
    /// </summary>
    internal static (IReadOnlyList<CrossReferenceEntry> Entries, int Rejected) ParseResults(
        string content, IReadOnlySet<Guid> batchIds)
        => ParseResults(content, batchIds, GoldenPinnedVersion);

    /// <inheritdoc cref="ParseResults(string, IReadOnlySet{Guid})"/>
    internal static (IReadOnlyList<CrossReferenceEntry> Entries, int Rejected) ParseResults(
        string content, IReadOnlySet<Guid> batchIds, int promptVersion)
    {
        var vocabulary = CrossReferenceReasons.ForVersion(promptVersion);
        var accepted = new Dictionary<Guid, CrossReferenceEntry>();
        var rejected = 0;

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("results", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return ([], 0);
            }

            foreach (var el in arr.EnumerateArray())
            {
                if (!TryReadEntry(el, batchIds, accepted.Keys.ToHashSet(), vocabulary, out var entry))
                {
                    rejected++;
                    continue;
                }

                accepted[entry.TitleId] = entry;
            }
        }
        catch (JsonException)
        {
            return ([], 0);
        }

        return (accepted.Values.ToList(), rejected);
    }

    private static bool TryReadEntry(
        JsonElement el,
        IReadOnlySet<Guid> batchIds,
        IReadOnlySet<Guid> already,
        IReadOnlyList<string> vocabulary,
        out CrossReferenceEntry entry)
    {
        entry = default!;
        try
        {
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("titleId", out var idEl)
                || idEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idEl.GetString(), out var id))
            {
                return false;
            }

            // Out of batch, or already answered. Both are the model answering a question it was not
            // asked, and both would corrupt a table keyed on (account, title, hash, version, round).
            if (!batchIds.Contains(id) || already.Contains(id))
            {
                return false;
            }

            if (!el.TryGetProperty("verdict", out var vEl) || vEl.ValueKind != JsonValueKind.String
                || ReadVerdict(vEl.GetString()) is not { } verdict)
            {
                return false;
            }

            if (!el.TryGetProperty("reason", out var rEl) || rEl.ValueKind != JsonValueKind.String
                || rEl.GetString() is not { Length: > 0 } reason
                || !vocabulary.Contains(reason, StringComparer.Ordinal)
                || !CrossReferenceReasons.IsLegal(verdict, reason))
            {
                return false;
            }

            if (!el.TryGetProperty("confidence", out var cEl) || cEl.ValueKind != JsonValueKind.Number
                || !cEl.TryGetDouble(out var confidence) || double.IsNaN(confidence)
                || confidence < -0.01d || confidence > 1.01d)
            {
                return false;
            }

            entry = new CrossReferenceEntry(id, verdict, reason, Math.Clamp(confidence, 0d, 1d));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static CrossReferenceVerdict? ReadVerdict(string? raw) => raw switch
    {
        "keep" => CrossReferenceVerdict.Keep,
        "out" => CrossReferenceVerdict.Out,
        "unsure" => CrossReferenceVerdict.Unsure,
        _ => null,
    };

    // ── Stub ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The deterministic stub verdict, and the rule is written down because tests assert against
    /// it: a candidate is judged on its own predicted rating and on the mean of its neighbours'
    /// actual ratings — the two numbers a real judge is being shown — with the neighbour mean
    /// standing in for the model's opinion where there is no prediction.
    ///
    /// <list type="bullet">
    /// <item>No prediction AND no neighbours ⇒ <c>unsure</c> / <c>insufficient-signal</c> at 0.50.
    /// There is genuinely nothing to go on.</item>
    /// <item>Evidence ≤ 5.0 ⇒ <c>out</c> / <c>neighbour-disliked</c>.</item>
    /// <item>Evidence ≥ 7.5 ⇒ <c>keep</c> / <c>neighbour-loved</c>.</item>
    /// <item>Anything between ⇒ <c>unsure</c> / <c>insufficient-signal</c>.</item>
    /// </list>
    ///
    /// <para>Confidence is the distance of the evidence from the neutral 6.25, scaled onto
    /// [0.50, 0.95] and rounded to two places, so a stub run produces a spread rather than a
    /// constant and the aggregation rules are exercised on both sides of the 0.70 Out threshold.</para>
    /// </summary>
    internal static CrossReferenceEntry StubVerdict(CrossReferenceCandidate candidate)
    {
        double? neighbourMean = candidate.Neighbours.Count > 0
            ? candidate.Neighbours.Average(n => (double)n.Rating)
            : null;
        double? predicted = candidate.Predicted is { } p ? (double)p : null;

        var evidence = (predicted, neighbourMean) switch
        {
            ({ } a, { } b) => (a + b) / 2d,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => (double?)null,
        };

        if (evidence is not { } score)
        {
            return new CrossReferenceEntry(
                candidate.TitleId, CrossReferenceVerdict.Unsure, CrossReferenceReasons.InsufficientSignal, 0.50d);
        }

        var confidence = Math.Round(Math.Clamp(0.50d + (Math.Abs(score - 6.25d) / 8d), 0.50d, 0.95d), 2);
        return score switch
        {
            <= 5.0d => new CrossReferenceEntry(
                candidate.TitleId, CrossReferenceVerdict.Out, CrossReferenceReasons.NeighbourDisliked, confidence),
            >= 7.5d => new CrossReferenceEntry(
                candidate.TitleId, CrossReferenceVerdict.Keep, CrossReferenceReasons.NeighbourLoved, confidence),
            _ => new CrossReferenceEntry(
                candidate.TitleId, CrossReferenceVerdict.Unsure, CrossReferenceReasons.InsufficientSignal, 0.50d),
        };
    }
}
