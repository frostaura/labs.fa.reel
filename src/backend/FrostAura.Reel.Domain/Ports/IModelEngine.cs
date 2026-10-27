namespace FrostAura.Reel.Domain.Ports;

/// <summary>Dense training matrix — rows align with labels; names align with columns.</summary>
public record TrainingMatrix(float[][] Features, float[] Labels, string[] FeatureNames);

/// <summary>A trained regressor: serialized artifact + global feature importances.</summary>
public record TrainedModel(byte[] ArtifactBytes, IReadOnlyDictionary<string, double> FeatureImportance);

/// <summary>
/// Regression engine port (ML.NET FastTree behind it — pure managed, arm64-safe). The same
/// engine trains both the throwaway split-models of the eval harness and the serving model,
/// so the gate measures exactly what production ships.
/// </summary>
/// <summary>One scored row: prediction + its top signed feature contributions (for why-this).</summary>
public record ScoredRow(float Score, IReadOnlyList<(string Feature, float Contribution)> TopContributions);

public interface IModelEngine
{
    TrainedModel Train(TrainingMatrix matrix, string hyperparamsJson);

    /// <summary>Scores feature rows with a serialized artifact (batch; row-aligned result).</summary>
    float[] Score(byte[] artifactBytes, float[][] features);

    /// <summary>
    /// Like <see cref="Score"/>, but reuses a model loaded once and cached by
    /// <paramref name="modelKey"/> (the artifact id) — the request-path scorer, so on-demand
    /// search doesn't re-deserialize the artifact on every call.
    /// </summary>
    float[] ScoreCached(Guid modelKey, byte[] artifactBytes, float[][] features);

    /// <summary>Scores with per-row feature contributions — the explainability contract behind every card.</summary>
    ScoredRow[] ScoreWithContributions(byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5);

    /// <summary>
    /// Like <see cref="ScoreWithContributions"/>, but reuses the model loaded once and cached by
    /// <paramref name="modelKey"/> — the request-path explainable scorer behind infinite-scroll
    /// feed pages, so paging doesn't re-deserialize the artifact on every fetch.
    /// </summary>
    ScoredRow[] ScoreWithContributionsCached(Guid modelKey, byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5);

    /// <summary>
    /// One row's score with EVERY feature's contribution in the score's own units (rating points for
    /// this regressor), decomposed so the score minus their sum is one start shared by every row —
    /// the three add up exactly. The prediction waterfall (F-25) needs that additivity; the card
    /// bars (<see cref="ScoreWithContributions"/>) are normalised to the largest contribution and do
    /// not have it. Null from an engine that cannot decompose its score.
    /// </summary>
    ScoredRow? ExplainCached(Guid modelKey, byte[] artifactBytes, float[] features, string[] featureNames) => null;
}
