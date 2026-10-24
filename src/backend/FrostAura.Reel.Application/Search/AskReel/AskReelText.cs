using System.Text.RegularExpressions;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// Model prose made safe to render as plain text.
///
/// <para>The chat surface renders text, not markdown, so a reply that italicised a title arrived on
/// screen as "*Waist Deep*" (B-16) — asterisks a person reads as a glitch. Rather than teach the
/// client a markdown subset, the server removes the markup at the one place model prose enters the
/// turn, so the transcript, the history the next prompt reads and the screen all hold the same plain
/// sentence.</para>
/// </summary>
public static partial class AskReelText
{
    /// <summary>
    /// <paramref name="text"/> with markdown emphasis, inline code, headings, list bullets and links
    /// reduced to their visible words, and runs of whitespace collapsed. Null stays null.
    /// </summary>
    public static string? StripMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var s = Link().Replace(text, "$1");
        s = Bold().Replace(s, "$2");
        s = Italic().Replace(s, "$2");
        s = Code().Replace(s, "$1");
        s = Heading().Replace(s, string.Empty);
        s = Bullet().Replace(s, string.Empty);
        s = Spaces().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Link();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(\*|_)(?!\s)(.+?)(?<!\s)\1(?![\p{L}\p{N}])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Italic();

    [GeneratedRegex(@"`([^`]*)`", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Code();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Heading();

    [GeneratedRegex(@"(?m)^\s*(?:[-*+]|\d+\.)\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Spaces();
}
