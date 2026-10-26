using System.Text.Json;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// A stored narrative's crowd claim is stale once the account crosses a crowd band, even when the
/// profile fingerprint (banded at 0.5) has not moved (IREV3S-07).
/// </summary>
public class NarrativeCrowdStanceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static TasteFacts Facts(decimal overall) => new(
        UserMean: 6.7m, RatingsCount: 100, MovieRatedShare: 0.6m, TopGenres: [], MostWatchedGenres: [],
        DriftingToward: [], LovedAnchors: [], RecentLoves: [], RecentDislikes: [], DeclinedLine: null,
        VerifiedPeople: [], Texture: null, Contrarian: new ContrarianFact(overall, []), PreferenceTags: [], Clusters: []);

    [Theory]
    [InlineData(-0.35, -0.18, true)]   // harsher → with: the fingerprint's 0.5 band does not see it
    [InlineData(-0.18, -0.22, false)]  // both with the crowd
    [InlineData(0.25, 0.31, true)]     // with → kinder
    [InlineData(-0.5, -0.4, false)]    // both harsher
    public void Crossing_a_band_marks_the_stored_narrative_stale(double stored, double now, bool moved)
    {
        var storedJson = JsonSerializer.Serialize(Facts((decimal)stored), Web);

        Assert.Equal(moved, TasteNarrativeService.CrowdStanceMoved(storedJson, Facts((decimal)now)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{}")]
    public void An_unreadable_stored_sheet_is_never_shown_as_current(string? storedJson)
    {
        Assert.True(TasteNarrativeService.CrowdStanceMoved(storedJson, Facts(0m)));
    }

    [Theory]
    [InlineData(0.30, CrowdStance.WithTheCrowd)]
    [InlineData(-0.30, CrowdStance.WithTheCrowd)]
    [InlineData(0.31, CrowdStance.KinderThanTheCrowd)]
    [InlineData(-0.31, CrowdStance.HarsherThanTheCrowd)]
    public void The_band_matches_the_tile(double offset, CrowdStance stance)
    {
        // TasteDna.tsx: > 0.3 "kinder than the crowd", < -0.3 "harsher than the crowd".
        Assert.Equal(stance, ContrarianFact.StanceOf((decimal)offset));
    }
}
