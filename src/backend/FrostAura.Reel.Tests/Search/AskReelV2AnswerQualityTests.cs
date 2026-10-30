using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The answer-quality residuals of the final golden live eval (v2, 120 scenarios), each pinned without a
/// database: the relaxation ladder (over-constrained plans), language requirements and their collision,
/// the crowd-floor collision, a genre the user named that the account excludes, a genre the account
/// never touched, and a judge that skips a title.
/// </summary>
public class AskReelV2AnswerQualityTests
{
    private static ConceptTerm Keyword(string words) => new(words, [words], [], [words]);

    private static ConceptTerm Genre(string slug) => new(slug, [], [slug], []);

    private static IReadOnlyList<string> Words(HardConstraints rung) =>
        [.. rung.Concepts.Select(c => c.UserWords).Order(StringComparer.Ordinal)];

    // ── The relaxation ladder ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_descriptive_keyword_beside_a_genre_is_dropped_and_the_genre_never_is()
    {
        // "big action movies" as the planner wrote it: a keyword "big action" and the action genre.
        var hard = HardConstraints.None with { Concepts = [Keyword("big action"), Genre("action")], Media = MediaScope.Movies };

        var rungs = ConceptLadder.Rungs(hard, "big action movies");

        Assert.Equal(2, rungs.Count);
        Assert.Equal(["action"], Words(rungs[1]));
        Assert.Equal(MediaScope.Movies, rungs[1].Media);
    }

    [Fact]
    public void Keywords_are_dropped_last_said_first_down_to_the_first_one_said()
    {
        var hard = HardConstraints.None with { Concepts = [Keyword("silence"), Keyword("space"), Keyword("isolation")] };

        var rungs = ConceptLadder.Rungs(hard, "space movies about isolation and silence");

        Assert.Equal([["isolation", "silence", "space"], ["isolation", "space"], ["space"]], rungs.Select(Words));
    }

    [Fact]
    public void A_concept_the_newest_message_does_not_say_is_dropped_before_one_it_does()
    {
        // "hitman movies", then "now spy movies" read as a refinement: the newest words anchor.
        var hard = HardConstraints.None with { Concepts = [Keyword("hitman"), Keyword("spy")] };

        Assert.Equal(["spy"], Words(ConceptLadder.Rungs(hard, "now spy movies")[^1]));
        Assert.Equal(["hitman"], Words(ConceptLadder.Rungs(hard, "hitman movies")[^1]));
    }

    [Theory]
    [InlineData("romantic comedies")]
    [InlineData("comedy romance")]
    public void Two_genres_with_nothing_else_to_stand_on_are_never_relaxed(string message)
    {
        var hard = HardConstraints.None with { Concepts = [Genre("romance"), Genre("comedy")] };

        Assert.Single(ConceptLadder.Rungs(hard, message));
    }

    [Fact]
    public void A_single_concept_has_nothing_to_relax()
    {
        Assert.Single(ConceptLadder.Rungs(HardConstraints.None with { Concepts = [Keyword("ninja")] }, "ninja movies"));
        Assert.Single(ConceptLadder.Rungs(HardConstraints.None, "something cosy"));
    }

    [Fact]
    public void Under_a_person_or_a_language_even_a_genre_may_go_and_the_anchor_never_does()
    {
        var person = HardConstraints.None with
        {
            Concepts = [Genre("action")],
            People = [new PersonConstraint("Jason Statham", null, PersonRole.Cast)],
        };
        var language = HardConstraints.None with { Concepts = [Genre("thriller")], Languages = [new LanguageTerm("korean", "ko")] };

        var byPerson = ConceptLadder.Rungs(person, "Jason Statham action movies");
        var byLanguage = ConceptLadder.Rungs(language, "korean thrillers");

        Assert.Empty(byPerson[^1].Concepts);
        Assert.Single(byPerson[^1].People);
        Assert.Empty(byLanguage[^1].Concepts);
        Assert.Equal("ko", Assert.Single(byLanguage[^1].LanguageTerms).Code);
    }

