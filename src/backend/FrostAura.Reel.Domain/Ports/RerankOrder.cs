using System.Security.Cryptography;
using System.Text;

namespace FrostAura.Reel.Domain.Ports;

/// <summary>
/// Position-bias mitigation for the LLM re-ranker.
///
/// <para><b>The problem.</b> LLM rankers score items that appear later in a prompt lower than the
/// same items placed earlier — a documented, reproducible bias, and one this product was fully
/// exposed to: candidates were listed in model-rank order, so the deterministic ranker's own order
/// was silently reinforced by the judge that was supposed to second-guess it. A title the model
/// ranked ninth was penalised twice for the same reason.</para>
///
/// <para><b>The mitigation, in two halves.</b> Here: shuffle the candidates before they are
/// rendered, deterministically, so the same batch always produces the same prompt (a cache key and
/// a golden-prompt test both depend on that) but the order bears no relation to the model's
/// ranking. At the caller: average a title's fit across the batches it appears in, so what remains
/// of the bias reads as noise rather than as a systematic penalty on whatever was placed last —
/// that half is <see cref="PositionDebiasedFit"/>.</para>
/// </summary>
public static class RerankOrder
{
    /// <summary>
    /// The batch, in a deterministic pseudo-random order. Seeded on the batch's own contents plus
    /// the query and anchor, so re-rendering the identical batch is byte-identical and two
    /// different batches over overlapping titles place those titles differently.
    /// </summary>
    public static IReadOnlyList<RerankInput> Shuffle(
        IReadOnlyList<RerankInput> items, string query, string? anchorName)
    {
        if (items.Count < 2)
        {
            return items;
        }

        // Seeded on the SET of ids, sorted — not on the order they arrived in. Seeding on the
        // caller's order would make the shuffle a function of the model's ranking, which is the one
        // thing it exists to break the dependence on.
        var seed = new StringBuilder(query).Append('|').Append(anchorName ?? string.Empty);
        foreach (var id in items.Select(i => i.TitleId).OrderBy(id => id))
        {
            seed.Append('|').Append(id.ToString("N"));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));

        // Order by a per-item key derived from the batch digest and the item's own id: stable,
        // uniform enough for this purpose, and free of any dependence on the input order.
        return items
            .Select(item => (Item: item, Key: KeyFor(digest, item.TitleId)))
            .OrderBy(x => x.Key)
            .ThenBy(x => x.Item.TitleId)
            .Select(x => x.Item)
            .ToList();
    }

    private static ulong KeyFor(byte[] digest, Guid titleId)
    {
        Span<byte> buffer = stackalloc byte[digest.Length + 16];
        digest.CopyTo(buffer);
        titleId.TryWriteBytes(buffer[digest.Length..]);
        var hash = SHA256.HashData(buffer);
        return BitConverter.ToUInt64(hash, 0);
    }
}

/// <summary>
/// The caller's half of the mitigation: a title's fit averaged over every batch it was judged in.
///
/// <para>A title reaches the re-ranker more than once whenever it sits on two rails, or when a
/// later page re-judges it — and each of those is a different prompt position. Keeping the FIRST
/// verdict would freeze in whatever position bias that one prompt happened to carry; keeping the
/// LAST would do the same and additionally make the answer depend on request ordering. The mean is
/// the only reading of several observations of the same quantity that does not privilege one of
/// them for a reason unrelated to the title.</para>
/// </summary>
public sealed class PositionDebiasedFit
{
    private readonly Dictionary<Guid, (double Sum, int Count, string Why)> _observations = [];

    /// <summary>Records one verdict. The why kept is the first accepted one — averaging prose is not a thing.</summary>
    public void Observe(RerankResult result)
    {
        if (_observations.TryGetValue(result.TitleId, out var existing))
        {
            _observations[result.TitleId] = (
                existing.Sum + result.Fit,
                existing.Count + 1,
                string.IsNullOrWhiteSpace(existing.Why) ? result.Why : existing.Why);
            return;
        }

        _observations[result.TitleId] = (result.Fit, 1, result.Why);
    }

    /// <summary>How many verdicts a title has attracted. One means no debiasing was possible.</summary>
    public int CountFor(Guid titleId) => _observations.TryGetValue(titleId, out var o) ? o.Count : 0;

    /// <summary>The averaged verdict, or null when the title was never judged.</summary>
    public RerankResult? For(Guid titleId) =>
        _observations.TryGetValue(titleId, out var o)
            ? new RerankResult(titleId, o.Sum / o.Count, o.Why)
            : null;
}
