using FrostAura.Reel.Application.Ranking;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// WS-5 task 4 — which titles anchor the "Because you loved" rows, and what a card must share
/// with its anchor. Anchors come from the 9–10 ratings first (the founder's rows were anchored on
/// two 8s while 18 tens and 208 nines sat unused), without breaking the stable-tonight contract;
/// shared evidence is people in the SAME capacity, story keywords and themes — never a production
/// keyword, never a person in a different role.
/// </summary>
public class AnchorSelectionTests
{
    private static readonly Guid[] Ids = Enumerable.Range(0, 12)
        .Select(i => new Guid($"0000000{i:x}-0000-0000-0000-000000000000"))
        .ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(17)]
    public void Anchors_come_from_the_top_tier_whenever_it_has_enough_fresh_titles(int offset)
    {
        // Pool order is recency-weighted, so 8s sit at the head; the top tier is scattered behind.
        var topTier = new HashSet<Guid> { Ids[3], Ids[6], Ids[8], Ids[9], Ids[11] };

        var chosen = AnchorRotation.Select(
            driftPick: Ids[0], // an 8: it does not get the first slot over a 9
            anchorPool: Ids,
            allTimeOrder: Ids,
            previousAnchors: new HashSet<Guid>(),
            rotationOffset: offset,
            count: 3,
            topTier: topTier);

        Assert.Equal(3, chosen.Count);
        Assert.All(chosen, id => Assert.Contains(id, topTier));
    }

    [Fact]
    public void A_top_tier_drift_pick_still_takes_the_first_slot()
    {
        var topTier = new HashSet<Guid> { Ids[4], Ids[7], Ids[10] };
        var chosen = AnchorRotation.Select(
            Ids[7], Ids, Ids, new HashSet<Guid>(), rotationOffset: 3, count: 3, topTier: topTier);

        Assert.Equal(Ids[7], chosen[0]);
    }

    [Fact]
    public void When_yesterday_used_the_top_tier_a_fresh_eight_beats_a_repeated_ten()
    {
        // "Different across days" outranks "rated 9+": with every top-tier title held back, the
        // rows rotate onto fresh 8s rather than re-deal yesterday's tens.
        var topTier = new HashSet<Guid> { Ids[1], Ids[2] };
        var chosen = AnchorRotation.Select(
            null, Ids, Ids, previousAnchors: topTier, rotationOffset: 2, count: 3, topTier: topTier);

        Assert.Equal(3, chosen.Count);
        Assert.DoesNotContain(Ids[1], chosen);
        Assert.DoesNotContain(Ids[2], chosen);
    }

    [Fact]
    public void A_thin_top_tier_is_topped_up_from_the_rest()
    {
        var topTier = new HashSet<Guid> { Ids[5] };
        var chosen = AnchorRotation.Select(
            null, Ids, Ids, new HashSet<Guid>(), rotationOffset: 0, count: 3, topTier: topTier);

        Assert.Equal(3, chosen.Count);
        Assert.Equal(Ids[5], chosen[0]);
        Assert.Equal(3, chosen.Distinct().Count());
    }

    [Fact]
    public void The_selection_is_stable_within_a_day()
    {
        var topTier = new HashSet<Guid> { Ids[3], Ids[6], Ids[8], Ids[9], Ids[11] };
        var offset = AnchorRotation.OffsetFor(new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc));
        var later = AnchorRotation.OffsetFor(new DateTime(2026, 10, 24, 23, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            AnchorRotation.Select(Ids[0], Ids, Ids, new HashSet<Guid>(), offset, 3, topTier),
            AnchorRotation.Select(Ids[0], Ids, Ids, new HashSet<Guid>(), later, 3, topTier));
    }

    [Fact]
    public void Evidence_is_people_in_the_same_capacity_then_keywords_then_themes()
    {
        var mann = Guid.NewGuid();
        var pacino = Guid.NewGuid();
        var anchor = new AnchorRowGate.Facts(
            [new(mann, "Michael Mann", "director"), new(pacino, "Al Pacino", "cast")],
            ["heist", "los angeles", "duringcreditsstinger"],
            ["obsession"]);
        var candidate = new AnchorRowGate.Facts(
            [new(pacino, "Al Pacino", "cast"), new(mann, "Michael Mann", "director")],
            ["Heist", "duringcreditsstinger", "obsession"],
            ["obsession"]);

        var evidence = AnchorRowGate.Between(anchor, candidate);

        Assert.Equal(
            [
                new SharedEvidence("person", "Michael Mann", "director"),
                new SharedEvidence("person", "Al Pacino", "cast"),
                new SharedEvidence("keyword", "Heist"),
            ],
            evidence);
    }

    [Fact]
    public void A_person_in_a_different_capacity_is_not_shared_evidence()
    {
        // Clint Eastwood directed the anchor and acts in the candidate: a director's credit is not
        // a reason to recommend a film he only appears in.
        var eastwood = Guid.NewGuid();
        var anchor = new AnchorRowGate.Facts([new(eastwood, "Clint Eastwood", "director")], [], []);
        var candidate = new AnchorRowGate.Facts([new(eastwood, "Clint Eastwood", "cast")], [], []);

        Assert.Empty(AnchorRowGate.Between(anchor, candidate));
    }

    [Fact]
    public void A_production_keyword_alone_is_not_evidence()
    {
        var anchor = new AnchorRowGate.Facts([], ["woman director", "aftercreditsstinger", "sequel"], []);
        var candidate = new AnchorRowGate.Facts([], ["Woman Director", "aftercreditsstinger", "sequel"], []);

        Assert.Empty(AnchorRowGate.Between(anchor, candidate));
    }

    [Fact]
    public void Evidence_stops_at_three_chips()
    {
        var anchor = new AnchorRowGate.Facts([], ["a", "b", "c", "d", "e"], ["f"]);
        var candidate = new AnchorRowGate.Facts([], ["a", "b", "c", "d", "e"], ["f"]);

        Assert.Equal(AnchorRowGate.MaxChips, AnchorRowGate.Between(anchor, candidate).Count);
    }
}
