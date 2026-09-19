using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FrostAura.Reel.Infrastructure.Persistence;

/// <summary>
/// Turns pgvector's iterative HNSW scan on for every connection this app opens, because WITHOUT it
/// every filtered vector search in Reel silently returns fewer rows than it asked for.
///
/// <para><b>The defect this closes (product, not test hygiene).</b> An HNSW index scan walks the
/// proximity graph collecting at most <c>hnsw.ef_search</c> candidates (default 40) and hands them
/// up to the executor; the query's own <c>WHERE</c> predicate is applied to that window
/// AFTERWARDS. With <c>hnsw.iterative_scan = off</c> — the pgvector default — the scan then simply
/// ends. So <c>WHERE "TitleId" IN (eligible…) ORDER BY "Embedding" &lt;=&gt; @q LIMIT 40</c> does not
/// return the 40 nearest ELIGIBLE titles: it returns however many of the 40 nearest titles OVERALL
/// happened to be eligible, which for a heavy watcher whose catalogue is dominated by titles they
/// have already seen is a small fraction, and can be zero. Writing the eligibility predicate
/// "before" the ordering in LINQ does not help — Postgres pushes <c>ORDER BY … LIMIT</c> into the
/// index either way, and only the planner decides. That is what makes the symptom intermittent
/// rather than absent: the same query returns every eligible row when the planner picks a sort over
/// a sequential scan, and a truncated window when it picks the index.
///
/// <para>Measured on 2026-09-10 against a real catalogue: Ask Reel's semantic arm asked for 40
/// neighbours out of a 20-row eligible set and got 10; a second account's 13-row eligible set
/// returned 6. Both are "the user asked for a tense thriller, we found four" in production.</para>
///
/// <para><b>The remedy</b> is pgvector's own (0.8.0+): with <c>hnsw.iterative_scan</c> set, the scan
/// resumes the graph walk instead of stopping when the filter empties its window, until it has
/// <c>LIMIT</c> rows or <c>hnsw.max_scan_tuples</c> (20 000) is reached. <c>strict_order</c> rather
/// than <c>relaxed_order</c> because both ANN call sites treat the result as distance-ordered —
/// <see cref="Application.Ranking.CandidateRetriever"/> takes the nearest survivors off the head of
/// it — so cheapening the ordering would trade one silent wrongness for another.</para>
///
/// <para><b>Why a connection interceptor and not a per-query setting.</b> The flaw belongs to every
/// filtered ANN query in the codebase, present and future, and a per-call-site <c>SET LOCAL</c>
/// would need its own transaction and would be forgotten by the next arm that is added. It is wired
/// from <see cref="ReelDbContext.OnConfiguring"/> rather than from DI registration so that a context
/// built by hand — the integration fixture does exactly that — cannot end up with a different
/// retrieval contract than the one that runs in production.</para>
///
/// <para><b>On older pgvector.</b> The parameter is set before anything has loaded the vector module
/// into the backend, so Postgres records it as a placeholder and applies it when the module loads —
/// verified against <c>pgvector/pgvector:pg16</c> (0.8.5). On an installation predating iterative
/// scans it stays an inert placeholder rather than failing, and the failure is swallowed regardless:
/// a retrieval setting must never be the reason a connection cannot be opened.</para>
/// </summary>
public sealed class HnswFilteredSearchInterceptor : DbConnectionInterceptor
{
    /// <summary>The single instance — the interceptor holds no state.</summary>
    public static readonly HnswFilteredSearchInterceptor Instance = new();

    /// <summary>
    /// Exposed so a test can assert the exact statement rather than restating it, and so the
    /// integration suite can prove the setting reaches the session.
    /// </summary>
    public const string Statement = "SET hnsw.iterative_scan = 'strict_order'";

    private HnswFilteredSearchInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Statement;
            cmd.ExecuteNonQuery();
        }
        catch (DbException)
        {
            // Deliberately swallowed — see the class remark. A provider that rejects the parameter
            // leaves retrieval where it already was rather than taking the application down.
        }

        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // See above.
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }
}
