namespace FrostAura.Reel.Domain.Catalog;

/// <summary>
/// The closed tone/era vocabularies for <see cref="TitleAttributes"/> — the single source of truth
/// shared by the extractor (which enforces them on the provider via a JSON-schema enum) and by any
/// surface that RENDERS an attribute value (the why-line templater). Rows written before the
/// closed-vocabulary enforcement landed carry polluted free-text labels, so a consumer must check
/// membership here before printing a stored Tone/Era rather than trusting the column.
/// </summary>
public static class AttributeVocabulary
{
    /// <summary>
    /// Closed tone vocabulary. Free-text here would let the model mint a new label per title,
    /// so the column stops being a taxonomy and becomes noise. The deterministic stub's three
    /// values ("brooding", "playful", "balanced") are members, so both modes share one vocabulary.
    /// </summary>
    public static readonly string[] Tones =
    [
        "bleak", "brooding", "melancholic", "tense", "gritty", "earnest", "contemplative",
        "dreamlike", "balanced", "wry", "satirical", "playful", "whimsical", "warm",
        "uplifting", "exhilarating",
    ];

    /// <summary>
    /// Closed era vocabulary, likewise a superset of the stub's ("period", "modern",
    /// "contemporary", "unknown").
    /// </summary>
    public static readonly string[] Eras =
    [
        "prehistoric", "ancient", "medieval", "period", "19th-century", "early-20th-century",
        "mid-20th-century", "modern", "contemporary", "near-future", "far-future", "timeless", "unknown",
    ];

    private static readonly HashSet<string> ToneSet = new(Tones, StringComparer.Ordinal);

    /// <summary>True when <paramref name="tone"/> is a member of the closed tone vocabulary —
    /// the render-side pollution guard for pre-enforcement rows.</summary>
    public static bool IsClosedVocabTone(string? tone) => tone is not null && ToneSet.Contains(tone);
}
