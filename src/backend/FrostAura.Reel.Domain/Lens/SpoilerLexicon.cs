using System.Text.RegularExpressions;

namespace FrostAura.Reel.Domain.Lens;

/// <summary>
/// The deterministic spoiler guard for lens copy — the sibling of
/// <see cref="Ports.SecondPersonVoice"/>, and the one the original lens design did not have.
///
/// <para><b>Why a lexicon and not the model's word.</b> The prompt says "first act only, never an
/// ending, twist, death or reveal". A model that ignores that instruction produces text which is
/// indistinguishable from text that obeyed it, and the damage — telling someone how a film ends
/// before they watch it — is not recoverable by a later correction. So the promise is checked, not
/// trusted, and a note that trips the check is REJECTED rather than edited: a spoiler with the
/// offending clause snipped out is still built around the thing it was not supposed to know.</para>
///
/// <para><b>Tuned for false positives over false negatives, deliberately.</b> Losing a good note
/// costs one templater fallback. Shipping a spoiler costs the trust the whole honest-copy
/// programme is built on. The patterns are therefore word-anchored but broad, and the phrases that
/// are innocuous in isolation ("reveals", "turns out") are still refused, because in 420 characters
/// of plot they almost always precede the thing that should not be there.</para>
///
/// <para>Founder decision, 2026-09-09: <b>spoiler tolerance is first act only.</b> This class is
/// that decision in code; widening it is a product decision, not a tuning pass.</para>
/// </summary>
public static partial class SpoilerLexicon
{
    [GeneratedRegex(
        @"\b(?:"
        + @"the ending|its ending|an ending|the finale|the final (?:act|scene|episode|shot|reel|moments?)"
        + @"|the last (?:act|scene|episode|shot|minutes?)"
        + @"|the (?:twist|reveal|betrayal|reversal)|a (?:twist|reveal)|plot twist"
        + @"|turns out|it emerges that|we learn that|you learn that|only to discover"
        + @"|reveals? that|revealed to be|revealing that"
        + @"|(?:who |the )?killer is|the murderer is|the culprit is|the traitor is"
        + @"|dies|died|is killed|gets killed|kills himself|kills herself|survives|does not survive|doesn'?t survive"
        + @"|in the end|by the end|ends with|ending with|concludes with|culminates in"
        + @"|resurrect(?:s|ed)|comes back to life|is actually|was actually|turns against"
        + @"|spoiler"
        + @")\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpoilerRegex();

    /// <summary>
    /// True when the text names something the guard will not let past. Checked over every prose
    /// field of a note — <c>lens</c>, <c>hook</c>, <c>caution</c> and every anchor relation —
    /// because a spoiler in the one-line hook is the most visible place it could possibly land.
    /// </summary>
    public static bool Spoils(string? text) =>
        !string.IsNullOrWhiteSpace(text) && SpoilerRegex().IsMatch(text);

    /// <summary>
    /// Every guarded field of a note, in one call. Returns the first phrase that tripped the guard
    /// (for the rejection reason and the log) or null when the note is clean.
    /// </summary>
    public static string? FirstSpoiler(LensNote note)
    {
        foreach (var field in Fields(note))
        {
            if (field is null)
            {
                continue;
            }

            var match = SpoilerRegex().Match(field);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }

    private static IEnumerable<string?> Fields(LensNote note)
    {
        yield return note.Lens;
        yield return note.Hook;
        yield return note.Caution;
        foreach (var anchor in note.Anchors)
        {
            yield return anchor.Relation;
        }
    }
}
