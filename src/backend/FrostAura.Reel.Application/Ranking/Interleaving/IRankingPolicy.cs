namespace FrostAura.Reel.Application.Ranking.Interleaving;

/// <summary>
/// One scored candidate, reduced to the numbers a RANKER is allowed to see. Deliberately not the
/// feed builder's own <c>Candidate</c>: a policy that could reach the <c>Title</c> would be able to
/// re-derive signals the ranking comparison is supposed to hold constant, and the interleaving
/// harness's whole value is that two policies are ordering exactly the same information.
///
/// <para><b>Why the caller supplies <see cref="Freshness"/>, <see cref="PreferenceBoost"/> and
/// <see cref="SlateAffinity"/> rather than the policy computing them.</b> Today's feed does not use
/// one ordering — the hero multiplies by freshness, a because-you-loved rail multiplies by
/// <c>0.5 + similarity(anchor)</c>, deep cuts multiplies by neither. Folding the slate-specific
/// terms into the candidate lets <see cref="CalibratedModelPolicy"/> reproduce today's order on
/// every slate exactly, with one formula and no per-slate branches — which is the property that
/// makes "interleaving off ⇒ the feed is byte-for-byte what it was" checkable.</para>
/// </summary>
/// <param name="TitleId">The title.</param>
/// <param name="Predicted">The calibrated predicted rating — what the card shows.</param>
/// <param name="Freshness">The build's freshness multiplier, or 1 on a slate that does not use it.</param>
/// <param name="PreferenceBoost">The declared-preference-tag multiplier, or 1.</param>
/// <param name="Popularity">Present-day TMDB popularity — the popularity baseline's ENTIRE signal.</param>
/// <param name="SlateAffinity">The slate's own multiplier (a rail's anchor similarity term), or 1.</param>
/// <param name="TastePrior">
/// The account-level taste prior for this title (see <c>TastePrior</c>): the no-history-genre
/// demotion, the volume-weighted genre-affinity term and the media prior, pre-multiplied. 1 where
/// the prior has nothing to say. Supplied by the caller for the same reason freshness is — it is a
/// property of the ACCOUNT and the title, not of the ranker, and both policies must be ordering
/// exactly the same information.
/// </param>
/// <param name="LlmFit">The re-rank fit where one has been observed for this title; otherwise null.</param>
/// <param name="CrowdPrior">
/// The title's crowd term (see <c>CrowdPrior</c>): a bounded multiplier read off the vote-shrunk,
/// Trakt-first crowd score, 1 where there is none. Supplied by the caller for the same reason the
/// taste prior is, and kept separate from it because it is a property of the title, not of the
/// account: folding it into <c>TastePrior</c> would inflate the build's taste-prior magnitude.
/// </param>
/// <param name="Challenger">
/// The transparent challenger's signals for this title (see <see cref="TransparentBlendPolicy"/>),
/// or null where the caller did not read them; only that policy looks at them.
/// </param>
public readonly record struct PolicyCandidate(
    Guid TitleId,
    decimal Predicted,
    decimal Freshness,
    double PreferenceBoost,
    decimal Popularity,
    double SlateAffinity = 1d,
    double TastePrior = 1d,
    double? LlmFit = null,
    double CrowdPrior = 1d,
    ChallengerSignals? Challenger = null);

/// <summary>
/// A ranker, as the interleaving harness sees one: an id that ends up on the exposure row, and a
/// total order over a candidate pool.
///
/// <para><b>The contract is a PERMUTATION.</b> An implementation must return every candidate it was
/// given, exactly once, and must be deterministic — the team-draft interleaver relies on both. A
/// policy that filtered would silently shrink its own team's supply and bias the comparison toward
/// whichever ranker was more permissive; a policy that was non-deterministic would break the seeded
/// reproducibility the exposure log's <c>InterleaveGroup</c> is supposed to guarantee.</para>
/// </summary>
public interface IRankingPolicy
{
    /// <summary>
    /// Stable identifier, persisted on every exposure row this policy contributed. Stable across
    /// deploys: changing it orphans the attribution of every slate already in flight.
    /// </summary>
    string PolicyId { get; }

    /// <summary>One-line description for the founder-facing panel.</summary>
    string Description { get; }

    /// <summary>Orders the pool best-first. Must be a deterministic permutation of the input.</summary>
    IReadOnlyList<PolicyCandidate> Order(IReadOnlyList<PolicyCandidate> candidates);
}

