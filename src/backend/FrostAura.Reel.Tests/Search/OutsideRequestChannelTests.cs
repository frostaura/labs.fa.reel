using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The two negation rules, and the difference between them.
///
/// <para>The original regression this class was written for: "no horror please" returning seven
/// horror titles. The old top-up refilled the clean picks with contradicting ones up to the full
/// ReturnCount, unflagged, with LLM why-lines that admitted the violation. The soft rule below pins
/// that fix — a title the USER ruled out reaches the screen only as an explicitly-flagged
/// closest-but-outside card, only when the clean set cannot fill the MinResults floor, always after
/// every clean pick, always captioned with which exclusion it trips.</para>
///
/// <para>The second rule is newer and is the opposite of soft. An account-level STANDING exclusion
/// is airtight — the product's own notice says "nothing you asked for there can appear" — and it was
/// reaching this partition as an ordinary soft negation, because the interpret prompt is told about
/// the standing rules and the model echoes them back on <c>intent.ExcludeGenres</c>. A measured
/// screen rendered that notice above three comedy cards for an account that permanently excludes
/// comedy, each one captioned by the product itself as a comedy title. A standing exclusion now
/// culls the title from the pool before either channel is filled.</para>
///
/// <para>(Runs without Postgres, like the other Search unit tests — the streaming end-to-end
/// version of the same rules lives in IntegrationTests/LiveSearchExpansionTests.)</para>
/// </summary>
public class OutsideRequestChannelTests
{
    /// <summary>"no horror please" — a negation the user typed, and therefore soft.</summary>
    private static readonly AskReelNegations NoHorrorAsked = AskReelNegations.FromRequest(genres: ["horror"]);

    /// <summary>The same word as a permanent account rule, and therefore airtight.</summary>
    private static readonly AskReelNegations NoHorrorStanding = AskReelNegations.FromStanding(genres: ["horror"]);

    [Fact]
    public void Enough_clean_candidates_drop_contradicting_ones_entirely()
    {
        // The QA-confirmed defect: 12+ clean but fewer than ReturnCount used to pull horror in as
        // unflagged top-up. Clean at-or-above the floor means contradicting titles vanish.
        var pool = Pool(clean: 14, contradicting: 6);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorrorAsked);