    [Fact]
    public void A_keyword_beside_a_genre_goes_first_whichever_was_said_first()
    {
        // "documentaries about space" and "nature documentary series": the genre is the shelf.
        var hard = HardConstraints.None with { Concepts = [Keyword("nature"), Genre("documentary")] };

        Assert.Equal(["documentary"], Words(ConceptLadder.Rungs(hard, "nature documentary series")[^1]));
        Assert.Equal(["documentary"], Words(ConceptLadder.Rungs(hard with { Concepts = [Genre("documentary"), Keyword("space")] }, "documentaries about space")[^1]));
    }

    [Theory]
    // The request itself has supply: searched and reported as asked.
    [InlineData(new[] { true }, 0, 0)]
    [InlineData(new[] { true, true }, 0, 0)]
    // Empty as asked, a wider rung has some: search that rung, report where it ran dry.
    [InlineData(new[] { false, true }, 1, 0)]
    [InlineData(new[] { false, false, true }, 2, 1)]
    // Empty all the way down: search as asked, report the anchor.
    [InlineData(new[] { false, false, false }, 0, 2)]
    [InlineData(new[] { false }, 0, 0)]
    public void The_searched_rung_is_the_first_with_supply_and_the_reported_one_where_it_ran_dry(bool[] offerable, int search, int report) =>
        Assert.Equal((search, report), ConceptLadder.Choose(offerable));

    // ── Languages ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("korean", "ko")]
    [InlineData("french", "fr")]
    [InlineData("japanese", "ja")]
    [InlineData("bollywood", "hi")]
    [InlineData("british", null)]
    [InlineData("american", null)]
    [InlineData("ninja", null)]
    public void A_language_word_names_its_code_and_a_country_of_english_names_none(string word, string? code) =>
        Assert.Equal(code, LanguageWords.CodeOf(word));

    [Fact]
    public void A_language_leads_the_shelf_name()
    {
        var hard = HardConstraints.None with { Concepts = [Genre("thriller")], Languages = [new LanguageTerm("korean", "ko")] };

        Assert.Equal("korean thriller", AskReelTopic.Shelf(hard));
        Assert.Equal("korean", AskReelTopic.Shelf(hard with { Concepts = [] }));
    }

    [Fact]
    public void A_required_language_filters_on_the_titles_own_language_and_is_evidence()
    {
        var hard = HardConstraints.None with { Languages = [new LanguageTerm("korean", "ko")] };
        Title[] titles =
        [
            new() { Id = Guid.NewGuid(), Name = "The Host", Language = "ko" },
            new() { Id = Guid.NewGuid(), Name = "Korean War Story", Language = "en", Keywords = ["korean war"] },
            new() { Id = Guid.NewGuid(), Name = "Undated", Language = null },
        ];

        var kept = HardConstraintFilter.Apply(titles.AsQueryable(), null!, hard).Select(t => t.Name).ToList();

        Assert.Equal(["The Host"], kept);
        Assert.Contains("language:ko", HardConstraintFilter.EvidenceFor(titles[0], hard));
    }

    [Fact]
    public void A_language_the_rule_does_not_allow_collides_and_is_named()
    {
        var englishOnly = FilterRuleSet.FromRows([new ContentFilter { Kind = FilterKind.Language, Value = "en" }]);
        var allowsKorean = FilterRuleSet.FromRows([new ContentFilter { Kind = FilterKind.Language, Value = "en" }, new ContentFilter { Kind = FilterKind.Language, Value = "ko" }]);
        LanguageTerm[] korean = [new("korean", "ko")];

        Assert.Equal(["Korean"], AskReelStandingFilters.CollidingLanguages(englishOnly, korean));
        Assert.Empty(AskReelStandingFilters.CollidingLanguages(allowsKorean, korean));
        Assert.Empty(AskReelStandingFilters.CollidingLanguages(englishOnly, []));
        Assert.Equal(
            "Your conditions only allow titles in English, so nothing in Korean can appear. You can change that under Conditions.",
            AskReelStandingFilters.Message([], [], false, englishOnly, ["Korean"], false));
    }

