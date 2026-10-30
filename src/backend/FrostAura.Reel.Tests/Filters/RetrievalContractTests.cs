using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The database-free half of the retrieval contract: how a v1 intent reads as hard constraints, how
/// stored media scopes degrade, how evidence names the tag that matched, and the plain-words labels a
/// relaxation chip carries. The counting half is pinned on Postgres in <c>SupplyReporterTests</c>.
/// </summary>
public class RetrievalContractTests
{
    [Fact]
    public void Intent_keywords_become_one_concept_whose_labels_are_alternatives()
    {
        var intent = Intent(keywords: ["Ninja", " shinobi ", "ninja"], genres: ["action"]);

        var hard = IntentConstraints.FromIntent(intent);

        var concept = Assert.Single(hard.Concepts);
        Assert.Equal(["ninja", "shinobi"], concept.CatalogueLabels);
        // Beside a keyword, a genre is the interpreter's flavour guess, not a requirement.
        Assert.Empty(concept.Genres);
        Assert.Equal("ninja shinobi", concept.UserWords);
    }

    [Fact]
    public void Intent_genres_become_the_concept_only_when_there_are_no_keywords()
    {
        var hard = IntentConstraints.FromIntent(Intent(genres: ["Horror", "thriller"]), userWords: "scary films");

        var concept = Assert.Single(hard.Concepts);
        Assert.Empty(concept.CatalogueLabels);
        Assert.Equal(["horror", "thriller"], concept.Genres);
        Assert.Equal("scary films", concept.UserWords);
    }

    /// <summary>
    /// HREV8-ASK-01: a genres-only intent stays a flavour list OR-ed in one concept unless the user's own
    /// words are a genre phrase without "or" — then each genre they named is its own concept and they AND.
    /// With no words of the user's, the stand-in names the OR it counts.
    /// </summary>
    [Theory]
    [InlineData("romantic comedies", new[] { "romance", "comedy" }, "romantic comedies:romance|comedies:comedy", false)]
    [InlineData("comedy romance", new[] { "romance", "comedy" }, "comedy romance:comedy|romance:romance", false)]
    [InlineData("romantic comedies", new[] { "comedy", "drama" }, "romantic comedies:romance|comedies:comedy", false)]
    [InlineData("romance or comedy", new[] { "romance", "comedy" }, "romance or comedy:romance,comedy", false)]
    [InlineData("scary films", new[] { "horror", "thriller" }, "scary films:horror,thriller", false)]
    [InlineData("comedies", new[] { "comedy", "romance" }, "comedies:comedy,romance", false)]
    [InlineData(null, new[] { "romance", "comedy" }, "romance or comedy:romance,comedy", true)]
    [InlineData(null, new[] { "horror", "thriller", "mystery" }, "horror, thriller or mystery:horror,thriller,mystery", true)]
    [InlineData(null, new[] { "comedy" }, "comedy:comedy", true)]
    public void Intent_genres_alone_and_only_when_the_user_names_them_as_one_phrase(
        string? userWords, string[] genres, string expected, bool standIn)
    {
        var hard = IntentConstraints.FromIntent(Intent(genres: genres), userWords: userWords);

        Assert.Equal(expected, string.Join('|', hard.Concepts.Select(c => $"{c.UserWords}:{string.Join(',', c.Genres)}")));
        Assert.All(hard.Concepts, c => Assert.Empty(c.CatalogueLabels));
        Assert.All(hard.Concepts, c => Assert.Equal(standIn, c.StandInWords));
    }

    [Fact]
    public void Intent_with_no_topic_has_no_concepts_and_negations_are_never_constraints()
    {
        var intent = Intent() with { ExcludeGenres = ["romance"], ExcludeKeywords = ["ghost"] };

        var hard = IntentConstraints.FromIntent(intent);

        Assert.Empty(hard.Concepts);
        Assert.False(hard.HasConcepts);
        Assert.Null(hard.Years);
    }

