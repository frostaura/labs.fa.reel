using System.Globalization;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Deterministic why-this sentences from the model's actual top contributions — the honest
/// explainability layer behind every card (foresight DescriptionTemplater precedent; optional
/// LLM polish stays behind a flag, off by default). Never claims a reason the model didn't use.
/// </summary>
public static class ExplanationTemplater
{
    /// <summary>
    /// The ultimate fallback sentence, emitted when nothing verifiable can be cited. Single-sourced
    /// so every surface that special-cases it (the once-per-response guard, the paginator's and the
    /// feed endpoint's fallback paths) compares against the exact sentence this templater builds.
    /// </summary>
    public const string GenericFallback = "Strong fit with your taste profile.";

    /// <summary>A person the user has actually rated (count ≥ 2), with their shrunken affinity.</summary>
    public record PersonAffinity(string Name, decimal Affinity);

    /// <summary>Shared empty person map — a title with no user-rated cast/crew grounds no name clause.</summary>
    public static readonly IReadOnlyDictionary<CreditRole, PersonAffinity> NoPersons =
        new Dictionary<CreditRole, PersonAffinity>();

    /// <summary>
    /// Everything the sentence is allowed to cite, pre-verified by the caller so the templater can
    /// never fabricate a taste fact. <paramref name="VerifiedPersonByRole"/> holds only people the
    /// user has genuinely rated, keyed by their actual credit role (so a director clause never prints
    /// an actor's name); <paramref name="RatedGenres"/> holds only genres present in the user's
    /// GenreRatings (so a genre clause never names a genre the user never rated).
    /// <paramref name="AnchorSharedGenre"/> is a genre the title genuinely shares with the anchor,
    /// and <paramref name="Tone"/> the title's LLM-derived tone — printed ONLY when it is a member
    /// of the closed tone vocabulary, because rows extracted before vocabulary enforcement carry
    /// polluted free-text labels.
    /// </summary>
    public record Context(
        Title Title,
        IReadOnlyList<(string Feature, float Contribution)> Contributions,
        IReadOnlyDictionary<CreditRole, PersonAffinity> VerifiedPersonByRole,
        IReadOnlySet<string> RatedGenres,
        string? AnchorName,
        string? AnchorSharedGenre = null,
        string? Tone = null);

