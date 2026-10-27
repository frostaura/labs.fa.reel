using FrostAura.Reel.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Ranking.Interleaving;

/// <summary>
/// The interleaving switch, resolved once from the environment.
///
/// <para><b>Off by default, and that default is a commitment.</b> With
/// <see cref="Enabled"/> false the feed build takes exactly the path it took before this harness
/// existed — no policy is consulted, no draft runs, no <c>PolicyId</c> is written — so every test
/// in the repository describes the shipped behaviour whether or not it knows this type exists.
/// Turning it on is a founder action, documented in <c>docs/interleaving.md</c>.</para>
///
/// <para><b>Why the statistical parameters live here too.</b> The mixing variance and α are part of
/// the experiment's definition, not of its reading: changing either mid-flight changes what the
/// always-valid p-value MEANS, so they belong beside the switch that starts the experiment and are
/// reported on the panel next to the number they produced.</para>
/// </summary>
public sealed class InterleaveOptions
{
    /// <summary>Master switch. Off ⇒ the feed is exactly today's behaviour.</summary>
    public const string EnabledKey = "INTERLEAVE_ENABLED";

    /// <summary>Team A's policy id. Default: the served ranker.</summary>
    public const string PolicyAKey = "INTERLEAVE_POLICY_A";

    /// <summary>Team B's policy id. Default: the eval harness's popularity comparator.</summary>
    public const string PolicyBKey = "INTERLEAVE_POLICY_B";

    /// <summary>The mSPRT mixing standard deviation on the preference rate. Default 0.15.</summary>
    public const string TauKey = "INTERLEAVE_MSPRT_TAU";

    /// <summary>The always-valid type-I ceiling. Default 0.05.</summary>
    public const string AlphaKey = "INTERLEAVE_ALPHA";

    /// <summary>
    /// When the experiment started, as an ISO-8601 UTC instant (<c>2026-11-02T00:00:00Z</c>). Unset:
    /// every interleaved slate ever served counts, and the challenger refits with each evaluation.
    /// </summary>
    public const string SinceKey = "INTERLEAVE_SINCE";

    /// <summary>Whether a Fresh build drafts its slates between two policies.</summary>
    public bool Enabled { get; init; }

    /// <summary>Team A's policy id.</summary>
    public string PolicyA { get; init; } = CalibratedModelPolicy.Id;

    /// <summary>Team B's policy id.</summary>
    public string PolicyB { get; init; } = PopularityBaselinePolicy.Id;

    /// <summary>
    /// The mixing standard deviation τ on the preference rate. 0.15 is deliberate: it centres the
    /// mixture on the 65/35 preference the panel named as the effect worth detecting, which is what
    /// makes the test most powerful exactly there.
    /// </summary>
    public double Tau { get; init; } = 0.15d;

    /// <summary>The always-valid type-I ceiling. Peeking at any time is safe at this level.</summary>
    public double Alpha { get; init; } = 0.05d;

    /// <summary>
    /// The experiment's start (D-14: interleaving starts once the ranker and the pool are frozen).
    /// Set, it freezes the comparison twice over: only slates served from this instant count, and a
    /// fitted challenger keeps the weights its pre-registered fit had at this instant instead of
    /// refitting with every evaluation — an arm that moves during the experiment is two experiments
    /// in one p-value. Moving it restarts the comparison, as reconfiguring the arms already does.
    /// </summary>
    public DateTime? Since { get; init; }

    /// <summary>The posture every test and every unconfigured host gets.</summary>
    public static InterleaveOptions Disabled { get; } = new();

    /// <summary>Reads the switch from configuration; anything unparseable degrades to the default.</summary>
    public static InterleaveOptions From(IConfiguration? configuration) => new()
    {
        Enabled = FeatureFlags.IsEnabled(configuration, EnabledKey, false),
        PolicyA = RankingPolicyCatalog.Normalise(configuration?[PolicyAKey], CalibratedModelPolicy.Id),
        PolicyB = RankingPolicyCatalog.Normalise(configuration?[PolicyBKey], PopularityBaselinePolicy.Id),
        Tau = ReadPositive(configuration, TauKey, 0.15d),
        Alpha = ReadUnitInterval(configuration, AlphaKey, 0.05d),
        Since = ReadInstant(configuration, SinceKey),
    };

    /// <summary>An ISO-8601 instant read invariant and normalised to UTC; anything unreadable is unset.</summary>
    private static DateTime? ReadInstant(IConfiguration? configuration, string key) =>
        configuration?[key] is { Length: > 0 } raw
        && DateTime.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    private static double ReadPositive(IConfiguration? configuration, string key, double fallback) =>
        configuration?[key] is { } raw
        && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0d
            ? parsed
            : fallback;

    private static double ReadUnitInterval(IConfiguration? configuration, string key, double fallback) =>
        configuration?[key] is { } raw
        && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed is > 0d and < 1d
            ? parsed
            : fallback;
}

/// <summary>
/// Resolves a policy id to its implementation. A closed set, on purpose: an unknown id in the
/// environment falls back to the default rather than throwing at build time, because a typo in a
/// compose file must not be able to stop the feed from being assembled.
/// </summary>
public static class RankingPolicyCatalog
{
    private static readonly IRankingPolicy[] All =
    [
        new CalibratedModelPolicy(),
        new PopularityBaselinePolicy(),
        new ProfileRerankPolicy(),
        new TransparentBlendPolicy(),
    ];

    /// <summary>Every policy the harness can run, for the founder-facing panel.</summary>
    public static IReadOnlyList<IRankingPolicy> Policies => All;

    /// <summary>The policy for an id, or null when the id is not one of ours.</summary>
    public static IRankingPolicy? Find(string? policyId) =>
        policyId is null ? null : All.FirstOrDefault(p => string.Equals(p.PolicyId, policyId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The policy for an id, falling back to the calibrated model.</summary>
    public static IRankingPolicy Resolve(string? policyId) => Find(policyId) ?? All[0];

    /// <summary>The canonical spelling of a configured id, or <paramref name="fallback"/> when unknown.</summary>
    public static string Normalise(string? policyId, string fallback) => Find(policyId)?.PolicyId ?? fallback;
}