    [Fact]
    public void Intent_people_years_runtime_and_media_carry_over()
    {
        var intent = Intent(keywords: ["heist"]) with
        {
            MinYear = 1990,
            MaxYear = 1999,
            MaxRuntimeMinutes = 110,
            MediaTypes = [MediaType.Show],
            People = [new PersonQuery(" Michael Mann ", PersonRole.Director), new PersonQuery(" ", PersonRole.Any)],
        };

        var hard = IntentConstraints.FromIntent(intent);

        Assert.Equal(new YearRange(1990, 1999), hard.Years);
        Assert.Equal(110, hard.MaxRuntimeMinutes);
        Assert.Equal(MediaScope.Shows, hard.Media);
        var person = Assert.Single(hard.People);
        Assert.Equal(new PersonConstraint("Michael Mann", null, PersonRole.Director), person);

        // The request's own media filter beats the interpreter's guess.
        Assert.Equal(MediaScope.Movies, IntentConstraints.FromIntent(intent, [MediaType.Movie]).Media);
        Assert.Equal(MediaScope.Any, IntentConstraints.FromIntent(intent, [MediaType.Movie, MediaType.Show]).Media);
    }

    [Theory]
    [InlineData("Movies", MediaScope.Movies)]
    [InlineData("shows", MediaScope.Shows)]
    [InlineData("Any", MediaScope.Any)]
    [InlineData("Documentaries", MediaScope.Any)]
    [InlineData("7", MediaScope.Any)]
    [InlineData("", MediaScope.Any)]
    [InlineData(null, MediaScope.Any)]
    public void Stored_media_scopes_map_unknown_values_to_any(string? stored, MediaScope expected) =>
        Assert.Equal(expected, MediaScopes.Parse(stored));

    [Fact]
    public void Evidence_names_the_tag_that_matched_not_the_label()
    {
        var title = new Title { Keywords = ["female ninja", "ninja", "katana"], Genres = ["action", "drama"] };
        var hard = HardConstraints.None with
        {
            Concepts = [new ConceptTerm("ninja", ["ninja"], ["action"]), new ConceptTerm("swords", [], ["horror"])],
            People = [new PersonConstraint(" Sho Kosugi ", null, PersonRole.Cast)],
        };

        Assert.Equal(
            ["keyword:female ninja", "keyword:ninja", "genre:action", "person:Sho Kosugi/cast"],
            HardConstraintFilter.EvidenceFor(title, hard));
    }

    [Theory]
    [InlineData("3 Ninjas", "ninja", true)]
    [InlineData("3 Ninjas: High Noon at Mega Mountain", "ninja", true)]
    [InlineData("Alien vs. Ninja", "ninja", true)]
    [InlineData("Batman Ninja vs. Yakuza League", "ninja", true)]
    [InlineData("NINJA ASSASSIN", "ninja", true)]
    [InlineData("Ninja's Creed", "ninja", true)]
    [InlineData("Ninjababy", "ninja", false)]
    [InlineData("The Lego Ninjago Movie", "ninja", false)]
    [InlineData("Shinobi: Heart Under Blade", "shinobi", true)]
    [InlineData("The Witches", "witch", true)]
    [InlineData("Switchblade", "witch", false)]
    [InlineData("Lost at Sea Again", "lost at sea", true)]
    [InlineData("Lost at Seaside", "lost at sea", false)]
    [InlineData("Rocky 2.0", "2.0", true)]
    [InlineData("Rocky 210", "2.0", false)]
    [InlineData("", "ninja", false)]
    public void The_name_arm_matches_a_label_as_a_whole_word_optionally_plural(string name, string label, bool expected) =>
        Assert.Equal(expected, HardConstraintFilter.NameMatches(name, label));