    // ── The crowd floor ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("so-bad-it's-good movies rated under 5", "6.0", true)]
    [InlineData("anything rated below 6", "6.0", true)]
    [InlineData("films with a rating less than 4.5", "6.0", true)]
    [InlineData("movies rated under 7", "6.0", false)]
    [InlineData("so-bad-it's-good movies", "6.0", false)]
    [InlineData("movies rated under 5", null, false)]
    public void Asking_for_titles_rated_under_the_crowd_floor_collides(string message, string? floor, bool collides)
    {
        var rules = FilterRuleSet.FromRows(floor is null ? [] : [new ContentFilter { Kind = FilterKind.MinCrowdRating, Value = floor }]);

        Assert.Equal(collides, AskReelStandingFilters.CrowdFloorCollides(rules, message));
    }

    // ── A genre the user named that the account excludes ────────────────────────────────────

    private static readonly FilterRuleSet ExcludesAnimationAndComedy = FilterRuleSet.FromRows(
    [
        new ContentFilter { Kind = FilterKind.ExcludeGenre, Value = "animation" },
        new ContentFilter { Kind = FilterKind.ExcludeGenre, Value = "comedy" },
    ]);

    private static ConversationState Planned(params string[] concepts) =>
        ConversationState.Empty with { Hard = HardConstraints.None with { Concepts = [.. concepts.Select(c => new ConceptTerm(c, [], []))] } };

    [Fact]
    public void An_excluded_genre_the_user_named_becomes_a_requirement_the_planner_dropped()
    {
        var next = AskReelEngine.WithNamedExcludedGenres(ExcludesAnimationAndComedy, Planned("kids"), "animated movies for kids");

        Assert.Contains(next.Hard.Concepts, c => c.Genres.SequenceEqual(["animation"]));
        Assert.Contains(next.Hard.Concepts, c => c.UserWords == "kids");
    }

    [Theory]
    // A mood that only suggests the genre is not the genre.
    [InlineData("something genuinely funny")]
    [InlineData("something to make me laugh")]
    // A genre the account does not exclude adds nothing.
    [InlineData("a western for tonight")]
    public void A_mood_or_an_allowed_genre_adds_no_requirement(string message)
    {
        var planned = Planned("tonight");

        Assert.Same(planned, AskReelEngine.WithNamedExcludedGenres(ExcludesAnimationAndComedy, planned, message));
    }

    [Fact]
    public void A_genre_word_inside_a_topic_the_request_holds_is_the_topics_word()
    {
        // "stand-up comedy specials": the topic is stand-up comedy, not the whole comedy shelf.
        var planned = Planned("stand-up comedy");

        Assert.Same(planned, AskReelEngine.WithNamedExcludedGenres(ExcludesAnimationAndComedy, planned, "stand-up comedy specials"));
        Assert.Contains(
            AskReelEngine.WithNamedExcludedGenres(ExcludesAnimationAndComedy, Planned("specials"), "stand-up comedy specials").Hard.Concepts,
            c => c.Genres.SequenceEqual(["comedy"]));
    }

    [Fact]
    public void An_excluded_genre_already_required_or_ruled_out_by_the_user_is_left_alone()
    {
        var required = ConversationState.Empty with { Hard = HardConstraints.None with { Concepts = [new ConceptTerm("animation", [], ["animation"])] } };
        var negated = Planned("heist") with { UserNegations = ["animation"] };

        Assert.Single(AskReelEngine.WithNamedExcludedGenres(ExcludesAnimationAndComedy, required, "animated films").Hard.Concepts);
        Assert.Same(negated, AskReelEngine.WithNamedExcludedGenres(ExcludesAnimationAndComedy, negated, "heist movies, no animation"));
    }

