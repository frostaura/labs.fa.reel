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
/// The narrower set of shapes that are known to be invented or broken: the stub sentence, the
/// retired era template and the a/an grammar defect. A persisted line failing it is served as no
/// line at all, which is the honest rendering of "nothing verifiable to say".</item>
/// </list>
///
/// <para><b>The hedge list is a local stopgap.</b> WS-1 owns the anti-recommendation guard in
/// <see cref="SecondPersonVoice"/> and is extending it to hedges (its task 9). Until that lands this
/// class carries the hedges observed on live cards ("suit you less than a brisk film", "a quieter
/// detour", "not an action sprint"); when it does, <see cref="IsHedged"/> folds into that call and
/// this list is deleted rather than kept as a second definition.</para>
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

    /// <summary>"A" before a lower-case vowel at the start of the line — "A action pick", "A adventure".</summary>
    [GeneratedRegex(@"^A [aeiou]")]
    private static partial Regex WrongArticleRegex();

    [GeneratedRegex(
        @"\b(?:suits? you less|less (?:than|suited|your speed)"
        + @"|(?:quieter|slower|gentler|softer) detour|\bdetour\b"
        + @"|not (?:an? )?[\w-]+ (?:sprint|thrill-ride|thrill ride|crowd-pleaser)"
        + @"|(?:may|might) not (?:land|suit|appeal|grab|click|work)"
        + @"|if you can (?:get past|forgive|tolerate|stomach)"
        + @"|despite (?:its|the|your))\b",
        RegexOptions.IgnoreCase)]
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

    /// <summary>May an already-persisted line be shown? See the class remarks.</summary>
    public static bool IsServable(string? why) =>
        !string.IsNullOrWhiteSpace(why) && !IsStub(why) && !IsEraTemplate(why) && !HasWrongArticle(why);

    /// <summary>May a freshly produced or cached line caption a pick? See the class remarks.</summary>
    public static bool Accepts(string? why) =>
        IsServable(why)
        && !SecondPersonVoice.Rejects(why)
        && !SecondPersonVoice.SoundsAntiRecommendation(why)
        && !IsHedged(why);

    /// <summary>The persisted line as it may be served: itself, or empty when it fails <see cref="IsServable"/>.</summary>
    public static string Servable(string? why) => why is not null && IsServable(why) ? why : string.Empty;
}
