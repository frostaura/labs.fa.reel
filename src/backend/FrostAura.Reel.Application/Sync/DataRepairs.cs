using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Sync;

/// <summary>What one repair changed on one pass. Zero on every pass after the first is the idempotency proof.</summary>
public sealed record DataRepairResult(string Name, int RowsChanged);

/// <summary>
/// The one-off data repairs that WS-4's integrity fixes need on an existing database, written so
/// they can ride every boot: each is <b>idempotent</b> (a second pass changes nothing) and
/// <b>self-terminating</b> (its predicate can only ever match rows written before the fix, so it
/// never reaches a row the fixed code produced). That is what lets them be a startup job rather
/// than a runbook step somebody has to remember on the live host.
///
/// <para><b>Order matters and is fixed.</b> <see cref="SettleAnsweredFailuresAsync"/> runs before
/// <see cref="ReopenUnansweredFailuresAsync"/>: the first takes every Failed row that holds an
/// answer out of the Failed set, so the second's "Failed with no answer" predicate can never see one
/// that has an answer.</para>
///
/// <para>Each repair runs across every tenant, so it reads with query filters off — a repair is a
/// system operation and must not depend on whether the caller happens to have an account pinned.</para>
/// </summary>
public static class DataRepairs
{
    public const string RevokeConsumedSavesName = "revoke-consumed-saves";

    public const string SettleAnsweredFailuresName = "settle-answered-attribute-failures";

    public const string ReopenUnansweredFailuresName = "reopen-unanswered-attribute-failures";

    /// <summary>Runs every repair once, in order, and reports what each changed.</summary>
    public static async Task<IReadOnlyList<DataRepairResult>> RunAllAsync(
        IReelDbContext db, DateTime nowUtc, CancellationToken ct) =>
    [
        new(RevokeConsumedSavesName, await RevokeConsumedSavesAsync(db, nowUtc, ct)),
        new(SettleAnsweredFailuresName, await SettleAnsweredFailuresAsync(db, ct)),
        new(ReopenUnansweredFailuresName, await ReopenUnansweredFailuresAsync(db, ct)),
    ];

