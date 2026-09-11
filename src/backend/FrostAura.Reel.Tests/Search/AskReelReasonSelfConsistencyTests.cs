using System.Globalization;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Search;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The reason line, parsed back against the counts that produced it, across the whole branch space.
///
/// <para><b>Why a parser and not more example strings.</b> The line is a CLAUSE LIST: clauses
/// compose, so the number of reachable sentences is the product of the branches, and the defect
/// that shipped was not in any one clause — every clause was individually defensible. It was in
/// the composition. A starved turn produced</para>
///
/// <code>
/// 0 picks matched everything you asked · 2 closest-but-outside shown separately ·
/// widened to find enough · only 1 pick cleared your 6.0 bar, so it was relaxed ·
/// 1 pick shown below your 6.0 bar
/// </code>
///
/// <para>against <c>results.length == 2</c>, <c>belowFloor == []</c> and two stored cards, neither
/// below the floor. Three of the five clauses were false as rendered, and the example-based tests
/// beside this file all passed, because none of them had that combination. So this one asserts a
/// PROPERTY instead: every clause the builder can emit matches a known pattern, and every number in
/// it equals the count it claims to be — for every combination of picks, outside, tray, pulled,
/// above-floor and the two flags.</para>
///
/// <para>It is deliberately strict about unknown clauses. A new clause added without a pattern here
/// fails the theory rather than slipping through unchecked, which is the only way a parser-based
/// test stays a test rather than becoming a rubber stamp.</para>
/// </summary>
public class AskReelReasonSelfConsistencyTests
{
    private const decimal Floor = 6.0m;

    /// <summary>The bar as the copy renders it — one invariant decimal, at every site.</summary>
    private static readonly string Bar = AskReelReason.FormatFloor(Floor);

    public static TheoryData<int, int, int, int, int, bool, bool> Branches()
    {
        var data = new TheoryData<int, int, int, int, int, bool, bool>();
        foreach (var picks in new[] { 0, 1, 2, 9, 24 })
        {
            foreach (var outside in new[] { 0, 1, 3, 11 })
            {
                foreach (var tray in new[] { 0, 1, 17 })
                {
                    foreach (var relaxed in new[] { false, true })
                    {
                        // Relaxation drops nothing, so a relaxed turn's tray is 0 by construction —
                        // a combination the pipeline cannot produce is not worth pinning copy for.
                        var effectiveTray = relaxed ? 0 : tray;
                        foreach (var aboveFloor in AboveFloorCases(picks + outside))
                        {
                            data.Add(picks, outside, effectiveTray, 40, aboveFloor, relaxed, picks + outside < 12);
                        }
                    }
                }
            }
        }

        return data;
    }

    /// <summary>Every above-floor count a screen of <paramref name="shown"/> cards can have, plus "cannot count".</summary>
    private static IEnumerable<int> AboveFloorCases(int shown) =>
        new[] { AskReelReason.AboveFloorUnknown, 0, 1, shown }.Distinct().Where(n => n <= shown || n < 0);

    [Theory]
    [MemberData(nameof(Branches))]
    public void Every_clause_is_known_and_every_number_is_the_count_it_claims(
        int picks, int outside, int tray, int pulled, int aboveFloor, bool relaxed, bool widened)
    {
        var counts = new TurnCounts(picks, outside, tray, pulled, aboveFloor);
        var line = AskReelReason.Build(counts, widened, relaxed, Floor);

        if (counts.Shown == 0)
        {
            Assert.Equal(AskReelReason.Empty, line);
            return;
        }

        var clauses = line.Split(AskReelReason.Separator);
        var seen = new List<string>(clauses.Length);
        foreach (var clause in clauses)
        {
            seen.Add(Check(clause, counts, relaxed));
        }

        // ── The composition rules, which no single clause can enforce ────────────────────────

        // Exactly one opening clause, and it names the PICKS — never picks plus the outside channel.
        Assert.Single(seen, k => k is "ranked" or "matched");
        Assert.Equal(outside > 0 ? "matched" : "ranked", seen[0]);
        Assert.Equal(outside > 0, seen.Contains("outside"));

        // The tray clause exists exactly when there IS a tray. A relaxed turn has none, and it was
        // borrowing this clause's own words ("shown below") for cards that were in the grid.
        Assert.Equal(tray > 0, seen.Contains("tray"));
        Assert.False(relaxed && seen.Contains("tray"));

        // The relaxed pair is all-or-nothing and mutually exclusive with "every … cleared".
        Assert.Equal(relaxed, seen.Any(k => k is "relaxed-unknown" or "relaxed-none" or "relaxed-some" or "relaxed-all"));
        Assert.Equal(
            seen.Contains("relaxed-none") || seen.Contains("relaxed-some"),
            seen.Contains("below-on-screen"));
        Assert.False(seen.Contains("relaxed-all") && seen.Contains("below-on-screen"));

        Assert.Equal(widened, seen.Contains("widened"));
    }

