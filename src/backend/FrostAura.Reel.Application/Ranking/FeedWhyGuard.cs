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
/// The shapes known to be invented, broken or self-undermining: the stub sentence, the retired era
/// template, the a/an grammar defect, a hedge against the pick, and the prompt's "the anchor"
/// jargon. A persisted line failing it is served as no line at all, which is the honest rendering
/// of "nothing verifiable to say".</item>
/// </list>
///
/// <para><b>The hedge list is a local stopgap.</b> WS-1 owns the anti-recommendation guard in
/// <see cref="SecondPersonVoice"/> and is extending it to hedges (its task 9). Until that lands this
/// class carries the hedge shapes measured on the slot's exposure log (see <see cref="HedgeRegex"/>);
/// when it does, <see cref="IsHedged"/> folds into that call and this list is deleted rather than
/// kept as a second definition. The same pattern list is the evidence WS-1 is asked to adopt.</para>
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
    /// The two retired era templates ("peak 1990s — your era", "from the 2010s, a decade you rate
    /// highly"). decadeAffinity is a survivorship artefact, and the template explained it to the
    /// user as taste — the feature stays in the model, the sentence is gone.
    /// </summary>
    [GeneratedRegex(@"(?:—\s*your era\b|\ba decade you rate highly\b)", RegexOptions.IgnoreCase)]
    private static partial Regex EraTemplateRegex();

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
    /// A line that argues against its own pick. Measured, not guessed: replayed over every distinct
    /// why-line the slot's exposure log held (200 from one day's builds, 4,000 older), this matched
    /// 101 and 492 respectively, and every match in two random samples of 45 and 40 was a caveat
    /// the card should not carry. The shapes, in order:
    /// <list type="bullet">
    /// <item>a concessive clause — ", though …", ", although …", ", yet …", "Though …" — which on
    /// a pick card is always the model qualifying its own recommendation;</item>
    /// <item>a ", but …" clause carrying a caveat word (weaker, softer, lighter, slower, less, not,
    /// may, lacks, little of, outweighs, rather than, too …);</item>
    /// <item>fit-language negatives anywhere: "weaker format fit", "a softer match", "less aligned
    /// with", "may test your patience", "far from your usual", "lighter sci-fi than you seek",
    /// "rather than the action-forward film you want", "you usually favor brisk films over this".</item>
    /// </list>
    /// Deliberately NOT matched, because they are praise: "nothing less than", "no less than",
    /// "a little of the …", "Detour" as a title, "but without the gore you dislike", "despite its
    /// quiet start".
    /// </summary>
    [GeneratedRegex(
        """
        (?:
          (?:^|[,;—–]\s*)(?:though|although|even\ though|even\ if)\b
         |(?:^|[,;—–]\s*)yet\b(?!\ another)
         |\bbut\b[^.;]{0,100}?\b(?:weaker|softer|thinner|loose|looser|mixed|gentler|lighter|slower|subdued|secondary|too\ \w+|temper|blunt|soften|miss(?:es)?\b|gamble|commitment|outweighs?|rather\ than|less\b|not\b|lacks?|little\ of|may\b|might\b|ideal|instead|drives\ this|leans?\ (?:into|on|heavily|more|cozy))
         |\bsuits?\ you\ less\b
         |\bless\ (?:suited|aligned|ideal|of\ a\ (?:fit|match)|your\ (?:speed|style|thing|usual|lane|strength))\b
         |\b(?:moves?|runs?|plays?|feels?|reads?|unfolds?)\ less\b
         |(?<!\bno\ )(?<!\bnothing\ )(?<!\blittle\ )\bless\ than\ (?:a|an|the|your)\b
         |\b(?:a|an)\s+(?:\w+\s+)?detour\b|\bdetour\ (?:from|away)\b
         |\bnot\ (?:an?\ )?[\w-]+\ (?:sprint|thrill-ride|thrill\ ride|crowd-pleaser)\b
         |\b(?:may|might|could)\ not\ (?:land|suit|appeal|grab|click|work|deliver|satisfy|match|hit|offer|be)\b
         |\b(?:won't|doesn't|does\ not|isn't|is\ not)\ (?:deliver|suit|land|match|satisfy|offer)\b
         |\btests?\ your\ patience\b|\b(?:may|might|could)\ (?:test|try|strain|stretch|miss|blunt|soften|slow)\b
         |\b(?:may|might|could)\ (?:feel|be|seem)\ (?:too\ |a\ bit\ |somewhat\ )?(?:slow|slower|subdued|measured|gentler|lighter|softer|thin|long|less|a\ stretch|a\ gamble)\b
         |(?<!\ba\ )\blittle\ of\ (?:the|your)\b|\bnone\ of\ the\b|\bnothing\ like\ (?:your|the)\b
         |\bfar\ (?:cry\ )?from\ (?:your|the\ anchor|what\ you)\b
         |\bif\ you\ can\ (?:get\ past|forgive|tolerate|stomach)\b
         |\bdespite\ your\b
         |\bnot\ (?:quite\ |exactly\ |really\ )?your\ (?:usual|typical|preferred|favou?rite|style|speed|thing|cup\ of\ tea|lane)\b
         |\b(?:outside|beyond|away\ from|departure\ from|break\ from)\ your\ (?:usual|comfort|typical|preferred|favou?rite)\b
         |\b(?:weaker|softer|thinner|loose|looser|mixed|partial|imperfect)\s+(?:[\w-]+\s+)?(?:fit|match|draws?)\b
         |\btemper(?:s|ed)?\ the\ (?:fit|appeal)\b|\bweaker\ spot\b
         |\b(?:lighter|gentler|softer|slower|weaker|quieter|tamer|milder|thinner|looser|calmer|cozier|less\ [\w-]+|more\ (?:reflective|procedural|measured|subdued|cerebral|comedic|melodramatic|heavily))\b[^.;,]{0,40}?\bthan\ (?:you|your)\b
         |\b(?:weighs?|weigh)\ more\ heavily\ than\b
         |\bthan\ your\ (?:preferred|usual|typical)\b|\bthan\ you\ (?:prefer|seek|want|usually|typically|tend)\b
         |\b(?:usually|typically|tend\ to)\ (?:favou?r|prefer)\b[^.;]{0,50}\bover\b
         |\brather\ than\ (?:your\b|the\ [\w-]+\ (?:film|series|show|picks?)\ you\b|a\ film\b)
         |\btoo\ (?:light|slow|soft|gentle|quiet|long|slight|tame|mild|young)\b
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex HedgeRegex();

    /// <summary>True when <paramref name="why"/> is the stub sentence a failed rerank batch produced.</summary>
    public static bool IsStub(string? why) => !string.IsNullOrWhiteSpace(why) && StubRegex().IsMatch(why);

    /// <summary>True when <paramref name="why"/> carries one of the retired era templates.</summary>
    public static bool IsEraTemplate(string? why) => !string.IsNullOrWhiteSpace(why) && EraTemplateRegex().IsMatch(why);

    /// <summary>True when the line opens "A" + vowel.</summary>
    public static bool HasWrongArticle(string? why) => !string.IsNullOrWhiteSpace(why) && WrongArticleRegex().IsMatch(why);

    /// <summary>
    /// True when the line hedges against its own pick — the softer cousin of an anti-recommendation
    /// ("though this complex series may suit you less than a brisk film").
    /// </summary>
    public static bool IsHedged(string? why) => !string.IsNullOrWhiteSpace(why) && HedgeRegex().IsMatch(why);

    /// <summary>True when the line calls the rail's title "the anchor" — prompt jargon, not copy.</summary>
    public static bool UsesAnchorJargon(string? why) => !string.IsNullOrWhiteSpace(why) && AnchorJargonRegex().IsMatch(why);

    /// <summary>May an already-persisted line be shown? See the class remarks.</summary>
    public static bool IsServable(string? why) =>
        !string.IsNullOrWhiteSpace(why) && !IsStub(why) && !IsEraTemplate(why) && !HasWrongArticle(why)
        && !IsHedged(why) && !UsesAnchorJargon(why);

    /// <summary>May a freshly produced or cached line caption a pick? See the class remarks.</summary>
    public static bool Accepts(string? why) =>
        IsServable(why)
        && !SecondPersonVoice.Rejects(why)
        && !SecondPersonVoice.SoundsAntiRecommendation(why);

    /// <summary>The persisted line as it may be served: itself, or empty when it fails <see cref="IsServable"/>.</summary>
    public static string Servable(string? why) => why is not null && IsServable(why) ? why : string.Empty;
}
