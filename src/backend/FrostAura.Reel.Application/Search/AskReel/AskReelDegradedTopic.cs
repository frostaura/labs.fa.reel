using FrostAura.Reel.Application.Search.Retrieval.Concepts;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The v1 topic of a turn whose interpret call failed, kept as the phrases the user wrote.
///
/// <para><b>Why (HREV4-ASK-04).</b> With the provider down, v1 searches the interpreter's free
/// heuristic reading, which tokenises the message and drops function words — "time" among them, a
/// floor word — so "time travel movies" was searched, counted and NAMED as "travel": "You have 19
/// unseen travel movies tagged that way", every number the travel shelf's, none the time-travel
/// shelf's (11 clearing the bar, 4 under it). The keyless reader keeps the phrase ("time travel"), so
/// each heuristic keyword is widened to the run of non-genre words around it in the phrase it came
/// from. Genre words stay where the heuristic put them (in the genres): "shark horrors" is still the
/// keyword "shark" in horror.</para>
/// </summary>
public static class AskReelDegradedTopic
{
    /// <summary>
    /// <paramref name="keywords"/> widened to the user's phrases in <paramref name="message"/>; a
    /// keyword no phrase holds is kept as it is.
    /// </summary>
    public static IReadOnlyList<string> WholePhrases(IReadOnlyList<string>? keywords, string? message, int currentYear)
    {
        var given = keywords ?? [];
        if (given.Count == 0)
        {
            return given;
        }

        var phrases = AskReelIntentSanitizer.ReadKeylessRequest(message, currentYear).Phrases
            .Select(AskReelIntentSanitizer.Words)
            .ToList();
        var result = new List<string>();
        foreach (var keyword in given)
        {
            var words = AskReelIntentSanitizer.Words(keyword);
            var widened = keyword.Trim().ToLowerInvariant();
            foreach (var phrase in phrases)
            {
                if (IndexOf(phrase, words) is not { } at)
                {
                    continue;
                }

                var start = at;
                var end = at + words.Count;
                while (start > 0 && !IsGenreWord(phrase[start - 1]))
                {
                    start--;
                }

                while (end < phrase.Count && !IsGenreWord(phrase[end]))
                {
                    end++;
                }

                widened = string.Join(' ', phrase.Skip(start).Take(end - start));
                break;
            }

            if (widened.Length > 0 && !result.Contains(widened, StringComparer.Ordinal))
            {
                result.Add(widened);
            }
        }

        return result;
    }

    private static bool IsGenreWord(string word) => ConceptSynonyms.GenreSlugsOf(word) is { Count: > 0 };

    private static int? IndexOf(IReadOnlyList<string> phrase, IReadOnlyList<string> words)
    {
        if (words.Count == 0 || words.Count > phrase.Count)
        {
            return null;
        }

        for (var start = 0; start + words.Count <= phrase.Count; start++)
        {
            var at = start;
            if (words.Select((w, i) => (w, i)).All(x => string.Equals(phrase[at + x.i], x.w, StringComparison.Ordinal)))
            {
                return start;
            }
        }

        return null;
    }
}
