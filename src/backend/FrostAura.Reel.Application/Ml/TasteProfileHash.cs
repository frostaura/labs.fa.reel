using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The single invalidation key behind every piece of generated copy in the product — the taste
/// narrative and every cached lens note. Twelve hex characters over a deliberately COARSE view of
/// the account's taste.
///
/// <para><b>The rounding is the design, not a detail.</b> This account produces roughly twenty
/// ratings a week. An exact hash would change on nearly every one of them, and the entire lens
/// cache — the thing whose steady-state hit rate the cost model depends on — would be cold for
/// ever. So genre affinities are rounded to the nearest 0.5 of a rating point, the attribute
/// texture to 0.1, and contrarian offsets to 0.5. One new rating moves none of those; a genuine
/// shift in what the person likes moves at least one. The two directions are separately tested,
/// because getting either wrong is silent: too sensitive and the cache never warms, too coarse and
/// the copy describes someone the user no longer is.</para>
///
/// <para><b>What is deliberately NOT in it.</b> The active model artifact id. The founder retrains
/// most weeks and every retrain triggers a Fresh build, so an artifact in the key means the whole
/// cache goes cold at 03:00 on retrain night and every note is a miss the next morning. That is
/// only affordable because the generated prose never states the predicted rating — the number is
/// rendered from the score at serve time, beside the copy, not inside it.</para>
///
/// <para><b>What IS in it that an obvious version of this would miss:</b> the dislike-centroid
/// version. The lens's honest-reservation line is grounded in the account's declined titles, so a
/// note written before a dismissal must not survive it — otherwise the product tells you that you
/// will enjoy the exact tone you dismissed ten minutes ago.</para>
/// </summary>
public static class TasteProfileHash
{
    /// <summary>How many genres and loved anchors enter the hash. Beyond this the tail is noise.</summary>
    public const int TopN = 8;

    private const decimal AffinityStep = 0.5m;

    private const decimal TextureStep = 0.1m;

    private const decimal ContrarianStep = 0.5m;

    /// <summary>
    /// The fingerprint. Inputs are canonically ordered before hashing (genres and tags by name,
    /// loved ids by their own ordering, which is already deterministic) so two reads of an
    /// unchanged account can never disagree.
    /// </summary>
    public static string Compute(
        IReadOnlyList<GenreFact> topGenres,
        IReadOnlyList<Guid> topLovedTitleIds,
        string dislikeVersion,
        IReadOnlyList<string> preferenceTags,
        TextureFact? texture,
        ContrarianFact contrarian)
    {
        var sb = new StringBuilder();

        sb.Append("g:");
        foreach (var genre in topGenres.Take(TopN).OrderBy(g => g.Genre, StringComparer.Ordinal))
        {
            sb.Append(genre.Genre).Append('=').Append(Quantise(genre.Affinity, AffinityStep)).Append(';');
        }

        sb.Append("|l:");
        foreach (var id in topLovedTitleIds.Take(TopN))
        {
            sb.Append(id.ToString("N")).Append(';');
        }

        sb.Append("|d:").Append(dislikeVersion);

        sb.Append("|p:");
        foreach (var tag in preferenceTags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            sb.Append(tag).Append(';');
        }

        sb.Append("|t:");
        if (texture is not null)
        {
            sb.Append(Quantise(texture.Darkness, TextureStep)).Append(',')
                .Append(Quantise(texture.Pacing, TextureStep)).Append(',')
                .Append(Quantise(texture.Complexity, TextureStep)).Append(',')
                .Append(Quantise(texture.EmotionalIntensity, TextureStep)).Append(',')
                .Append(Quantise(texture.Humor, TextureStep)).Append(',')
                .Append(Quantise(texture.Optimism, TextureStep)).Append(',')
                .Append(Quantise(texture.EnsembleVsSolo, TextureStep));
        }

        sb.Append("|c:").Append(Quantise(contrarian.Overall, ContrarianStep)).Append(';');
        foreach (var offset in contrarian.ByGenre.OrderBy(o => o.Genre, StringComparer.Ordinal))
        {
            sb.Append(offset.Genre).Append('=').Append(Quantise(offset.Offset, ContrarianStep)).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>Convenience overload — the fingerprint of an assembled fact set.</summary>
    public static string Compute(TasteFacts facts, IReadOnlyList<Guid> topLovedTitleIds, string dislikeVersion) =>
        Compute(facts.TopGenres, topLovedTitleIds, dislikeVersion, facts.PreferenceTags, facts.Texture, facts.Contrarian);

    /// <summary>
    /// Rounds to the nearest multiple of <paramref name="step"/>, away from zero on a tie, and
    /// renders invariant. The invariant culture is not cosmetic here: this machine runs en_ZA,
    /// where the default format writes 8.5 as "8,5" — the hash would then depend on the host's
    /// locale and every cached note would miss the moment the app moved to a different one.
    /// </summary>
    internal static string Quantise(decimal value, decimal step)
    {
        var steps = Math.Round(value / step, MidpointRounding.AwayFromZero);
        return (steps * step).ToString("0.0#", CultureInfo.InvariantCulture);
    }
}
