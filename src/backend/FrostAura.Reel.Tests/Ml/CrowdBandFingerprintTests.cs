using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// WS-1 CR-3: the crowd is one band everywhere. The why-line's contrarian clause reads its threshold
/// from <see cref="ContrarianFact.Band"/>, and the taste fingerprint re-keys the copy when the reader
/// crosses that band even where the coarse 0.5 contrarian component holds (IREV3S-07's -0.35 -> -0.2).
/// </summary>
public class CrowdBandFingerprintTests
{
    private static readonly IReadOnlyList<GenreFact> Genres = [new("thriller", 8.6m, 40), new("drama", 7.9m, 40)];

    private static TasteFingerprint Fingerprint(decimal overall, string? previous = null) =>
        TasteProfileHash.Compute(Genres, [], "abc123abc123", [], texture: null, new ContrarianFact(overall, []), previous);

    [Fact]
    public void Crossing_the_crowd_band_re_keys_the_copy_even_inside_the_contrarian_hysteresis()
    {
        var harsher = Fingerprint(-0.35m);
        var withTheCrowd = Fingerprint(-0.2m, harsher.Components);

        Assert.NotEqual(harsher.Hash, withTheCrowd.Hash);
        Assert.Contains("cs=HarsherThanTheCrowd", harsher.Components, StringComparison.Ordinal);
        Assert.Contains("cs=WithTheCrowd", withTheCrowd.Components, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_inside_one_band_keeps_the_key()
    {
        var anchor = Fingerprint(-0.1m);
        Assert.Equal(anchor.Hash, Fingerprint(0.2m, anchor.Components).Hash);
    }

    [Fact]
    public void The_why_line_threshold_is_the_band_edge()
    {
        Assert.Equal(-ContrarianFact.Band, ExplanationTemplater.HarsherThanCrowdBelow);
    }
}
