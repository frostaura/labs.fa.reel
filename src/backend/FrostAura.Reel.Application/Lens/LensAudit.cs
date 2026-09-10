using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Lens;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Lens;

/// <summary>One audited note: what was generated, and what the judge thought of it.</summary>
public sealed record LensAuditRow(
    Guid TitleId,
    string TitleName,
    string ProfileHash,
    int PromptVersion,
    string Status,
    string? RejectionReason,
    DateTime GeneratedAt,
    string? Lens,
    string? Hook,
    string? Caution,
    string? Mood,
    double? Specificity,
    double? Grounding,
    double? SpoilerFreeness,
    double? Voice,
    double? Overall,
    string? Comment);

/// <summary>The audit as a whole — per-prompt-version means, and the rows behind them.</summary>
public sealed record LensAuditReport(
    int PromptVersion,
    int Scored,
    int Unscored,
    int RejectedByGuards,
    double? MeanSpecificity,
    double? MeanGrounding,
    double? MeanSpoilerFreeness,
    double? MeanVoice,
    double? MeanOverall,
    IReadOnlyList<LensAuditRow> Rows);

/// <summary>
/// The founder-only LLM-judge pass over recently generated lens copy.
///
/// <para><b>Why it exists.</b> Offline metrics cannot see whether a piece of personalised prose is
/// specific, grounded, spoiler-free and in the right voice — and a human reading fifty generations
/// per prompt version is the bar this feature is held to before anyone but the founder sees it.
/// This does not replace that reading; it makes the reading comparable across prompt versions, by
/// scoring the same rubric the same way every time. A version whose mean grounding drops is a
/// version that regressed, and without this the only evidence would be a feeling.</para>
///
/// <para><b>How to read it.</b> Compare MEANS between prompt versions, never absolute values — the
/// judge is the same model family as the writer and shares its blind spots, so the number is
/// meaningful as a difference and close to meaningless as a level. <c>rejectedByGuards</c> is the
/// deterministic half and is worth more than any of the means: it is a count of notes that were
/// actually refused, not an opinion about them. Rising specificity with falling grounding is the
/// failure mode to watch for — it is what "more confident and less true" looks like.</para>
///
/// <para>Runs under its own <c>lens-audit</c> purpose, so auditing a prompt version can never
/// consume the lens budget it is auditing.</para>
/// </summary>
public sealed class LensAudit(
    IReelDbContext db, ILensWriter writer, LensInputBuilder inputs, PersonalisationContext personalisation)
{
    /// <summary>How many recent notes an audit covers. Matches the human-review bar it supports.</summary>
    public const int DefaultSampleSize = 50;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LensAuditReport> RunAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, int sampleSize, bool score, CancellationToken ct)
    {
        var take = Math.Clamp(sampleSize, 1, 200);
        var rows = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId)
            .OrderByDescending(c => c.GeneratedAt)
            .Take(take)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new LensAuditReport(writer.LensPromptVersion, 0, 0, 0, null, null, null, null, null, []);
        }

        var titleIds = rows.Select(r => r.TitleId).Distinct().ToList();
        var names = await db.Titles
            .Where(t => titleIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var built = await inputs.BuildAsync(accountId, titleIds, ct);
        var byId = built.Titles.ToDictionary(t => t.TitleId);
        var persona = await personalisation.PersonaForAsync(accountId, taste, ct);

        var audited = new List<LensAuditRow>(rows.Count);
        foreach (var row in rows)
        {
            LensNote? note = null;
            try
            {
                note = JsonSerializer.Deserialize<LensNote>(row.LensJson, JsonOptions);
            }
            catch (JsonException)
            {
                // An unreadable row still belongs in the count — it is a generation that happened.
            }

            LensAuditScore? verdict = null;
            // Only Done notes are judged. A note a guard already refused has a deterministic
            // verdict on it, and paying a model to agree is spend for nothing.
            if (score && note is not null && row.Status == LensStatus.Done && byId.TryGetValue(row.TitleId, out var input))
            {
                verdict = await writer.JudgeAsync(input, persona, note, ct);
            }

            audited.Add(new LensAuditRow(
                row.TitleId,
                names.GetValueOrDefault(row.TitleId) ?? "(unknown)",
                row.ProfileHash,
                row.PromptVersion,
                row.Status.ToString(),
                row.RejectionReason,
                row.GeneratedAt,
                note?.Lens,
                note?.Hook,
                note?.Caution,
                note?.Mood,
                verdict?.Specificity,
                verdict?.Grounding,
                verdict?.SpoilerFreeness,
                verdict?.Voice,
                verdict?.Overall,
                verdict?.Comment));
        }

        var scored = audited.Where(a => a.Overall is not null).ToList();
        return new LensAuditReport(
            writer.LensPromptVersion,
            scored.Count,
            audited.Count - scored.Count,
            audited.Count(a => a.Status == nameof(LensStatus.Rejected)),
            Mean(scored.Select(a => a.Specificity)),
            Mean(scored.Select(a => a.Grounding)),
            Mean(scored.Select(a => a.SpoilerFreeness)),
            Mean(scored.Select(a => a.Voice)),
            Mean(scored.Select(a => a.Overall)),
            audited);
    }

    /// <summary>The mean, or null over an empty set — a mean of nothing is not zero.</summary>
    private static double? Mean(IEnumerable<double?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 3);
    }
}
