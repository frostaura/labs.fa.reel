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
    }

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
    }

    [Fact]
    public void Only_a_recipe_that_names_the_rollups_had_them()
    {
        Assert.True(TrainingRecipe.UsesShowRollups("""{"pools": "out-of-fold-5+show-rollups"}"""));
        Assert.False(TrainingRecipe.UsesShowRollups("""{"pools": "out-of-fold-5"}"""));
        Assert.False(TrainingRecipe.UsesShowRollups("""{"pools": "show-rollups-later"}"""));
    }
}
