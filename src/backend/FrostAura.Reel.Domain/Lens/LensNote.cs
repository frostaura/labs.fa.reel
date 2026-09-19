using System.Text.Json.Serialization;

namespace FrostAura.Reel.Domain.Lens;

/// <summary>
/// The closed vocabularies the lens may answer with. Closed for the same reason the tone and era
/// taxonomies are: free text lets the model mint one value per title until the field stops being
/// filterable and starts being noise. These two ARE filterable — the tonight-mood chips re-rank
/// over <see cref="Moods"/> — so an invented value is a silently dead card, not merely untidy.
/// </summary>
public static class LensVocabulary
{
    /// <summary>How the title feels to sit with. Drives the tonight-mood chips.</summary>
    public static readonly string[] Moods =
    [
        "light", "heavy", "tense", "cosy", "cerebral", "background",
    ];

    /// <summary>The watching occasion it suits.</summary>
    public static readonly string[] WhenToWatch =
    [
        "tonight-alone", "with-someone", "needs-full-attention", "fine-as-background", "weekend",
    ];

    private static readonly HashSet<string> MoodSet = new(Moods, StringComparer.Ordinal);

    private static readonly HashSet<string> WhenSet = new(WhenToWatch, StringComparer.Ordinal);

    public static bool IsMood(string? value) => value is not null && MoodSet.Contains(value);

    public static bool IsWhenToWatch(string? value) => value is not null && WhenSet.Contains(value);
}

/// <summary>
/// One anchor: a title from the user's OWN library, with a one-clause statement of how the
/// candidate relates to it. The name must match a title the account has actually rated — an
/// invented comparison is the most convincing kind of wrong, because it sounds like memory.
/// </summary>
public sealed record LensAnchor(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("relation")] string Relation);

/// <summary>
/// The lens object, as generated and as stored in <see cref="TitleLensCopy.LensJson"/>.
///
/// <para><b>No numbers in the prose, ever.</b> The model is given the calibrated prediction and its
/// error bar as CONTEXT — so it can pitch a 8.4 differently from a 6.1 — and is explicitly forbidden
/// from restating either. That is what keeps the model artifact out of the cache key and the cache
/// warm across a retrain.</para>
///
/// <para><b>First act only.</b> Every plot fact must come from the supplied overview and keywords,
/// and nothing past the first act may be described. A deterministic lexicon enforces it after the
/// fact, because the model's own promise that it did not spoil anything is worth nothing.</para>
/// </summary>
public sealed record LensNote(
    /// <summary>≤420 chars. The premise, retold in this viewer's own taste vocabulary, second person.</summary>
    [property: JsonPropertyName("lens")] string Lens,
    /// <summary>≤90 chars. The one reason to press play tonight — the card and push headline.</summary>
    [property: JsonPropertyName("hook")] string Hook,
    /// <summary>≤120 chars, or null. The honest reservation. The one field allowed to argue against the pick.</summary>
    [property: JsonPropertyName("caution")] string? Caution,
    /// <summary>Up to 3 titles from the user's own ratings, loved or hated, each with its relation.</summary>
    [property: JsonPropertyName("anchors")] IReadOnlyList<LensAnchor> Anchors,
    /// <summary>One of <see cref="LensVocabulary.Moods"/>.</summary>
    [property: JsonPropertyName("mood")] string Mood,
    /// <summary>One of <see cref="LensVocabulary.WhenToWatch"/>.</summary>
    [property: JsonPropertyName("whenToWatch")] string WhenToWatch)
{
    public const int MaxLensChars = 420;
    public const int MaxHookChars = 90;
    public const int MaxCautionChars = 120;
    public const int MaxAnchors = 3;

    /// <summary>
    /// What the PROMPT and the response schema ask for — roughly a tenth under the hard ceilings
    /// above, which stay where they are as the parse and storage limit.
    ///
    /// <para><b>Why the two numbers differ.</b> A structured-output <c>maxLength</c> is enforced by
    /// constrained decoding: the string stops at exactly that character, mid-word if that is where
    /// the budget ran out. Live copy came back reading "…a crowd-pleasing sw" and "…marriage
    /// strain,." for precisely that reason. Asking for less than the ceiling gives the model room
    /// to land its own sentence before the hard stop, which is a better fix than tidying up after
    /// one — <c>LensGuards.Tidy</c> is the backstop for the times it still overruns, not the
    /// policy.</para>
    ///
    /// <para>Keeping <see cref="MaxLensChars"/> and friends at their old values matters: every note
    /// already persisted was written against them, and narrowing the parse ceiling would make
    /// perfectly good stored copy unreadable.</para>
    /// </summary>
    public const int RequestLensChars = 378;

    /// <inheritdoc cref="RequestLensChars"/>
    public const int RequestHookChars = 81;

    /// <inheritdoc cref="RequestLensChars"/>
    public const int RequestCautionChars = 108;

    /// <summary>
    /// The first clause of <see cref="Lens"/> — what a rail card's two-line why and the hero's
    /// second line render. Split on the first sentence boundary, falling back to a word-boundary
    /// trim so a single long sentence still yields something readable rather than nothing.
    /// </summary>
    public string FirstClause(int maxChars = 160)
    {
        var text = Lens.Trim();
        var stop = text.AsSpan().IndexOfAny('.', '!', '?');
        if (stop > 0 && stop + 1 <= maxChars)
        {
            return text[..(stop + 1)];
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', Math.Min(maxChars, text.Length - 1));
        return (cut > 0 ? text[..cut] : text[..maxChars]).TrimEnd(',', ';', ' ') + "…";
    }
}
