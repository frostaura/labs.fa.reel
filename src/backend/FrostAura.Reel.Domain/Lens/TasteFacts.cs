using System.Text.Json.Serialization;

namespace FrostAura.Reel.Domain.Lens;

/// <summary>One genre and how highly this account rates it (shrunken mean, 0–10) over how many titles.</summary>
public sealed record GenreFact(
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("affinity")] decimal Affinity,
    [property: JsonPropertyName("count")] int Count);

/// <summary>
/// One genre by VOLUME — what the account actually spends its evenings on — with the affinity
/// beside it and its share of all whole-title ratings.
///
/// <para><b>Why this exists separately from <see cref="GenreFact"/>.</b> Affinity alone answers
/// "which genres does this person rate well", which is not the same question as "what does this
/// person watch", and a narrative that opens on the first while claiming the second is simply
/// wrong about the reader. A handful of high ratings in a genre they see twice a year outranks
/// nine hundred thrillers on a shrunken mean, and the opening sentence of a taste profile is the
/// one sentence that must be about volume.</para>
/// </summary>
public sealed record GenreVolumeFact(
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("affinity")] decimal Affinity,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("share")] decimal Share);

/// <summary>A genre the recency-weighted estimate has moved on, and by how much against all-time.</summary>
public sealed record DriftFact(
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("delta")] decimal Delta);

/// <summary>A title the account rated, by name. The narrative may name these and nothing else.</summary>
public sealed record LovedFact(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("rating")] short Rating);

/// <summary>
/// A credited person the account's taste is VERIFIED on — ≥2 rated titles. <c>Average</c> is the plain
/// mean of the account's own ratings, each title once (HREV2-D-01); people are ranked by shrunken affinity.
/// </summary>
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

/// <summary>
/// One pocket of taste the account keeps returning to, named by the titles nearest its centre.
///
/// <para>This is the prose half of the multi-centroid retrieval work: a partition over embeddings
/// finds corners of a library that no genre label describes — the late-night procedurals, the
/// December films, the three-hour ones — and <paramref name="Exemplars"/> is the only handle a human
/// (or a narrative) has on such a pocket, because it has no name.</para>
///
/// <para><paramref name="Share"/> is the pocket's share of the account's total taste weight, 0–1.
/// It is the ranking key, and it is what the 3% bar is applied to.</para>
/// </summary>
public sealed record ClusterFact(
    [property: JsonPropertyName("exemplars")] IReadOnlyList<string> Exemplars,
    [property: JsonPropertyName("share")] decimal Share,
    [property: JsonPropertyName("titleCount")] int TitleCount);

/// <summary>Where an account's ratings sit against the crowd's, in the three bands every surface uses.</summary>
public enum CrowdStance
{
    /// <summary>Within <see cref="ContrarianFact.Band"/> of the crowd either way.</summary>
    WithTheCrowd,

    /// <summary>Above the crowd by more than the band.</summary>
    KinderThanTheCrowd,

    /// <summary>Below the crowd by more than the band.</summary>
    HarsherThanTheCrowd,
}

