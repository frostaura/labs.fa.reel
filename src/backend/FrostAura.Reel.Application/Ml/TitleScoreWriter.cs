using System.Globalization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The single write path for <see cref="TitleScore"/> rows: one <c>INSERT … ON CONFLICT DO UPDATE</c>
/// on the <c>(AccountId, TitleId, ModelArtifactId)</c> unique key.
///
/// <para><b>Why not load-then-insert.</b> The three serving writers (the feed build, the paginator
/// and the on-demand scorer) each read the existing rows, then added the missing ones. Home fires
/// nine <c>POST /feed/more</c> requests within 165 ms, every one scoring overlapping titles against
/// the same artifact, so two requests routinely both read "missing" and both inserted: the loser
/// threw <c>23505 … IX_TitleScores_AccountId_TitleId_ModelArtifactId</c> out of
/// <c>SaveChangesAsync</c> and the user's row came back as an HTTP 500. The upsert makes the race
/// a non-event — whichever request lands second simply refreshes the row.</para>
///
/// <para>The write is a raw statement, so nothing is tracked and the caller's context is left as it
/// was. Callers that also rely on their own pending changes being saved still call
/// <c>SaveChangesAsync</c> themselves.</para>
///
/// <para><b>Rows are written in <c>TitleId</c> order, always.</b> A multi-row upsert locks its
/// conflicting rows in VALUES order, so two writers covering the same titles in different orders
/// (the paginator writes rank order, and two rails rank the same titles differently) each held a
/// row the other wanted: 35 deadlocks (<c>40P01</c>) in 15 rounds of 4 concurrent writers alternating
/// ascending and descending order, every one an HTTP 500 out of <c>/feed/more</c>. One global
/// order makes the wait-for graph acyclic. A deadlock that still happens (another statement in the
/// caller's own transaction holding a lock) is retried once when there is no ambient transaction,
/// because the statement is idempotent; inside a transaction it is rethrown, since Postgres has
/// already aborted that transaction.</para>
/// </summary>
public static class TitleScoreWriter
{
    /// <summary>One score to write.</summary>
    /// <param name="TitleId">The scored title.</param>
    /// <param name="PredictedRating">The displayed prediction, already at its stored resolution (2 dp).</param>
    /// <param name="ContributionsJson">The top contributions as a json array.</param>
    public readonly record struct Score(Guid TitleId, decimal PredictedRating, string ContributionsJson);

    /// <summary>
    /// Upserts every score in one statement and returns the number of rows inserted or updated.
    /// Duplicate title ids in one batch keep the LAST entry — Postgres refuses to update one row
    /// twice in a single <c>ON CONFLICT DO UPDATE</c>.
    /// </summary>
    /// <param name="keepStoredContributions">
    /// True for a writer that scores WITHOUT contributions (the on-demand scorer): a new row gets
    /// whatever <see cref="Score.ContributionsJson"/> says (<c>[]</c>), and an existing row keeps the
    /// contributions a build stored rather than having them erased by a number-only refresh.
    /// </param>
    public static async Task<int> UpsertAsync(
        IReelDbContext db, Guid accountId, Guid artifactId, IEnumerable<Score> scores, DateTime scoredAt,
        CancellationToken ct, bool keepStoredContributions = false)
    {
        var byTitle = new Dictionary<Guid, Score>();
        foreach (var score in scores)
        {
            byTitle[score.TitleId] = score;
        }

        if (byTitle.Count == 0)
        {
            return 0;
        }

        var utc = scoredAt.Kind switch
        {
            DateTimeKind.Utc => scoredAt,
            DateTimeKind.Local => scoredAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(scoredAt, DateTimeKind.Utc),
        };

        var parameters = new List<object>(byTitle.Count * 7);
        string Next(object value)
        {
            parameters.Add(value);
            return "{" + (parameters.Count - 1).ToString(CultureInfo.InvariantCulture) + "}";
        }

        var values = byTitle.Values
            .OrderBy(score => score.TitleId)
            .Select(score =>
                "(" + Next(Guid.NewGuid()) + ", " + Next(accountId) + ", " + Next(score.TitleId) + ", "
                + Next(artifactId) + ", " + Next(score.PredictedRating) + ", CAST(" + Next(score.ContributionsJson)
                + " AS jsonb), " + Next(utc) + ")")
            .ToList();

        var sql =
            "INSERT INTO \"TitleScores\" (\"Id\", \"AccountId\", \"TitleId\", \"ModelArtifactId\", \"PredictedRating\", "
            + "\"ContributionsJson\", \"ScoredAt\") VALUES " + string.Join(", ", values)
            + " ON CONFLICT (\"AccountId\", \"TitleId\", \"ModelArtifactId\") DO UPDATE SET "
            + "\"PredictedRating\" = EXCLUDED.\"PredictedRating\", \"ScoredAt\" = EXCLUDED.\"ScoredAt\""
            + (keepStoredContributions ? string.Empty : ", \"ContributionsJson\" = EXCLUDED.\"ContributionsJson\"");

        try
        {
            return await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected
            && db.Database.CurrentTransaction is null)
        {
            return await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
    }
}
