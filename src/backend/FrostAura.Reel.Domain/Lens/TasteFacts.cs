using System.Text.Json.Serialization;

namespace FrostAura.Reel.Domain.Lens;

/// <summary>One genre and how highly this account rates it (shrunken mean, 0–10) over how many titles.</summary>
public sealed record GenreFact(
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("affinity")] decimal Affinity,
    [property: JsonPropertyName("count")] int Count);

/// <summary>A genre the recency-weighted estimate has moved on, and by how much against all-time.</summary>
public sealed record DriftFact(
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("delta")] decimal Delta);

/// <summary>A title the account loves, by name. The narrative may name these and nothing else.</summary>
public sealed record LovedFact(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("rating")] short Rating);

/// <summary>A credited person the account's taste is VERIFIED on — ≥2 rated titles, shrunken average.</summary>
public sealed record PersonFact(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("average")] decimal Average,
    [property: JsonPropertyName("titleCount")] int TitleCount);

/// <summary>
/// The account's average attribute texture over the titles it rated highly — the seven scalars the
/// catalogue was enriched with, aggregated into a description of the person rather than the film.
/// This is where the extraction spend finally reaches the prose.
/// </summary>
public sealed record TextureFact(
    [property: JsonPropertyName("darkness")] decimal Darkness,
    [property: JsonPropertyName("pacing")] decimal Pacing,
    [property: JsonPropertyName("complexity")] decimal Complexity,
    [property: JsonPropertyName("emotionalIntensity")] decimal EmotionalIntensity,
    [property: JsonPropertyName("humor")] decimal Humor,
    [property: JsonPropertyName("optimism")] decimal Optimism,
    [property: JsonPropertyName("ensembleVsSolo")] decimal EnsembleVsSolo,
    [property: JsonPropertyName("sampleSize")] int SampleSize);

/// <summary>How far the account sits from the crowd, overall and on its strongest genres.</summary>
public sealed record ContrarianFact(
    [property: JsonPropertyName("overall")] decimal Overall,
    [property: JsonPropertyName("byGenre")] IReadOnlyList<GenreOffset> ByGenre);

public sealed record GenreOffset(
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("offset")] decimal Offset);

/// <summary>
/// Everything the taste narrative is allowed to be written from — and, persisted alongside the
/// text, everything a later reader can check it against. The fact-grounding guard rejects a
/// narrative that names any title or person absent from here, which is only enforceable because
/// this object is stored rather than recomputed.
/// </summary>
public sealed record TasteFacts(
    [property: JsonPropertyName("userMean")] decimal UserMean,
    [property: JsonPropertyName("ratingsCount")] int RatingsCount,
    [property: JsonPropertyName("movieRatedShare")] decimal MovieRatedShare,
    [property: JsonPropertyName("topGenres")] IReadOnlyList<GenreFact> TopGenres,
    [property: JsonPropertyName("driftingToward")] IReadOnlyList<DriftFact> DriftingToward,
    [property: JsonPropertyName("lovedAnchors")] IReadOnlyList<LovedFact> LovedAnchors,
    [property: JsonPropertyName("declinedLine")] string? DeclinedLine,
    [property: JsonPropertyName("verifiedPeople")] IReadOnlyList<PersonFact> VerifiedPeople,
    [property: JsonPropertyName("texture")] TextureFact? Texture,
    [property: JsonPropertyName("contrarian")] ContrarianFact Contrarian,
    [property: JsonPropertyName("preferenceTags")] IReadOnlyList<string> PreferenceTags)
{
    /// <summary>
    /// Every proper noun the narrative is permitted to use — loved titles, verified people, and the
    /// titles named in the declined line. The grounding check tests each candidate name against
    /// this set; anything else is an invention and sinks the draft.
    /// </summary>
    public IReadOnlyCollection<string> GroundedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loved in LovedAnchors)
        {
            names.Add(loved.Name);
        }

        foreach (var person in VerifiedPeople)
        {
            names.Add(person.Name);
        }

        return names;
    }
}