/// <summary>How far the account sits from the crowd, overall and on its strongest genres.</summary>
public sealed record ContrarianFact(
    [property: JsonPropertyName("overall")] decimal Overall,
    [property: JsonPropertyName("byGenre")] IReadOnlyList<GenreOffset> ByGenre)
{
    /// <summary>
    /// The width of "with the crowd", in rating points either way. The Taste DNA tile and the
    /// why-line contrarian clause both read an offset inside ±0.3 as "with the crowd", so prose
    /// written from these facts must too: a narrative that says "you rate below the crowd" beside a
    /// tile reading "-0.2 · with the crowd" contradicts the page it sits on (IREV3S-07).
    /// </summary>
    public const decimal Band = 0.3m;

    /// <summary>The band an offset falls in. The edge itself (exactly ±0.3) is still with the crowd.</summary>
    public static CrowdStance StanceOf(decimal offset) => offset switch
    {
        > Band => CrowdStance.KinderThanTheCrowd,
        < -Band => CrowdStance.HarsherThanTheCrowd,
        _ => CrowdStance.WithTheCrowd,
    };

    /// <summary>The overall band — the only claim about the crowd the narrative may make.</summary>
    public CrowdStance Stance() => StanceOf(Overall);
}

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
    /// <summary>
    /// Genres this account rates highest, among those it has rated OFTEN ENOUGH for a mean to
    /// carry information. Ordered by affinity. The profile fingerprint reads this list, and only
    /// this list.
    /// </summary>
    [property: JsonPropertyName("topGenres")] IReadOnlyList<GenreFact> TopGenres,
    /// <summary>Genres by VOLUME, most-watched first — what this account actually watches.</summary>
    [property: JsonPropertyName("mostWatchedGenres")] IReadOnlyList<GenreVolumeFact> MostWatchedGenres,
    [property: JsonPropertyName("driftingToward")] IReadOnlyList<DriftFact> DriftingToward,
    [property: JsonPropertyName("lovedAnchors")] IReadOnlyList<LovedFact> LovedAnchors,
    /// <summary>Titles rated highly in the recent window — what the narrative may cite as "lately".</summary>
    [property: JsonPropertyName("recentLoves")] IReadOnlyList<LovedFact> RecentLoves,
    /// <summary>Titles rated badly in the recent window. The honest half of the same evidence.</summary>
    [property: JsonPropertyName("recentDislikes")] IReadOnlyList<LovedFact> RecentDislikes,
    [property: JsonPropertyName("declinedLine")] string? DeclinedLine,
    [property: JsonPropertyName("verifiedPeople")] IReadOnlyList<PersonFact> VerifiedPeople,
    [property: JsonPropertyName("texture")] TextureFact? Texture,
    [property: JsonPropertyName("contrarian")] ContrarianFact Contrarian,
    [property: JsonPropertyName("preferenceTags")] IReadOnlyList<string> PreferenceTags,
    /// <summary>
    /// Pockets of taste the account keeps returning to, heaviest first — see <see cref="ClusterFact"/>.
    ///
    /// <para><b>Deliberately not in the writer's prompt, and deliberately not in
    /// <see cref="GroundedNames"/>.</b> Naming these to the model is a prompt change, which is a
    /// <c>PromptVersion</c> bump, which cold-starts every cached lens note on the account — a real
    /// cost that belongs to whichever release actually wants the sentence, not to the release that
    /// computes the facts. The invariant this file states runs one way only: everything the PROMPT
    /// names must appear in the fact sheet. The fact sheet is allowed to know more.</para>
    /// </summary>
    [property: JsonPropertyName("clusters")] IReadOnlyList<ClusterFact> Clusters)
{
    /// <summary>
    /// Genres the account rates well but rarely watches — present in <see cref="TopGenres"/> and
    /// absent from <see cref="MostWatchedGenres"/>. These are the ONLY genres the narrative may
    /// describe with "when you do watch X you rate it well", and it may never describe them as a
    /// defining preference. Derived rather than stored: it is a relationship between two lists, and
    /// a stored copy of it would be a third thing to keep in step.
    /// </summary>
    public IReadOnlyList<GenreFact> SparseButWellRated()
    {
        var watched = MostWatchedGenres.Select(g => g.Genre).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TopGenres.Where(g => !watched.Contains(g.Genre)).ToList();
    }

    /// <summary>
    /// Every proper noun the narrative is permitted to use — loved titles, recent loves and
    /// dislikes, and verified people. The grounding check tests each candidate name against this
    /// set; anything else is an invention and sinks the draft.
    ///
    /// <para>Anything the prompt NAMES must appear here. A fact added to the prompt and forgotten
    /// here does not produce a lenient guard — it produces a guard that refuses every narrative
    /// that obeys the prompt, which is the harder failure to read.</para>
    /// </summary>
    public IReadOnlyCollection<string> GroundedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loved in LovedAnchors.Concat(RecentLoves).Concat(RecentDislikes))
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
