using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Ml.ClosedLoop;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The selection notes printed under each block of the track record. They are static copy shared
/// by every account and every day, so none of them may carry a number that describes one account's
/// data at one moment (HREV5-DESK-02: "Only about 2% of ratings are made in the app" was 3.4% on the
/// snapshot that caught it, and would have been printed for any other account too).
/// </summary>
public class TrackRecordPopulationNoteTests
{
    public static TheoryData<string> Sources => [AccuracySource.Exposures, AccuracySource.Retro];

    [Theory]
    [MemberData(nameof(Sources))]
    public void No_selection_note_states_a_share_or_a_count(string source)
    {
        var populations = AccuracyQuery.PopulationsFor(source);

        foreach (var note in new[] { populations.Headline, populations.Counterfactual, populations.Conversion, populations.CrossReference })
        {
            Assert.DoesNotMatch(new Regex(@"\d"), note.SelectionNote);
        }
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void The_cross_reference_note_still_says_why_its_join_is_temporal(string source)
    {
        var note = AccuracyQuery.PopulationsFor(source).CrossReference.SelectionNote;

        Assert.Contains("joins by time rather than cause", note, StringComparison.Ordinal);
        Assert.Contains("it never gates anything", note, StringComparison.Ordinal);
    }
}
