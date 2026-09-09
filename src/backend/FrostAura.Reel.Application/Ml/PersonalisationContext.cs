using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Lens;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Resolves the personalisation context every LLM call in the product rides on: the account's
/// effective taste narrative — the user's own edit if they made one, otherwise the generated text
/// — falling back to the compact <see cref="TasteSummary"/> line for an account that has not been
/// written one yet.
///
/// <para>This is the single place the substitution happens, so the feed why pass, the paginator's
/// rerank and Ask Reel cannot end up describing the same person differently. Before it existed the
/// three surfaces each built their own twenty-token line and all three were wrong in the same two
/// ways.</para>
///
/// <para><b>The injected narrative is capped.</b> A user can edit their narrative to any length
/// they like, and it is prepended to every rerank prompt in the product — an unbounded field on a
/// per-batch prompt is a per-batch bill. The cap trims on a sentence boundary so the context ends
/// mid-thought as rarely as possible.</para>
/// </summary>
public sealed class PersonalisationContext(IReelDbContext db, DislikeSignal dislikes)
{
    /// <summary>
    /// The ceiling on the narrative injected into a prompt. ~1,200 characters is comfortably more
    /// than ten sentences and a hard stop on an edited field that is not.
    /// </summary>
    public const int MaxInjectedChars = 1200;

    /// <summary>
    /// The account's current narrative row, or null. Reads the newest version — regeneration
    /// writes a new row rather than mutating history, so the ordering is what picks the live one.
    /// </summary>
    public Task<AccountTasteNarrative?> CurrentAsync(Guid accountId, CancellationToken ct) =>
        db.AccountTasteNarratives
            .Where(n => n.AccountId == accountId)
            .OrderByDescending(n => n.Version)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The taste text to hand a model: the effective narrative, capped, or the compact summary.
    /// Never empty — an account with nothing to say still gets "no strong signal yet", which is a
    /// true statement and a better prompt than an empty line.
    /// </summary>
    public async Task<string> ContextForAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var narrative = await CurrentAsync(accountId, ct);
        if (narrative is not null && !string.IsNullOrWhiteSpace(narrative.Effective))
        {
            return Cap(narrative.Effective);
        }

        return await TasteSummary.BuildAsync(db, accountId, taste, ct);
    }

    /// <summary>
    /// The full persona — taste context plus the recently-declined line. The declined line was
    /// already reaching Ask Reel and had never reached the feed, so the feed's model had never been
    /// told a single thing the account dislikes.
    /// </summary>
    public async Task<LensPersona> PersonaForAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var taste_ = await ContextForAsync(accountId, taste, ct);
        var profile = await dislikes.GetProfileAsync(accountId, ct);
        return new LensPersona(taste_, DislikeSignal.DeclinedLine(profile));
    }

    /// <summary>Trims to <see cref="MaxInjectedChars"/> on the last sentence boundary that fits.</summary>
    internal static string Cap(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= MaxInjectedChars)
        {
            return trimmed;
        }

        var window = trimmed[..MaxInjectedChars];
        var stop = window.LastIndexOfAny(['.', '!', '?']);
        return stop > MaxInjectedChars / 2 ? window[..(stop + 1)] : window.TrimEnd() + "…";
    }
}