    /// <summary>
    /// Matches one clause against the closed set of shapes the builder may emit, asserting the
    /// numbers inside it, and returns a key naming which shape it was. An unrecognised clause fails
    /// here: a builder that grows a new sentence has to grow a rule for it in the same change.
    /// </summary>
    private static string Check(string clause, TurnCounts counts, bool relaxed)
    {
        var unit = counts.Outside > 0 ? "cards?" : "picks?";

        if (Match(clause, @"^(\d+) (pick|picks) ranked for you, from (\d+) pulled$") is { } ranked)
        {
            Assert.Equal(counts.Picks, Number(ranked, 1));
            Assert.Equal(counts.Pulled, Number(ranked, 3));
            AssertPlural(ranked.Groups[2].Value, counts.Picks, "pick", "picks");
            return "ranked";
        }

        if (Match(clause, @"^(\d+) (pick|picks) matched everything you asked$") is { } matched)
        {
            Assert.Equal(counts.Picks, Number(matched, 1));
            AssertPlural(matched.Groups[2].Value, counts.Picks, "pick", "picks");
            return "matched";
        }

        if (Match(clause, @"^(\d+) closest-but-outside shown separately$") is { } outside)
        {
            Assert.Equal(counts.Outside, Number(outside, 1));
            return "outside";
        }

        if (clause == "widened to find enough")
        {
            return "widened";
        }

        if (Match(clause, $@"^(\d+) under your {Bar} bar — shown below$") is { } trayClause)
        {
            Assert.Equal(counts.Tray, Number(trayClause, 1));
            Assert.True(counts.Tray > 0, "the tray clause fired on a turn with no tray.");
            Assert.False(relaxed, "a relaxed turn stores no tray and must not claim one.");
            return "tray";
        }

        if (clause == $"your {Bar} bar was relaxed to fill the screen")
        {
            Assert.True(counts.AboveFloor < 0, "a caller that CAN count must not use the count-free clause.");
            return "relaxed-unknown";
        }

        if (clause == $"nothing cleared your {Bar} bar, so it was relaxed")
        {
            Assert.Equal(0, counts.AboveFloor);
            return "relaxed-none";
        }

        if (Match(clause, $@"^only (\d+) ({unit}) cleared your {Bar} bar, so it was relaxed$") is { } some)
        {
            Assert.Equal(counts.AboveFloor, Number(some, 1));
            Assert.InRange(counts.AboveFloor, 1, counts.Shown - 1);
            AssertUnit(some.Groups[2].Value, counts, counts.AboveFloor);
            return "relaxed-some";
        }

        if (Match(clause, $@"^every (pick|card) cleared your {Bar} bar$") is { } all)
        {
            Assert.Equal(counts.Shown, counts.AboveFloor);
            Assert.Equal(counts.Outside > 0 ? "card" : "pick", all.Groups[1].Value);
            return "relaxed-all";
        }

        if (Match(clause, $@"^(\d+) ({unit}) on screen under your {Bar} bar$") is { } below)
        {
            Assert.Equal(counts.Shown - counts.AboveFloor, Number(below, 1));
            Assert.True(counts.AboveFloor >= 0, "a caller that cannot count must not claim a below-bar count.");
            AssertUnit(below.Groups[2].Value, counts, counts.Shown - counts.AboveFloor);
            return "below-on-screen";
        }

        Assert.Fail($"unrecognised clause: '{clause}'");
        return string.Empty;
    }

    private static Match? Match(string clause, string pattern)
    {
        var match = Regex.Match(clause, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success ? match : null;
    }

    private static int Number(Match match, int group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static void AssertPlural(string rendered, int count, string singular, string plural) =>
        Assert.Equal(count == 1 ? singular : plural, rendered);

    /// <summary>
    /// The noun is "pick" only while the whole screen is picks. Once an outside channel exists these
    /// counts span both lists, and an outside-request card is by definition not a pick.
    /// </summary>
    private static void AssertUnit(string rendered, TurnCounts counts, int count) =>
        AssertPlural(
            rendered, count,
            counts.Outside > 0 ? "card" : "pick",
            counts.Outside > 0 ? "cards" : "picks");
}
