using System.Text;
using FrostAura.Reel.Domain.Catalog;
using Xunit.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests.ExclusionMatrix;

/// <summary>
/// WS-7 task 7, the app-wide exclusion matrix: every standing exclusion — genre (and its TV stand-in),
/// keyword, language, crowd floor, the For-you bar, and every kind of seen footprint — for movies and
/// TV, against every surface — tonight's picks, feed rows and their pages, genre rails, Similar, the
/// digest, Ask, typeahead, person pages, Saved and the Watch-tonight pin — both when the exclusion
/// stood before the build and when it arrived after it (<see cref="MatrixWorld"/>,
/// <see cref="MatrixCells"/>).
///
/// <para><b>Leaks are ratcheted, not hidden.</b> A cell that leaks fails unless <see cref="MatrixLedger"/>
/// names it with its defect and owner; a ledgered cell that stops leaking fails too, so the ledger is
/// emptied as the owners land their fixes rather than going stale. An unexercised cell — one where the
/// control account never saw the violator — fails outright: a matrix that measured nothing is not a
/// pass.</para>
/// </summary>
[Collection("postgres")]
public class ExclusionMatrixTests(PostgresFixture fixture, ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cells() => MatrixCells.All().Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Every_exclusion_holds_on_every_surface_for_movies_and_tv(string cellId)
    {
        var world = await MatrixWorld.SharedAsync(fixture);
        if (!world.Available)
        {
            return;
        }

        var cell = Parse(cellId);
        var (verdict, evidence) = MatrixCells.Evaluate(world, cell);
        var known = MatrixLedger.Find(cell);
        output.WriteLine($"{cell.Id}: {verdict} — {evidence}{(known is null ? string.Empty : $" [ledger: {known}]")}");

        switch (verdict)
        {
            case Verdict.Leaks or Verdict.OverExcludes:
                Assert.True(known is not null, $"{cell.Id} {verdict}: {evidence}. Fix the surface, or name the defect and its owner in MatrixLedger.");
                break;
            case Verdict.Unexercised:
                Assert.Fail($"{cell.Id} measured nothing: {evidence}. The world must make the control see the violator.");
                break;
            default:
                Assert.True(known is null, $"{cell.Id} is in MatrixLedger ({known}) but now {verdict}: {evidence}. Remove it from the ledger.");
                break;
        }
    }

    [Fact]
    public async Task The_world_builds_cleanly_and_inside_ten_minutes()
    {
        var world = await MatrixWorld.SharedAsync(fixture);
        if (!world.Available)
        {
            return;
        }

        output.WriteLine(Report(world));
        Assert.Empty(world.Problems);
        Assert.True(world.BuildTime < TimeSpan.FromMinutes(10), $"the matrix world took {world.BuildTime} (task 7: under 10 minutes)");
    }

    /// <summary>The whole matrix as a grid, one row per (exclusion, medium, timing), one column per surface.</summary>
    private static string Report(MatrixWorld world)
    {
        var text = new StringBuilder();
        text.AppendLine($"Exclusion matrix — For-you bar {world.Bar}, built in {world.BuildTime:mm\\:ss}");
        text.AppendLine("Legend: ok holds · LEAK · known (ledgered) · OVER over-excludes · ?? unexercised · -- not gated · n/a");
        text.AppendLine($"{"exclusion",-28}| {string.Join(" | ", MatrixWorld.Surfaces.Select(s => s.PadRight(8)[..8]))}");
        foreach (var group in MatrixCells.All().GroupBy(c => (c.Exclusion, c.Media, c.Late)))
        {
            var row = group.Select(cell =>
            {
                var (verdict, _) = MatrixCells.Evaluate(world, cell);
                var known = MatrixLedger.Find(cell) is not null;
                return (verdict switch
                {
                    Verdict.Holds => "ok",
                    Verdict.Leaks => known ? "known" : "LEAK",
                    Verdict.OverExcludes => known ? "known" : "OVER",
                    Verdict.Unexercised => "??",
                    Verdict.NotGated => "--",
                    _ => "n/a",
                }).PadRight(8);
            });
            var label = $"{group.Key.Exclusion}/{(group.Key.Media == MediaType.Movie ? "movie" : "tv")}/{(group.Key.Late ? "late" : "standing")}";
            text.AppendLine($"{label,-28}| {string.Join(" | ", row)}");
        }

        foreach (var problem in world.Problems)
        {
            text.AppendLine($"PROBLEM {problem}");
        }

        return text.ToString();
    }

    private static MatrixCell Parse(string id)
    {
        var parts = id.Split('|');
        return new MatrixCell(parts[0], Enum.Parse<MediaType>(parts[1]), parts[2], parts[3] == "late");
    }
}
