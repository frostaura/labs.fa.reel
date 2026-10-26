using System.Text.RegularExpressions;

namespace FrostAura.Reel.Domain.Ports;

/// <summary>
/// Output guard for the LLM's "why this fits you" lines. Cards speak TO the viewer, so a sentence
/// that talks ABOUT them ("their tastes", "the user's favourites") is a voice leak from the
/// prompt's third-person taste summary. Callers discard a leaking line and use their deterministic
/// second-person fallback instead of showing the viewer a card that refers to them in the third
/// person. Shared by <c>OpenRouterSearchAgent</c> (rerank result parsing) and <c>FeedPaginator</c>
/// (curated why usage) so both edges apply the identical rule.
/// </summary>
public static partial class SecondPersonVoice
{
    [GeneratedRegex(@"\b(their|they|them|the user'?s?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThirdPersonRegex();

    /// <summary>True when the sentence refers to the viewer in the third person.</summary>
    public static bool LeaksThirdPerson(string? why) =>
        !string.IsNullOrWhiteSpace(why) && ThirdPersonRegex().IsMatch(why);

    /// <summary>
    /// True when the line opens with the banned "Because you loved …" template — the exact vague
    /// sentence shape the founder called out repeating verbatim down a rail. An anchored why must
    /// name what actually links the two titles, not merely restate the anchor.
    /// </summary>
    public static bool OpensWithBecauseYouLoved(string? why) =>
        !string.IsNullOrWhiteSpace(why)
        && why.TrimStart().StartsWith("Because you loved", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full output guard for an LLM why-line: third-person voice leak, the banned
    /// "Because you loved" opener, or a runaway length (the prompt asks for ≤160 characters; a
    /// line past 220 has ignored the contract and would overflow the card). Callers discard a
    /// rejected line for their deterministic second-person fallback.
    /// </summary>
    public static bool Rejects(string? why) =>
        LeaksThirdPerson(why) || OpensWithBecauseYouLoved(why) || (why is { Length: > 220 });

    [GeneratedRegex(
        @"\b(?:you (?:may|might|likely|probably|will)(?: well)? (?:bounce off|skip|avoid|dislike|hate|pass on)"
        + @"|you'?ll (?:probably |likely )?(?:bounce off|dislike|avoid|skip|hate|pass on)"
        + @"|(?:probably|likely|usually) (?:skips|avoids)"
        + @"|not for you"
        + @"|not your (?:lane|thing|speed|scene)"
        + @"|steer clear"
        + @"|give (?:this|it) a (?:miss|pass)"
        + @"|sit this one out)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AntiRecommendationRegex();

    /// <summary>
    /// True when the line argues AGAINST its own card — "You may bounce off the gore", "probably
    /// skips this", "you'll likely avoid it". The LLM why pass is asked for honest fit, and honesty
    /// about a weak pick is valuable as a SELECTION signal — but a pick surface (feed hero, rails,
    /// paginated rows) must never caption its own recommendation with a reason to skip it (observed
    /// live 2026-08-10). Applied only where whys are accepted for pick surfaces; the Ask Reel
    /// outside-request channel deliberately keeps its honest negatives.
    /// </summary>
    public static bool SoundsAntiRecommendation(string? why) =>
        !string.IsNullOrWhiteSpace(why) && AntiRecommendationRegex().IsMatch(why);

    /// <summary>
    /// A line that argues against its own pick — the softer cousin of an anti-recommendation.
    ///
    /// <para><b>Adopted from <c>FeedWhyGuard</c>'s measured list (WS-1 task 9), widened once.</b>
    /// That list was measured on the slot's exposure log: replayed over every distinct why-line
    /// (200 from one day's builds, 4,000 older), it matched 101 and 492, and every match in two
    /// random samples of 45 and 40 was a caveat a pick card should not carry. It lived in the feed's
    /// guard as a stopgap; it lives here now so the re-rank parser, the lens and the feed apply one
    /// definition. The one widening: the detour arm allows up to three adjectives with commas, so
    /// "a quieter, heavier detour" — rendered on Home in the round-1 surface review — is caught,
    /// where the old arm allowed exactly one word with no comma.</para>
    ///
    /// <para>Deliberately NOT matched, because they are praise: "nothing less than", "no less
    /// than", "a little of the …", "Detour" as a title, "but without the gore you dislike",
    /// "despite its quiet start".</para>
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
         |\b(?:a|an)\s+(?:[\w-]+,?\s+){0,3}detour\b|\bdetour\ (?:from|away)\b
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

    /// <summary>
    /// True when <paramref name="text"/> hedges against its own pick ("…though this is a much
    /// slower drama than you often prefer"). Applied wherever a line captions a PICK; never to a
    /// lens's <c>caution</c>, which exists to carry the reservation.
    /// </summary>
    public static bool SoundsHedged(string? text) => !string.IsNullOrWhiteSpace(text) && HedgeRegex().IsMatch(text);
}
