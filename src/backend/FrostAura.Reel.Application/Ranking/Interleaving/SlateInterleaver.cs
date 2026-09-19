namespace FrostAura.Reel.Application.Ranking.Interleaving;

/// <summary>
/// The feed build's entry point into the harness: two configured policies, and a draft per slate.
///
/// <para>It exists so <c>BuildFeedJobHandler</c> never touches a policy, a seed or a coin flip
/// directly — the build says "draft ten from this pool for this row" and gets back members plus
/// attribution. Everything about HOW the draft runs, including the property that turning the switch
/// off leaves the build on exactly its old path, is decided here and testable without a database.</para>
/// </summary>
public sealed class SlateInterleaver
{
    private readonly IRankingPolicy _a;
    private readonly IRankingPolicy _b;

    /// <summary>Builds an interleaver over two named policies.</summary>
    public SlateInterleaver(IRankingPolicy a, IRankingPolicy b)
    {
        _a = a;
        _b = b;
    }

    /// <summary>
    /// The interleaver a build should use, or null when the experiment is off. Null is the signal
    /// the build branches on, so there is exactly one place that decides whether interleaving
    /// happens and no way for a caller to half-enable it.
    /// </summary>
    public static SlateInterleaver? For(InterleaveOptions options) =>
        options.Enabled
            ? new SlateInterleaver(RankingPolicyCatalog.Resolve(options.PolicyA), RankingPolicyCatalog.Resolve(options.PolicyB))
            : null;

    /// <summary>Team A's policy id — what lands on the exposure rows A contributed.</summary>
    public string PolicyIdA => _a.PolicyId;

    /// <summary>Team B's policy id.</summary>
    public string PolicyIdB => _b.PolicyId;

    /// <summary>Opens a draft for one slate. The pool is ordered independently by each policy.</summary>
    public SlateDraft Begin(Guid snapshotId, string rowKey, IReadOnlyList<PolicyCandidate> pool) =>
        new(
            _a.Order(pool),
            _b.Order(pool),
            InterleaveSeed.ADraftsFirst(snapshotId, rowKey),
            InterleaveSeed.GroupId(snapshotId, rowKey),
            _a.PolicyId,
            _b.PolicyId);
}

/// <summary>One slate, mid-draft. See <see cref="TeamDraft"/> for the algorithm and its guarantees.</summary>
public sealed class SlateDraft
{
    private readonly TeamDraft _draft;
    private readonly string _policyA;
    private readonly string _policyB;

    internal SlateDraft(
        IReadOnlyList<PolicyCandidate> orderedA,
        IReadOnlyList<PolicyCandidate> orderedB,
        bool aFirst,
        Guid groupId,
        string policyA,
        string policyB)
    {
        _draft = new TeamDraft(
            [.. orderedA.Select(c => c.TitleId)],
            [.. orderedB.Select(c => c.TitleId)],
            aFirst);
        GroupId = groupId;
        _policyA = policyA;
        _policyB = policyB;
    }

    /// <summary>The slate id written to every exposure row this draft produced.</summary>
    public Guid GroupId { get; }

    /// <summary>The titles drafted so far, in draft order.</summary>
    public IReadOnlyList<Guid> Selected => [.. _draft.Picks.Select(p => p.TitleId)];

    /// <summary>Adds picks until the slate holds <paramref name="size"/>, or the team on turn is stuck.</summary>
    public void Fill(int size, Func<Guid, bool> admissible) => _draft.Fill(size, admissible);

    /// <summary>The finished slate: each title with the policy id that put it there.</summary>
    public IReadOnlyList<(Guid TitleId, string PolicyId)> Picks =>
        [.. _draft.Picks.Select(p => (p.TitleId, p.Team == InterleaveTeam.A ? _policyA : _policyB))];
}
