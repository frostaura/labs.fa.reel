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

        return feature switch
        {
            // Named person clauses fire ONLY when the contribution's role has a verified, user-rated
            // person — so a directorAffinity contribution can never print an actor's name, and no
            // clause ever names someone the user hasn't actually rated. Otherwise fall to the generic
            // (still contribution-backed) line, which asserts affinity without inventing a name.
            "castAffinity" or "lovedCastCount" when Verified(context, CreditRole.Actor) is { } actor =>
                string.Create(CultureInfo.InvariantCulture, $"you rate {actor.Name} {actor.Affinity:0.0} on average"),
            "directorAffinity" when Verified(context, CreditRole.Director) is { } director =>
                string.Create(CultureInfo.InvariantCulture, $"you rate {director.Name} {director.Affinity:0.0} on average"),
            "writerAffinity" when Verified(context, CreditRole.Writer) is { } writer =>
                string.Create(CultureInfo.InvariantCulture, $"you rate {writer.Name} {writer.Affinity:0.0} on average"),
            "castAffinity" or "lovedCastCount" => "the cast overlaps your favourites",
            "directorAffinity" => "the director is in your top tier",
            "writerAffinity" => "written by people you rate highly",
            "userGenreAffinity" when TopRatedGenre(context) is { } genre =>
                $"right in your {Pretty(genre)} wheelhouse",
            "genreDrift" when TopRatedGenre(context) is { } genre =>
                $"matches the {Pretty(genre)} streak you've been on",
            "decadeAffinity" when context.Title.Year is { } year =>
                $"peak {(year / 10) * 10}s — your era",
            "traktRating" when context.Title.TraktRating is { } rating =>
                string.Create(CultureInfo.InvariantCulture, $"the crowd backs it too ({rating:0.0} on Trakt)"),
            "tmdbPopularityLog" => "trending right now",
            "showEngagement" => "you're already deep in this one",
            "releaseAgeLog" when IsFresh(context.Title) => "fresh off release",
            "contrarianAdjustedRating" => "scores well even by your contrarian standards",
            "simLovedCentroid" or "simRecentCentroid" or "simTopLovedMax" or "simTopLovedMean" =>
                "close in feel to the titles you love",
            // A per-genre indicator clause names a genre only when the user has actually rated it.
            _ when feature.StartsWith("genre:") && context.RatedGenres.Contains(feature[6..]) =>
                $"squarely {Pretty(feature[6..])}",
            _ => null,
        };
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
