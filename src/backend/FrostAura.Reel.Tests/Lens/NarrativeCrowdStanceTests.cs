using System.Text.Json;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// A stored narrative's crowd claim is stale once the account crosses a crowd band, even when the
/// profile fingerprint (banded at 0.5) has not moved (IREV3S-07) — and generated text from an older
/// prompt is stale whether or not a rewrite can be queued (HREV6-DESK-01).
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

    private static AccountTasteNarrative Stored(int promptVersion, decimal overall, string hash = "h", string? edited = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Version = 1,
        ProfileHash = hash,
        ProfileFingerprint = "{}",
        Text = "You rate below the crowd overall.",
        FactsJson = JsonSerializer.Serialize(Facts(overall), Web),
        ModelId = "stub/deterministic-v1",
        PromptVersion = promptVersion,
        GeneratedAt = DateTime.UtcNow,
        EditedText = edited,
    };

    // HREV6-DESK-01: an older prompt is stale on its own — the fingerprint and crowd band unchanged —
    // so a read that cannot queue a rewrite (no key, open breaker) still flags it.
    [Theory]
    [InlineData(2, 3, -0.18, null, TasteNarrativeService.StaleOutdatedPrompt)]
    [InlineData(1, 3, 0.10, null, TasteNarrativeService.StaleOutdatedPrompt)]
    [InlineData(2, 3, -0.18, "   ", TasteNarrativeService.StaleOutdatedPrompt)] // a blank edit is no edit
    [InlineData(3, 3, -0.18, null, null)]
    [InlineData(2, 3, -0.18, "I rate with the crowd.", null)] // the user's words, which no prompt wrote
    public void An_older_prompt_is_stale_unless_the_user_rewrote_it(
        int stored, int current, double overall, string? edited, string? expected)
    {
        var narrative = Stored(stored, (decimal)overall, edited: edited);

        Assert.Equal(expected, TasteNarrativeService.StaleReason(narrative, "h", Facts((decimal)overall), current));
    }

    [Theory]
    [InlineData("moved", -0.18, null)]
    [InlineData("h", -0.35, null)]                     // crowd band crossed, fingerprint not
    [InlineData("moved", -0.18, "My own words.")]    // holds for an edited narrative too
    public void A_taste_move_outranks_an_older_prompt(string currentHash, double now, string? edited)
    {
        var narrative = Stored(promptVersion: 2, overall: -0.18m, edited: edited);

        Assert.Equal(
            TasteNarrativeService.StaleTasteMoved,
            TasteNarrativeService.StaleReason(narrative, currentHash, Facts((decimal)now), currentPromptVersion: 3));
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
