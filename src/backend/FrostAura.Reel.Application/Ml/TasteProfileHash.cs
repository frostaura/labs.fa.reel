using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// The account's taste fingerprint — the canonical component string — and the twelve-hex-character
/// hash of it that keys every piece of generated copy in the product.
/// </summary>
public sealed record TasteFingerprint(string Components, string Hash);

/// <summary>
/// The single invalidation key behind the taste narrative and every cached lens note.
///
/// <para><b>The problem it solves.</b> This account produces roughly twenty ratings a week. An
/// exact hash of its taste would change on nearly every one of them, and the lens cache — whose
/// steady-state hit rate the entire cost model depends on — would be cold for ever. So the inputs
/// are quantised coarsely: genre affinities to 0.5 of a rating point, the attribute texture to 0.1,
/// contrarian offsets to 0.5.</para>
///
/// <para><b>Why quantising alone is not enough, which is the part that is easy to get wrong.</b>
/// Quantisation moves the problem to the boundaries rather than solving it. The texture is an
/// average over ~120 loved titles, so one new rating shifts a component by roughly 0.008 — small
/// against a 0.1 step, but a component sitting near a step edge crosses it on almost any rating,
/// and there are seven of them plus eight genre affinities. Compounded across twenty ratings a
/// week, a purely positional quantiser flips SOMETHING most days, and the cache is cold again for
/// a reason that has nothing to do with the person's taste changing.</para>
///
/// <para><b>The fix is hysteresis.</b> Each numeric component is compared against the value stored
/// in the previous fingerprint, and keeps that value until it has moved a FULL step away from it.
/// A component recorded at 0.3 stays 0.3 until the underlying number reaches 0.4 — it cannot
/// oscillate at 0.35, because there is no boundary there any more, only a band. The fingerprint
/// re-anchors whenever a narrative is written, which is exactly when the copy is being regenerated
/// anyway. The function stays pure: same inputs plus same previous fingerprint, same answer.</para>
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
    /// <summary>How many genres and loved anchors enter the fingerprint. Beyond this the tail is noise.</summary>
    public const int TopN = 8;

    private const decimal AffinityStep = 0.5m;

    private const decimal TextureStep = 0.1m;

    private const decimal ContrarianStep = 0.5m;

    /// <summary>
    /// The fingerprint and its hash. <paramref name="previousComponents"/> is the last stored
    /// fingerprint for this account (null when it has never had one) and supplies the hysteresis
    /// band described above; passing null gives plain positional quantisation, which is correct for
    /// a first computation and wrong for every subsequent one.
    /// </summary>
    public static TasteFingerprint Compute(
        IReadOnlyList<GenreFact> topGenres,
        IReadOnlyList<Guid> topLovedTitleIds,
        string dislikeVersion,
        IReadOnlyList<string> preferenceTags,
        TextureFact? texture,
        ContrarianFact contrarian,
        string? previousComponents = null)
    {
        var previous = Parse(previousComponents);
        var components = new List<(string Key, string Value)>();

        foreach (var genre in topGenres.Take(TopN).OrderBy(g => g.Genre, StringComparer.Ordinal))
        {
            components.Add(($"g:{genre.Genre}", Band($"g:{genre.Genre}", genre.Affinity, AffinityStep, previous)));
        }

        components.Add(("l", string.Join(",", topLovedTitleIds.Take(TopN).Select(id => id.ToString("N")))));
        components.Add(("d", dislikeVersion));
        components.Add(("p", string.Join(
            ",",
            preferenceTags
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal))));

        if (texture is not null)
        {
            components.Add(("t:dark", Band("t:dark", texture.Darkness, TextureStep, previous)));
            components.Add(("t:pace", Band("t:pace", texture.Pacing, TextureStep, previous)));
            components.Add(("t:complex", Band("t:complex", texture.Complexity, TextureStep, previous)));
            components.Add(("t:intense", Band("t:intense", texture.EmotionalIntensity, TextureStep, previous)));
            components.Add(("t:humor", Band("t:humor", texture.Humor, TextureStep, previous)));
            components.Add(("t:hope", Band("t:hope", texture.Optimism, TextureStep, previous)));
            components.Add(("t:ensemble", Band("t:ensemble", texture.EnsembleVsSolo, TextureStep, previous)));
        }

        components.Add(("c", Band("c", contrarian.Overall, ContrarianStep, previous)));
        foreach (var offset in contrarian.ByGenre.OrderBy(o => o.Genre, StringComparer.Ordinal))
        {
            components.Add(($"c:{offset.Genre}", Band($"c:{offset.Genre}", offset.Offset, ContrarianStep, previous)));
        }

        var canonical = string.Join(";", components.Select(c => $"{c.Key}={c.Value}"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
        return new TasteFingerprint(canonical, hash);
    }

    /// <summary>Convenience overload — the fingerprint of an assembled fact set.</summary>
    public static TasteFingerprint Compute(
        TasteFacts facts, IReadOnlyList<Guid> topLovedTitleIds, string dislikeVersion, string? previousComponents = null) =>
        Compute(
            facts.TopGenres, topLovedTitleIds, dislikeVersion, facts.PreferenceTags,
            facts.Texture, facts.Contrarian, previousComponents);

    /// <summary>
    /// One numeric component, with hysteresis: the previously recorded value survives until the
    /// live number has moved a full step away from it. Without the previous value this is plain
    /// quantisation.
    /// </summary>
    internal static string Band(string key, decimal value, decimal step, IReadOnlyDictionary<string, string> previous)
    {
        if (previous.TryGetValue(key, out var recorded)
            && decimal.TryParse(recorded, NumberStyles.Float, CultureInfo.InvariantCulture, out var anchor)
            && Math.Abs(value - anchor) < step)
        {
            return recorded;
        }

        return Quantise(value, step);
    }

    /// <summary>
    /// Rounds to the nearest multiple of <paramref name="step"/>, away from zero on a tie, and
    /// renders invariant. The invariant culture is not cosmetic here: this machine runs en_ZA,
    /// where the default format writes 8.5 as "8,5" — the fingerprint would then depend on the
    /// host's locale and every cached note would miss the moment the app moved to a different one.
    /// </summary>
    internal static string Quantise(decimal value, decimal step)
    {
        var steps = Math.Round(value / step, MidpointRounding.AwayFromZero);
        return (steps * step).ToString("0.0#", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a stored fingerprint back into its components. An unreadable one is simply absent.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string? components)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(components))
        {
            return parsed;
        }

        foreach (var part in components.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=');
            if (split > 0)
            {
                parsed[part[..split]] = part[(split + 1)..];
            }
        }

        return parsed;
    }
}
