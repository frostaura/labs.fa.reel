using System.Text.Json;
using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The per-feature μ/σ the online residual head learns under. Two things can break here silently and
/// neither would fail anything else: a json key whose casing drifts (the stats would simply never be
/// read, and every head would quietly go back to raw features), and a constant feature dividing by a
/// zero σ. Both are asserted, along with the round trip through the exact document shape
/// <c>TrainJobHandler</c> writes.
/// </summary>
public class FeatureStandardizerTests
{
    [Fact]
    public void Absent_stats_fall_back_to_the_raw_pass_through()
    {
        // The two-key document every artifact trained before this shipped carries — and every test
        // fixture in this repo still writes.
        var json = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() });

        var stats = FeatureStandardizer.FromSchemaJson(json);

        Assert.True(stats.IsIdentity);
        var raw = new float[] { 105f, 7.4f, 0f };
        Assert.Same(raw, stats.Standardize(raw));
    }

    [Fact]
    public void Malformed_or_missing_json_falls_back_rather_than_throwing()
    {
        Assert.True(FeatureStandardizer.FromSchemaJson(null).IsIdentity);
        Assert.True(FeatureStandardizer.FromSchemaJson("").IsIdentity);
        Assert.True(FeatureStandardizer.FromSchemaJson("{not json").IsIdentity);
        Assert.True(FeatureStandardizer.FromSchemaJson("[]").IsIdentity);
        Assert.True(FeatureStandardizer.FromSchemaJson("""{"stats":{"mean":[1,2]}}""").IsIdentity);
        Assert.True(FeatureStandardizer.FromSchemaJson("""{"stats":{"mean":[1,2],"std":[1]}}""").IsIdentity);
    }

    [Fact]
    public void Stats_round_trip_through_the_document_the_trainer_writes()
    {
        // Exactly the shape TrainJobHandler serializes — this is the test that catches a key-casing
        // drift, because the reader and the writer are only connected by these literal strings.
        var rows = new[]
        {
            new[] { 100f, 6f, 1f },
            new[] { 110f, 8f, 1f },
            new[] { 90f, 7f, 1f },
        };
        var fitted = FeatureStandardizer.FromRows(rows);
        var json = JsonSerializer.Serialize(new
        {
            version = FeatureSchema.Version,
            names = FeatureSchema.AllNames(),
            stats = fitted.ToPayload(),
        });

        var reloaded = FeatureStandardizer.FromSchemaJson(json);

        Assert.False(reloaded.IsIdentity);
        Assert.Equal(3, reloaded.Length);
        var a = fitted.Standardize(rows[1]);
        var b = reloaded.Standardize(rows[1]);
        Assert.Equal(a[0], b[0], 5);
        Assert.Equal(a[1], b[1], 5);
    }

    [Fact]
    public void Standardising_centres_the_columns_and_puts_them_on_one_scale()
    {
        var rows = new[]
        {
            new[] { 100f, 6f },
            new[] { 110f, 8f },
            new[] { 90f, 7f },
            new[] { 120f, 5f },
        };

        var stats = FeatureStandardizer.FromRows(rows);
        var standardized = rows.Select(stats.Standardize).ToList();

        // Column means land on zero and the two columns end up comparable in magnitude — which is
        // the whole point: runtimeMinutes must stop setting the learning rate for everything else.
        Assert.Equal(0d, standardized.Average(r => r[0]), 4);
        Assert.Equal(0d, standardized.Average(r => r[1]), 4);
        Assert.True(Math.Abs(standardized.Max(r => Math.Abs(r[0])) - standardized.Max(r => Math.Abs(r[1]))) < 1d);
    }

    [Fact]
    public void A_constant_feature_standardises_to_zero_rather_than_infinity()
    {
        // σ = 0 for any feature nobody in this account varies — a genre they have never touched, an
        // attribute that is not extracted yet. An unguarded divide would turn the whole vector into
        // NaN and the head into garbage on the very first event.
        var rows = new[]
        {
            new[] { 5f, 3f },
            new[] { 5f, 9f },
            new[] { 5f, 6f },
        };

        var standardized = FeatureStandardizer.FromRows(rows).Standardize(rows[0]);

        Assert.Equal(0f, standardized[0]);
        Assert.All(standardized, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void A_short_stats_block_leaves_the_extra_slots_raw()
    {
        // A schema that grew after an artifact was trained: the covered slots standardise, the rest
        // pass through. Degraded, but finite and predictable — never NaN and never an exception.
        var stats = FeatureStandardizer.FromSchemaJson("""{"stats":{"mean":[10,20],"std":[2,4]}}""");

        var standardized = stats.Standardize([12f, 24f, 7f]);

        Assert.Equal(1f, standardized[0]);
        Assert.Equal(1f, standardized[1]);
        Assert.Equal(7f, standardized[2]);
    }
}