    // ── A genre the account never touched ───────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<MediaType, (int History, IReadOnlySet<string> Genres)> ShowsWithNoReality =
        new Dictionary<MediaType, (int, IReadOnlySet<string>)>
        {
            [MediaType.Show] = (252, new HashSet<string>(StringComparer.Ordinal) { "drama", "crime", "comedy" }),
            [MediaType.Movie] = (20, new HashSet<string>(StringComparer.Ordinal) { "action" }),
        };

    [Theory]
    [InlineData(MediaType.Show, new[] { "reality" }, false)]
    [InlineData(MediaType.Show, new[] { "reality", "talk-show" }, false)]
    // One genre the account knows is enough.
    [InlineData(MediaType.Show, new[] { "reality", "crime" }, true)]
    // A title with no genres cannot be read either way.
    [InlineData(MediaType.Show, new string[0], true)]
    // Twenty films are too few for "never" to mean anything.
    [InlineData(MediaType.Movie, new[] { "western" }, true)]
    public void A_title_only_in_genres_the_account_never_touched_is_not_engaged(MediaType media, string[] genres, bool engaged) =>
        Assert.Equal(engaged, AskReelEngine.Engaged(ShowsWithNoReality, new Title { MediaType = media, Genres = genres }));

    // ── The judge re-asks about a title it skipped ──────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_title_the_judge_skipped_is_asked_about_once_more_and_comes_back_judged(int skipped)
    {
        var titles = Enumerable.Range(0, 16)
            .Select(i => new JudgeTitle(Guid.NewGuid(), $"Title {i}", 2020, MediaType.Movie, ["drama"], ["heist"], "A heist.", [], null))
            .ToList();
        var skip = titles.Skip(5).Take(skipped).Select(t => t.TitleId).ToHashSet();
        var model = new SkippingJudgeModel(skip);
        var judge = new RequestJudge(model, new MemoryCache(new MemoryCacheOptions()), NullLogger<RequestJudge>.Instance);

        var verdicts = new List<JudgeVerdict>();
        await foreach (var v in judge.JudgeAsync(new JudgeInput(Guid.NewGuid(), "Request: heist movies", "persona", titles), CancellationToken.None))
        {
            verdicts.Add(v);
        }

        Assert.Equal(16, verdicts.Count);
        Assert.All(verdicts, v => Assert.True(v.Judged));
        Assert.All(skip, id => Assert.Equal(2, model.SendsOf(id)));
    }

    /// <summary>Answers every batch, but leaves out the titles in <c>skipFirstTime</c> the first time each is sent.</summary>
    private sealed class SkippingJudgeModel(IReadOnlySet<Guid> skipFirstTime) : IRequestJudgeModel
    {
        private readonly Dictionary<Guid, int> _sends = [];

        public bool IsAvailable => true;

        public string ModelId => "skipping";

        public string CacheSalt => "skipping";

        public int SendsOf(Guid id)
        {
            lock (_sends)
            {
                return _sends.GetValueOrDefault(id);
            }
        }

        public Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
        {
            var answered = new List<JudgeVerdict>();
            lock (_sends)
            {
                foreach (var title in input.Titles)
                {
                    var sends = _sends[title.TitleId] = _sends.GetValueOrDefault(title.TitleId) + 1;
                    if (sends == 1 && skipFirstTime.Contains(title.TitleId))
                    {
                        continue;
                    }

                    answered.Add(new JudgeVerdict(title.TitleId, Satisfies.Yes, 0.8, "You get a heist.", "keyword: heist", ResultSource.Llm, ResultSource.Llm));
                }
            }

            return Task.FromResult(new JudgeBatchResult(answered, LlmCallMeta.None, false));
        }
    }
}
