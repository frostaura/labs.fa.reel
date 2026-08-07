using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// Regression for "no horror please" returning seven horror titles. The old top-up refilled the
/// clean picks with contradicting ones up to the full ReturnCount, unflagged, with LLM why-lines
/// that admitted the violation. These pin the hard rule: a title tripping an exclusion reaches the
/// screen only as an explicitly-flagged closest-but-outside card, only when the clean set cannot
/// fill the MinResults floor, always after every clean pick, and always captioned with the
/// deterministic statement of WHICH exclusion it trips — never an LLM pitch.
/// (Runs without Postgres, like the other Search unit tests — the streaming end-to-end version of
/// the same rules lives in IntegrationTests/LiveSearchExpansionTests.)
/// </summary>
public class OutsideRequestChannelTests
{
    private static readonly SearchIntent NoHorror =
        new([], [], [], string.Empty, null, null, null, ExcludeGenres: ["horror"]);

    [Fact]
    public void Enough_clean_candidates_drop_contradicting_ones_entirely()
    {
        // The QA-confirmed defect: 12+ clean but fewer than ReturnCount used to pull horror in as
        // unflagged top-up. Clean at-or-above the floor means contradicting titles vanish.
        var pool = Pool(clean: 14, contradicting: 6);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorror);

        Assert.Equal(14, ranked.Count);
        Assert.Empty(outside);
        Assert.DoesNotContain(ranked, x => x.Title.Genres.Contains("horror"));
    }

    [Fact]
    public void Too_few_clean_candidates_top_up_only_to_the_floor_and_only_into_the_outside_channel()
    {
        var pool = Pool(clean: 5, contradicting: 10);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorror);

        Assert.Equal(5, ranked.Count);
        Assert.DoesNotContain(ranked, x => x.Title.Genres.Contains("horror"));
        Assert.Equal(7, outside.Count); // MinResults(12) − 5 clean, never ReturnCount(24) − 5
        Assert.All(outside, x => Assert.Contains("horror", x.Title.Genres));
    }

    [Fact]
    public void A_query_that_only_has_contradicting_candidates_fills_the_floor_flagged()
    {
        var pool = Pool(clean: 0, contradicting: 20);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorror);

        Assert.Empty(ranked);
        Assert.Equal(12, outside.Count);
    }

    [Fact]
    public void Without_negations_the_partition_is_a_plain_top_cut()
    {
        var intent = new SearchIntent([], [], [], string.Empty, null, null, null);
        var pool = Pool(clean: 30, contradicting: 0);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, intent);

        Assert.Equal(24, ranked.Count); // ReturnCount
        Assert.Empty(outside);
    }

    [Fact]
    public void A_matched_excluded_genre_is_phrased_as_a_genre_title()
    {
        var title = Title("Sinister", ["horror", "thriller"], "A true-crime writer finds footage.");

        var matches = LiveSearchExpansionService.NegationMatches(title, NoHorror);

        Assert.Equal(["a horror title"], matches);
    }

    [Fact]
    public void A_matched_excluded_keyword_is_phrased_as_a_match()
    {
        var intent = new SearchIntent([], [], [], string.Empty, null, null, null, ExcludeKeywords: ["ghosts"]);
        var title = Title("The Others", ["drama"], "A mother believes ghosts haunt the house.");

        var matches = LiveSearchExpansionService.NegationMatches(title, intent);

        Assert.Equal(["one that matches ‘ghosts’"], matches);
    }

    [Fact]
    public void The_caption_is_the_honest_template_never_an_llm_pitch()
    {
        var title = Title("Sinister", ["horror"], "A true-crime writer finds footage.");

        var why = LiveSearchExpansionService.OutsideWhy(LiveSearchExpansionService.NegationMatches(title, NoHorror));

        Assert.Equal("Closest match we could find, but it's a horror title — which you asked to skip.", why);
    }

    /// <summary>A similarity-ordered pool: clean thrillers first, then horror contradictions.</summary>
    private static List<(Title Title, double Sim)> Pool(int clean, int contradicting)
    {
        var pool = new List<(Title, double)>();
        for (var i = 0; i < clean; i++)
        {
            pool.Add((Title($"Clean {i}", ["thriller"], "A tense pursuit."), 0.9 - (i * 0.001)));
        }

        for (var i = 0; i < contradicting; i++)
        {
            pool.Add((Title($"Horror {i}", ["horror"], "A haunting."), 0.8 - (i * 0.001)));
        }

        return pool;
    }

    private static Title Title(string name, string[] genres, string overview) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TraktSlug = string.Empty,
        Name = name,
        Genres = genres,
        Overview = overview,
        CreatedAt = DateTime.UtcNow,
    };
}
