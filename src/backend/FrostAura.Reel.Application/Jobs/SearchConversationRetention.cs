using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Ask Reel conversation retention: a conversation whose last turn is older than
/// <c>ASK_RETENTION_DAYS</c> is hard-deleted, cards first, then turns, then the conversation.
///
/// <para>Shaped on <see cref="FeedSnapshotRetention"/> deliberately — same policy-as-a-pure-static,
/// same delete-children-first ordering, same account scoping. Two properties are load-bearing and
/// are what the tests pin.</para>
///
/// <para><b>It is DATE-swept, not depth-capped.</b> A feed snapshot is replaced every build, so
/// "keep the newest seven" is a natural bound; a conversation is a person's own transcript and
/// there is no natural number of them. What makes it prunable at all is age: a chat nobody has
/// touched in three months is not a feature anyone is using, and the rows are cheap only while
/// there is a rule that ends them.</para>
///
/// <para><b>It never touches <c>ExposureEvents</c>.</b> The exposure log is append-only, and its
/// rows outlive the conversations that produced them by design — a dangling
/// <c>ExposureEvent.SourceTurnId</c> after a prune is the correct state, not an orphan to clean up.
/// Deleting exposures here would silently shorten every accuracy number computed from the log, and
/// there would be nothing left to notice it by. The same rule holds for the user-facing conversation
/// delete: this is the second of exactly two places that remove a conversation, and neither may
/// reach the log.</para>
/// </summary>
public static class SearchConversationRetention
{
    /// <summary>The configuration key. Absent or unparseable falls back to <see cref="DefaultRetentionDays"/>.</summary>
    public const string RetentionDaysKey = "ASK_RETENTION_DAYS";

    /// <summary>Ninety days: a quarter of transcript, long enough that nobody notices the edge.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>How long a conversation survives after its last turn.</summary>
    public static int RetentionDays(IConfiguration? configuration) =>
        FeatureFlags.ReadInt(configuration, RetentionDaysKey, DefaultRetentionDays);

    /// <summary>
    /// Deletes every conversation whose <c>LastTurnAt</c> is at or before
    /// <paramref name="cutoff"/>, for <paramref name="accountId"/>. Returns how many went.
    ///
    /// <para>Children first: cards reference turns, turns reference the conversation, and none of
    /// those references is a foreign key — so a wrong order leaves rows nothing can ever reach
    /// again rather than raising an error that would say so.</para>
    /// </summary>
    public static async Task<int> PruneAsync(
        IReelDbContext db, Guid accountId, DateTime cutoff, CancellationToken ct)
    {
        var staleIds = await db.SearchConversations
            .Where(c => c.AccountId == accountId && c.LastTurnAt <= cutoff)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (staleIds.Count == 0)
        {
            return 0;
        }

        await db.SearchTurnCards.Where(c => staleIds.Contains(c.ConversationId)).ExecuteDeleteAsync(ct);
        await db.SearchTurns.Where(t => staleIds.Contains(t.ConversationId)).ExecuteDeleteAsync(ct);
        await db.SearchConversations.Where(c => staleIds.Contains(c.Id)).ExecuteDeleteAsync(ct);

        return staleIds.Count;
    }

    /// <summary>
    /// The nightly sweep across every account that has a conversation at all. Returns the total
    /// number pruned. Reads the account list off the conversations themselves rather than off
    /// <c>TraktConnections</c>: a disconnected account's transcript still ages out, and it is the
    /// one most likely to be sitting there forgotten.
    /// </summary>
    public static async Task<int> SweepAsync(
        IReelDbContext db, IConfiguration? configuration, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow.AddDays(-RetentionDays(configuration));
        var accountIds = await db.SearchConversations
            .Where(c => c.LastTurnAt <= cutoff)
            .Select(c => c.AccountId)
            .Distinct()
            .ToListAsync(ct);

        var pruned = 0;
        foreach (var accountId in accountIds)
        {
            pruned += await PruneAsync(db, accountId, cutoff, ct);
        }

        return pruned;
    }
}
