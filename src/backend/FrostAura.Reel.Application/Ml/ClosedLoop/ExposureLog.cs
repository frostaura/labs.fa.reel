using System.Globalization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace FrostAura.Reel.Application.Ml.ClosedLoop;

/// <summary>
/// One card about to be logged. Everything the exposure row needs that the caller already has —
/// nothing here is re-derived from the database, because a serve path must not pay a second read
/// to record what it is already returning.
/// </summary>
/// <param name="TitleId">The title on the card.</param>
/// <param name="Rank">Position within the row, zero-based.</param>
/// <param name="PredictedRating">The prediction behind the card, at full precision — rounded to 2 dp here.</param>
/// <param name="BaseScore">
/// Base model score plus the online residual, before calibration and demotion. <b>Null means the
/// surface could not recover the decomposition</b> — a refill off a persisted TitleScore, a digest
/// card — and is written as NULL. It used to default to zero, and a zero is a claim: 1,800+ rows
/// on four surfaces said "the model scored this 0 and calibration lifted it to 7.4", which no
/// accuracy query can tell apart from a real breakdown. The four breakdown fields travel together
/// or not at all (see <see cref="ExposureLog"/>).
/// </param>
/// <param name="CalibrationDelta">How far the monotone calibrator moved <paramref name="BaseScore"/>. Null with it.</param>
/// <param name="ResidualApplied">The online residual head's scalar contribution. Null with it.</param>
/// <param name="DemotionApplied">The dislike demotion subtracted after calibration. Null with it.</param>
/// <param name="LlmFit">The re-rank fit where the surface computed one.</param>
/// <param name="WhySentence">The exact why-line rendered.</param>
/// <param name="WhySource">
/// How that sentence was produced — typed, and the only way to say it: there is no conversion from
/// a string. Left at its default it records <see cref="ExposureWhySource.Unknown"/>, which the log
/// accepts and reports (see <see cref="ExposureLog.Stage"/>) but the trust acceptance counts to zero.
/// </param>
/// <param name="RowKey">The rail's identity, anchor-qualified; null on hero/digest/search surfaces.</param>
/// <param name="PageIndex">Pagination depth; 0 on the first page and every non-paginated surface.</param>
/// <param name="PolicyId">The ranking policy that drafted this card, when a slate was interleaved.</param>
/// <param name="InterleaveGroup">The slate the draft belonged to. Null wherever PolicyId is null.</param>
/// <param name="XrefVerdict">
/// The cross-reference standing this card carried at the moment it was served — an
/// <c>AggregateVerdict</c> name. <b>Null means no verdict existed</b>, never "Unsure": a serve path
/// that has not looked leaves it null, and the difference is unrecoverable afterwards.
/// </param>
/// <param name="XrefConfidence">The confidence behind that standing. Null wherever the verdict is.</param>
public readonly record struct ExposureCard(
    Guid TitleId,
    int Rank,
    decimal PredictedRating,
    decimal? BaseScore = null,
    decimal? CalibrationDelta = null,
    decimal? ResidualApplied = null,
    decimal? DemotionApplied = null,
    decimal? LlmFit = null,
    string? WhySentence = null,
    ExposureWhySource WhySource = default,
    string? RowKey = null,
    int PageIndex = 0,
    string? PolicyId = null,
    Guid? InterleaveGroup = null,
    string? XrefVerdict = null,
    decimal? XrefConfidence = null);

