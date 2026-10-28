using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>The recipe stamp an artifact carries, and what serving reads back from it (HREV2-RET-02).</summary>
public class TrainingRecipeTests
{
    [Fact]
    public void The_current_recipe_says_whether_it_has_rollups_and_the_switch_agrees()
    {
        var stamped = $$"""{"version": 5, "pools": "{{TrainingRecipe.Current}}"}""";
        Assert.True(TrainingRecipe.IsCurrent(stamped));
        Assert.Equal(TrainingRecipe.ShowRollups, TrainingRecipe.UsesShowRollups(stamped));
        Assert.Equal(TrainingRecipe.OutOfFoldPools, TrainingRecipe.UsesOutOfFoldPools(stamped));
    }

    /// <summary>
    /// HREV2-RET-02: the current recipe trains on out-of-fold pools, and says so in the stamp serving
    /// reads back. Literal on purpose: training back on leave-one-out is a recipe change that has to
    /// be made here, with its ablation, not by flipping a default.
    /// </summary>
    [Fact]
    public void The_current_recipe_trains_on_out_of_fold_pools()
    {
        Assert.True(TrainingRecipe.OutOfFoldPools);
        Assert.Equal(TrainingRecipe.OutOfFoldRecipe, $"out-of-fold-{FeatureVectorBuilder.TrainingFolds}");
        Assert.StartsWith(TrainingRecipe.OutOfFoldRecipe, TrainingRecipe.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"pools": "out-of-fold-5"}""", true)]
    [InlineData("""{"version": 5, "pools": "out-of-fold-5+show-rollups"}""", true)]
    [InlineData("""{"pools": "leave-one-out"}""", false)]
    [InlineData("""{"pools": "leave-one-out+show-rollups"}""", false)]
    [InlineData("""{"pools": "out-of-fold-50"}""", false)]
    [InlineData("""{"pools": "out-of-fold-3"}""", false)]
    [InlineData("""{"pools": "out-of-fold-5+show-rollups+show-rollups"}""", false)]
    public void Only_a_recipe_whose_pools_are_out_of_fold_trained_on_them(string json, bool expected) =>
        Assert.Equal(expected, TrainingRecipe.UsesOutOfFoldPools(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"version": 5}""")]
    [InlineData("""{"version": 5, "pools": 3}""")]
    public void An_unstamped_or_unreadable_artifact_is_the_legacy_recipe(string? json)
    {
        Assert.Null(TrainingRecipe.ReadFrom(json));
        Assert.False(TrainingRecipe.IsCurrent(json));
        Assert.False(TrainingRecipe.UsesShowRollups(json));
        Assert.False(TrainingRecipe.UsesOutOfFoldPools(json));
    }

    [Fact]
    public void Only_a_recipe_that_names_the_rollups_had_them()
    {
        Assert.True(TrainingRecipe.UsesShowRollups("""{"pools": "out-of-fold-5+show-rollups"}"""));
        Assert.False(TrainingRecipe.UsesShowRollups("""{"pools": "out-of-fold-5"}"""));
        Assert.False(TrainingRecipe.UsesShowRollups("""{"pools": "show-rollups-later"}"""));
    }
}
