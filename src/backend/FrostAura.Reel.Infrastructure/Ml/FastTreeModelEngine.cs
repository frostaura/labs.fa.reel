using System.Collections.Concurrent;
using System.Text.Json;
using FrostAura.Reel.Domain.Ports;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.FastTree;

namespace FrostAura.Reel.Infrastructure.Ml;

/// <summary>
/// ML.NET FastTree regression engine. FastTree over LightGBM because it is pure managed —
/// the multi-arch (amd64+arm64) image mandate rules out native-binary trainers.
/// </summary>
public class FastTreeModelEngine : IModelEngine
{
    public record Hyperparams(int Trees = 200, int Leaves = 20, int MinExamplesPerLeaf = 10, double LearningRate = 0.2);

    // Request-path model cache: loaded transformers keyed by artifact id. ITransformer.Transform is
    // safe for concurrent reads (unlike PredictionEngine), so one load serves every request.
    private readonly ConcurrentDictionary<Guid, ITransformer> _modelCache = new();
    // Last-access ordering for the size-bounded LRU. A monotonic counter (not wall-clock) gives a
    // strict total order with no ties, so eviction always has a single unambiguous victim.
    private readonly ConcurrentDictionary<Guid, long> _lastAccess = new();
    private long _accessCounter;
    private readonly object _evictionLock = new();
    private readonly MLContext _cacheMl = new(seed: 7);
    private const int MaxCachedModels = 32;

    /// <summary>
    /// Load-or-reuse a tenant's transformer with a size-bounded LRU. When the cache grows past
    /// <see cref="MaxCachedModels"/> we evict ONLY the single least-recently-used entry — never the
    /// whole dictionary. The old <c>Clear()</c> wiped every tenant's warm model at once, so the 33rd
    /// concurrent user triggered a reload stampede across all of them; single-entry LRU eviction
    /// keeps the hot set warm and bounds memory the same way.
    /// </summary>
    private ITransformer GetOrLoadModel(Guid modelKey, byte[] artifactBytes)
    {
        var model = _modelCache.GetOrAdd(modelKey, _ =>
        {
            using var stream = new MemoryStream(artifactBytes);
            return _cacheMl.Model.Load(stream, out DataViewSchema _);
        });

        // Mark most-recently-used AFTER the load so the entry we're about to score can never be the
        // eviction victim below.
        _lastAccess[modelKey] = Interlocked.Increment(ref _accessCounter);

        while (_modelCache.Count > MaxCachedModels)
        {
            lock (_evictionLock)
            {
                if (_modelCache.Count <= MaxCachedModels)
                {
                    break;
                }

                var victim = _lastAccess
                    .Where(kv => kv.Key != modelKey)
                    .OrderBy(kv => kv.Value)
                    .Select(kv => (Guid?)kv.Key)
                    .FirstOrDefault();
                if (victim is not { } evictKey)
                {
                    break;
                }

                _modelCache.TryRemove(evictKey, out _);
                _lastAccess.TryRemove(evictKey, out _);
            }
        }

        return model;
    }

    private sealed class Row
    {
        public float[] Features { get; set; } = [];
        public float Label { get; set; }
    }

    private sealed class Prediction
    {
        public float Score { get; set; }
    }

    private sealed class ContributionPrediction
    {
        public float Score { get; set; }
        public float[] FeatureContributions { get; set; } = [];
    }

    public TrainedModel Train(TrainingMatrix matrix, string hyperparamsJson)
    {
        if (matrix.Features.Length != matrix.Labels.Length)
        {
            throw new ArgumentException("Feature rows and labels are misaligned.");
        }

        var hp = JsonSerializer.Deserialize<Hyperparams>(hyperparamsJson) ?? new Hyperparams();
        var ml = new MLContext(seed: 7); // deterministic runs — the iteration ledger compares apples
        var width = matrix.FeatureNames.Length;

        var data = ml.Data.LoadFromEnumerable(
            matrix.Features.Zip(matrix.Labels, (f, l) => new Row { Features = f, Label = l }),
            SchemaDefinitionFor(width));

        var trainer = ml.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
        {
            LabelColumnName = nameof(Row.Label),
            FeatureColumnName = nameof(Row.Features),
            NumberOfTrees = hp.Trees,
            NumberOfLeaves = hp.Leaves,
            MinimumExampleCountPerLeaf = hp.MinExamplesPerLeaf,
            LearningRate = hp.LearningRate,
        });