    /// <summary>
    /// Builds the deterministic why-sentence. <paramref name="variantSeed"/> shifts every phrasing
    /// pick by that many variants — 0 is the title's canonical sentence; the no-repeat guard bumps
    /// it when two cards on one rail would otherwise render identical text.
    /// </summary>
    public static string BuildSentence(Context context, int variantSeed = 0)
    {
        var clauses = new List<string>();

        foreach (var (feature, contribution) in context.Contributions)
        {
            if (clauses.Count >= 2)
            {
                break;
            }

            var clause = Translate(feature, contribution, context, variantSeed);
            if (clause is not null && !clauses.Contains(clause))
            {
                clauses.Add(clause);
            }
        }

        if (clauses.Count == 0)
        {
            clauses.Add(GenericFallback.TrimEnd('.'));
        }

        if (context.AnchorName is { Length: > 0 } anchor)
        {
            // The anchor clause names what links the two titles (shared genre, tone) whenever
            // something verifiable is on hand — never the vague "Because you loved {anchor}"
            // template the founder rejected.
            clauses.Insert(0, AnchorClause(context, anchor, variantSeed));
        }

        var sentence = string.Join(", and ", clauses.Take(2)).TrimEnd('.') + ".";

        // Sentence integrity: clause templates are written as mid-sentence fragments so they can
        // follow an anchor clause ("…, and you rate X 7.2 on average"). A fragment that ends up
        // LEADING the sentence must be promoted to sentence case — "you rate X 7.2 on average."
        // and "written by Stephen King, who you rate 6.8." shipped as whole whys (2026-08-10).
        return char.IsUpper(sentence[0]) ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    /// <summary>
    /// The no-repeat guard for one rail: builds the title's sentence, and when an earlier card on
    /// the same rail already rendered exactly that text, retries with bumped variant seeds (max 3)
    /// until distinct. If every variant collides the last attempt is returned as-is — a repeated
    /// line is a flaw, a crash or a blank card would be a failure. Adds the result to
    /// <paramref name="takenWhys"/> either way.
    /// </summary>
    public static string BuildDistinctSentence(Context context, ISet<string> takenWhys)
    {
        var sentence = BuildSentence(context);
        for (var seed = 1; seed <= 3 && takenWhys.Contains(sentence); seed++)
        {
            sentence = BuildSentence(context, seed);
        }

        takenWhys.Add(sentence);
        return sentence;
    }

    /// <summary>
    /// The response-level guard on <see cref="GenericFallback"/>: the generic sentence may appear
    /// at most once per served payload. The per-rail no-repeat guard cannot help here — the
    /// fallback has a single phrasing, so every seed collides and the same boilerplate rendered
    /// three times in one view (observed live, 2026-08-10). Later occurrences are blanked: an
    /// empty caption reads better than repeated filler. Items must arrive in display order so the
    /// first card shown is the one that keeps the sentence.
    /// </summary>
    public static void EnforceGenericOnce(IEnumerable<Domain.Feed.FeedItem> orderedItems)
    {
        var seen = false;
        foreach (var item in orderedItems)
        {
            if (!string.Equals(item.WhyThisSentence, GenericFallback, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen)
            {
                item.WhyThisSentence = string.Empty;
            }
            else
            {
                seen = true;
            }
        }
    }

    /// <summary>
    /// The opening clause of an anchored card: the concrete link to the anchor. Prefers the richest
    /// verifiable combination — shared genre + closed-vocab tone, then tone, then genre — and only
    /// falls back to a bare same-lane phrasing when nothing specific is available to cite.
    /// </summary>
    private static string AnchorClause(Context context, string anchor, int seed)
    {
        var id = context.Title.Id;
        var tone = AttributeVocabulary.IsClosedVocabTone(context.Tone) ? context.Tone : null;
        var genre = context.AnchorSharedGenre is { Length: > 0 } g ? Pretty(g) : null;

        return (genre, tone) switch
        {
            ({ } sharedGenre, { } sharedTone) => Pick(id, seed,
                $"A {sharedGenre} pick with the same {sharedTone} streak as {anchor}",
                $"Brings {anchor}'s {sharedTone} energy back to your {sharedGenre} lane",
                $"Like {anchor}, it plays {sharedGenre} {sharedTone}"),
            (null, { } sharedTone) => Pick(id, seed,
                $"Carries the same {sharedTone} streak as {anchor}",
                $"Shares {anchor}'s {sharedTone} tone",
                $"Runs on the {sharedTone} mood that made {anchor} land for you"),
            ({ } sharedGenre, null) => Pick(id, seed,
                $"A {sharedGenre} pick in the same lane as {anchor}",
                $"Sits right beside {anchor} on the {sharedGenre} shelf",
                $"Scratches the {sharedGenre} itch {anchor} left you with"),
            _ => Pick(id, seed,
                $"Cut from the same cloth as {anchor}",
                $"In the same lane as {anchor}",
                $"A close cousin of {anchor}"),
        };
    }

    private static string? Translate(string feature, float contribution, Context context, int seed)
    {
        if (contribution <= 0)
        {
            return null; // only positive reasons belong on a recommendation card
        }

        var id = context.Title.Id;
        return feature switch
        {
            // Named person clauses fire ONLY when the contribution's role has a verified, user-rated
            // person — so a directorAffinity contribution can never print an actor's name, and no
            // clause ever names someone the user hasn't actually rated. When no verified person
            // exists for the role there is nothing honest to say, so the clause is skipped entirely
            // (fall through to the next contribution) rather than asserting an overlap or a "top
            // tier" the user cannot verify on the card.
            "castAffinity" or "lovedCastCount" when Verified(context, CreditRole.Actor) is { } actor =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {actor.Name} {actor.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"{actor.Name} is in this — a {actor.Affinity:0.0} for you on average"),
                    string.Create(CultureInfo.InvariantCulture, $"stars {actor.Name}, whose work you rate {actor.Affinity:0.0}")),
            "directorAffinity" when Verified(context, CreditRole.Director) is { } director =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {director.Name} {director.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"directed by {director.Name}, who you rate {director.Affinity:0.0}"),
                    string.Create(CultureInfo.InvariantCulture, $"{director.Name} directs — you rate them {director.Affinity:0.0}")),
            "writerAffinity" when Verified(context, CreditRole.Writer) is { } writer =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {writer.Name} {writer.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"written by {writer.Name}, who you rate {writer.Affinity:0.0}")),
            "castAffinity" or "lovedCastCount" or "directorAffinity" or "writerAffinity" => null,
            "userGenreAffinity" when TopRatedGenre(context) is { } genre =>
                Pick(id, seed,
                    $"right in your {Pretty(genre)} wheelhouse",
                    $"{Pretty(genre)} is one of your strongest genres",
                    $"built for your {Pretty(genre)} taste"),
            "genreDrift" when TopRatedGenre(context) is { } genre =>
                Pick(id, seed,
                    $"matches the {Pretty(genre)} streak you've been on",
                    $"keeps your recent {Pretty(genre)} run going"),
            "decadeAffinity" when context.Title.Year is { } year =>
                Pick(id, seed,
                    $"peak {(year / 10) * 10}s — your era",
                    $"from the {(year / 10) * 10}s, a decade you rate highly"),
            "traktRating" when context.Title.TraktRating is { } rating =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"the crowd backs it too ({rating:0.0} on Trakt)"),
                    string.Create(CultureInfo.InvariantCulture, $"rated {rating:0.0} on Trakt by the crowd")),
            "tmdbPopularityLog" => Pick(id, seed, "trending right now", "having a moment right now"),
            "releaseAgeLog" when IsFresh(context.Title) => Pick(id, seed, "fresh off release", "just released"),
            "contrarianAdjustedRating" =>
                Pick(id, seed,
                    "scores well even by your contrarian standards",
                    "holds up even by your tougher-than-the-crowd standards"),
            "simLovedCentroid" or "simRecentCentroid" or "simTopLovedMax" or "simTopLovedMean" =>
                Pick(id, seed,
                    "close in feel to the titles you love",
                    "shares the feel of your favourites",
                    "in the same lane as the titles you love"),
            // A per-genre indicator clause names a genre only when the user has actually rated it.
            _ when feature.StartsWith("genre:") && context.RatedGenres.Contains(feature[6..]) =>
                Pick(id, seed, $"squarely {Pretty(feature[6..])}", $"firmly in {Pretty(feature[6..])} territory"),
            // Tone one-hots (schema v4), guarded by the same closed-vocabulary check AnchorClause
            // applies — a pre-enforcement row's free text must never reach the screen.
            _ when feature.StartsWith("tone:") && AttributeVocabulary.IsClosedVocabTone(feature[5..]) =>
                Pick(id, seed, $"{feature[5..]} in tone", $"has that {feature[5..]} streak"),
            // The era one-hots deliberately have NO clause yet. Half the vocabulary ("timeless",
            // "unknown", "contemporary") produces a sentence that says nothing, and an unexplained
            // feature costs nothing here: BuildSentence walks the whole contribution list, so a null
            // clause is skipped rather than blanking the card.
            _ => null,
        };
    }

    /// <summary>
    /// Deterministic phrasing pick per title, so a rail of cards doesn't repeat one sentence shape
    /// while the same title always renders the same sentence (stable across processes — no
    /// randomised string hashing involved). The seed walks the variants in order, so consecutive
    /// bumps from the no-repeat guard always reach a different phrasing when one exists.
    /// </summary>
    private static string Pick(Guid titleId, int seed, params string[] variants) =>
        variants[(int)((StableHash(titleId) + (uint)seed) % (uint)variants.Length)];

    private static uint StableHash(Guid titleId)
    {
        var bytes = titleId.ToByteArray();
        return (uint)(bytes[0] | (bytes[5] << 8) | (bytes[10] << 16) | (bytes[15] << 24));
    }

    /// <summary>The verified, user-rated person for a role (null when none — no name is invented).</summary>
    private static PersonAffinity? Verified(Context context, CreditRole role) =>
        context.VerifiedPersonByRole.GetValueOrDefault(role);

    /// <summary>The title's first genre the user has actually rated (null when none is verifiable).</summary>
    private static string? TopRatedGenre(Context context) =>
        context.Title.Genres.FirstOrDefault(context.RatedGenres.Contains);

    private static bool IsFresh(Title title)
    {
        var released = title.ReleasedAt ?? title.FirstAiredAt;
        return released is { } date && (DateTime.UtcNow - date).TotalDays <= 120;
    }

    private static string Pretty(string slug) => slug.Replace('-', ' ');
}
