using System.Text.RegularExpressions;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The unscripted-format demotion applied to the LLM's fit verdict — and the reason it is
/// conditional rather than absolute.
///
/// <para><b>What it corrects.</b> Reality, talk, game and panel shows exist to fill schedules, so
/// they carry enormous vote counts and enormous popularity relative to how much any one person
/// wants to watch them. An LLM judging "fit" has read the whole internet about them and has that
/// reach baked into its priors; ask it how well a long-running panel show fits someone and the
/// honest answer it gives is contaminated by how many people watch it. The prompt says as much in
/// words. This is the deterministic half of the same instruction, because a prompt line is a
/// request and this is a rule.</para>
///
/// <para><b>Why it is conditional, and why that matters.</b> Plenty of people genuinely love
/// unscripted television, and a recommender that quietly refuses to show it to them is broken in a
/// way they will never be able to name. So the demotion fires ONLY when the account's own taste
/// narrative says nothing positive about the format — and the narrative is the user's to edit, so
/// anyone this gets wrong about can correct it in one sentence and the demotion stops. That is the
/// whole design: a default that is right for most people and revocable by the person it is wrong
/// about.</para>
///
/// <para>It scales the LLM's fit only. It never touches the model's predicted rating, which is
/// trained on this account's own labels and is entitled to disagree.</para>
/// </summary>
public static partial class UnscriptedFormats
{
    /// <summary>
    /// Genre slugs and TMDB genre names that mark a title as unscripted. Reality-adjacent
    /// documentary is deliberately absent: a documentary is a made thing, and the founder's own
    /// library rates them.
    /// </summary>
    private static readonly string[] Slugs =
    [
        "reality", "reality-tv", "talk", "talk-show", "game-show", "news",
    ];

    /// <summary>How far a verdict is scaled when the demotion applies. A nudge, never a ban.</summary>
    public const double FitScale = 0.6d;

    /// <summary>
    /// Words that, in a taste narrative, mean "I like this kind of thing". Matched against the
    /// narrative as a whole rather than per sentence: a person who mentions reality television at
    /// all in six to ten sentences about their taste has said something about it.
    /// </summary>
    [GeneratedRegex(@"\b(?:reality|talk[- ]show|game[- ]show|panel show|competition show|unscripted|bake[- ]off|dating show)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MentionRegex();

    /// <summary>True when the title is an unscripted format by its genre list.</summary>
    public static bool IsUnscripted(IReadOnlyList<string> genres)
    {
        foreach (var genre in genres)
        {
            var normalised = genre.Trim().ToLowerInvariant().Replace(' ', '-');
            if (Slugs.Contains(normalised, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the account's taste text mentions unscripted television at all. A mention is
    /// treated as consent, deliberately: the narrative is written honestly and would say "you avoid
    /// reality television" as readily as the opposite, and the cost of being wrong in the permissive
    /// direction is one card the user scrolls past, against a whole format silently withheld.
    /// </summary>
    public static bool NarrativeMentions(string? tasteText) =>
        !string.IsNullOrWhiteSpace(tasteText) && MentionRegex().IsMatch(tasteText);

    /// <summary>
    /// The fit a verdict should carry after the demotion. Unchanged when the title is scripted, or
    /// when the account's narrative has something to say about unscripted television.
    /// </summary>
    public static double Adjust(double fit, IReadOnlyList<string> genres, string? tasteText) =>
        IsUnscripted(genres) && !NarrativeMentions(tasteText) ? fit * FitScale : fit;
}
