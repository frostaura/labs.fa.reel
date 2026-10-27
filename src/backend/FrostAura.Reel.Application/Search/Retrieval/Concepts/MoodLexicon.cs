namespace FrostAura.Reel.Application.Search.Retrieval.Concepts;

/// <summary>What one mood word means in the catalogue's own terms.</summary>
/// <param name="Mood">The mood as the lexicon knows it ("cosy" reads as "cozy").</param>
/// <param name="Genres">Genre slugs that carry the mood.</param>
/// <param name="Labels">Keyword-label stems that carry it, expanded through the vocabulary like any concept's.</param>
/// <param name="Tones">
/// Values of the extractor's closed <c>TitleAttributes.Tone</c> set (tense, playful, gritty, earnest, wry,
/// warm, brooding, melancholic, exhilarating, bleak, contemplative, balanced, satirical, whimsical,
/// uplifting, dreamlike) that carry it.
/// </param>
/// <param name="Directions">The attribute steers the mood implies; an explicit steer from the request wins over these.</param>
public sealed record MoodReading(
    string Mood,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Tones,
    IReadOnlyList<AttributeDirection> Directions);

/// <summary>
/// The curated reading of mood words ("feel-good", "cosy", "tense", "mind-bending") for the mood arm
/// and the keyless search: genres, keyword labels and extracted tones that carry the mood, and the
/// attribute steers it implies. A mood is SOFT: it orders and finds, it never excludes, and it never
/// becomes a hard concept (program §4.3 <c>SoftPreferences</c>).
///
/// <para><b>Why a table and not embeddings alone.</b> A mood has no TMDB keyword to stand on ("cozy"
/// tags 0 titles in the eval catalogue, "feel-good" 0), and an uncached mood text misses the semantic
/// arm's 1.0 s budget, so a mood request used to be answered by the catalogue's best-rated titles of
/// any kind. The extractor's tone (16 values, 13,987 titles) and attributes (0–1 scalars) are the
/// catalogue's own vocabulary for moods; this table maps the words people use onto them.</para>
///
/// <para>Attribute names are the planner's closed set: darkness, pacing, complexity, intensity, humor,
/// optimism (see <see cref="AttributeSteer"/>).</para>
/// </summary>
public static class MoodLexicon
{
    private static readonly AttributeDirection OptimismUp = new("optimism", AttributeTrend.More);
    private static readonly AttributeDirection OptimismDown = new("optimism", AttributeTrend.Less);
    private static readonly AttributeDirection DarknessUp = new("darkness", AttributeTrend.More);
    private static readonly AttributeDirection DarknessDown = new("darkness", AttributeTrend.Less);
    private static readonly AttributeDirection IntensityUp = new("intensity", AttributeTrend.More);
    private static readonly AttributeDirection IntensityDown = new("intensity", AttributeTrend.Less);
    private static readonly AttributeDirection PacingUp = new("pacing", AttributeTrend.More);
    private static readonly AttributeDirection PacingDown = new("pacing", AttributeTrend.Less);
    private static readonly AttributeDirection HumorUp = new("humor", AttributeTrend.More);
    private static readonly AttributeDirection ComplexityUp = new("complexity", AttributeTrend.More);
    private static readonly AttributeDirection ComplexityDown = new("complexity", AttributeTrend.Less);

    private sealed record Entry(string[] Words, string[] Genres, string[] Labels, string[] Tones, AttributeDirection[] Directions);

