using System.Text.Json;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Lens;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Lens;

/// <summary>Where a rendered lens came from. Sent to the client so a stale note can say so.</summary>
public static class LensSource
{
    /// <summary>Written against the account's current taste fingerprint.</summary>
    public const string Fresh = "fresh";

    /// <summary>An older note, kept because old prose beats no prose. Flagged, and regenerated later.</summary>
    public const string Stale = "stale";

    /// <summary>No note at all — the caller renders the templater sentence and the overview.</summary>
    public const string None = "none";
}

/// <summary>One resolved lens, with the honesty about where it came from.</summary>
public sealed record ResolvedLens(LensNote? Note, string Source, string ProfileHash, DateTime? GeneratedAt);

/// <summary>
/// Reads, writes and falls back on "through your lens" copy — the one place the cache key, the
/// budget gate, the guard chain and the three-rung fallback are applied, so a surface cannot
/// accidentally skip one.
///
/// <para><b>The fallback ladder, in order, and never a blank:</b> a note at the current profile
/// hash → the newest note at an older hash, flagged stale → nothing, and the caller renders the
/// deterministic templater sentence over the TMDB overview. A budget-exhausted account or an
/// unavailable provider lands on the same ladder rather than on an error.</para>
///
/// <para><b>A Rejected row is a memory, not a gap.</b> A note refused by a guard — or reported by
/// the user as having spoiled something — is kept at its key so the same generation is not
/// attempted again at that (profile hash, prompt version), and is never rendered.</para>
/// </summary>
public sealed class LensService(
    IReelDbContext db,
    ILensWriter writer,
    LensInputBuilder inputs,
    LensVerifier verifier,
    LensBudget budget,
    PersonalisationContext personalisation,
    ILogger<LensService> logger)
{
    /// <summary>Titles per provider batch. Small: each is one request, and the loop is what paces them.</summary>
    public const int GenerateBatch = 3;

    /// <summary>The on-demand ceiling. Past this the modal shows the fallback rather than a spinner.</summary>
    public static readonly TimeSpan OnDemandCeiling = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan OnDemandBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long a title whose lens call was made and came back unusable is left alone before it is
    /// tried again at the same key. Without it, every open of a title the model cannot write for
    /// queues another paid attempt that fails the same way (WS-1 task 7, F-6).
    /// </summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(6);

    /// <summary>What a title-open's lens request did — see <see cref="RequestAsync"/>.</summary>
    public enum RequestOutcome
    {
        /// <summary>A current note exists; nothing to do.</summary>
        Current,

        /// <summary>A pass that will write this title is queued (new, merged, or already there).</summary>
        Queued,

        /// <summary>Nothing queued: no writer, a refusal on record, a recent failure, a spent budget, or a pass already running.</summary>
        NotQueued,
    }

    /// <summary>The background job's wall clock, and its soft budget. Nothing is waiting on it.</summary>
    public static readonly TimeSpan JobCeiling = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The prompt generation every row this service writes is stamped with.</summary>
    public int PromptVersion => writer.LensPromptVersion;

    /// <summary>
    /// The model-and-effort every row this service writes is stamped with (in <c>ModelId</c>), and
    /// the third part of what makes a note CURRENT beside the profile hash and prompt version. A
    /// model swap or an effort change re-keys the cache the way a prompt bump does: older notes
    /// step down the ladder to stale — still rendered — and are rewritten in place.
    /// </summary>
    public string CacheSalt => writer.LensCacheSalt;

    private bool IsCurrent(TitleLensCopy c, string profileHash, string salt) =>
        c.ProfileHash == profileHash && c.PromptVersion == PromptVersion && c.ModelId == salt;

    /// <summary>
    /// The best note available for one title WITHOUT generating anything. This is what every
    /// render path calls; only the on-demand endpoint and the background job may generate.
    /// </summary>
    public async Task<ResolvedLens> ResolveAsync(
        Guid accountId, Guid titleId, string profileHash, CancellationToken ct)
    {
        var rows = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && c.TitleId == titleId && c.Status == LensStatus.Done)
            .OrderByDescending(c => c.GeneratedAt)
            .Take(4)
            .ToListAsync(ct);

        var salt = CacheSalt;
        var current = rows.FirstOrDefault(c => IsCurrent(c, profileHash, salt));
        if (current is not null && Deserialise(current) is { } fresh)
        {
            return new ResolvedLens(fresh, LensSource.Fresh, current.ProfileHash, current.GeneratedAt);
        }

        var stale = rows.FirstOrDefault(c => c != current);
        if (stale is not null && Deserialise(stale) is { } older)
        {
            return new ResolvedLens(older, LensSource.Stale, stale.ProfileHash, stale.GeneratedAt);
        }

        return new ResolvedLens(null, LensSource.None, profileHash, null);
    }

    /// <summary>
    /// The best note for each of many titles, without generating. Used by the feed and digest
    /// render paths, where one query for the whole page beats one per card.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ResolvedLens>> ResolveManyAsync(
        Guid accountId, IReadOnlyCollection<Guid> titleIds, string profileHash, CancellationToken ct)
    {
        var ids = titleIds.Distinct().ToList();
        var resolved = new Dictionary<Guid, ResolvedLens>();
        if (ids.Count == 0)
        {
            return resolved;
        }

        var rows = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && ids.Contains(c.TitleId) && c.Status == LensStatus.Done)
            .OrderByDescending(c => c.GeneratedAt)
            .ToListAsync(ct);

        var salt = CacheSalt;
        foreach (var group in rows.GroupBy(r => r.TitleId))
        {
            var current = group.FirstOrDefault(c => IsCurrent(c, profileHash, salt));
            if (current is not null && Deserialise(current) is { } fresh)
            {
                resolved[group.Key] = new ResolvedLens(fresh, LensSource.Fresh, current.ProfileHash, current.GeneratedAt);
                continue;
            }

            var stale = group.FirstOrDefault(c => c != current);
            if (stale is not null && Deserialise(stale) is { } older)
            {
                resolved[group.Key] = new ResolvedLens(older, LensSource.Stale, stale.ProfileHash, stale.GeneratedAt);
            }
        }

        return resolved;
    }

    /// <summary>How a generation pass ended, for the caller's log line and the job's progress message.</summary>
    public sealed record GenerateOutcome(int Written, int Rejected, int Skipped, string Reason, int Failed = 0);

    /// <summary>
    /// Generates missing notes for the given titles, in priority order, inside a time box and the
    /// account's daily ceiling. Titles already carrying a note at the current key are skipped
    /// without spending, and so is everything past the point the budget runs out.
    /// </summary>
    public async Task<GenerateOutcome> GenerateAsync(
        Guid accountId,
        FeatureVectorBuilder.TasteState taste,
        string profileHash,
        IReadOnlyList<Guid> titleIds,
        TimeSpan softBudget,
        TimeSpan ceiling,
        CancellationToken ct)
    {
        if (!writer.IsAvailable || titleIds.Count == 0)
        {
            return new GenerateOutcome(0, 0, titleIds.Count, "writer unavailable");
        }

        // Anything already written at this key — including a Rejected row, which is a decision and
        // must not be re-attempted at the same key — needs no spend. A row at the same (hash,
        // prompt) from ANOTHER model or effort is a different generation: it is rewritten in place
        // (the unique index is on hash and prompt), except a user's spoiler report, which stands
        // whatever model asks — the refusal is the record.
        var salt = CacheSalt;
        var atKey = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && titleIds.Contains(c.TitleId)
                && c.ProfileHash == profileHash && c.PromptVersion == PromptVersion)
            .ToListAsync(ct);
        // A Done row the read path can no longer render (a hedged hook written before that rule) is
        // not "known" either: it is rewritten in place, once, under today's guards — otherwise the
        // title would show no note until the taste fingerprint next moved.
        // A Failed row is a negative cache: inside FailureBackoff it is known (no spend), after it
        // the title is tried again, in place.
        var retryAfter = DateTime.UtcNow - FailureBackoff;
        var known = atKey
            .Where(c => (c.ModelId == salt && c.Status switch
                {
                    LensStatus.Done => Deserialise(c) is not null,
                    LensStatus.Failed => c.GeneratedAt > retryAfter,
                    _ => true,
                })
                || c.RejectionReason == LensRejectionReasons.UserReportedSpoiler)
            .Select(c => c.TitleId)
            .ToHashSet();
        var superseded = atKey.Where(c => !known.Contains(c.TitleId)).ToDictionary(c => c.TitleId);
        var pending = titleIds.Where(id => !known.Contains(id)).ToList();
        if (pending.Count == 0)
        {
            return new GenerateOutcome(0, 0, titleIds.Count, "already warm");
        }

        var allowance = await budget.AllowanceAsync(accountId, ct);
        if (!allowance.HasHeadroom)
        {
            logger.LogInformation(
                "Lens generation skipped for {AccountId}: daily budget exhausted ({Allowance}). Surfaces fall back.",
                accountId, allowance.Describe());
            return new GenerateOutcome(0, 0, pending.Count, "budget exhausted");
        }

        // Never start more work than the remaining call allowance can pay for.
        if (pending.Count > allowance.CallsRemaining)
        {
            pending = pending.Take((int)allowance.CallsRemaining).ToList();
        }

        var built = await inputs.BuildAsync(accountId, pending, ct);
        if (built.Titles.Count == 0)
        {
            return new GenerateOutcome(0, 0, pending.Count, "no hydrated titles");
        }

        var persona = await personalisation.PersonaForAsync(accountId, taste, ct);
        var written = 0;
        var rejected = 0;
        var failed = 0;

        using var loop = new LlmBudgetLoop(new LlmBudgetOptions(softBudget, ceiling, GenerateBatch), ct);
        var stop = await loop.RunAsync(built.Titles, async (batch, batchCt) =>
        {
            var attempts = await writer.AttemptLensAsync(batch, persona, batchCt);
            // Read AFTER the call: a model that rejected the requested effort mid-call was answered
            // at its fallback, and the row must say which effort actually wrote it.
            var writtenAt = CacheSalt;
            for (var i = 0; i < batch.Count; i++)
            {
                var title = batch[i];
                var attempt = i < attempts.Count ? attempts[i] : new LensAttempt(null, Stopped: true);
                if (attempt.Draft is not { } draft)
                {
                    // The writer already logged why and already declined to invent a substitute.
                    // A call that was made and failed is remembered for FailureBackoff, so the next
                    // open does not pay for the same failure; a stop (budget, breaker, the pass's
                    // own clock) says nothing about the title and is not remembered.
                    if (attempt.Failed && !batchCt.IsCancellationRequested)
                    {
                        RecordFailure(accountId, title.TitleId, profileHash, writtenAt, superseded);
                        failed++;
                    }

                    continue;
                }

                // The length repair runs before anything reads the copy, so the guards judge — and
                // the row stores — exactly the characters the surface will render. A word the
                // response schema cut in half is damage, not a judgement, and it should not reach
                // a rule that is trying to decide whether the prose is honest.
                var note = LensGuards.Tidy(draft.Note);

                // The pure guards first, then the provenance pass — which may TRIM the note rather
                // than refuse it. What gets stored is the verdict's note, never the raw draft: a
                // stripped anchor that survives into LensJson is a dropped anchor that still
                // renders the next time anything reads the row back.
                var deterministic = LensGuards.Inspect(note, title.Name);
                var verdict = deterministic is not null
                    ? new LensVerdict(note, deterministic, [])
                    : await verifier.VerifyAsync(
                        accountId, title.Name, note, built.CreditedNames.GetValueOrDefault(title.TitleId) ?? [], batchCt);

                if (verdict.StrippedAnchors.Count > 0)
                {
                    // Not a rejection and not silent. An anchor the model could not ground is the
                    // single most informative thing a prompt iteration can read, and it is invisible
                    // in the stored note precisely because this dropped it.
                    logger.LogInformation(
                        "Lens for '{Title}' dropped {Count} ungrounded anchor(s) ({Anchors}); the note stands.",
                        title.Name, verdict.StrippedAnchors.Count, string.Join(", ", verdict.StrippedAnchors));
                }

                var row = superseded.GetValueOrDefault(title.TitleId);
                if (row is null)
                {
                    row = new TitleLensCopy
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        TitleId = title.TitleId,
                        ProfileHash = profileHash,
                        PromptVersion = PromptVersion,
                    };
                    db.TitleLensCopies.Add(row);
                }

                // A superseded row (same hash and prompt, another model or effort) is rewritten in
                // place; everything below describes THIS generation.
                row.LensJson = JsonSerializer.Serialize(verdict.Note, JsonOptions);
                row.ModelId = writtenAt;
                row.PromptTokens = draft.PromptTokens;
                row.CompletionTokens = draft.CompletionTokens;
                row.Status = verdict.IsRejected ? LensStatus.Rejected : LensStatus.Done;
                row.RejectionReason = verdict.Reason;
                row.GeneratedAt = DateTime.UtcNow;

                if (!verdict.IsRejected)
                {
                    written++;
                }
                else
                {
                    rejected++;
                    logger.LogInformation(
                        "Lens for '{Title}' rejected by the {Reason} guard; the surface falls back.",
                        title.Name, verdict.Reason);
                }
            }

            // Saved per batch, so a ceiling that fires mid-pass keeps what has already been earned.
            await SaveAbsorbingDuplicateAsync(batchCt);
        });

        return new GenerateOutcome(
            written, rejected, Math.Max(0, titleIds.Count - written - rejected), stop.ToString(), failed);
    }

    /// <summary>
    /// Marks one title's generation at this key as failed (the negative cache), reusing the row at
    /// the key when there is one so the unique index on (account, title, hash, prompt) holds.
    /// </summary>
    private void RecordFailure(
        Guid accountId, Guid titleId, string profileHash, string salt, Dictionary<Guid, TitleLensCopy> superseded)
    {
        if (superseded.GetValueOrDefault(titleId) is { Status: LensStatus.Done })
        {
            // An older model's usable note at this key stays rendered (as stale); failing to write
            // a better one is no reason to take away the one that exists. Its in-place rewrite is
            // simply tried again on a later pass.
            return;
        }

        var row = superseded.GetValueOrDefault(titleId);
        if (row is null)
        {
            row = new TitleLensCopy
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ProfileHash = profileHash,
                PromptVersion = PromptVersion,
            };
            db.TitleLensCopies.Add(row);
            superseded[titleId] = row;
        }

        row.LensJson = "{}";
        row.ModelId = salt;
        row.PromptTokens = null;
        row.CompletionTokens = null;
        row.Status = LensStatus.Failed;
        row.RejectionReason = null;
        row.GeneratedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A title was opened and its note is not current: queue a pass that writes it, so the GET that
    /// asked never waits on a provider (WS-1 task 7, F-6). Returns without queueing when nothing
    /// would come of it — no writer, a refusal on record at this key, a failure inside
    /// <see cref="FailureBackoff"/>, or a spent allowance — and never queues the same title twice:
    /// a pass already pending takes the title on (it is written first), and a pass already running
    /// is left alone (the next open asks again).
    ///
    /// <para>The job is <c>PersonaliseTitles</c>, one per account by the partial unique index, so a
    /// request merges into a pending Fresh-build pass rather than racing it. A pass started this way
    /// still writes the build's selection after the requested titles: those are the titles the next
    /// build would queue anyway, and the warm ones cost nothing.</para>
    /// </summary>
    public async Task<RequestOutcome> RequestAsync(
        Guid accountId, Guid titleId, string profileHash, ResolvedLens resolved, CancellationToken ct)
    {
        if (resolved.Source == LensSource.Fresh)
        {
            return RequestOutcome.Current;
        }

        if (!writer.IsAvailable)
        {
            return RequestOutcome.NotQueued;
        }

        var salt = CacheSalt;
        var retryAfter = DateTime.UtcNow - FailureBackoff;
        var onRecord = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && c.TitleId == titleId
                && c.ProfileHash == profileHash && c.PromptVersion == PromptVersion
                && c.Status != LensStatus.Done)
            .Select(c => new { c.Status, c.ModelId, c.RejectionReason, c.GeneratedAt })
            .FirstOrDefaultAsync(ct);
        if (onRecord is not null
            && (onRecord.RejectionReason == LensRejectionReasons.UserReportedSpoiler
                || (onRecord.ModelId == salt
                    && (onRecord.Status == LensStatus.Rejected || onRecord.GeneratedAt > retryAfter))))
        {
            return RequestOutcome.NotQueued;
        }

        var inFlight = await db.SyncJobs
            .Where(j => j.AccountId == accountId && j.Kind == Domain.Sync.JobKind.PersonaliseTitles
                && (j.Status == Domain.Sync.JobStatus.Pending || j.Status == Domain.Sync.JobStatus.Running))
            .FirstOrDefaultAsync(ct);
        if (inFlight is { Status: Domain.Sync.JobStatus.Running })
        {
            return Jobs.PersonaliseTitlesJobHandler.RequestedTitles(inFlight.CursorJson).Contains(titleId)
                ? RequestOutcome.Queued
                : RequestOutcome.NotQueued;
        }

        if (inFlight is not null)
        {
            if (!Jobs.PersonaliseTitlesJobHandler.RequestedTitles(inFlight.CursorJson).Contains(titleId))
            {
                inFlight.CursorJson = Jobs.PersonaliseTitlesJobHandler.WithRequestedTitle(inFlight.CursorJson, titleId);
                inFlight.Priority = Math.Min(inFlight.Priority, Jobs.PersonaliseTitlesJobHandler.OnOpenPriority);
                await db.SaveChangesAsync(ct);
            }

            return RequestOutcome.Queued;
        }

        if (!(await budget.AllowanceAsync(accountId, ct)).HasHeadroom)
        {
            return RequestOutcome.NotQueued;
        }

        db.SyncJobs.Add(new Domain.Sync.SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = Domain.Sync.JobKind.PersonaliseTitles,
            Priority = Jobs.PersonaliseTitlesJobHandler.OnOpenPriority,
            EnqueuedAt = DateTime.UtcNow,
            TriggerReason = Jobs.PersonaliseTitlesJobHandler.OnOpenReason,
            CursorJson = Jobs.PersonaliseTitlesJobHandler.WithRequestedTitle(null, titleId),
        });
        // A concurrent open (or build) that queued first wins the partial unique index; the loser's
        // row is dropped. The title is then not on that job — the next open merges it.
        return await db.SaveAbsorbingRebuildRaceAsync(ct) > 0 ? RequestOutcome.Queued : RequestOutcome.NotQueued;
    }

    /// <summary>
    /// The on-demand path behind the modal: resolve, and generate this one title if nothing usable
    /// exists and the budget allows. Bounded by <see cref="OnDemandCeiling"/> — past that the
    /// caller renders the fallback, because a spinner that resolves to nothing is worse than prose
    /// that was always going to be adequate.
    /// </summary>
    public async Task<ResolvedLens> ResolveOrGenerateAsync(
        Guid accountId, Guid titleId, FeatureVectorBuilder.TasteState taste, string profileHash, CancellationToken ct)
    {
        var existing = await ResolveAsync(accountId, titleId, profileHash, ct);
        if (existing.Source == LensSource.Fresh)
        {
            return existing;
        }

        var outcome = await GenerateAsync(
            accountId, taste, profileHash, [titleId], OnDemandBudget, OnDemandCeiling, ct);
        if (outcome.Written == 0)
        {
            // Nothing new — the stale note (or nothing) that was already resolved is the answer.
            return existing;
        }

        return await ResolveAsync(accountId, titleId, profileHash, ct);
    }

    /// <summary>
    /// The user's "this spoiled it" report. Marks every Done note this account holds for the title
    /// as Rejected, so the surface falls back immediately and no later pass re-renders it. The row
    /// is kept rather than deleted — the refusal is the record.
    /// </summary>
    public async Task<int> ReportSpoiledAsync(Guid accountId, Guid titleId, CancellationToken ct)
    {
        var rows = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && c.TitleId == titleId && c.Status == LensStatus.Done)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = LensStatus.Rejected;
            row.RejectionReason = LensRejectionReasons.UserReportedSpoiler;
        }

        if (rows.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Account {AccountId} reported the lens for title {TitleId} as a spoiler; {Count} note(s) withdrawn.",
                accountId, titleId, rows.Count);
        }

        return rows.Count;
    }

    /// <summary>
    /// Saves the batch, absorbing the one duplicate this design provokes on purpose: the on-demand
    /// endpoint and the background job can both decide the same title needs a note within the same
    /// second, and the unique key on (account, title, profile hash, prompt version) then rejects
    /// the loser. Losing that race means the note already exists — which is the outcome both
    /// callers wanted. Detaching rather than deleting, because the row was never inserted.
    /// </summary>
    private async Task SaveAbsorbingDuplicateAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueRaceSave.IsUniqueViolation(ex))
        {
            foreach (var entry in ex.Entries.Where(e => e is { State: EntityState.Added, Entity: TitleLensCopy }).ToList())
            {
                entry.State = EntityState.Detached;
            }

            logger.LogInformation("A concurrent pass had already written one of these lens notes; the duplicate was dropped.");
            await db.SaveChangesAsync(ct);
        }
    }

    private LensNote? Deserialise(TitleLensCopy row)
    {
        try
        {
            // A note the current guards would refuse (a hedged hook, written before that rule)
            // reads as no note: the ladder steps past it exactly as it steps past an unreadable one.
            // A note whose hook was cut off reads with no hook and its lens intact (IR2S-03).
            return JsonSerializer.Deserialize<LensNote>(row.LensJson, JsonOptions) is { } note
                ? LensGuards.ForServing(note)
                : null;
        }
        catch (JsonException ex)
        {
            // A row written by an older shape. Not fatal and not worth a warning per render — the
            // ladder simply steps past it.
            logger.LogDebug(ex, "Lens row {Id} could not be read back; falling through the ladder.", row.Id);
            return null;
        }
    }

    /// <summary>The job's time box, exposed so the handler and the tests name one thing.</summary>
    public Task<GenerateOutcome> GenerateForJobAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, string profileHash,
        IReadOnlyList<Guid> titleIds, CancellationToken ct) =>
        GenerateAsync(accountId, taste, profileHash, titleIds, JobBudget, JobCeiling, ct);
}
