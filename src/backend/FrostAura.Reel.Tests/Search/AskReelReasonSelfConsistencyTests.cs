using System.Globalization;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Search;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The reason line, parsed back against the counts that produced it, across the whole branch space.
///
/// <para><b>Why a parser and not more example strings.</b> The line is a CLAUSE LIST: clauses
/// compose, so the number of reachable sentences is the product of the branches, and the defect
/// that shipped once was not in any one clause — it was in the composition (a starved turn read
/// "0 picks matched everything you asked · 2 closest-but-outside shown separately · … · 1 pick shown
/// below your 6.0 bar" against an empty tray). So this asserts a PROPERTY: every clause the builder
/// can emit matches a known pattern, every number in it equals the count it claims to be, and no
/// clause speaks pipeline (A-11) — for every combination of picks, outside, tray and near misses.</para>
///
/// <para>Deliberately strict about unknown clauses: a new clause added without a pattern here fails
/// the theory rather than slipping through unchecked.</para>
/// </summary>
public class AskReelReasonSelfConsistencyTests
{
    private const decimal Floor = 6.0m;

    private static readonly string Bar = AskReelReason.FormatFloor(Floor);

    /// <summary>
    /// Every REACHABLE combination: unjudged cards reach the grid only beneath a judged pick, and the
    /// unchecked tray exists only on a turn with nothing in the grid (§4.8, §4.10).
    /// </summary>
    public static TheoryData<int, int, int, int, int, int> Branches()
    {
        var data = new TheoryData<int, int, int, int, int, int>();
        foreach (var picks in new[] { 0, 1, 2, 9, 16 })
        {
            foreach (var outside in new[] { 0, 1, 3, 11 })
            {
                foreach (var tray in new[] { 0, 1, 17 })
                {
                    foreach (var nearMiss in new[] { 0, 1, 4 })
                    {
                        foreach (var notChecked in new[] { 0, 1, 5 })
                        {
                            if (picks == 0 && (outside > 0 || notChecked > 0))
                            {
                                // Neither reaches the grid without a judged pick above it.
                                continue;
                            }

                            data.Add(picks, outside, tray, nearMiss, notChecked, 0);
                            if (picks == 0)
                            {
                                data.Add(picks, outside, tray, nearMiss, 0, notChecked + 3);
                            }
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Branches))]
    public void Every_clause_is_true_of_the_counts_that_produced_it(
        int picks, int outside, int tray, int nearMiss, int uncheckedInGrid, int uncheckedTray)
    {
        var line = AskReelReason.Build(new TurnCounts(picks, outside, tray, nearMiss, uncheckedInGrid, uncheckedTray), Floor);
        var clauses = line.Split(AskReelReason.Separator);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var clause in clauses)
        {
            Assert.DoesNotContain("pulled", clause, StringComparison.Ordinal);
            Assert.DoesNotContain("widened", clause, StringComparison.Ordinal);
            Assert.DoesNotContain("relaxed", clause, StringComparison.Ordinal);

            if (clause == AskReelReason.Empty)
            {
                Assert.Equal(0, picks + outside + uncheckedInGrid + uncheckedTray);
                seen.Add("empty");
            }
            else if (Match(clause, @"^(\d+) more not checked against what you asked$") is { } u)
            {
                Assert.Equal(uncheckedInGrid, Count(u));
                seen.Add("unchecked");
            }
            else if (Match(clause, @"^(\d+) not checked against what you asked — shown below$") is { } ut)
            {
                Assert.Equal(uncheckedTray, Count(ut));
                seen.Add("uncheckedTray");
            }
            else if (Match(clause, @"^(\d+) (pick|picks) for you$") is { } forYou)
            {
                Assert.Equal(0, outside);
                Assert.Equal(picks, Count(forYou));
                Assert.Equal(picks == 1 ? "pick" : "picks", forYou.Groups[2].Value);
                seen.Add("picks");
            }
            else if (Match(clause, @"^(\d+) (pick|picks) match what you asked$") is { } match)
            {
                Assert.True(outside > 0);
                Assert.Equal(picks, Count(match));
                seen.Add("picks");
            }
            else if (Match(clause, @"^(\d+) close but outside what you asked, shown separately$") is { } o)
            {
                Assert.Equal(outside, Count(o));
                seen.Add("outside");
            }
            else if (Match(clause, $@"^(\d+) held back by your {Regex.Escape(Bar)} bar — shown below$") is { } t)
            {
                Assert.Equal(tray, Count(t));
                seen.Add("tray");
            }
            else if (Match(clause, @"^(\d+) close but not quite — tucked away below$") is { } n)
            {
                Assert.Equal(nearMiss, Count(n));
                seen.Add("nearMiss");
            }
            else
            {
                Assert.Fail($"Unknown clause \"{clause}\" in \"{line}\".");
            }
        }

        // Every non-zero count is spoken, exactly once.
        Assert.Equal(picks + outside + uncheckedInGrid + uncheckedTray == 0, seen.Contains("empty"));
        Assert.Equal(picks + outside + uncheckedInGrid > 0, seen.Contains("picks"));
        Assert.Equal(uncheckedInGrid > 0, seen.Contains("unchecked"));
        Assert.Equal(uncheckedTray > 0, seen.Contains("uncheckedTray"));
        Assert.Equal(outside > 0, seen.Contains("outside"));
        Assert.Equal(tray > 0, seen.Contains("tray"));
        Assert.Equal(nearMiss > 0, seen.Contains("nearMiss"));
        Assert.Equal(seen.Count, clauses.Length);
    }

    private static Match? Match(string clause, string pattern)
    {
        var m = Regex.Match(clause, pattern, RegexOptions.CultureInvariant);
        return m.Success ? m : null;
    }

    private static int Count(Match m) => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
}
