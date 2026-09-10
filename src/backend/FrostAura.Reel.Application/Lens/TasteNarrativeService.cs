using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Lens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Lens;

/// <summary>Why a narrative write ended the way it did — the job's progress message, and a test's assertion.</summary>
public enum NarrativeOutcome
{
    /// <summary>A new version was written.</summary>
    Written,

    /// <summary>The account's taste has not moved since the current narrative; nothing was spent.</summary>
    UpToDate,

    /// <summary>The writer is not configured. Surfaces keep the compact fallback line.</summary>
    Unavailable,

    /// <summary>The provider did not answer usably, twice. The old narrative stands.</summary>
    Failed,

    /// <summary>The draft named something it was not given. Refused rather than published.</summary>
    Ungrounded,
}

/// <summary>
/// Writes and rewrites the account's taste narrative.
///
/// <para><b>The grounding check is the reason this is a service and not three lines in a handler.</b>
/// The narrative is prose about a person, generated from a fact sheet, and shown to that person as
/// what the product believes about them. A sentence naming a film they have never rated is not a
/// stylistic problem — it is the product asserting a memory that never happened. So every proper
/// noun in the draft is checked against the facts it was written from, and a draft that invents one
/// is refused whole. There is no partial acceptance: a paragraph with one fabricated sentence
/// removed still reads as if the rest were verified the same way.</para>
///
/// <para><b>A user edit is never overwritten.</b> Regeneration writes a new version carrying the
/// new generated text and CARRIES THE EDIT FORWARD, so a correction the user made once keeps
/// winning. Only the explicit regenerate action clears it, and that is the user asking.</para>
/// </summary>
public sealed class TasteNarrativeService(
    IReelDbContext db,
    ILensWriter writer,
    TasteFactsBuilder facts,
    PersonalisationContext context,
    ILogger<TasteNarrativeService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The result, plus the row in force afterwards (which may be the pre-existing one).</summary>
    public sealed record Result(NarrativeOutcome Outcome, AccountTasteNarrative? Narrative);

    /// <summary>
    /// Writes a narrative when one is owed. <paramref name="force"/> is the user pressing
    /// "regenerate": it spends even when the taste fingerprint has not moved, and it CLEARS the
    /// user's edit, because asking for a rewrite is asking to see what the product would say.
    /// </summary>
    public async Task<Result> WriteAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, DateTime now, bool force, CancellationToken ct)
    {
        var current = await context.CurrentAsync(accountId, ct);
        var built = await facts.BuildAsync(accountId, taste, now, ct);

        if (!force
            && current is not null
            && current.ProfileHash == built.ProfileHash
            && current.PromptVersion == writer.NarrativePromptVersion)
        {
            return new Result(NarrativeOutcome.UpToDate, current);
        }

        if (!writer.IsAvailable)
        {
            return new Result(NarrativeOutcome.Unavailable, current);
        }

        var draft = await writer.WriteNarrativeAsync(built.Facts, ct);
        if (draft is null)
        {
            logger.LogWarning(
                "Taste narrative for {AccountId} could not be written; the previous one stands.", accountId);
            return new Result(NarrativeOutcome.Failed, current);
        }

        var text = string.Join(" ", draft.Sentences.Select(s => s.Trim()));
        if (Ungrounded(text, built.Facts) is { } invented)
        {
            logger.LogWarning(
                "Taste narrative for {AccountId} named \"{Invented}\", which is not in its own fact sheet. "
                + "Refused whole — a profile that invents one memory cannot be trusted on the others.",
                accountId, invented);
            return new Result(NarrativeOutcome.Ungrounded, current);
        }

        var narrative = new AccountTasteNarrative
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = (current?.Version ?? 0) + 1,
            ProfileHash = built.ProfileHash,
            ProfileFingerprint = built.ProfileFingerprint,
            Text = text,
            // Carried forward, not dropped: a correction the user made once keeps winning until
            // they explicitly ask for a rewrite, which is what `force` means.
            EditedText = force ? null : current?.EditedText,
            EditedAt = force ? null : current?.EditedAt,
            FactsJson = JsonSerializer.Serialize(built.Facts, JsonOptions),
            RatingsCount = built.Facts.RatingsCount,
            ModelId = draft.ModelId,
            PromptVersion = writer.NarrativePromptVersion,
            PromptTokens = draft.PromptTokens,
            CompletionTokens = draft.CompletionTokens,
            GeneratedAt = now,
        };
        db.AccountTasteNarratives.Add(narrative);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Taste narrative v{Version} written for {AccountId} from {Ratings} ratings (profile {Hash}).",
            narrative.Version, accountId, narrative.RatingsCount, narrative.ProfileHash);
        return new Result(NarrativeOutcome.Written, narrative);
    }

    /// <summary>
    /// The user's own rewrite. Stored on the CURRENT version rather than as a new one — the edit is
    /// a correction to what the product said, not a new thing the product said.
    /// </summary>
    public async Task<AccountTasteNarrative?> SaveEditAsync(
        Guid accountId, string? edited, DateTime now, CancellationToken ct)
    {
        var current = await context.CurrentAsync(accountId, ct);
        if (current is null)
        {
            return null;
        }

        // An empty edit is the user reverting to the generated text, not an empty narrative.
        current.EditedText = string.IsNullOrWhiteSpace(edited) ? null : edited.Trim();
        current.EditedAt = current.EditedText is null ? null : now;
        await db.SaveChangesAsync(ct);
        return current;
    }

    /// <summary>
    /// The fact-grounding check. Returns the first proper noun the draft used that its own fact
    /// sheet does not contain, or null when every name is accounted for.
    ///
    /// <para>Same shape as the lens's person check and same reasoning: candidate proper nouns are
    /// extracted loosely, then each is required to appear in the grounded set. A multi-word phrase
    /// is matched by containment in either direction, so "Denis Villeneuve" clears a fact sheet
    /// listing "Denis Villeneuve" and "Sicario" clears one listing "Sicario: Day of the Soldado".</para>
    /// </summary>
    internal static string? Ungrounded(string text, TasteFacts factSheet)
    {
        var grounded = factSheet.GroundedNames();
        foreach (var candidate in ProperNouns(text))
        {
            if (grounded.Any(name =>
                    name.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                    || candidate.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Capitalised multi-word phrases, minus the ones that open a sentence — "You" and "Lately"
    /// are grammar, not evidence. Single capitalised words are deliberately not candidates: the
    /// false-positive rate at one word is high enough to refuse every honest narrative.
    /// </summary>
    private static IEnumerable<string> ProperNouns(string text)
    {
        foreach (var candidate in LensGuards.CandidateNames(
                     new LensNote(text, string.Empty, null, [], LensVocabulary.Moods[0], LensVocabulary.WhenToWatch[0])))
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}