        Assert.Equal(14, ranked.Count);
        Assert.Empty(outside);
        Assert.DoesNotContain(ranked, x => x.Title.Genres.Contains("horror"));
    }

    [Fact]
    public void Too_few_clean_candidates_top_up_only_to_the_floor_and_only_into_the_outside_channel()
    {
        var pool = Pool(clean: 5, contradicting: 10);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorrorAsked);

        Assert.Equal(5, ranked.Count);
        Assert.DoesNotContain(ranked, x => x.Title.Genres.Contains("horror"));
        Assert.Equal(7, outside.Count); // MinResults(12) − 5 clean, never ReturnCount(24) − 5
        Assert.All(outside, x => Assert.Contains("horror", x.Title.Genres));
    }

    [Fact]
    public void A_query_that_only_has_contradicting_candidates_fills_the_floor_flagged()
    {
        var pool = Pool(clean: 0, contradicting: 20);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorrorAsked);

        Assert.Empty(ranked);
        Assert.Equal(12, outside.Count);
    }

    [Fact]
    public void Without_negations_the_partition_is_a_plain_top_cut()
    {
        var pool = Pool(clean: 30, contradicting: 0);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, AskReelNegations.None);

        Assert.Equal(24, ranked.Count); // ReturnCount
        Assert.Empty(outside);
    }

    // ── The airtight rule ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// F4-01, as a unit. The clean set is EMPTY and the floor is unfilled — exactly the condition
    /// that tops the outside channel up — and a standing exclusion still yields nothing, because
    /// the alternative is a card the account has permanently forbidden appearing under a notice
    /// saying it cannot.
    /// </summary>
    [Fact]
    public void A_standing_exclusion_is_never_topped_up_into_the_outside_channel()
    {
        var pool = Pool(clean: 0, contradicting: 20);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorrorStanding);

        Assert.Empty(ranked);
        Assert.Empty(outside);
    }

    /// <summary>
    /// The probe: one field moved. The SAME pool and the SAME word, sourced from the user's own
    /// sentence instead of the account's rules, fills the floor — so the difference measured above
    /// is the rule's source and nothing else.
    /// </summary>
    [Fact]
    public void The_same_word_asked_for_rather_than_standing_still_fills_the_floor()
    {
        var pool = Pool(clean: 0, contradicting: 20);

        var (_, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorrorAsked);

        Assert.Equal(12, outside.Count);
    }

    /// <summary>
    /// The gap that let it happen, pinned. <c>EligibilityQueryBuilder</c> excludes a genre by exact
    /// match against <c>Titles.Genres</c>; this pipeline's matcher also reads Subgenres and Keywords
    /// by containment. A title whose genre says drama and whose keywords say "black comedy"
    /// therefore passes the SQL filter for an account that permanently excludes comedy — and is
    /// culled here, which is the last place it can be.
    /// </summary>
    [Fact]
    public void A_title_the_sql_filter_cannot_see_is_still_culled_by_a_standing_exclusion()
    {
        var laundered = Title("Panic", ["drama"], "A hitman in therapy.");
        laundered.Keywords = ["black comedy", "hitman"];
        var pool = new List<(Title, double)> { (laundered, 0.95) };

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(
            pool, AskReelNegations.FromStanding(genres: ["comedy"]));

        Assert.Empty(ranked);
        Assert.Empty(outside);
    }

    /// <summary>A standing KEYWORD is as airtight as a standing genre — same rule, one vocabulary down.</summary>
    [Fact]
    public void A_standing_keyword_is_airtight_too()
    {
        var title = Title("The Others", ["drama"], "A mother believes ghosts haunt the house.");
        var pool = new List<(Title, double)> { (title, 0.95) };

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(
            pool, AskReelNegations.FromStanding(keywords: ["ghosts"]));

        Assert.Empty(ranked);
        Assert.Empty(outside);
    }

    // ── Captions ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_matched_excluded_genre_is_phrased_as_a_genre_title()
    {
        var title = Title("Sinister", ["horror", "thriller"], "A true-crime writer finds footage.");

        var matches = LiveSearchExpansionService.NegationMatches(title, NoHorrorAsked);

        Assert.Equal(["a horror title"], matches.Select(m => m.Phrase));
        Assert.All(matches, m => Assert.False(m.Standing));
    }

    [Fact]
    public void A_matched_excluded_keyword_is_phrased_as_a_match()
    {
        var title = Title("The Others", ["drama"], "A mother believes ghosts haunt the house.");

        var matches = LiveSearchExpansionService.NegationMatches(
            title, AskReelNegations.FromRequest(keywords: ["ghosts"]));

        Assert.Equal(["one that matches ‘ghosts’"], matches.Select(m => m.Phrase));
    }

    [Fact]
    public void The_caption_is_the_honest_template_never_an_llm_pitch()
    {
        var title = Title("Sinister", ["horror"], "A true-crime writer finds footage.");

        var why = LiveSearchExpansionService.OutsideWhy(
            LiveSearchExpansionService.NegationMatches(title, NoHorrorAsked));

        Assert.Equal("Closest match we could find, but it's a horror title — which you asked to skip.", why);
    }

    /// <summary>
    /// F4-02. "Which you asked to skip" is a quotation of the user, and it was being printed for
    /// rules the user never typed — seven of eleven outside cards on one screen told a user who had
    /// asked for a 90s romantic comedy that they had asked to skip horror. A standing rule gets the
    /// other true sentence, and it names a screen the user can actually open.
    /// </summary>
    [Fact]
    public void A_standing_rule_is_captioned_as_a_condition_not_as_something_the_user_said()
    {
        var title = Title("Sinister", ["horror"], "A true-crime writer finds footage.");

        var why = LiveSearchExpansionService.OutsideWhy(
            LiveSearchExpansionService.NegationMatches(title, NoHorrorStanding));

        Assert.Equal(
            "Closest match we could find, but it's a horror title — which your conditions exclude.", why);
    }

    /// <summary>
    /// A model's inference that is neither standing nor typed is not a negation at all: it neither
    /// culls, nor flags, nor gets quoted back at the user as their own words.
    /// </summary>
    [Fact]
    public void An_exclusion_from_neither_source_is_not_a_negation()
    {
        var pool = Pool(clean: 0, contradicting: 20);

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, AskReelNegations.None);

        Assert.Equal(20, ranked.Count);
        Assert.Empty(outside);
        Assert.Empty(LiveSearchExpansionService.NegationMatches(pool[0].Item1, AskReelNegations.None));
    }

    /// <summary>
    /// IREV1-SYS-01: titles carrying catalogue evidence for the concept lead the partition BEFORE the
    /// ReturnCount cut. The cut used to run on similarity alone, and the seed is the kNN arm's own
    /// vector, so the nearest 24 were kNN titles by construction and a tagged heist film the reply
    /// counted never reached the judge. Varied over where the evidence sits, how much of it there is,
    /// and a soft negation on top.
    /// </summary>
    [Theory]
    [InlineData(30, 5, false)]
    [InlineData(40, 1, false)]
    [InlineData(30, 24, false)]
    [InlineData(30, 5, true)]
    public void Evidence_titles_lead_the_partition_before_any_cut(int nearer, int tagged, bool negated)
    {
        var pool = Pool(clean: nearer + tagged, contradicting: 0);
        var evidence = pool.Skip(nearer).Select(x => x.Title.Id).ToHashSet();
        var negations = negated ? NoHorrorAsked : AskReelNegations.None;

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, negations, evidence.Contains);

        Assert.Equal(24, ranked.Count);
        Assert.Empty(outside);
        Assert.Equal(pool.Skip(nearer).Take(24).Select(x => x.Title.Id), ranked.Take(Math.Min(tagged, 24)).Select(x => x.Title.Id));
        // Equal footing keeps the similarity order behind them.
        Assert.Equal(pool.Take(24 - Math.Min(tagged, 24)).Select(x => x.Title.Id), ranked.Skip(tagged).Select(x => x.Title.Id));
    }

    [Fact]
    public void Without_evidence_the_partition_keeps_the_similarity_order()
    {
        var pool = Pool(clean: 30, contradicting: 0);

        var (ranked, _) = LiveSearchExpansionService.PartitionByNegation(pool, AskReelNegations.None, _ => false);

        Assert.Equal(pool.Take(24).Select(x => x.Title.Id), ranked.Select(x => x.Title.Id));
    }

    [Fact]
    public void Contradicting_evidence_titles_still_go_to_the_outside_channel_first()
    {
        var pool = Pool(clean: 3, contradicting: 20);
        var tagged = pool.Skip(3 + 15).Select(x => x.Title.Id).ToHashSet();

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorrorAsked, tagged.Contains);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(9, outside.Count);
        Assert.Equal(tagged, outside.Take(5).Select(x => x.Title.Id).ToHashSet());
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

    [Fact]
    public void Tv_horror_with_no_horror_genre_is_still_caught_via_keywords_and_subgenres()
    {
        // 2026-08-10 live-replay regression: TMDB's TV taxonomy has no "Horror" genre, so American
        // Horror Story (drama/mystery/sci-fi + keywords ["horror", "supernatural horror", ...])
        // sailed into the MAIN list after "no horror please". Keywords/Subgenres now trip the
        // exclusion too.
        var ahs = Title("American Horror Story", ["drama", "mystery", "science-fiction"], "An anthology.");
        ahs.MediaType = MediaType.Show;
        ahs.Keywords = ["haunted house", "supernatural horror", "horror", "witchcraft"];
        var clean = Enumerable.Range(0, 14)
            .Select(i => (Title($"Clean {i}", ["thriller"], "A tense pursuit."), 0.9 - (i * 0.001)));
        var pool = clean.Append((ahs, 0.95)).ToList();

        var (ranked, outside) = LiveSearchExpansionService.PartitionByNegation(pool, NoHorrorAsked);

        Assert.DoesNotContain(ranked, x => x.Title.Name == "American Horror Story");
        Assert.Empty(outside); // 14 clean ≥ MinResults → contradicting TV horror dropped entirely
        Assert.Contains(
            LiveSearchExpansionService.NegationMatches(ahs, NoHorrorAsked),
            m => m.Phrase == "a horror title");
    }

    [Fact]
    public void Subgenre_containment_trips_the_exclusion_too()
    {
        var t = Title("Creepshow", ["comedy"], "Segments.");
        t.Subgenres = ["anthology horror"];

        Assert.Contains(
            LiveSearchExpansionService.NegationMatches(t, NoHorrorAsked),
            m => m.Phrase == "a horror title");
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