/// <summary>
/// Writes the exposure log. The rule this class exists to enforce is that <b>recording a serve may
/// never break the serve</b>: a card the user can see is worth more than the row that says so.
///
/// <para><b>Two entry points, deliberately.</b> <see cref="Stage"/> adds rows to the caller's
/// already-tracked context and saves nothing — that is what the feed build uses, so the exposures
/// commit in the same transaction as the snapshot they describe, at zero extra round-trips, and
/// the snapshot id it keys on was minted by that same build, so nothing can already hold it.
/// <see cref="RecordAsync"/> is what read-path endpoints use, because a GET has no transaction of
/// its own to ride.</para>
///
/// <para><b>Idempotency on the read paths is the index, row by row.</b> <see cref="RecordAsync"/>
/// writes the whole batch as one <c>INSERT … ON CONFLICT DO NOTHING</c>, so a row that collides
/// on <c>(AccountId, TitleId, Surface, FeedSnapshotId, MinuteBucket, SourceTurnId)</c> is skipped
/// by itself and every other row in the batch still lands. It used to stage the batch on the
/// caller's context and save it as a unit, and one collision — Home fires nine <c>/feed/more</c>
/// requests within 165 ms, and two genre rails could serve the same film inside one minute —
/// threw a 23505 that detached and LOST THE WHOLE BATCH: 39 batches in 28 minutes of retained
/// logs. It also means a read path no longer saves whatever else the caller happened to have
/// tracked, which a context-wide <c>SaveChangesAsync</c> silently did.</para>
/// </summary>
public sealed class ExposureLog(IReelDbContext db, ILogger<ExposureLog> logger)
{
    /// <summary>
    /// Stages exposure rows on the caller's context WITHOUT saving. Rows already present for the
    /// same idempotency key are skipped. Returns the entries staged, so a caller that later needs
    /// to abandon them (a guarded save) can detach exactly those.
    /// </summary>
    /// <remarks>
    /// Every card states its <see cref="ExposureCard.WhySource"/>. A card left at
    /// <see cref="ExposureWhySource.Unknown"/> is still written — the serve happened — and the batch
    /// logs one warning naming the surface and the count, so an unattributed writer is visible in
    /// the logs the day it ships rather than in a month's accuracy numbers.
    /// </remarks>
    /// <param name="sourceTurnId">
    /// The Ask Reel turn that served these cards, where one did. Trailing and optional because it
    /// is meaningful on exactly one of the eight write sites: every other surface passes nothing
    /// and its rows keep the null that honestly says "this surface does not have a turn".
    /// </param>
    public async Task<IReadOnlyList<EntityEntry<ExposureEvent>>> Stage(
        Guid accountId,
        ExposureSurface surface,
        Guid? feedSnapshotId,
        Guid modelArtifactId,
        DateTime occurredAt,
        IReadOnlyList<ExposureCard> cards,
        CancellationToken ct,
        Guid? sourceTurnId = null)
    {
        if (cards.Count == 0)
        {
            return [];
        }

        var minuteBucket = Truncate(occurredAt);
        var titleIds = cards.Select(c => c.TitleId).Distinct().ToList();

        // The already-logged set for this exact key. Scoped by the caller's account explicitly as
        // well as by the global filter: this method is also reachable from a background job whose
        // context is unpinned, where the filter admits every tenant.
        //
        // SourceTurnId is in the predicate because it is in the index. Mirroring the index here is
        // what keeps the cheap pre-filter and the structural guarantee saying the same thing; a
        // pre-filter that is stricter than the index silently drops rows the database would have
        // accepted.
        var alreadyLogged = (await db.ExposureEvents
                .Where(e => e.AccountId == accountId
                    && e.Surface == surface
                    && e.FeedSnapshotId == feedSnapshotId
                    && e.MinuteBucket == minuteBucket
                    && (sourceTurnId == null ? e.SourceTurnId == null : e.SourceTurnId == sourceTurnId)
                    && titleIds.Contains(e.TitleId))
                .Select(e => e.TitleId)
                .ToListAsync(ct))
            .ToHashSet();

        var rows = Rows(accountId, surface, feedSnapshotId, modelArtifactId, occurredAt, cards, sourceTurnId, alreadyLogged);
        var staged = rows.Select(r => db.ExposureEvents.Add(r)).ToList();
        WarnIfUnattributed(surface, rows);
        return staged;
    }

    /// <summary>
    /// Writes the batch, swallowing every failure. Returns the number of rows written; zero is a
    /// perfectly ordinary answer (everything was already logged this minute).
    ///
    /// <para>One statement, <c>ON CONFLICT DO NOTHING</c>: a row whose idempotency key is already
    /// taken — by an RTK refetch, by a concurrent request, by the same film on a second rail in the
    /// same minute — is skipped on its own, and the rest of the batch still lands. Nothing is
    /// staged on the caller's context and nothing else the caller has tracked is saved.</para>
    ///
    /// <para>The catch is deliberately unconditional. A serve path calls this AFTER it has decided
    /// what to return, and there is no failure here — a transient connection drop, a column that
    /// grew a constraint nobody expected — that justifies turning a rendered page into a 500.</para>
    /// </summary>
    /// <inheritdoc cref="Stage" path="/param[@name='sourceTurnId']"/>
    public async Task<int> RecordAsync(
        Guid accountId,
        ExposureSurface surface,
        Guid? feedSnapshotId,
        Guid modelArtifactId,
        DateTime occurredAt,
        IReadOnlyList<ExposureCard> cards,
        CancellationToken ct,
        Guid? sourceTurnId = null)
    {
        if (cards.Count == 0)
        {
            return 0;
        }

        try
        {
            var rows = Rows(accountId, surface, feedSnapshotId, modelArtifactId, occurredAt, cards, sourceTurnId, alreadyLogged: null);
            if (rows.Count == 0)
            {
                return 0;
            }

            WarnIfUnattributed(surface, rows);
            var (sql, parameters) = InsertIgnoringConflicts(rows);
            return await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller is gone; this is not a logging failure
        }
        catch (Exception ex)
        {
            // Error, not Warning. Swallowing the failure is right — the page is already rendered and
            // a 500 here would trade a served card for the row that records it — but swallowing it
            // QUIETLY is what let the exposure log sit at zero rows for a day without anyone's
            // dashboard turning a colour. The catch keeps the serve; the level keeps the evidence.
            logger.LogError(
                ex,
                "Exposure logging failed for {Surface} on {AccountId} ({Count} cards) — the response is unaffected, "
                + "but the closed loop lost this serve and every accuracy number computed from it is now short.",
                surface, accountId, cards.Count);
            return 0;
        }
    }

