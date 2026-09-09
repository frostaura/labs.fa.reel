using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Per-feature μ/σ for the ONLINE RESIDUAL HEAD, computed over the training matrix at train time and
/// persisted inside <c>ModelArtifact.FeatureSchemaJson</c> as a <c>stats</c> object alongside the
/// existing <c>version</c> and <c>names</c>.
///
/// Why the head needs this and the FastTree base does not: trees are scale-invariant, a linear model
/// is not. The raw vector's ‖x‖² is dominated by <c>runtimeMinutes</c> (~105, so ~11,000 of a total
/// around 11,300) — the NLMS step therefore divides almost the whole gradient by runtime's own
/// magnitude, and every other feature learns at a rate set by a number that has nothing to do with
/// it. Standardising first puts every slot on the same footing, which is what makes "one rule for
/// all slots" (weights AND bias) meaningful rather than nominal.
///
/// Absent stats fall back to <see cref="Identity"/> — artifacts trained before this shipped, and
/// every test fixture that writes the two-key schema json, keep working with raw features exactly as
/// they did. σ is guarded: a constant feature (σ ≈ 0, e.g. a genre nobody in the account has rated)
/// standardises to 0 rather than to infinity.
/// </summary>
public sealed class FeatureStandardizer
{
    /// <summary>Below this a feature is constant across the training matrix and carries no information.</summary>
    private const float MinScale = 1e-6f;

    private readonly float[]? _mean;
    private readonly float[]? _scale;

    private FeatureStandardizer(float[]? mean, float[]? scale)
    {
        _mean = mean;
        _scale = scale;
    }

    /// <summary>The no-op: features pass through raw. What an artifact with no persisted stats gets.</summary>
    public static FeatureStandardizer Identity { get; } = new(null, null);

    /// <summary>True when this is the pass-through (no stats were available).</summary>
    public bool IsIdentity => _mean is null;

    /// <summary>Number of features the stats cover; 0 for <see cref="Identity"/>.</summary>
    public int Length => _mean?.Length ?? 0;

    /// <summary>
    /// Returns z = (x − μ)/σ, allocating a new array. Slots beyond the persisted stats pass through
    /// raw, so a stats block that is short (a schema that grew) degrades rather than throwing.
    /// </summary>
    public float[] Standardize(float[] raw)
    {
        if (_mean is null || _scale is null)
        {
            return raw;
        }

        var standardized = new float[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            standardized[i] = i < _mean.Length
                ? (raw[i] - _mean[i]) / _scale[i]
                : raw[i];
        }

        return standardized;
    }

    /// <summary>Column means and (guarded) standard deviations over a training matrix.</summary>
    public static FeatureStandardizer FromRows(IReadOnlyList<float[]> rows)
    {
        if (rows.Count == 0)
        {
            return Identity;
        }

        var width = rows[0].Length;
        var mean = new float[width];
        var scale = new float[width];

        for (var f = 0; f < width; f++)
        {
            double sum = 0;
            foreach (var row in rows)
            {
                sum += f < row.Length ? row[f] : 0d;
            }

            var mu = sum / rows.Count;
            double squared = 0;
            foreach (var row in rows)
            {
                var delta = (f < row.Length ? row[f] : 0d) - mu;
                squared += delta * delta;
            }

            var sigma = Math.Sqrt(squared / rows.Count);
            mean[f] = (float)mu;
            scale[f] = sigma < MinScale ? 1f : (float)sigma;
        }

        return new FeatureStandardizer(mean, scale);
    }

    /// <summary>
    /// Reads the <c>stats</c> block out of a persisted feature-schema document. Anything malformed,
    /// missing or mismatched in length degrades to <see cref="Identity"/> — a head that scores on raw
    /// features is merely the old behaviour, whereas one that scores on half-standardised features
    /// would be nonsense nobody could see.
    /// </summary>
    public static FeatureStandardizer FromSchemaJson(string? featureSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(featureSchemaJson))
        {
            return Identity;
        }

        try
        {
            using var document = JsonDocument.Parse(featureSchemaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("stats", out var stats)
                || stats.ValueKind != JsonValueKind.Object
                || !stats.TryGetProperty("mean", out var meanElement)
                || !stats.TryGetProperty("std", out var stdElement)
                || meanElement.ValueKind != JsonValueKind.Array
                || stdElement.ValueKind != JsonValueKind.Array
                || meanElement.GetArrayLength() != stdElement.GetArrayLength()
                || meanElement.GetArrayLength() == 0)
            {
                return Identity;
            }

            var mean = new float[meanElement.GetArrayLength()];
            var scale = new float[mean.Length];
            var index = 0;
            foreach (var value in meanElement.EnumerateArray())
            {
                mean[index++] = value.GetSingle();
            }

            index = 0;
            foreach (var value in stdElement.EnumerateArray())
            {
                var sigma = value.GetSingle();
                scale[index++] = float.IsFinite(sigma) && sigma >= MinScale ? sigma : 1f;
            }

            return new FeatureStandardizer(mean, scale);
        }
        catch (JsonException)
        {
            return Identity;
        }
    }

    /// <summary>The serializable shape written into <c>FeatureSchemaJson.stats</c>.</summary>
    public StatsPayload? ToPayload() =>
        _mean is null || _scale is null ? null : new StatsPayload(_mean, _scale);

    /// <summary>
    /// Persisted form of the stats block. The json keys are pinned with attributes rather than left
    /// to the serializer's default PascalCase, because <see cref="FromSchemaJson"/> reads them back
    /// by literal name and a silent casing change would degrade every head to raw features without
    /// anything failing.
    /// </summary>
    public sealed record StatsPayload(
        [property: JsonPropertyName("mean")] float[] Mean,
        [property: JsonPropertyName("std")] float[] Std);
}
