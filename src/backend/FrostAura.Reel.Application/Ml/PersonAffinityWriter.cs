using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Materialises the account's per-person taste affinities into <see cref="PersonAffinity"/>.
///
/// <para>
/// <b>Why this exists.</b> The entity, the DbSet, the unique index and a READER all shipped —
/// <c>CandidateGenerator.TopPeopleTmdbIdsAsync</c> seeds discovery arm 2 from
/// <c>PersonAffinities</c> where the role is Director or Writer — but nothing ever wrote a row.
/// The table has been empty on every account since it was created, so that arm has silently
/// contributed nothing to every feed build: no exception, no log line, just one fewer supply
/// channel than the design assumes. This is the writer.
/// </para>
///
/// <para>
/// <b>It forks no maths.</b> Affinity and pool size come from <see cref="VerifiedPeople"/>, which
/// is the same code path the why-sentences, the rail captions and the title endpoint's
/// tap-through breakdown read — so a stored row can never quote a different number for a person
/// than the card the user is looking at. <see cref="FeatureVectorBuilder"/> still recomputes
/// affinities from raw ratings as-of the split point during training and evaluation and must
/// never read this table; that is the leakage guarantee stated on the entity, and materialising
/// a serving-time cache does not weaken it.
/// </para>
///
/// <para>
/// <b>Volume.</b> One row per (person, role) the user's taste is verified on — 5,350 rows on the
/// founder's 3,746-rating account (measured 2026-09-10), and it grows roughly linearly with
/// ratings. Small enough to rewrite wholesale on every train rather than to reconcile
/// incrementally, which is what this does: rows that no longer qualify are deleted, so a person
/// whose pool shrank below the bar stops seeding discovery instead of lingering for ever.
/// </para>
/// </summary>
public class PersonAffinityWriter(IReelDbContext db, ILogger<PersonAffinityWriter> logger)
{
    /// <summary>What one refresh changed. Returned rather than logged so callers can assert on it.</summary>
    public readonly record struct Result(int Inserted, int Updated, int Deleted)
    {
        public int Total => Inserted + Updated;
    }

    /// <summary>
    /// Reconciles the account's <see cref="PersonAffinity"/> rows against
    /// <paramref name="taste"/>. <b>Stages the change only — the caller owns
    /// <c>SaveChangesAsync</c></b>, so a train can land its artifact and its affinities in one
    /// write rather than leaving a window where the two disagree.
    /// </summary>
    public async Task<Result> RefreshAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, DateTime now, CancellationToken ct)
    {
        var wanted = await VerifiedPeople.ForAccountAsync(db, taste, accountId, ct);
        var byKey = wanted.ToDictionary(e => (e.PersonId, e.Role));

        var existing = await db.PersonAffinities
            .Where(a => a.AccountId == accountId)
            .ToListAsync(ct);

        var inserted = 0;
        var updated = 0;
        var deleted = 0;

        foreach (var row in existing)
        {
            if (byKey.Remove((row.PersonId, row.Role), out var entry))
            {
                row.Affinity = entry.Average;
                row.RatedTitleCount = entry.TitleCount;
                row.ComputedAt = now;
                updated++;
            }
            else
            {
                // The pool fell below VerifiedPeople.MinRatedTitles (a rating was withdrawn on
                // Trakt, or a credit row was corrected). Leaving it would keep seeding discovery
                // from evidence that no longer exists.
                db.PersonAffinities.Remove(row);
                deleted++;
            }
        }

        foreach (var ((personId, role), entry) in byKey.Select(kv => (kv.Key, kv.Value)))
        {
            db.PersonAffinities.Add(new PersonAffinity
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                PersonId = personId,
                Role = role,
                Affinity = entry.Average,
                RatedTitleCount = entry.TitleCount,
                ComputedAt = now,
            });
            inserted++;
        }

        logger.LogInformation(
            "Person affinities for {AccountId}: {Inserted} new, {Updated} refreshed, {Deleted} retired.",
            accountId, inserted, updated, deleted);

        return new Result(inserted, updated, deleted);
    }

    /// <summary>
    /// True when this account has no affinity rows at all — the nightly backfill's trigger.
    /// Reads through the tenancy filter, which is inert in a background scope (no account set)
    /// and correctly restrictive inside a request, so this can never see another tenant's rows.
    /// </summary>
    public async Task<bool> IsEmptyAsync(Guid accountId, CancellationToken ct) =>
        !await db.PersonAffinities.AnyAsync(a => a.AccountId == accountId, ct);
}
