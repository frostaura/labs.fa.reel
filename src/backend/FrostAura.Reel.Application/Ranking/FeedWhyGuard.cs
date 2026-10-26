using System.Text.RegularExpressions;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The one gate a why-line passes before it may caption a card on a PICK surface — the feed build,
/// the paginated rails and the live refill all ask this, and nothing else, so the three can never
/// disagree about what is shown.
///
/// <para>Two questions, deliberately separate:</para>
/// <list type="bullet">
/// <item><see cref="Accepts"/> — may a freshly produced (model or cached) line be used? Everything
/// <see cref="IsServable"/> refuses, plus the shared voice guard and the anti-recommendation and
/// hedge guards.</item>
/// <item><see cref="IsServable"/> — may a line that is ALREADY persisted on a snapshot be shown?
/// The shapes known to be invented, broken or self-undermining: the two stub sentences, the retired era
/// template, the retired contrarian and "trending" boilerplate, the a/an grammar defect, a hedge
/// against the pick, and the prompt's "the anchor" jargon. A persisted line failing it is served as no line at all, which is the honest rendering
/// of "nothing verifiable to say".</item>
/// </list>
///
/// <para><b>One hedge definition.</b> <see cref="IsHedged"/> is <see cref="SecondPersonVoice.SoundsHedged"/>,
/// which took over the hedge shapes this class measured on the slot's exposure log; there is no
/// second list here.</para>
/// </summary>
public static partial class FeedWhyGuard
{
    /// <summary>
    /// The deterministic stub WS-1's agent wrote on every failed rerank batch ("A {genre} pick
    /// aligned with your taste."). Matched on its tail, which no genuine line has ever ended with,
    /// so the a/an variant and any genre slug are caught alike.
    /// </summary>
    [GeneratedRegex(@"\bpick aligned with your taste\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StubRegex();

    /// <summary>
    /// The templater's retired generic sentence (<see cref="ExplanationTemplater.GenericFallback"/>),
    /// alone or spliced after an anchor clause ("Shares Twitches Too's theme of magic, and Strong fit
    /// with your taste profile."). Matched on its tail for the same reason as <see cref="StubRegex"/>:
    /// snapshots built before 2026-10-26 persisted both shapes, and neither cites anything.
    /// </summary>
    [GeneratedRegex(@"\bstrong fit with your taste profile\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericStubRegex();

    /// <summary>
    /// The two retired era templates ("peak 1990s — your era", "from the 2010s, a decade you rate
    /// highly"). decadeAffinity is a survivorship artefact, and the template explained it to the
    /// user as taste — the feature stays in the model, the sentence is gone.
    /// </summary>
    [GeneratedRegex(@"(?:—\s*your era\b|\ba decade you rate highly\b)", RegexOptions.IgnoreCase)]
    private static partial Regex EraTemplateRegex();

    /// <summary>
    /// The templater's two retired boilerplate families, as they were persisted before 2026-10-25:
    /// the contrarian clause ("scores well even by your contrarian standards", "holds up even by
    /// your tougher-than-the-crowd standards"), which fired on 77% of lines whatever the reader's
    /// contrarian score, and the popularity clause ("trending right now", "having a moment right
    /// now"), which captioned twenty-year-old shows from a popularity LEVEL. The templater no longer
    /// writes either phrasing; a persisted copy is a claim nobody checked.
    /// </summary>
    [GeneratedRegex(@"\b(?:even by your contrarian standards|even by your tougher-than-the-crowd standards|trending right now|having a moment right now)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RetiredBoilerplateRegex();

    /// <summary>
    /// "A" before a lower-case vowel at the start of the line — "A action pick", "A adventure". Words
    /// that open with a vowel LETTER but a consonant SOUND are exempt ("A unique…", "A one-off…",
    /// "A useful…", "A eu…"), because "A" is the correct article there and blanking those lines
    /// would hide good copy for a grammar rule it does not break.
    /// </summary>
    [GeneratedRegex(@"^A (?!(?:uni(?!n|m|d)|unan|use|usu|uti|ure|uro|eu|ewe|one\b|one-|once\b)\w*)[aeiou]")]
    private static partial Regex WrongArticleRegex();

    /// <summary>
    /// "the anchor" — the rerank prompt's own word for the title a rail is anchored on, leaking
    /// into the user's copy. Seven of seven model lines on the one rail dealt in a live build said
    /// "the anchor's …". The card names the title; the user never named an anchor.
    /// </summary>
    [GeneratedRegex(@"\bthe anchor(?:'s|’s)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorJargonRegex();

    /// <summary>
    /// True when <paramref name="why"/> is a stub: the sentence a failed rerank batch produced, or the
    /// templater's retired generic sentence, alone or as a tail.
    /// </summary>
    public static bool IsStub(string? why) =>
        !string.IsNullOrWhiteSpace(why) && (StubRegex().IsMatch(why) || GenericStubRegex().IsMatch(why));

    /// <summary>True when <paramref name="why"/> carries one of the retired era templates.</summary>
    public static bool IsEraTemplate(string? why) => !string.IsNullOrWhiteSpace(why) && EraTemplateRegex().IsMatch(why);

    /// <summary>True when the line carries a retired templater boilerplate phrasing (see <see cref="RetiredBoilerplateRegex"/>).</summary>
    public static bool IsRetiredBoilerplate(string? why) => !string.IsNullOrWhiteSpace(why) && RetiredBoilerplateRegex().IsMatch(why);

    /// <summary>True when the line opens "A" + vowel.</summary>
    public static bool HasWrongArticle(string? why) => !string.IsNullOrWhiteSpace(why) && WrongArticleRegex().IsMatch(why);

    /// <summary>
    /// True when the line hedges against its own pick — the softer cousin of an anti-recommendation
    /// ("though this complex series may suit you less than a brisk film").
    /// </summary>
    public static bool IsHedged(string? why) => SecondPersonVoice.SoundsHedged(why);

    /// <summary>True when the line calls the rail's title "the anchor" — prompt jargon, not copy.</summary>
    public static bool UsesAnchorJargon(string? why) => !string.IsNullOrWhiteSpace(why) && AnchorJargonRegex().IsMatch(why);

    /// <summary>May an already-persisted line be shown? See the class remarks.</summary>
    public static bool IsServable(string? why) =>
        !string.IsNullOrWhiteSpace(why) && !IsStub(why) && !IsEraTemplate(why) && !IsRetiredBoilerplate(why)
        && !HasWrongArticle(why) && !IsHedged(why) && !UsesAnchorJargon(why);

    /// <summary>May a freshly produced or cached line caption a pick? See the class remarks.</summary>
    public static bool Accepts(string? why) =>
        IsServable(why)
        && !SecondPersonVoice.Rejects(why)
        && !SecondPersonVoice.SoundsAntiRecommendation(why);

    /// <summary>
    /// The persisted line as it may be served: itself; or, for a line that is an anchor clause with the
    /// retired generic sentence spliced after it, the anchor clause alone (the line the templater now
    /// writes for that card) when that clause is itself servable; or empty when it fails
    /// <see cref="IsServable"/>.
    /// </summary>
    public static string Servable(string? why)
    {
        if (why is null)
        {
            return string.Empty;
        }

        if (IsServable(why))
        {
            return why;
        }

        var tail = GenericTailRegex().Match(why);
        if (tail.Success)
        {
            var anchorClause = why[..tail.Index].TrimEnd() + ".";
            return IsServable(anchorClause) ? anchorClause : string.Empty;
        }

        return string.Empty;
    }

    /// <summary>The retired generic sentence as a tail spliced after a clause: ", and Strong fit with your taste profile."</summary>
    [GeneratedRegex(@",\s+and\s+strong fit with your taste profile\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTailRegex();
}
