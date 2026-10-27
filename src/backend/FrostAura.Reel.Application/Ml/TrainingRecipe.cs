using System.Text.Json;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// How an artifact's training rows were described, beyond what <see cref="FeatureSchema"/> pins. The
/// schema fixes WHICH numbers a vector holds and in what order; the recipe fixes how the user-side
/// pools behind them were built for a training row. Stamped into <c>FeatureSchemaJson</c> by
/// <c>TrainJobHandler</c> (no column, no migration) and read back wherever serving has to describe a
/// user the way the Active artifact learned to read one.
///
/// <para><b>Recipes.</b> An artifact with no stamp was trained with leave-one-out pools and without
/// show rollups — every artifact before HREV2-RET-02. <see cref="Current"/> trains on out-of-fold
/// pools (<see cref="FeatureVectorBuilder.FoldPools"/>). A recipe carrying
/// <see cref="ShowRollupsMarker"/> also had the show rollups (<see cref="ShowRatingRollup"/>) in its
/// pools.</para>
///
/// <para><b>Why serving follows the artifact.</b> The rollups shifted the founder's 2020s decade pool
/// by 0.005 and a leave-one-out-trained artifact read that as three rating points off every 2020s
/// title. The taste state an artifact is served must be built the way its training rows were
/// (<see cref="FeatureVectorBuilder.IncludeShowRollups"/> null = follow it), whatever the current
/// recipe says.</para>
/// </summary>
public static class TrainingRecipe
{
    /// <summary>The <c>FeatureSchemaJson</c> property the recipe is stamped under.</summary>
    public const string Property = "pools";

    /// <summary>
    /// The recipe this build trains with. Changing what a training row reads changes this string, and
    /// with it every account's training-input hash (<see cref="TrainingInputHash"/>). It must carry
    /// <see cref="ShowRollupsMarker"/> exactly when <see cref="ShowRollups"/> is true.
    /// </summary>
    public const string Current = "out-of-fold-5";

    /// <summary>
    /// Whether the current recipe puts show rollups in the training pools. <b>Off</b>, on measurement:
    /// with out-of-fold pools the rollups cost NDCG@10 0.092, mean-origin NDCG 0.042 and Spearman 0.058
    /// on the founder's eval data (<c>eval/model/show-rollup-ablation.json</c>), the same direction the
    /// leave-one-out ablation had shown. Turn it on only with an ablation that does not regress.
    /// </summary>
    public const bool ShowRollups = false;

    /// <summary>The recipe suffix that says the pools held show rollups.</summary>
    public const string ShowRollupsMarker = "+show-rollups";

    /// <summary>The recipe stamped into <paramref name="featureSchemaJson"/>, or null for an unstamped artifact.</summary>
    public static string? ReadFrom(string? featureSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(featureSchemaJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(featureSchemaJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(Property, out var recipe)
                && recipe.ValueKind == JsonValueKind.String
                    ? recipe.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the artifact was trained with <see cref="Current"/>.</summary>
    public static bool IsCurrent(string? featureSchemaJson) =>
        string.Equals(ReadFrom(featureSchemaJson), Current, StringComparison.Ordinal);

    /// <summary>
    /// Whether the artifact's training rows had show rollups in their taste pools: only a recipe that
    /// says so. An unstamped artifact did not, so it is served without them.
    /// </summary>
    public static bool UsesShowRollups(string? featureSchemaJson) =>
        ReadFrom(featureSchemaJson)?.EndsWith(ShowRollupsMarker, StringComparison.Ordinal) == true;
}