    /// <summary>
    /// Revokes every live save, on every account, whose title the account has fully watched or
    /// rated — the 116 watched-and-rated titles on the founder's Saved shelf (G-1). Revoked, never
    /// deleted. Idempotent by construction: a revoked save is no longer live. Not one-off in spirit
    /// either — it is the reconciler's own rule, so a pass that finds something is a drift repair.
    /// </summary>
    public static Task<int> RevokeConsumedSavesAsync(IReelDbContext db, DateTime nowUtc, CancellationToken ct) =>
        db.UserTitleReactions
            .IgnoreQueryFilters()
            .Where(r => r.Kind == ReactionKind.SaveForLater && r.RevokedAt == null)
            .Where(r =>
                db.WatchedTitles.IgnoreQueryFilters()
                    .Any(w => w.AccountId == r.AccountId && w.TitleId == r.TitleId && w.IsFullyWatched)
                || db.UserRatings.IgnoreQueryFilters()
                    .Any(u => u.AccountId == r.AccountId && u.TitleId == r.TitleId))
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.RevokedAt, nowUtc), ct);

    /// <summary>
    /// Takes the pre-fix Failed rows that still hold a complete answer out of the Failed set — the
    /// 1,077 that a stale concurrent run demoted after another run had answered (E*2, NEW-1/NEW-2).
    ///
    /// <para><b>Provenance is restored, not assumed.</b> The old handler stamped the CURRENT model
    /// and version on a row before it knew the outcome, so a row answered under gpt-5.4-mini and then
    /// failed under gpt-6-luna carries a luna stamp on mini values. Each row is matched to the
    /// extraction era its <c>ExtractedAt</c> falls in — the eras being the (model, version) pairs
    /// that have Done rows, each starting at its earliest Done extraction — and restamped to that
    /// era. A row answered in the current era stays as it is and becomes Done. A row from an older
    /// era becomes Done under that era's stamp: usable today, and eligible for re-extraction behind
    /// the never-attributed backlog. A row older than every era (the prompt generation before the
    /// current one) is not trusted at all: it goes back to Pending at version 0, invisible to readers
    /// and first in line for a fresh extraction.</para>
    ///
    /// <para>Self-terminating: only rows the fixed handler never touched (<c>LastAttemptAt IS
    /// NULL</c>) are eligible, and the fixed handler never writes Failed onto a row with an
    /// answer.</para>
    /// </summary>
    public static Task<int> SettleAnsweredFailuresAsync(IReelDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            WITH eras AS (
                SELECT "ExtractorModel" AS model, "ExtractorVersion" AS version, MIN("ExtractedAt") AS since
                FROM "TitleAttributes"
                WHERE "Status" = 'Done' AND "ExtractedAt" IS NOT NULL
                GROUP BY "ExtractorModel", "ExtractorVersion"
            ),
            target AS (
                SELECT a."TitleId", era.model, era.version
                FROM "TitleAttributes" a
                LEFT JOIN LATERAL (
                    SELECT e.model, e.version
                    FROM eras e
                    WHERE a."ExtractedAt" IS NOT NULL AND e.since <= a."ExtractedAt"
                    ORDER BY e.since DESC
                    LIMIT 1
                ) era ON TRUE
                WHERE a."Status" = 'Failed' AND a."RawJson" IS NOT NULL AND a."LastAttemptAt" IS NULL
            )
            UPDATE "TitleAttributes" t
            SET "Status" = CASE WHEN target.model IS NULL THEN 'Pending' ELSE 'Done' END,
                "ExtractorModel" = COALESCE(target.model, t."ExtractorModel"),
                "ExtractorVersion" = COALESCE(target.version, 0),
                "AttemptCount" = 0
            FROM target
            WHERE t."TitleId" = target."TitleId"
            """,
            ct);

    /// <summary>
    /// Returns the pre-fix Failed rows that hold no answer to the queue with a fresh attempt budget —
    /// the 961 titles whose two attempts were spent on dropped connections, not on bad answers (E-6).
    /// Self-terminating for the same reason as <see cref="SettleAnsweredFailuresAsync"/>: once the
    /// fixed handler attempts a row it stamps <c>LastAttemptAt</c>, and this predicate never matches
    /// it again, so a genuinely unextractable title cannot be re-opened by every restart.
    /// </summary>
    public static Task<int> ReopenUnansweredFailuresAsync(IReelDbContext db, CancellationToken ct) =>
        db.TitleAttributes
            .Where(a => a.Status == AttributeExtractionStatus.Failed && a.RawJson == null && a.LastAttemptAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, AttributeExtractionStatus.Pending)
                .SetProperty(a => a.AttemptCount, 0), ct);

    /// <summary>
    /// The read-only form of each repair's predicate — what a pass WOULD change right now. For the
    /// hand-off and for acceptance: after a pass every count is zero.
    /// </summary>
    public static async Task<IReadOnlyList<DataRepairResult>> PendingAsync(IReelDbContext db, CancellationToken ct) =>
    [
        new(RevokeConsumedSavesName, await db.UserTitleReactions
            .IgnoreQueryFilters()
            .Where(r => r.Kind == ReactionKind.SaveForLater && r.RevokedAt == null)
            .CountAsync(r =>
                db.WatchedTitles.IgnoreQueryFilters()
                    .Any(w => w.AccountId == r.AccountId && w.TitleId == r.TitleId && w.IsFullyWatched)
                || db.UserRatings.IgnoreQueryFilters()
                    .Any(u => u.AccountId == r.AccountId && u.TitleId == r.TitleId), ct)),
        new(SettleAnsweredFailuresName, await db.TitleAttributes.CountAsync(
            a => a.Status == AttributeExtractionStatus.Failed && a.RawJson != null && a.LastAttemptAt == null, ct)),
        new(ReopenUnansweredFailuresName, await db.TitleAttributes.CountAsync(
            a => a.Status == AttributeExtractionStatus.Failed && a.RawJson == null && a.LastAttemptAt == null, ct)),
    ];
}
