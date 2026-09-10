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

    /// <summary>The background job's wall clock, and its soft budget. Nothing is waiting on it.</summary>
    public static readonly TimeSpan JobCeiling = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The prompt generation every row this service writes is stamped with.</summary>
    public int PromptVersion => writer.LensPromptVersion;

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

        var current = rows.FirstOrDefault(c => c.ProfileHash == profileHash && c.PromptVersion == PromptVersion);
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

        foreach (var group in rows.GroupBy(r => r.TitleId))
        {
            var current = group.FirstOrDefault(c => c.ProfileHash == profileHash && c.PromptVersion == PromptVersion);
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
    public sealed record GenerateOutcome(int Written, int Rejected, int Skipped, string Reason);

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
        // must not be re-attempted at the same key — needs no spend.
        var known = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && titleIds.Contains(c.TitleId)
                && c.ProfileHash == profileHash && c.PromptVersion == PromptVersion)
            .Select(c => c.TitleId)
            .ToListAsync(ct);
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

        using var loop = new LlmBudgetLoop(new LlmBudgetOptions(softBudget, ceiling, GenerateBatch), ct);
        var stop = await loop.RunAsync(built.Titles, async (batch, batchCt) =>
        {
            var drafts = await writer.WriteLensAsync(batch, persona, batchCt);
            for (var i = 0; i < batch.Count; i++)
            {
                var title = batch[i];
                var draft = i < drafts.Count ? drafts[i] : null;
                if (draft is null)
                {
                    // The writer already logged why and already declined to invent a substitute.
                    continue;
                }

                // The pure guards first, then the provenance pass — which may TRIM the note rather
                // than refuse it. What gets stored is the verdict's note, never the raw draft: a
                // stripped anchor that survives into LensJson is a dropped anchor that still
                // renders the next time anything reads the row back.
                var deterministic = LensGuards.Inspect(draft.Note, title.Name);
                var verdict = deterministic is not null
                    ? new LensVerdict(draft.Note, deterministic, [])
                    : await verifier.VerifyAsync(
                        accountId, draft.Note, built.CreditedNames.GetValueOrDefault(title.TitleId) ?? [], batchCt);

                if (verdict.StrippedAnchors.Count > 0)
                {
                    // Not a rejection and not silent. An anchor the model could not ground is the
                    // single most informative thing a prompt iteration can read, and it is invisible
                    // in the stored note precisely because this dropped it.
                    logger.LogInformation(
                        "Lens for '{Title}' dropped {Count} ungrounded anchor(s) ({Anchors}); the note stands.",
                        title.Name, verdict.StrippedAnchors.Count, string.Join(", ", verdict.StrippedAnchors));
                }

                db.TitleLensCopies.Add(new TitleLensCopy
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.TitleId,
                    ProfileHash = profileHash,
                    PromptVersion = PromptVersion,
                    LensJson = JsonSerializer.Serialize(verdict.Note, JsonOptions),
                    ModelId = draft.ModelId,
                    PromptTokens = draft.PromptTokens,
                    CompletionTokens = draft.CompletionTokens,
                    Status = verdict.IsRejected ? LensStatus.Rejected : LensStatus.Done,
                    RejectionReason = verdict.Reason,
                    GeneratedAt = DateTime.UtcNow,
                });

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
            written, rejected, Math.Max(0, titleIds.Count - written - rejected), stop.ToString());
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
            return JsonSerializer.Deserialize<LensNote>(row.LensJson, JsonOptions);
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
