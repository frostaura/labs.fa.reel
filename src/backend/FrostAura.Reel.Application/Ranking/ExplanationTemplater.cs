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
    /// </summary>
    public record Context(
        Title Title,
        IReadOnlyList<(string Feature, float Contribution)> Contributions,
        IReadOnlyDictionary<CreditRole, PersonAffinity> VerifiedPersonByRole,
        IReadOnlySet<string> RatedGenres,
        string? AnchorName);

    public static string BuildSentence(Context context)
    {
        var clauses = new List<string>();

        foreach (var (feature, contribution) in context.Contributions)
        {
            if (clauses.Count >= 2)
            {
                break;
            }

            var clause = Translate(feature, contribution, context);
            if (clause is not null && !clauses.Contains(clause))
            {
                clauses.Add(clause);
            }
        }

        if (clauses.Count == 0)
        {
            clauses.Add("Strong fit with your taste profile");
        }

        if (context.AnchorName is { Length: > 0 } anchor)
        {
            clauses.Insert(0, $"Because you loved {anchor}");
        }

        return string.Join(", and ", clauses.Take(2)).TrimEnd('.') + ".";
    }

    private static string? Translate(string feature, float contribution, Context context)
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
                Pick(id,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {actor.Name} {actor.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"{actor.Name} is in this — a {actor.Affinity:0.0} for you on average"),
                    string.Create(CultureInfo.InvariantCulture, $"stars {actor.Name}, whose work you rate {actor.Affinity:0.0}")),
            "directorAffinity" when Verified(context, CreditRole.Director) is { } director =>
                Pick(id,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {director.Name} {director.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"directed by {director.Name}, a {director.Affinity:0.0} for you on average"),
                    string.Create(CultureInfo.InvariantCulture, $"{director.Name} directs — you rate them {director.Affinity:0.0}")),
            "writerAffinity" when Verified(context, CreditRole.Writer) is { } writer =>
                Pick(id,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {writer.Name} {writer.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"written by {writer.Name}, a {writer.Affinity:0.0} for you on average")),
            "castAffinity" or "lovedCastCount" or "directorAffinity" or "writerAffinity" => null,
            "userGenreAffinity" when TopRatedGenre(context) is { } genre =>
                Pick(id,
                    $"right in your {Pretty(genre)} wheelhouse",
                    $"{Pretty(genre)} is one of your strongest genres",
                    $"built for your {Pretty(genre)} taste"),
            "genreDrift" when TopRatedGenre(context) is { } genre =>
                Pick(id,
                    $"matches the {Pretty(genre)} streak you've been on",
                    $"keeps your recent {Pretty(genre)} run going"),
            "decadeAffinity" when context.Title.Year is { } year =>
                Pick(id,
                    $"peak {(year / 10) * 10}s — your era",
                    $"from the {(year / 10) * 10}s, a decade you rate highly"),
            "traktRating" when context.Title.TraktRating is { } rating =>
                Pick(id,
                    string.Create(CultureInfo.InvariantCulture, $"the crowd backs it too ({rating:0.0} on Trakt)"),
                    string.Create(CultureInfo.InvariantCulture, $"rated {rating:0.0} on Trakt by the crowd")),
            "tmdbPopularityLog" => Pick(id, "trending right now", "having a moment right now"),
            "showEngagement" => Pick(id, "you're already deep in this one", "you've put real time into this one"),
            "releaseAgeLog" when IsFresh(context.Title) => Pick(id, "fresh off release", "just released"),
            "contrarianAdjustedRating" =>
                Pick(id,
                    "scores well even by your contrarian standards",
                    "holds up even by your tougher-than-the-crowd standards"),
            "simLovedCentroid" or "simRecentCentroid" or "simTopLovedMax" or "simTopLovedMean" =>
                Pick(id,
                    "close in feel to the titles you love",
                    "shares the feel of your favourites",
                    "in the same lane as the titles you love"),
            // A per-genre indicator clause names a genre only when the user has actually rated it.
            _ when feature.StartsWith("genre:") && context.RatedGenres.Contains(feature[6..]) =>
                Pick(id, $"squarely {Pretty(feature[6..])}", $"firmly in {Pretty(feature[6..])} territory"),
            _ => null,
        };
    }

    /// <summary>
    /// Deterministic phrasing pick per title, so a rail of cards doesn't repeat one sentence shape
    /// while the same title always renders the same sentence (stable across processes — no
    /// randomised string hashing involved).
    /// </summary>
    private static string Pick(Guid titleId, params string[] variants) =>
        variants[(int)(StableHash(titleId) % (uint)variants.Length)];

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
