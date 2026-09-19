using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The pure math and pool rules behind Phase 8's reference anchoring and runtime intents:
/// the query-vector blend (<c>normalize(concept + 0.5 × mean(references))</c>), the rule that a
/// reference title never comes back as a result, and the runtime ceiling's unknown-runtime
/// tolerance (kept only as a floor top-up, never as an ordinary pass).
/// </summary>
public class ReferenceAnchoringTests
{
    // ── BlendQueryVector ────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_references_leave_the_concept_vector_untouched()
    {
        float[] concept = [0.6f, 0.8f];

        var blended = LiveSearchExpansionService.BlendQueryVector(concept, []);

        Assert.Same(concept, blended);
    }

    [Fact]
    public void The_blend_is_the_normalized_concept_plus_half_the_reference_mean()
    {
        float[] concept = [1f, 0f];
        float[][] references = [[0f, 1f], [0f, 0.5f]];

        var blended = LiveSearchExpansionService.BlendQueryVector(concept, references);

        // mean(refs) = [0, 0.75]; concept + 0.5×mean = [1, 0.375]; ‖·‖ = √1.140625.
        var norm = Math.Sqrt(1.140625);
        Assert.Equal(1d / norm, blended[0], 5);
        Assert.Equal(0.375d / norm, blended[1], 5);
    }

    [Fact]
    public void The_blended_vector_is_unit_length()
    {
        float[] concept = [3f, 4f, 0f];
        float[][] references = [[0f, 0f, 5f]];

        var blended = LiveSearchExpansionService.BlendQueryVector(concept, references);

        Assert.Equal(1d, Math.Sqrt(blended.Sum(x => (double)x * x)), 5);
    }

    [Fact]
    public void A_reference_of_the_wrong_dimensionality_is_skipped_not_crashed_on()
    {
        float[] concept = [1f, 0f];

        var blended = LiveSearchExpansionService.BlendQueryVector(concept, [[0f, 1f, 0f]]);

        Assert.Same(concept, blended); // nothing usable → concept passes through
    }

    // ── Reference exclusion ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_resolved_reference_is_excluded_from_the_pool()
    {
        var reference = Title("Blade Runner");
        var neighbour = Title("Gattaca");
        var referenceIds = new HashSet<Guid> { reference.Id };

        var pool = new List<Title> { reference, neighbour }
            .Where(t => !LiveSearchExpansionService.IsReference(t, referenceIds))
            .ToList();

        var survivor = Assert.Single(pool);
        Assert.Equal("Gattaca", survivor.Name);
    }

    // ── ApplyRuntimeCeiling ─────────────────────────────────────────────────────────────────

    [Fact]
    public void No_ceiling_passes_the_pool_through_untouched()
    {
        var pool = new List<Title> { Title("A", 200), Title("B", null) };

        Assert.Same(pool, LiveSearchExpansionService.ApplyRuntimeCeiling(pool, null, 12));
    }

    [Fact]
    public void A_known_runtime_over_the_ceiling_is_always_dropped()
    {
        var pool = Enumerable.Range(0, 15).Select(i => Title($"Fit {i}", 85)).ToList();
        pool.Add(Title("Epic", 180));

        var filtered = LiveSearchExpansionService.ApplyRuntimeCeiling(pool, 90, 12);

        Assert.Equal(15, filtered.Count);
        Assert.DoesNotContain(filtered, t => t.Name == "Epic");
    }

    [Fact]
    public void Unknown_runtimes_are_dropped_when_the_known_fits_fill_the_floor()
    {
        var pool = Enumerable.Range(0, 12).Select(i => Title($"Fit {i}", 80)).ToList();
        pool.Add(Title("Mystery length", null));

        var filtered = LiveSearchExpansionService.ApplyRuntimeCeiling(pool, 90, 12);

        Assert.Equal(12, filtered.Count);
        Assert.DoesNotContain(filtered, t => t.RuntimeMinutes is null);
    }

    [Fact]
    public void Unknown_runtimes_top_up_only_to_the_floor_when_the_fits_fall_short()
    {
        var pool = Enumerable.Range(0, 5).Select(i => Title($"Fit {i}", 80)).ToList();
        pool.AddRange(Enumerable.Range(0, 20).Select(i => Title($"Unknown {i}", null)));
        pool.Add(Title("Too long", 150));

        var filtered = LiveSearchExpansionService.ApplyRuntimeCeiling(pool, 90, 12);

        Assert.Equal(12, filtered.Count); // 5 fits + 7 unknown top-up, never all 20 unknowns
        Assert.Equal(7, filtered.Count(t => t.RuntimeMinutes is null));
        Assert.DoesNotContain(filtered, t => t.Name == "Too long"); // a known violation never tops up
    }

    private static Title Title(string name, int? runtimeMinutes = null) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TraktSlug = string.Empty,
        Name = name,
        RuntimeMinutes = runtimeMinutes,
        CreatedAt = DateTime.UtcNow,
    };
}