    /// <summary>
    /// The minute bucket, computed the same way Postgres computes the generated column. Kept here
    /// rather than inlined so the idempotency read and the stored value can never drift apart.
    /// </summary>
    public static DateTime Truncate(DateTime occurredAt) =>
        new(occurredAt.Year, occurredAt.Month, occurredAt.Day, occurredAt.Hour, occurredAt.Minute, 0, DateTimeKind.Utc);

    /// <summary>
    /// The rows a batch writes, after the in-batch dedupe. A single payload can legitimately carry
    /// one title twice (two rails, one film); the idempotency key does not separate them, so the
    /// first occurrence — the one shown first — is the one recorded.
    /// </summary>
    private static List<ExposureEvent> Rows(
        Guid accountId, ExposureSurface surface, Guid? feedSnapshotId, Guid modelArtifactId, DateTime occurredAt,
        IReadOnlyList<ExposureCard> cards, Guid? sourceTurnId, IReadOnlySet<Guid>? alreadyLogged)
    {
        var utc = AsUtc(occurredAt);
        var seen = new HashSet<Guid>();
        var rows = new List<ExposureEvent>(cards.Count);
        foreach (var card in cards)
        {
            if (alreadyLogged?.Contains(card.TitleId) == true || !seen.Add(card.TitleId))
            {
                continue;
            }

            // The breakdown is all-or-nothing. A surface that knows its base score knows the other
            // three (they are arithmetic on the same scoring pass); a card carrying only some of
            // them is a writer that half-guessed, and half a decomposition is a wrong one.
            var hasBreakdown = card is
            {
                BaseScore: not null, CalibrationDelta: not null, ResidualApplied: not null, DemotionApplied: not null,
            };

            rows.Add(new ExposureEvent
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = card.TitleId,
                OccurredAt = utc,
                Surface = surface,
                RowKey = card.RowKey,
                Rank = card.Rank,
                PageIndex = card.PageIndex,
                FeedSnapshotId = feedSnapshotId,
                ModelArtifactId = modelArtifactId,
                PredictedRating = Math.Round(card.PredictedRating, 2, MidpointRounding.AwayFromZero),
                BaseScore = hasBreakdown ? Round3(card.BaseScore!.Value) : null,
                CalibrationDelta = hasBreakdown ? Round3(card.CalibrationDelta!.Value) : null,
                ResidualApplied = hasBreakdown ? Round3(card.ResidualApplied!.Value) : null,
                DemotionApplied = hasBreakdown ? Round3(card.DemotionApplied!.Value) : null,
                LlmFit = card.LlmFit is { } fit ? Math.Round(fit, 3, MidpointRounding.AwayFromZero) : null,
                WhySentence = string.IsNullOrWhiteSpace(card.WhySentence) ? null : card.WhySentence,
                WhySource = card.WhySource.Name,
                // Attribution is written only where it was actually established. The two travel as
                // a pair — a policy with no slate, or a slate with no policy, would be an exposure
                // the sequential test could neither count nor attribute — so a card missing either
                // records neither.
                PolicyId = card.InterleaveGroup is null ? null : card.PolicyId,
                InterleaveGroup = card.PolicyId is null ? null : card.InterleaveGroup,
                // The two travel as a pair for the same reason the interleaving attribution does:
                // a confidence with no verdict is a number with no claim attached, and a verdict
                // with no confidence cannot be read back against the backtest it is judged by.
                XrefVerdict = card is { XrefVerdict: { } verdict, XrefConfidence: not null } ? verdict : null,
                XrefConfidence = card is { XrefVerdict: not null, XrefConfidence: { } confidence }
                    ? Math.Round(confidence, 3, MidpointRounding.AwayFromZero)
                    : null,
                SourceTurnId = sourceTurnId,
            });
        }

