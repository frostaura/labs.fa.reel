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
}
