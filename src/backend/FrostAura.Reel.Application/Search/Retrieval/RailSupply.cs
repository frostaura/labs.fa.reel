namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// Supply counts for a feed rail (D-16): the same <see cref="SupplyReport"/> Ask Reel quotes, for a
/// genre rail's genre, so a rail that comes up short can say "your conditions hide N — see why"
/// with the per-condition knockouts behind it instead of rendering three cards and no explanation.
/// </summary>
public static class RailSupply
{
    /// <summary>
    /// The supply behind a genre rail: titles carrying <paramref name="genreSlug"/> (with its aliases,
    /// the anime composite and the TV stand-ins — what the rail's genre means everywhere else), in
    /// <paramref name="media"/>, partitioned exactly like an Ask Reel supply report. Titles the rail
    /// is already showing go in <paramref name="shown"/> so they are not offered again.
    ///
    /// <para>Counted as the rail serves: the rail waives the account's include-genre rules (FD-6), so
    /// they are left out of the count and of the knockouts; an exclude-genre rule never is.</para>
    /// </summary>
    public static Task<SupplyReport> ForGenreRailAsync(
        this ISupplyReporter supply,
        Guid accountId,
        string genreSlug,
        MediaScope media,
        IReadOnlyCollection<Guid> shown,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(supply);
        var slug = genreSlug.Trim().ToLowerInvariant();

        // No user words and no labels: the concept is the genre alone, never a title whose NAME
        // happens to contain the word "comedy".
        var hard = new HardConstraints([new ConceptTerm(string.Empty, [], [slug], [])], [], null, null, media);
        return supply.ReportAsync(accountId, hard, shown, IncludeGenreRules.Waived, ct);
    }
}