        return rows;
    }

    /// <summary>
    /// Recorded, not refused: an unattributed row is still a true record of a serve, and dropping
    /// it would short the closed loop. But it is a writer defect, so it is loud.
    /// </summary>
    private void WarnIfUnattributed(ExposureSurface surface, IReadOnlyList<ExposureEvent> rows)
    {
        var unattributed = rows.Count(r => ExposureWhySource.Parse(r.WhySource).Kind == WhySourceKind.Unknown);
        if (unattributed > 0)
        {
            logger.LogWarning(
                "Exposure writer for {Surface} staged {Unattributed} of {Count} cards with WhySource=Unknown — "
                + "the writer did not say how the why-line was produced.",
                surface, unattributed, rows.Count);
        }
    }

    /// <summary>
    /// The batch as one multi-row insert that skips any row whose unique key is already taken.
    /// <c>MinuteBucket</c> is a stored generated column and is deliberately not written. Every
    /// value is a TYPED parameter — a raw-SQL null has no CLR type for the provider to map — and
    /// the column list is the only text spliced in, from a constant.
    /// </summary>
    private static (string Sql, object[] Parameters) InsertIgnoringConflicts(IReadOnlyList<ExposureEvent> rows)
    {
        var parameters = new List<object>(rows.Count * InsertColumns.Length);
        var values = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            (object? Value, NpgsqlDbType Type)[] cells =
            [
                (row.Id, NpgsqlDbType.Uuid),
                (row.AccountId, NpgsqlDbType.Uuid),
                (row.TitleId, NpgsqlDbType.Uuid),
                (row.OccurredAt, NpgsqlDbType.TimestampTz),
                (row.Surface.ToString(), NpgsqlDbType.Varchar),
                (row.RowKey, NpgsqlDbType.Varchar),
                (row.Rank, NpgsqlDbType.Integer),
                (row.PageIndex, NpgsqlDbType.Integer),
                (row.FeedSnapshotId, NpgsqlDbType.Uuid),
                (row.ModelArtifactId, NpgsqlDbType.Uuid),
                (row.PredictedRating, NpgsqlDbType.Numeric),
                (row.BaseScore, NpgsqlDbType.Numeric),
                (row.CalibrationDelta, NpgsqlDbType.Numeric),
                (row.ResidualApplied, NpgsqlDbType.Numeric),
                (row.DemotionApplied, NpgsqlDbType.Numeric),
                (row.LlmFit, NpgsqlDbType.Numeric),
                (row.WhySentence, NpgsqlDbType.Text),
                (row.WhySource, NpgsqlDbType.Varchar),
                (row.PolicyId, NpgsqlDbType.Varchar),
                (row.InterleaveGroup, NpgsqlDbType.Uuid),
                (row.XrefVerdict, NpgsqlDbType.Varchar),
                (row.XrefConfidence, NpgsqlDbType.Numeric),
                (row.SourceTurnId, NpgsqlDbType.Uuid),
            ];
            var placeholders = new string[cells.Length];
            for (var i = 0; i < cells.Length; i++)
            {
                var name = "e" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                placeholders[i] = "@" + name;
                parameters.Add(new NpgsqlParameter(name, cells[i].Type) { Value = cells[i].Value ?? DBNull.Value });
            }

            values.Add("(" + string.Join(", ", placeholders) + ")");
        }

        var sql = "INSERT INTO \"ExposureEvents\" (" + string.Join(", ", InsertColumns.Select(c => "\"" + c + "\""))
            + ") VALUES " + string.Join(", ", values) + " ON CONFLICT DO NOTHING";
        return (sql, [.. parameters]);
    }

    /// <summary>Every written column, in the order <see cref="InsertIgnoringConflicts"/> supplies them.</summary>
    private static readonly string[] InsertColumns =
    [
        nameof(ExposureEvent.Id), nameof(ExposureEvent.AccountId), nameof(ExposureEvent.TitleId),
        nameof(ExposureEvent.OccurredAt), nameof(ExposureEvent.Surface), nameof(ExposureEvent.RowKey),
        nameof(ExposureEvent.Rank), nameof(ExposureEvent.PageIndex), nameof(ExposureEvent.FeedSnapshotId),
        nameof(ExposureEvent.ModelArtifactId), nameof(ExposureEvent.PredictedRating), nameof(ExposureEvent.BaseScore),
        nameof(ExposureEvent.CalibrationDelta), nameof(ExposureEvent.ResidualApplied),
        nameof(ExposureEvent.DemotionApplied), nameof(ExposureEvent.LlmFit), nameof(ExposureEvent.WhySentence),
        nameof(ExposureEvent.WhySource), nameof(ExposureEvent.PolicyId), nameof(ExposureEvent.InterleaveGroup),
        nameof(ExposureEvent.XrefVerdict), nameof(ExposureEvent.XrefConfidence), nameof(ExposureEvent.SourceTurnId),
    ];

    /// <summary>
    /// The model-wide UTC rule (<c>UtcDateTimeConverter</c>) restated for the one write that does
    /// not go through EF's converters: Unspecified is declared UTC, Local converts.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static decimal Round3(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