/// <summary>
/// The order the feed serves today: calibrated prediction × the taste prior × the crowd term ×
/// freshness × preference boost, times the slate's own affinity term. This is policy A in the honest first
/// comparison, and it exists as a named policy so that "what the product does now" is a thing the
/// harness can measure rather than an implicit baseline.
/// </summary>
public sealed class CalibratedModelPolicy : IRankingPolicy
{
    /// <summary>The id written to <c>ExposureEvent.PolicyId</c>.</summary>
    public const string Id = "CalibratedModel";

    /// <inheritdoc />
    public string PolicyId => Id;

    /// <inheritdoc />
    public string Description => "Calibrated prediction × taste prior × crowd term × freshness × preference boost — today's served order.";

    /// <summary>
    /// The served ordering key. Public because the other policies fall back to it and the tests pin
    /// that it reproduces the feed builder's own comparator.
    /// </summary>
    public static decimal Key(PolicyCandidate c) =>
        c.Predicted * (decimal)c.TastePrior * (decimal)c.CrowdPrior * c.Freshness * (decimal)c.PreferenceBoost
            * (decimal)c.SlateAffinity;

    /// <inheritdoc />
    public IReadOnlyList<PolicyCandidate> Order(IReadOnlyList<PolicyCandidate> candidates) =>
        [.. candidates.OrderByDescending(Key).ThenBy(c => c.TitleId)];
}

/// <summary>
/// Crowd popularity within the eligible pool — the SAME comparator the offline eval harness ranks
/// its baseline with (<c>OrderByDescending(TmdbPopularity ?? 0)</c>). That identity is the point:
/// the ledger has been asking "does the model beat popularity" offline for twelve iterations, and
/// this is the only way to put the same question to the live product.
///
/// <para>The harness's own caveat applies unchanged and is NOT softened here: present-day TMDB
/// popularity is 100% of this policy's ranking signal, so it is a strictly present-day opponent.
/// Online that asymmetry does not run the same way it does offline — both policies are ranking the
/// same pool at the same moment — which is precisely why the online instrument is worth having.</para>
/// </summary>
public sealed class PopularityBaselinePolicy : IRankingPolicy
{
    /// <summary>The id written to <c>ExposureEvent.PolicyId</c>.</summary>
    public const string Id = "PopularityBaseline";

    /// <inheritdoc />
    public string PolicyId => Id;

    /// <inheritdoc />
    public string Description => "Crowd popularity within the eligible pool — the eval harness's baseline comparator.";

    /// <inheritdoc />
    public IReadOnlyList<PolicyCandidate> Order(IReadOnlyList<PolicyCandidate> candidates) =>
        [.. candidates.OrderByDescending(c => c.Popularity).ThenBy(c => c.TitleId)];
}

/// <summary>
/// Orders by the LLM's fit verdict where one exists, falling back to
/// <see cref="CalibratedModelPolicy"/> for everything else.
///
/// <para><b>This is no longer a stub.</b> It was one for as long as the only fit verdicts came from
/// the why/re-rank pass, which runs AFTER assembly — every candidate's
/// <see cref="PolicyCandidate.LlmFit"/> was null at draft time and this policy was byte-for-byte
/// the calibrated one. A Fresh build now judges its top shortlist slice against the account's taste
/// narrative BEFORE hero and rail selection and hands those verdicts to the draft, so on a Fresh
/// build this is a genuinely different order and a legitimate arm.</para>
///
/// <para><b>Two honest limits remain.</b> Only the pre-judged slice carries a fit — a candidate the
/// pre-pass never reached, or a build where the agent was unavailable or the budget ran out, still
/// falls back to the calibrated key, which is the correct behaviour and not a silent one. And a
/// PRESERVE rebuild never re-drafts at all, so this policy only ever expresses itself on the Fresh
/// builds it was given.</para>
/// </summary>
public sealed class ProfileRerankPolicy : IRankingPolicy
{
    /// <summary>The id written to <c>ExposureEvent.PolicyId</c>.</summary>
    public const string Id = "ProfileRerank";

    /// <inheritdoc />
    public string PolicyId => Id;

    /// <inheritdoc />
    public string Description => "LLM narrative fit where one was judged before assembly, calibrated order otherwise.";

    /// <inheritdoc />
    public IReadOnlyList<PolicyCandidate> Order(IReadOnlyList<PolicyCandidate> candidates) =>
        [.. candidates
            .OrderByDescending(c => c.LlmFit.HasValue)
            .ThenByDescending(c => c.LlmFit ?? 0d)
            .ThenByDescending(CalibratedModelPolicy.Key)
            .ThenBy(c => c.TitleId)];
}
