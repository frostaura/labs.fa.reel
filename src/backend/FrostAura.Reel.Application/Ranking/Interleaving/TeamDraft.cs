using System.Security.Cryptography;
using System.Text;

namespace FrostAura.Reel.Application.Ranking.Interleaving;

/// <summary>Which ranker contributed a card. A is the configured <c>INTERLEAVE_POLICY_A</c>.</summary>
public enum InterleaveTeam
{
    /// <summary>The first configured policy.</summary>
    A,

    /// <summary>The second configured policy.</summary>
    B,
}

/// <summary>One drafted slot: the title, and the team whose turn produced it.</summary>
/// <param name="TitleId">The drafted title.</param>
/// <param name="Team">The contributing team.</param>
public readonly record struct DraftPick(Guid TitleId, InterleaveTeam Team);

/// <summary>
/// Team-draft interleaving (Radlinski, Kurup &amp; Joachims 2008) over one slate.
///
/// <para><b>How it works.</b> One coin flip per slate decides who picks first; from there the teams
/// strictly alternate, and on its turn a team takes its own highest-ranked candidate that is still
/// available. A title already drafted by either team is skipped, so the slate never repeats a card
/// and every card is attributable to exactly one ranker. Strict alternation is what makes the
/// contributions balanced by construction — ⌈n/2⌉ and ⌊n/2⌋ — which matters because an unbalanced
/// draft would let the more prolific arm win on supply rather than on preference.</para>
///
/// <para><b>Why the coin flip is seeded and not random.</b> A slate has to be reproducible: the
/// exposure rows are the only record of what was served, and an agent (or the founder) re-running
/// a build must be able to get the same draft back. The seed is derived from the snapshot id and
/// the row key, so two slates in one build differ, two builds differ, and one slate is stable.</para>
///
/// <para><b>Admissibility, and why the draft is stateful.</b> The hero applies MMR diversity against
/// the cards already chosen, and that set is only known mid-draft. So a turn takes a predicate and
/// picks the highest-ranked remaining candidate satisfying it. When the team on turn has no
/// admissible candidate left, the turn yields null and the caller relaxes the predicate and
/// continues — the alternation counter carries across those phases, so relaxing never costs
/// balance. This mirrors the feed's own three-stage hero (diversity → floor → never-open-blank)
/// exactly rather than replacing it.</para>
/// </summary>
public sealed class TeamDraft
{
    private readonly IReadOnlyList<Guid> _a;
    private readonly IReadOnlyList<Guid> _b;
    private readonly bool _aFirst;
    private readonly HashSet<Guid> _taken = [];
    private readonly List<DraftPick> _picks = [];
    private int _turn;

    /// <summary>
    /// Starts a draft between two ordered lists. The lists are normally permutations of one pool —
    /// two policies ordering the same candidates — but nothing here requires that.
    /// </summary>
    public TeamDraft(IReadOnlyList<Guid> orderedA, IReadOnlyList<Guid> orderedB, bool aFirst)
    {
        _a = orderedA;
        _b = orderedB;
        _aFirst = aFirst;
    }

    /// <summary>The picks made so far, in draft order.</summary>
    public IReadOnlyList<DraftPick> Picks => _picks;

    /// <summary>Titles drafted so far — the caller's diversity predicate reads this.</summary>
    public IReadOnlyCollection<Guid> Taken => _taken;

    /// <summary>Whose turn it is next.</summary>
    public InterleaveTeam NextTeam => (_turn % 2 == 0) == _aFirst ? InterleaveTeam.A : InterleaveTeam.B;

    /// <summary>
    /// Runs turns until <paramref name="size"/> picks exist or the team on turn has no admissible
    /// candidate left. Returns the number of picks added by this call.
    /// </summary>
    public int Fill(int size, Func<Guid, bool> admissible)
    {
        var added = 0;
        while (_picks.Count < size)
        {
            var team = NextTeam;
            var list = team == InterleaveTeam.A ? _a : _b;
            var chosen = (Guid?)null;
            foreach (var id in list)
            {
                if (!_taken.Contains(id) && admissible(id))
                {
                    chosen = id;
                    break;
                }
            }

            if (chosen is null)
            {
                // The team on turn is out of admissible supply. Stopping here rather than passing
                // the turn is deliberate: letting the other team fill the gap would hand it extra
                // slots and quietly break the balance the whole comparison rests on.
                break;
            }

            _taken.Add(chosen.Value);
            _picks.Add(new DraftPick(chosen.Value, team));
            _turn++;
            added++;
        }

        return added;
    }

    /// <summary>Convenience for a slate with no admissibility constraint beyond "not already taken".</summary>
    public int Fill(int size) => Fill(size, _ => true);

    /// <summary>
    /// The whole draft in one call — the shape the tests and any slate without a mid-draft
    /// constraint use.
    /// </summary>
    public static IReadOnlyList<DraftPick> Draft(
        IReadOnlyList<Guid> orderedA, IReadOnlyList<Guid> orderedB, int size, bool aFirst)
    {
        var draft = new TeamDraft(orderedA, orderedB, aFirst);
        draft.Fill(size);
        return draft.Picks;
    }
}

/// <summary>
/// Deterministic per-slate randomness. Both values come from one SHA-256 over
/// <c>{snapshotId}|{rowKey}</c>: the low bit is the coin flip, and the first sixteen bytes are the
/// slate's <c>InterleaveGroup</c> id.
///
/// <para>SHA-256 rather than <see cref="string.GetHashCode()"/> because the latter is randomised
/// per process by design — a seed that changed on restart would make a slate unreproducible, which
/// is the one property this type exists to provide. The formatting is invariant throughout: a
/// culture-sensitive Guid or int rendering here would seed differently on a differently-configured
/// host and the drafts would silently stop matching.</para>
/// </summary>
public static class InterleaveSeed
{
    /// <summary>The seed bytes for one slate.</summary>
    public static byte[] Bytes(Guid snapshotId, string rowKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{snapshotId:N}|{rowKey}")));

    /// <summary>True when team A drafts first on this slate.</summary>
    public static bool ADraftsFirst(Guid snapshotId, string rowKey) => (Bytes(snapshotId, rowKey)[0] & 1) == 0;

    /// <summary>The slate's stable group id, written to every exposure row the slate produced.</summary>
    public static Guid GroupId(Guid snapshotId, string rowKey) => new(Bytes(snapshotId, rowKey).AsSpan(0, 16));
}