    [Theory]
    [InlineData("heist", "heist", true)]
    [InlineData("bank heist", "heist", true)]
    [InlineData("heists", "heist", true)]
    [InlineData("atheist", "heist", false)]
    [InlineData("female ninja", "ninja", true)]
    [InlineData("time traveler", "time travel", true)]
    [InlineData("overtime travel", "time travel", false)]
    [InlineData("the 1.5 rule", "1.5", true)]
    [InlineData("the 105 rule", "1.5", false)]
    // HREV3-RET-04: the label ends at a word end, allowing only an inflection — "alienation" is not alien,
    // "assassination" is not an assassin, but "aliens", "shipwrecked" and "treasure hunter" still match.
    [InlineData("aliens", "alien", true)]
    [InlineData("alien invasion", "alien", true)]
    [InlineData("alienation", "alien", false)]
    [InlineData("parental alienation", "alien", false)]
    [InlineData("assassination attempt", "assassin", false)]
    [InlineData("assassins", "assassin", true)]
    [InlineData("shipwrecked", "shipwreck", true)]
    [InlineData("treasure hunter", "treasure hunt", true)]
    [InlineData("treasure hunting", "treasure hunt", true)]
    [InlineData("submariner", "submarine", true)]
    [InlineData("world war iii", "world war ii", false)]
    [InlineData("pop culture", "cult", false)]
    [InlineData("cults", "cult", true)]
    [InlineData("mobile phone", "mob", false)]
    [InlineData("c++ programming", "c++", true)]
    [InlineData("shoujo-ai", "ai", false)]
    [InlineData("rogue ai", "ai", true)]
    public void The_keyword_arm_matches_a_label_from_a_word_start(string keyword, string label, bool expected) =>
        Assert.Equal(expected, HardConstraintFilter.KeywordMatches(keyword, label));

    [Theory]
    [InlineData("alien", "alienation", false)]
    [InlineData("alien", "parental alienation", false)]
    [InlineData("alien", "aliens", true)]
    [InlineData("alien", "alien invasion", true)]
    [InlineData("assassin", "assassination", false)]
    [InlineData("space", "spacewalk", false)]
    [InlineData("witch", "witches", true)]
    [InlineData("heist", "heist gone wrong", true)]
    [InlineData("time travel", "time traveler", true)]
    [InlineData("mountaineer", "mountaineering", true)]
    [InlineData("mob", "mobile phone", false)]
    [InlineData("mob", "mobs", true)]
    // A two-letter stem is no word of a hyphenated compound: "shoujo-ai" is girls' love, not AI.
    [InlineData("ai", "shoujo-ai", false)]
    [InlineData("ai", "rogue ai", true)]
    [InlineData("ai", "ai-powered film", true)]
    [InlineData("noir", "neo-noir", true)]
    public void A_stem_reaches_vocabulary_labels_only_up_to_a_word_end_or_an_inflection(string stem, string label, bool expected) =>
        Assert.Equal(expected, Application.Search.Retrieval.Concepts.KeywordVocabulary.Expand([new(label, 1)], stem).Any());

    [Theory]
    [InlineData("witch", "witchcraft")]
    [InlineData("space", "spacecraft")]
    public void A_compound_the_word_end_bound_no_longer_reaches_is_kept_as_a_synonym(string stem, string compound)
    {
        // Open at the end, "witch" reached "witchcraft" (60 titles) and "space" "spacecraft" (104); bounded,
        // they are reached only because the synonym table names them.
        Assert.False(Application.Search.Retrieval.Concepts.KeywordVocabulary.Expand([new(compound, 1)], stem).Any());
        Assert.Contains(compound, Application.Search.Retrieval.Concepts.ConceptSynonyms.GroupOf(stem));
    }

    [Fact]
    public void The_keyword_arm_pattern_is_word_bounded_with_inflections_and_literal()
    {
        Assert.Equal(@"\mheist(e?s|e?rs?|ings?|e?d)?\M", HardConstraintFilter.KeywordPattern("heist"));
        // A short stem is a whole word, plural allowed — as the vocabulary expands it.
        Assert.Equal(@"\mmob(e?s)?\M", HardConstraintFilter.KeywordPattern("mob"));
        Assert.Equal(@"(?<![[:alnum:]_-])ai(e?s)?\M", HardConstraintFilter.KeywordPattern("ai"));
        // A label ending in punctuation cannot be bounded by \M (it needs a word character before it).
        Assert.Equal(@"\mc\+\+", HardConstraintFilter.KeywordPattern("c++"));
    }