    private static readonly Entry[] Entries =
    [
        new(["feel-good", "feel good", "feelgood", "happy", "cheerful", "joyful"],
            ["comedy", "family"], ["feel-good", "friendship"], ["warm", "uplifting", "playful"], [OptimismUp, DarknessDown]),
        new(["uplifting", "heartwarming", "heart-warming", "wholesome", "inspiring", "inspirational", "hopeful", "feel-good drama"],
            [], ["inspirational", "underdog", "heartwarming"], ["uplifting", "warm", "earnest"], [OptimismUp, DarknessDown]),
        new(["cozy", "cosy", "comforting", "comfort", "comfort watch", "snug"],
            ["comedy", "family", "romance"], ["small town"], ["warm", "whimsical", "playful"], [IntensityDown, DarknessDown, OptimismUp]),
        new(["fun", "entertaining", "crowd-pleaser", "crowd pleaser"],
            ["comedy", "adventure", "family"], ["buddy"], ["playful", "exhilarating"], [HumorUp, OptimismUp]),
        new(["funny", "hilarious", "silly", "witty", "comedic", "laugh", "laughs", "goofy"],
            ["comedy"], ["parody", "satire", "dark comedy"], ["playful", "wry", "satirical"], [HumorUp]),
        new(["light", "lighthearted", "light-hearted", "breezy", "easy", "easygoing", "easy-going", "undemanding", "easy watch"],
            ["comedy", "family"], [], ["playful", "whimsical", "warm"], [DarknessDown, ComplexityDown, IntensityDown]),
        new(["dark", "bleak", "grim", "gritty", "disturbing", "nihilistic"],
            ["thriller", "crime", "drama"], ["neo-noir", "psychological thriller"], ["bleak", "gritty", "brooding"], [DarknessUp, OptimismDown]),
        new(["scary", "creepy", "spooky", "frightening", "terrifying", "eerie", "chilling"],
            ["horror"], ["supernatural", "ghost"], ["tense", "brooding"], [DarknessUp, IntensityUp]),
        new(["tense", "suspenseful", "gripping", "nail-biting", "nail biting", "edge of my seat", "edge-of-your-seat"],
            ["thriller"], ["suspense", "psychological thriller"], ["tense"], [IntensityUp, PacingUp]),
        new(["exciting", "thrilling", "action-packed", "action packed", "adrenaline", "fast-paced", "fast paced", "high-octane", "high octane"],
            ["action", "adventure"], [], ["exhilarating", "tense"], [PacingUp, IntensityUp]),
        new(["slow-burn", "slow burn", "slow", "contemplative", "meditative", "quiet", "thoughtful", "reflective"],
            [], ["slow burn"], ["contemplative", "brooding", "melancholic"], [PacingDown]),
        new(["mind-bending", "mindbending", "mind bending", "cerebral", "thought-provoking", "thought provoking", "smart", "clever", "twisty", "complex", "brainy"],
            ["science-fiction", "mystery"], ["philosophical", "twist ending", "mind-bending", "psychological thriller"], ["dreamlike", "contemplative"], [ComplexityUp]),
        new(["sad", "tearjerker", "tear-jerker", "emotional", "moving", "heartbreaking", "heart-breaking", "melancholy", "melancholic", "bittersweet", "weepy"],
            ["drama"], ["tearjerker", "melancholy"], ["melancholic", "earnest"], [IntensityUp, OptimismDown]),
        new(["romantic", "sweet", "swoony", "swoon-worthy"],
            ["romance"], ["love"], ["warm"], []),
        new(["epic", "grand", "sweeping", "spectacular"],
            ["adventure", "fantasy", "action"], ["epic"], ["exhilarating"], []),
        new(["relaxing", "chill", "chilled", "calm", "soothing", "mellow", "laid-back", "laid back", "low-key", "low key"],
            [], ["relaxing"], ["warm", "whimsical", "contemplative"], [IntensityDown, PacingDown, DarknessDown]),
        new(["weird", "quirky", "offbeat", "strange", "surreal", "bizarre", "trippy", "dreamy", "odd"],
            [], ["surreal", "absurd", "whimsical"], ["whimsical", "dreamlike", "wry"], [ComplexityUp]),
        new(["nostalgic", "nostalgia"],
            [], ["nostalgic", "coming of age"], ["warm", "melancholic"], []),
        new(["whimsical", "magical", "enchanting", "charming"],
            ["fantasy", "family"], ["whimsical"], ["whimsical", "dreamlike", "warm"], [OptimismUp]),
        new(["satirical", "biting", "sharp"],
            ["comedy"], ["satire"], ["satirical", "wry"], [HumorUp]),
        new(["violent", "brutal", "gory", "bloody", "visceral"],
            ["action", "horror"], ["violence", "gore"], ["gritty"], [DarknessUp, IntensityUp]),
    ];

    private static readonly IReadOnlyDictionary<string, MoodReading> ByWord = Build();

    /// <summary>Every word the lexicon reads, for typo correction against it.</summary>
    public static IReadOnlyCollection<string> Words => (IReadOnlyCollection<string>)ByWord.Keys;

    /// <summary>The extractor's closed tone set, lower-case.</summary>
    public static readonly IReadOnlySet<string> KnownTones = new HashSet<string>(StringComparer.Ordinal)
    {
        "tense", "playful", "gritty", "earnest", "wry", "warm", "brooding", "melancholic", "exhilarating",
        "bleak", "contemplative", "balanced", "satirical", "whimsical", "uplifting", "dreamlike",
    };

    /// <summary>
    /// The reading of <paramref name="mood"/> (normalised as a concept is: lower-case, media words and
    /// leading filler dropped, so "a cosy movie" reads as "cozy"), or null when the lexicon does not
    /// know the word. A word that is itself one of the extractor's tones reads as that tone.
    /// </summary>
    public static MoodReading? Read(string? mood)
    {
        var words = ConceptResolver.Normalise(mood);
        if (words.Length == 0)
        {
            return null;
        }

        if (ByWord.TryGetValue(words, out var reading))
        {
            return reading;
        }

        // "feel good vibes", "something cosy": the first known word or two-word phrase stands for the mood.
        var tokens = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i + 1 < tokens.Length && ByWord.TryGetValue($"{tokens[i]} {tokens[i + 1]}", out var pair))
            {
                return pair;
            }

            if (ByWord.TryGetValue(tokens[i], out var single))
            {
                return single;
            }
        }

        return KnownTones.Contains(words) ? new MoodReading(words, [], [], [words], []) : null;
    }

    private static Dictionary<string, MoodReading> Build()
    {
        var map = new Dictionary<string, MoodReading>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            var reading = new MoodReading(entry.Words[0], entry.Genres, entry.Labels, entry.Tones, entry.Directions);
            foreach (var word in entry.Words)
            {
                map.TryAdd(word, reading);
            }
        }

        return map;
    }
}
