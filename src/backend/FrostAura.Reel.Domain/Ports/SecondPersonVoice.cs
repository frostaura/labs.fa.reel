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
}
