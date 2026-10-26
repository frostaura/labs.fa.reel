using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Lens;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Lens;

/// <summary>
/// One audited note: what was generated, and what the judge thought of it.
///
/// <para><b>A rejected note is a row like any other</b>, carrying its reason and its anchors.
/// Reading why the guards refused something is the whole point of running this before and after a
/// prompt edit, and a report that showed only the notes that passed would be a report on the
/// prompt's best day.</para>
/// </summary>
public sealed record LensAuditRow(
    Guid TitleId,
    string TitleName,
    string ProfileHash,
    int PromptVersion,
    string Status,
    /// <summary>The guard's verdict, with the offending token appended — "invented-anchor:Frontera".</summary>
    string? RejectionReason,
    /// <summary>The reason without its token, so rows can be grouped by failure class.</summary>
    string? RejectionKind,
    /// <summary>The token that caused the refusal, or null. The evidence a prompt edit is judged on.</summary>
    string? RejectionToken,
    DateTime GeneratedAt,
    string? Lens,
    string? Hook,
    string? Caution,
    string? Mood,
    /// <summary>The anchor titles as stored — after any ungrounded entry was stripped.</summary>
    IReadOnlyList<string> Anchors,
    double? Specificity,
    double? Grounding,
    double? SpoilerFreeness,
    double? Voice,
    double? Overall,
    /// <summary>
    /// The judge's quoted POV slips — copy that made the viewer a character in the plot — or null.
    /// Deliberately not folded into any mean; the phrase is the evidence.
    /// </summary>
    string? PovSlips,
    string? Comment);

/// <summary>The audit as a whole — per-prompt-version means, and the rows behind them.</summary>
public sealed record LensAuditReport(
    int PromptVersion,
    int Scored,
    int Unscored,
    int RejectedByGuards,
    /// <summary>
    /// Refusals by failure class, worst count first — "invented-anchor: 9". The single most useful
    /// number on the report, because it says WHICH way a prompt version is failing rather than how
    /// often, and a prompt edit that trades one class for another is invisible in the total.
    /// </summary>
    IReadOnlyList<LensRejectionCount> RejectedByReason,
    double? MeanSpecificity,
    double? MeanGrounding,
    double? MeanSpoilerFreeness,
    double? MeanVoice,
    double? MeanOverall,
    IReadOnlyList<LensAuditRow> Rows);

/// <summary>One failure class, its count, and the distinct tokens that produced it.</summary>
public sealed record LensRejectionCount(string Reason, int Count, IReadOnlyList<string> Tokens);

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
        // A Failed row is the negative cache — no note was written, so there is nothing to audit.
        var rows = await db.TitleLensCopies
            .Where(c => c.AccountId == accountId && c.Status != LensStatus.Failed)
            .OrderByDescending(c => c.GeneratedAt)
            .Take(take)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new LensAuditReport(writer.LensPromptVersion, 0, 0, 0, [], null, null, null, null, null, []);
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

            var (kind, token) = SplitReason(row.RejectionReason);
            audited.Add(new LensAuditRow(
                row.TitleId,
                names.GetValueOrDefault(row.TitleId) ?? "(unknown)",
                row.ProfileHash,
                row.PromptVersion,
                row.Status.ToString(),
                row.RejectionReason,
                kind,
                token,
                row.GeneratedAt,
                note?.Lens,
                note?.Hook,
                note?.Caution,
                note?.Mood,
                note?.Anchors.Select(a => a.Title).ToList() ?? [],
                verdict?.Specificity,
                verdict?.Grounding,
                verdict?.SpoilerFreeness,
                verdict?.Voice,
                verdict?.Overall,
                verdict?.PovSlips,
                verdict?.Comment));
        }

        var scored = audited.Where(a => a.Overall is not null).ToList();
        return new LensAuditReport(
            writer.LensPromptVersion,
            scored.Count,
            audited.Count - scored.Count,
            audited.Count(a => a.Status == nameof(LensStatus.Rejected)),
            ByReason(audited),
            Mean(scored.Select(a => a.Specificity)),
            Mean(scored.Select(a => a.Grounding)),
            Mean(scored.Select(a => a.SpoilerFreeness)),
            Mean(scored.Select(a => a.Voice)),
            Mean(scored.Select(a => a.Overall)),
            audited);
    }

    /// <summary>
    /// Splits a stored reason into its class and the token that caused it. A reason written before
    /// tokens existed has no colon and simply reports no token, rather than being unreadable.
    /// </summary>
    internal static (string? Kind, string? Token) SplitReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return (null, null);
        }

        var split = reason.IndexOf(':');
        return split <= 0
            ? (reason, null)
            : (reason[..split], reason[(split + 1)..]);
    }

    /// <summary>Refusals grouped by failure class, worst first, with the distinct tokens behind each.</summary>
    private static IReadOnlyList<LensRejectionCount> ByReason(IReadOnlyList<LensAuditRow> rows) =>
        rows
            .Where(r => r.RejectionKind is { Length: > 0 })
            .GroupBy(r => r.RejectionKind!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new LensRejectionCount(
                g.Key,
                g.Count(),
                g.Select(r => r.RejectionToken)
                    .Where(t => t is { Length: > 0 })
                    .Select(t => t!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

    /// <summary>The mean, or null over an empty set — a mean of nothing is not zero.</summary>
    private static double? Mean(IEnumerable<double?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 3);
    }
}
