using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// The rounding rule IS the design of the lens cache, so both directions are pinned here.
///
/// <para>Too sensitive and the cache never warms: this account produces ~20 ratings a week, and a
/// fingerprint that moves on each of them means every cached note is a miss for ever and the whole
/// cost model collapses. Too coarse and the copy describes someone the user has stopped being. The
/// tests below are the two halves of that, and neither failure is visible from the outside —
/// which is why they are asserted rather than assumed.</para>
/// </summary>
public class TasteProfileHashTests
{
    private static readonly ContrarianFact NoContrarian = new(0.4m, []);

    private static readonly TextureFact Texture = new(0.72m, 0.31m, 0.78m, 0.55m, 0.20m, 0.35m, 0.44m, 120);

    private static readonly Guid[] Loved = Enumerable.Range(0, 8)
        .Select(i => Guid.Parse($"00000000-0000-0000-0000-0000000000{i:00}"))
        .ToArray();

    private static List<GenreFact> Genres(params (string Genre, decimal Affinity)[] genres) =>
        genres.Select(g => new GenreFact(g.Genre, g.Affinity, 40)).ToList();

    private static TasteFingerprint Fingerprint(
        IReadOnlyList<GenreFact>? genres = null,
        IReadOnlyList<Guid>? loved = null,
        string dislikeVersion = "abc123abc123",
        IReadOnlyList<string>? tags = null,
        TextureFact? texture = null,
        ContrarianFact? contrarian = null,
        string? previous = null) =>
        TasteProfileHash.Compute(
            genres ?? Genres(("thriller", 8.6m), ("crime", 8.4m), ("science-fiction", 8.2m), ("drama", 7.9m)),
            loved ?? Loved,
            dislikeVersion,
            tags ?? ["cerebral sci-fi", "practical effects"],
            texture ?? Texture,
            contrarian ?? NoContrarian,
            previous);

    private static string Hash(
        IReadOnlyList<GenreFact>? genres = null,
        IReadOnlyList<Guid>? loved = null,
        string dislikeVersion = "abc123abc123",
        IReadOnlyList<string>? tags = null,
        TextureFact? texture = null,
        ContrarianFact? contrarian = null,
        string? previous = null) =>
        Fingerprint(genres, loved, dislikeVersion, tags, texture, contrarian, previous).Hash;

    [Fact]
    public void The_hash_is_stable_for_identical_inputs()
    {
        Assert.Equal(Hash(), Hash());
    }

    [Fact]
    public void One_more_rating_does_not_move_it()
    {
        // A single rating nudges a shrunken genre mean by hundredths and the texture average — an
        // average over ~120 loved titles — by less than a hundredth. Anchored to the previous
        // fingerprint, none of them travels a full quantisation step, so a normal week of watching
        // leaves every cached lens note valid. That is the whole point of the key.
        var anchor = Fingerprint();
        var after = Hash(
            genres: Genres(("thriller", 8.64m), ("crime", 8.41m), ("science-fiction", 8.18m), ("drama", 7.93m)),
            texture: new TextureFact(0.723m, 0.312m, 0.779m, 0.551m, 0.204m, 0.348m, 0.442m, 121),
            previous: anchor.Components);

        Assert.Equal(anchor.Hash, after);
    }

    [Fact]
    public void A_component_sitting_on_a_step_edge_cannot_chatter()
    {
        // The failure a purely positional quantiser has and hysteresis fixes. Optimism at exactly
        // 0.35 sits on a 0.1 boundary: it quantises to 0.4, and the next rating nudging it to 0.348
        // would quantise to 0.3. Across seven texture components and eight genre affinities at
        // ~20 ratings a week, SOMETHING lands on an edge most days — and every one of those flips
        // would empty the account's whole lens cache for a reason unrelated to their taste.
        var onTheEdge = new TextureFact(0.72m, 0.31m, 0.78m, 0.55m, 0.20m, 0.35m, 0.44m, 120);
        var anchor = Fingerprint(texture: onTheEdge);

        var jitterDown = Hash(
            texture: onTheEdge with { Optimism = 0.348m }, previous: anchor.Components);
        var jitterUp = Hash(
            texture: onTheEdge with { Optimism = 0.354m }, previous: anchor.Components);

        Assert.Equal(anchor.Hash, jitterDown);
        Assert.Equal(anchor.Hash, jitterUp);

        // And it still moves when the taste genuinely does — a full step from where it was.
        Assert.NotEqual(
            anchor.Hash,
            Hash(texture: onTheEdge with { Optimism = 0.51m }, previous: anchor.Components));
    }