    [Fact]
    public void The_name_arm_pattern_matches_the_label_literally_in_postgres()
    {
        Assert.Equal(@"\mninja(e?s)?\M", HardConstraintFilter.NamePattern("ninja"));
        Assert.Equal(@"\mlost at sea(e?s)?\M", HardConstraintFilter.NamePattern("lost at sea"));
        // Every ARE metacharacter is escaped; letters and digits never are (a backslash before one
        // would start an ARE escape such as \d).
        Assert.Equal(@"\mc\+\+ \(2\.0\) \[a\|b\] \{x\} \^\$\?\*\\(e?s)?\M", HardConstraintFilter.NamePattern(@"c++ (2.0) [a|b] {x} ^$?*\"));
        Assert.Equal(@"\mdéjà vu(e?s)?\M", HardConstraintFilter.NamePattern("déjà vu"));
    }

    [Fact]
    public void Evidence_names_a_label_found_only_in_the_title_name()
    {
        var hard = HardConstraints.None with { Concepts = [new ConceptTerm("ninja", ["ninja", "shinobi"], [])] };

        Assert.Equal(["name:ninja"], HardConstraintFilter.EvidenceFor(new Title { Name = "3 Ninjas Kick Back", Keywords = ["martial arts"] }, hard));
        Assert.Equal(
            ["keyword:ninja", "name:ninja"],
            HardConstraintFilter.EvidenceFor(new Title { Name = "Ninja Scroll", Keywords = ["ninja", "anime"] }, hard));
        Assert.Empty(HardConstraintFilter.EvidenceFor(new Title { Name = "Ninjababy", Keywords = ["pregnancy"] }, hard));
    }

    [Fact]
    public void A_synonym_matches_by_keyword_but_never_by_name()
    {
        // HREV3-RET-A: "ronin" is a samurai word only as a keyword. Ronin (1998) carries no samurai tag, and
        // matching the synonym against names told the founder he had seen 9 samurai films, not 8.
        var samurai = HardConstraints.None with { Concepts = [new ConceptTerm("samurai", ["samurai", "ronin"], [])] };
        Assert.Empty(HardConstraintFilter.EvidenceFor(new Title { Name = "Ronin", Keywords = ["mercenary"] }, samurai));
        Assert.Equal(["keyword:ronin"], HardConstraintFilter.EvidenceFor(new Title { Name = "Harakiri", Keywords = ["ronin"] }, samurai));
        Assert.Equal(["name:samurai"], HardConstraintFilter.EvidenceFor(new Title { Name = "Seven Samurai", Keywords = ["japan"] }, samurai));

        var courtroom = HardConstraints.None with
        {
            Concepts = [new ConceptTerm("courtroom movies", ["courtroom", "court case", "legal drama", "trial", "lawyer"], [])],
        };
        Assert.Empty(HardConstraintFilter.EvidenceFor(new Title { Name = "Maze Runner: The Scorch Trials", Keywords = ["maze"] }, courtroom));
        Assert.Equal(["keyword:trial"], HardConstraintFilter.EvidenceFor(new Title { Name = "Anatomy of a Fall", Keywords = ["trial"] }, courtroom));

        var ai = HardConstraints.None with
        {
            Concepts = [new ConceptTerm("artificial intelligence", ["artificial intelligence", "ai", "robot", "android"], [])],
        };
        Assert.Empty(HardConstraintFilter.EvidenceFor(new Title { Name = "Vacanze ai Caraibi", Keywords = ["holiday"] }, ai));
        Assert.Empty(HardConstraintFilter.EvidenceFor(new Title { Name = "Robot & Frank", Keywords = ["heist"] }, ai));
        Assert.Equal(["keyword:ai"], HardConstraintFilter.EvidenceFor(new Title { Name = "Her", Keywords = ["ai"] }, ai));
    }

    [Theory]
    [InlineData("samurai", new[] { "samurai", "ronin" }, new[] { "samurai" })]
    [InlineData("samurai movies", new[] { "samurai", "ronin" }, new[] { "samurai" })]
    [InlineData("ninjas", new[] { "ninja", "shinobi", "kunoichi" }, new[] { "ninja" })]
    [InlineData("robberies", new[] { "heist", "bank robbery", "robbery" }, new[] { "robbery" })]
    [InlineData("romantic heist", new[] { "heist", "bank robbery", "robbery" }, new[] { "heist" })]
    [InlineData("ai robot", new[] { "artificial intelligence", "ai", "robot", "android" }, new[] { "robot" })]
    // The user's own "AI" is two letters: in a name it is the Italian "ai" far more often than the topic.
    [InlineData("AI movies", new[] { "artificial intelligence", "ai", "robot", "android" }, new string[0])]
    [InlineData("disaster movies", new[] { "natural disaster", "disaster", "volcano", "earthquake", "tornado", "tsunami" }, new[] { "disaster" })]
    [InlineData("natural disaster movies", new[] { "natural disaster", "disaster", "volcano", "earthquake", "tornado", "tsunami" }, new[] { "natural disaster", "disaster" })]
    [InlineData("alien invasion movies", new[] { "alien invasion", "alien", "extraterrestrial" }, new[] { "alien invasion", "alien" })]
    [InlineData(" Time Loop ", new string[0], new[] { "time loop" })]
    public void The_name_arm_reads_only_the_users_own_words(string words, string[] labels, string[] expected) =>
        Assert.Equal(expected, HardConstraintFilter.NameLabelsOf(new ConceptTerm(words, labels, [])));

    /// <summary>
    /// HREV3-RET-A through the v1 entry point: a caller with no words of the user's — the v2 state
    /// rebuilt from a v1 reading, a v1 topic no word of the user's anchored — reads the planner's
    /// keywords as a stand-in, and a curated synonym among them never reaches a name. Joined into
    /// "samurai ronin" they once said "ronin" for the user, and Ronin (1998) counted as a samurai film
    /// he had seen. The concept's own name still does ("samurai", "ninja", "vampires"): reading none of
    /// them dropped 7 of the founder's 70 ninja movies, the ones found by name alone.
    /// </summary>
    [Theory]
    [InlineData(null, new[] { "samurai", "ronin" }, new[] { "samurai" })]
    [InlineData("", new[] { "samurai", "ronin" }, new[] { "samurai" })]
    [InlineData("  ", new[] { "ronin", "samurai" }, new[] { "samurai" })]
    [InlineData(null, new[] { "ronin" }, new string[0])]
    [InlineData(null, new[] { "ninja" }, new[] { "ninja" })]
    [InlineData(null, new[] { "ninja", "shinobi", "kunoichi" }, new[] { "ninja" })]
    [InlineData(null, new[] { "heist", "bank robbery", "robbery" }, new[] { "heist" })]
    [InlineData(null, new[] { "courtroom", "trial" }, new[] { "courtroom" })]
    [InlineData(null, new[] { "artificial intelligence", "ai", "robot" }, new[] { "artificial intelligence" })]
    [InlineData(null, new[] { "natural disaster", "volcano", "tsunami" }, new[] { "natural disaster" })]
    [InlineData(null, new[] { "vampires" }, new[] { "vampires" })]
    [InlineData(null, new[] { "serial killer" }, new[] { "serial killer" })]
    [InlineData("samurai films", new[] { "samurai", "ronin" }, new[] { "samurai" })]
    [InlineData("ronin", new[] { "samurai", "ronin" }, new[] { "ronin" })]
    public void Intent_keywords_without_the_users_words_never_name_a_synonym(string? userWords, string[] keywords, string[] expected)
    {
        var concept = Assert.Single(IntentConstraints.FromIntent(Intent(keywords: keywords), userWords: userWords).Concepts);

        Assert.Equal(expected, HardConstraintFilter.NameLabelsOf(concept));
        // The keyword arm is untouched: every keyword still counts as a tag.
        Assert.Equal(keywords, concept.CatalogueLabels);
    }

    [Fact]
    public void A_stand_in_still_names_the_concept_and_the_flag_survives_every_reading()
    {
        var standIn = Assert.Single(IntentConstraints.FromIntent(Intent(keywords: ["samurai", "ronin"])).Concepts);
        var said = Assert.Single(IntentConstraints.FromIntent(Intent(keywords: ["samurai", "ronin"]), userWords: "samurai films").Concepts);
        var genres = Assert.Single(IntentConstraints.FromIntent(Intent(genres: ["romance", "comedy"])).Concepts);

        // The planner, the semantic arm and TMDB discovery still read the keywords as the concept's words.
        Assert.Equal("samurai ronin", standIn.UserWords);
        Assert.True(standIn.StandInWords);
        Assert.False(said.StandInWords);
        Assert.True(genres.StandInWords);

        // The stored conversation state keeps it, and a row written before the field reads as the user's words.
        var state = new Application.Search.AskReel.Engine.ConversationState(
            1, HardConstraints.None with { Concepts = [standIn, said] }, SoftPreferences.None, [], [], [], [], null);
        var json = Application.Search.AskReel.Engine.ConversationStateJson.Write(state);
        var back = Application.Search.AskReel.Engine.ConversationStateJson.Read(json)!;
        Assert.Equal([true, false], back.Hard.Concepts.Select(c => c.StandInWords));
        Assert.Contains("\"standInWords\":true", json, StringComparison.Ordinal);
        var legacy = Application.Search.AskReel.Engine.ConversationStateJson.Read(
            json.Replace(",\"standInWords\":true", string.Empty, StringComparison.Ordinal)
                .Replace(",\"standInWords\":false", string.Empty, StringComparison.Ordinal))!;
        Assert.Equal([false, false], legacy.Hard.Concepts.Select(c => c.StandInWords));

        // The static genre reading splits a stand-in's genre words into stand-ins too.
        var split = Application.Search.Retrieval.Concepts.ConceptResolver.GenreReading(new ConceptTerm("romantic comedy", [], [], StandInWords: true))!;
        Assert.Equal(2, split.Count);
        Assert.All(split, c => Assert.True(c.StandInWords));
    }

    [Theory]
    [InlineData("ronin", true)]
    [InlineData("robberies", true)]
    [InlineData("volcano", true)]
    [InlineData("ai", true)]
    [InlineData("samurai", false)]
    [InlineData("vampires", false)]
    [InlineData("spies", false)]
    [InlineData("serial killer", false)]
    public void A_synonym_is_a_group_member_other_than_its_name(string stem, bool synonym) =>
        Assert.Equal(synonym, Application.Search.Retrieval.Concepts.ConceptSynonyms.IsSynonym(stem));

    [Fact]
    public void A_concept_without_labels_matches_on_the_users_own_words()
    {
        Assert.Equal(["time loop"], HardConstraintFilter.LabelsOf(new ConceptTerm(" Time Loop ", [], [])));
        Assert.Equal(["shinobi", "ninja"], HardConstraintFilter.LabelsOf(new ConceptTerm("ninja", ["Shinobi", "ninja", ""], [])));
    }

    [Fact]
    public void Clearing_floor_is_what_is_left_of_eligible()
    {
        var report = new SupplyReport(20, 6, 2, 7, 5, 1, 2, [], 3, 50, 6.0m, KnockoutsMeasured: true);

        Assert.Equal(2, report.ClearingFloor);
        Assert.Equal(report.MatchingInCatalogue, report.AlreadySeen + report.SavedOrDismissed + report.ExcludedByConditions + report.Eligible);
        // Nothing shown: everything clearing the bar is still on offer.
        Assert.Equal(0, report.AlreadyShown);
        Assert.Equal(2, report.Offerable);
    }

    [Fact]
    public void Already_shown_titles_are_cut_from_the_offerable_supply_and_nothing_else()
    {
        var report = new SupplyReport(20, 6, 2, 7, 5, 1, 2, [], 3, 50, 6.0m, KnockoutsMeasured: true, AlreadyShown: 2);

        Assert.Equal(5, report.Eligible);
        Assert.Equal(2, report.ClearingFloor);
        Assert.Equal(0, report.Offerable);
        Assert.Equal(report.ClearingFloor, report.AlreadyShown + report.Offerable);
    }

    [Theory]
    [InlineData(new[] { "keyword:shark" }, new[] { "fts:overview" }, new[] { "name:shark", "fts:overview" }, 2, 1)]
    [InlineData(new[] { "fts:overview", "mood:dark" }, new[] { "fts:overview", "steer:darker" }, new[] { "fts:overview" }, 0, 3)]
    [InlineData(new[] { "keyword:shark" }, new[] { "fts:themes", "fts:overview" }, new[] { "genre:horror" }, 3, 0)]
    [InlineData(new[] { "mood:dark" }, new[] { "reference:jaws" }, new[] { "fts:name" }, 3, 0)]
    public void Overview_only_leads_are_counted_apart_from_the_tagged_matches(
        string[] first, string[] second, string[] third, int tagged, int prose)
    {
        // HREV5-RET-04: "shark movies" found Sarah's Oil ("greedy oil sharks") in its overview. A reply
        // that says "3 shark films" must not count a synopsis lead as a shark film nobody checked.
        var result = new RetrievalResult(
            [Lead(first), Lead(second), Lead(third)],
            new SupplyReport(3, 0, 0, 0, 3, 0, 0, [], 0, 50, null, KnockoutsMeasured: true),
            StageTimings.Empty);

        Assert.Equal(prose, result.ProseLeads);
        Assert.Equal(tagged, result.TaggedMatches);
        Assert.Equal(result.Candidates.Count, result.TaggedMatches + result.ProseLeads);
    }

    [Fact]
    public void An_empty_retrieval_has_no_tagged_matches_and_no_prose_leads()
    {
        var result = new RetrievalResult([], new SupplyReport(0, 0, 0, 0, 0, 0, 0, [], 0, 50, null, KnockoutsMeasured: true), StageTimings.Empty);

        Assert.Equal(0, result.ProseLeads);
        Assert.Equal(0, result.TaggedMatches);
    }

    private static Candidate Lead(string[] evidence) => new(Guid.NewGuid(), 0d, null, evidence);

    [Theory]
    [InlineData(FilterKind.ExcludeGenre, "comedy", "excluded genre: comedy")]
    [InlineData(FilterKind.IncludeGenre, "drama", "only these genres: drama")]
    [InlineData(FilterKind.ExcludeKeyword, "zombie", "excluded keyword: zombie")]
    [InlineData(FilterKind.Language, "en", "English only")]
    [InlineData(FilterKind.Language, "en,fr", "languages: en,fr")]
    [InlineData(FilterKind.MinCrowdRating, "6.0", "crowd rating at least 6.0")]
    [InlineData(FilterKind.MinYear, "2000", "released 2000 or later")]
    [InlineData(FilterKind.MaxYear, "2010", "released 2010 or earlier")]
    public void Knockout_labels_say_the_rule_in_plain_words(FilterKind kind, string value, string expected) =>
        Assert.Equal(expected, SupplyReporter.LabelFor(new FilterRule(kind, value)));

    private static SearchIntent Intent(IReadOnlyList<string>? keywords = null, IReadOnlyList<string>? genres = null) =>
        new(genres ?? [], keywords ?? [], [], string.Empty, null, null);
}