        var model = trainer.Fit(data);

        // Global gains per feature — the iteration ledger's "what mattered" answer.
        var weights = default(VBuffer<float>);
        model.Model.GetFeatureWeights(ref weights);
        var importance = new Dictionary<string, double>();
        foreach (var item in weights.Items(all: false))
        {
            if (item.Key < width && Math.Abs(item.Value) > 1e-9)
            {
                importance[matrix.FeatureNames[item.Key]] = item.Value;
            }
        }

        using var stream = new MemoryStream();
        ml.Model.Save(model, data.Schema, stream);
        return new TrainedModel(stream.ToArray(), importance);
    }

    public float[] Score(byte[] artifactBytes, float[][] features)
    {
        if (features.Length == 0)
        {
            return [];
        }

        var ml = new MLContext(seed: 7);
        using var stream = new MemoryStream(artifactBytes);
        var model = ml.Model.Load(stream, out _);

        var width = features[0].Length;
        var data = ml.Data.LoadFromEnumerable(
            features.Select(f => new Row { Features = f }),
            SchemaDefinitionFor(width));

        var scored = model.Transform(data);
        return ml.Data.CreateEnumerable<Prediction>(scored, reuseRowObject: false)
            .Select(p => p.Score)
            .ToArray();
    }

    public float[] ScoreCached(Guid modelKey, byte[] artifactBytes, float[][] features)
    {
        if (features.Length == 0)
        {
            return [];
        }

        var model = GetOrLoadModel(modelKey, artifactBytes);

        var width = features[0].Length;
        var data = _cacheMl.Data.LoadFromEnumerable(
            features.Select(f => new Row { Features = f }),
            SchemaDefinitionFor(width));

        var scored = model.Transform(data);
        return _cacheMl.Data.CreateEnumerable<Prediction>(scored, reuseRowObject: false)
            .Select(p => p.Score)
            .ToArray();
    }

    public ScoredRow[] ScoreWithContributions(byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5)
    {
        if (features.Length == 0)
        {
            return [];
        }

        var ml = new MLContext(seed: 7);
        using var stream = new MemoryStream(artifactBytes);
        var loaded = ml.Model.Load(stream, out _);

        if (loaded is not ISingleFeaturePredictionTransformer<ICalculateFeatureContribution> predictor)
        {
            // Chained pipelines would need unwrapping; our artifacts are single FastTree transformers.
            throw new InvalidOperationException($"Artifact is not a contribution-capable transformer ({loaded.GetType().Name}).");
        }

        var width = features[0].Length;
        var data = ml.Data.LoadFromEnumerable(
            features.Select(f => new Row { Features = f }),
            SchemaDefinitionFor(width));

        var withContributions = ml.Transforms
            .CalculateFeatureContribution(predictor, numberOfPositiveContributions: width, numberOfNegativeContributions: width)
            .Fit(data)
            .Transform(predictor.Transform(data));

        return ml.Data.CreateEnumerable<ContributionPrediction>(withContributions, reuseRowObject: false)
            .Select(p => new ScoredRow(
                p.Score,
                p.FeatureContributions
                    .Select((value, index) => (Feature: index < featureNames.Length ? featureNames[index] : $"f{index}", Contribution: value))
                    .Where(c => Math.Abs(c.Contribution) > 1e-6)
                    .OrderByDescending(c => Math.Abs(c.Contribution))
                    .Take(topK)
                    .ToList()))
            .ToArray();
    }

    public ScoredRow[] ScoreWithContributionsCached(Guid modelKey, byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5)
    {
        if (features.Length == 0)
        {
            return [];
        }

        var model = GetOrLoadModel(modelKey, artifactBytes);

        if (model is not ISingleFeaturePredictionTransformer<ICalculateFeatureContribution> predictor)
        {
            throw new InvalidOperationException($"Artifact is not a contribution-capable transformer ({model.GetType().Name}).");
        }

        var width = features[0].Length;
        var data = _cacheMl.Data.LoadFromEnumerable(
            features.Select(f => new Row { Features = f }),
            SchemaDefinitionFor(width));

        var withContributions = _cacheMl.Transforms
            .CalculateFeatureContribution(predictor, numberOfPositiveContributions: width, numberOfNegativeContributions: width)
            .Fit(data)
            .Transform(predictor.Transform(data));

        return _cacheMl.Data.CreateEnumerable<ContributionPrediction>(withContributions, reuseRowObject: false)
            .Select(p => new ScoredRow(
                p.Score,
                p.FeatureContributions
                    .Select((value, index) => (Feature: index < featureNames.Length ? featureNames[index] : $"f{index}", Contribution: value))
                    .Where(c => Math.Abs(c.Contribution) > 1e-6)
                    .OrderByDescending(c => Math.Abs(c.Contribution))
                    .Take(topK)
                    .ToList()))
            .ToArray();
    }

    /// <summary>
    /// An exact additive decomposition by walking the ensemble (the tree-path method): each tree's
    /// output is its root value plus the change in node value at every split on the way to the leaf,
    /// and each change is credited to the feature the split tested. A node's value is the mean of the
    /// leaf values beneath it. The roots and the bias are the same for every title, so
    /// <c>score − Σ contributions</c> is one shared start; that telescoping is what makes the walk add
    /// up. ML.NET's own contribution transform is not additive (it scores the opposite sub-tree at
    /// each split), which is why this walks the trees itself. Null for a model it cannot walk: not a
    /// FastTree regressor, a categorical split, or a missing feature value.
    /// </summary>
    public ScoredRow? ExplainCached(Guid modelKey, byte[] artifactBytes, float[] features, string[] featureNames)
    {
        if (GetOrLoadModel(modelKey, artifactBytes) is not RegressionPredictionTransformer<FastTreeRegressionModelParameters> { Model: { } parameters }
            || features.Any(float.IsNaN))
        {
            return null;
        }

        var ensemble = parameters.TrainedTreeEnsemble;
        var contributions = new double[features.Length];
        var start = ensemble.Bias;
        for (var t = 0; t < ensemble.Trees.Count; t++)
        {
            var tree = ensemble.Trees[t];
            var weight = ensemble.TreeWeights[t];
            if (tree.NumberOfNodes == 0)
            {
                start += weight * tree.LeafValues[0];
                continue;
            }

            if (tree.CategoricalSplitFlags.Any(f => f))
            {
                return null;
            }

            var nodeValues = NodeValues(tree);
            start += weight * nodeValues[0];
            var node = 0;
            while (true)
            {
                var feature = tree.NumericalSplitFeatureIndexes[node];
                if (feature >= features.Length)
                {
                    return null;
                }

                var next = features[feature] <= tree.NumericalSplitThresholds[node] ? tree.LeftChild[node] : tree.RightChild[node];
                var child = next < 0 ? tree.LeafValues[~next] : nodeValues[next];
                contributions[feature] += weight * (child - nodeValues[node]);
                if (next < 0)
                {
                    break;
                }

                node = next;
            }
        }

        return new ScoredRow(
            (float)(start + contributions.Sum()),
            contributions
                .Select((value, index) => (Feature: index < featureNames.Length ? featureNames[index] : $"f{index}", Contribution: (float)value))
                .Where(c => c.Contribution != 0f)
                .OrderByDescending(c => Math.Abs(c.Contribution))
                .ToList());
    }

    /// <summary>Each internal node's value: the mean of the leaf values in its subtree.</summary>
    private static double[] NodeValues(RegressionTree tree)
    {
        var sums = new double[tree.NumberOfNodes];
        var counts = new int[tree.NumberOfNodes];

        (double Sum, int Count) Walk(int node)
        {
            if (node < 0)
            {
                return (tree.LeafValues[~node], 1);
            }

            var (leftSum, leftCount) = Walk(tree.LeftChild[node]);
            var (rightSum, rightCount) = Walk(tree.RightChild[node]);
            sums[node] = leftSum + rightSum;
            counts[node] = leftCount + rightCount;
            return (sums[node], counts[node]);
        }

        Walk(0);
        return [.. sums.Select((sum, i) => sum / counts[i])];
    }

    private static SchemaDefinition SchemaDefinitionFor(int width)
    {
        var schema = SchemaDefinition.Create(typeof(Row));
        schema[nameof(Row.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, width);
        return schema;
    }
}