    [Fact]
    public void Hysteresis_bands_do_not_drift_indefinitely()
    {
        // A band holds the ANCHOR, it does not follow the live value. Darkness starts at 0.72 and
        // its anchor is recorded as 0.7, so the band runs to 0.8; fifteen nudges of 0.004 take it
        // to 0.78, which is 0.08 from the anchor and still inside the step, and the hash holds
        // throughout. Because the anchor never moves, the band cannot ratchet along behind a slow
        // drift — the component either stays where it was recorded or crosses cleanly.
        var anchor = Fingerprint();
        var drifted = anchor;
        for (var i = 1; i <= 15; i++)
        {
            drifted = Fingerprint(
                texture: Texture with { Darkness = 0.72m + (0.004m * i) },
                previous: anchor.Components);
        }

        Assert.Equal(anchor.Hash, drifted.Hash);
        Assert.NotEqual(
            anchor.Hash,
            Hash(texture: Texture with { Darkness = 0.83m }, previous: anchor.Components));
    }

    [Fact]
    public void A_genre_climbing_into_the_top_eight_moves_it()
    {
        // A different set of top genres is a different person to describe, however close the
        // numbers behind it are.
        var after = Hash(genres: Genres(
            ("horror", 8.6m), ("crime", 8.4m), ("science-fiction", 8.2m), ("drama", 7.9m)));

        Assert.NotEqual(Hash(), after);
    }

    [Fact]
    public void A_genre_affinity_crossing_a_rounding_step_moves_it()
    {
        // 8.6 quantises to 8.5; 8.8 quantises to 9.0. The step is deliberately coarse, but it is a
        // step, and a taste that has genuinely shifted has to be able to cross one.
        var after = Hash(genres: Genres(
            ("thriller", 8.8m), ("crime", 8.4m), ("science-fiction", 8.2m), ("drama", 7.9m)));

        Assert.NotEqual(Hash(), after);
    }

    [Fact]
    public void A_new_dislike_moves_it()
    {
        // The reason this component is in the key at all: the lens's honest-reservation line is
        // grounded in the account's declined titles, so a note written before a dismissal must not
        // survive it. Without this, the product can tell you that you will enjoy the exact tone you
        // dismissed ten minutes ago.
        Assert.NotEqual(Hash(), Hash(dislikeVersion: "def456def456"));
    }

    [Fact]
    public void A_new_loved_anchor_moves_it()
    {
        var shifted = Loved.Skip(1).Append(Guid.Parse("00000000-0000-0000-0000-000000000099")).ToList();
        Assert.NotEqual(Hash(), Hash(loved: shifted));
    }

    [Fact]
    public void A_preference_tag_moves_it_and_its_order_does_not()
    {
        Assert.NotEqual(Hash(), Hash(tags: ["cerebral sci-fi", "practical effects", "slow burns"]));
        // Tags are a SET. Reordering the chips on the settings screen must not invalidate a
        // fortnight of cached copy.
        Assert.Equal(Hash(), Hash(tags: ["practical effects", "cerebral sci-fi"]));
    }

    [Fact]
    public void Genre_order_does_not_matter_but_membership_does()
    {
        var reordered = Genres(("drama", 7.9m), ("thriller", 8.6m), ("science-fiction", 8.2m), ("crime", 8.4m));
        Assert.Equal(Hash(), Hash(genres: reordered));
    }

    [Fact]
    public void A_contrarian_shift_past_the_step_moves_it()
    {
        var anchor = Fingerprint();
        Assert.Equal(anchor.Hash, Hash(contrarian: new ContrarianFact(0.6m, []), previous: anchor.Components));
        Assert.NotEqual(anchor.Hash, Hash(contrarian: new ContrarianFact(1.4m, []), previous: anchor.Components));
    }

    [Fact]
    public void A_fingerprint_reads_back_into_its_own_components()
    {
        var parsed = TasteProfileHash.Parse(Fingerprint().Components);

        Assert.Equal("abc123abc123", parsed["d"]);
        Assert.True(parsed.ContainsKey("g:thriller"));
        Assert.True(parsed.ContainsKey("t:dark"));
    }

    [Fact]
    public void An_unreadable_previous_fingerprint_degrades_to_plain_quantisation()
    {
        // A stored fingerprint from an older shape must not break the hash — it simply stops
        // supplying an anchor, and the next narrative writes a readable one.
        Assert.Equal(Hash(), Hash(previous: "garbage without any separators"));
    }

    [Fact]
    public void Quantise_is_invariant_culture()
    {
        // This machine runs en_ZA, where the default format writes 8.5 as "8,5". A locale-dependent
        // hash would silently invalidate every cached note the moment the app moved host.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-ZA");
            Assert.Equal("8.5", TasteProfileHash.Quantise(8.6m, 0.5m));
            Assert.Equal("0.7", TasteProfileHash.Quantise(0.72m, 0.1m));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_hash_is_twelve_hex_characters()
    {
        var hash = Hash();
        Assert.Equal(12, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }
}
