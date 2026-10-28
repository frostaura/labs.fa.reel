using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The hybrid retriever on real Postgres (program §4.7): concepts resolve through the vocabulary and
/// its synonyms, the full-text arm finds what the tags miss, facets act as filters on the semantic
/// arm, and every arm stays inside eligibility, the turn's own rejections and the exclude list.
/// Every assertion is about the titles this class seeded; the shared catalogue can only add
/// candidates around them, never change what these ones are. Embedding rows are removed and the
/// vector index vacuumed on teardown (<see cref="EmbeddedCatalogTestBase"/>).
/// </summary>
[Collection("postgres")]
public class HybridRetrieverTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    [Fact]
    public async Task A_concept_request_returns_only_titles_with_catalogue_evidence()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        var accountId = await SeedAccountAsync();
        var conceptVector = StubVector("ninja");

        var tagged = await SeedTitleAsync(t => t.Keywords = ["female ninja"], conceptVector);
        var synonym = await SeedTitleAsync(t => t.Keywords = ["kunoichi"]);
        var overviewOnly = await SeedTitleAsync(t => t.Overview = $"A young shinobi {tag} trains in secret.");
        var nearButOffTopic = await SeedTitleAsync(t => t.Keywords = ["office"], conceptVector);
        var watched = await SeedTitleAsync(t => t.Keywords = ["ninja"]);
        var comedy = await SeedTitleAsync(t => { t.Keywords = ["ninja"]; t.Genres = ["comedy"]; });
        var shown = await SeedTitleAsync(t => t.Keywords = ["ninja"]);
        await SeedWatchAsync(accountId, watched);

        var hard = new HardConstraints([new ConceptTerm("ninja movies", [], [])], [], null, null, MediaScope.Any);
        var result = await RetrieveAsync(new RetrievalQuery(accountId, hard, SoftPreferences.None, [shown], 200)
        {
            Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic,
            ExcludeGenres = ["comedy"],
        });
        var byId = result.Candidates.ToDictionary(c => c.TitleId);

        Assert.Contains(tagged, byId.Keys);
        Assert.Contains("keyword:female ninja", byId[tagged].Evidence);
        Assert.NotNull(byId[tagged].Similarity);
        Assert.Contains(synonym, byId.Keys);
        Assert.Contains("keyword:kunoichi", byId[synonym].Evidence);
        Assert.Contains(overviewOnly, byId.Keys);
        Assert.Contains("fts:overview", byId[overviewOnly].Evidence);

        // The semantic arm ranks; it never admits. The nearest title of all carries no ninja evidence.
        Assert.DoesNotContain(nearButOffTopic, byId.Keys);
        Assert.DoesNotContain(watched, byId.Keys);
        Assert.DoesNotContain(comedy, byId.Keys);
        Assert.DoesNotContain(shown, byId.Keys);
        Assert.All(result.Candidates, c => Assert.Contains(c.Evidence, e => e.StartsWith("keyword:", StringComparison.Ordinal)
            || e.StartsWith("name:", StringComparison.Ordinal) || e.StartsWith("genre:", StringComparison.Ordinal)
            || e.StartsWith("fts:", StringComparison.Ordinal)));
        Assert.Equal(result.Candidates.OrderByDescending(c => c.FusedScore).Select(c => c.TitleId), result.Candidates.Select(c => c.TitleId));
        Assert.Empty(result.SkippedArms);
    }

    [Fact]
    public async Task A_year_window_the_user_asked_for_needs_a_known_date()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // HREV5-ASK-03: "horror movies" then "only from the 90s" showed Glenville 2, Doppelgänger and
        // Glenville 3 — no year, no release date, no air date — as 90s horror. A date the title does
        // not have cannot satisfy a window the user asked for; each fallback date still does.
        var topic = $"decade{Guid.NewGuid():N}"[..18];
        var accountId = await SeedAccountAsync();
        var byYear = await SeedTitleAsync(t => { t.Keywords = [topic]; t.Year = 1995; });
        var byRelease = await SeedTitleAsync(t => { t.Keywords = [topic]; t.Year = null; t.ReleasedAt = new DateTime(1996, 5, 1, 0, 0, 0, DateTimeKind.Utc); });
        var byAirDate = await SeedTitleAsync(t =>
        {
            t.Keywords = [topic];
            t.MediaType = MediaType.Show;
            t.Year = null;
            t.FirstAiredAt = new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        });
        var undated = await SeedTitleAsync(t => { t.Keywords = [topic]; t.Year = null; t.ReleasedAt = null; t.FirstAiredAt = null; });
        var undatedFewVotes = await SeedTitleAsync(t =>
        {
            t.Keywords = [topic];
            t.Year = null;
            t.ReleasedAt = null;
            t.FirstAiredAt = null;
            t.TmdbVoteCount = 1;
        });
        var tooLate = await SeedTitleAsync(t => { t.Keywords = [topic]; t.Year = 2000; });

        var decade = new HardConstraints([new ConceptTerm(topic, [], [])], [], new YearRange(1990, 1999), null, MediaScope.Any);
        var result = await RetrieveAsync(new RetrievalQuery(accountId, decade, SoftPreferences.None, [], 50) { Arms = RetrievalArms.Catalogue });
        var ids = result.Candidates.Select(c => c.TitleId).ToList();

        Assert.Contains(byYear, ids);
        Assert.Contains(byRelease, ids);
        Assert.Contains(byAirDate, ids);
        Assert.DoesNotContain(undated, ids);
        Assert.DoesNotContain(undatedFewVotes, ids);
        Assert.DoesNotContain(tooLate, ids);
        Assert.Equal(3, result.Supply.MatchingInCatalogue);

        // An open-ended window ("before 2000") is a window too.
        var before = decade with { Years = new YearRange(null, 1999) };
        var open = await RetrieveAsync(new RetrievalQuery(accountId, before, SoftPreferences.None, [], 50) { Arms = RetrievalArms.Catalogue });
        Assert.DoesNotContain(undated, open.Candidates.Select(c => c.TitleId));

        // The STANDING year rule keeps letting an undated title through: it is a condition on what to
        // hide, and the truth SQL reads an absent date as no breach.
        var standing = await SeedAccountAsync();
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            seed.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = standing,
                Kind = FilterKind.MinYear,
                Value = "1990",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var anyYear = decade with { Years = null };
        var kept = await RetrieveAsync(new RetrievalQuery(standing, anyYear, SoftPreferences.None, [], 50) { Arms = RetrievalArms.Catalogue });
        Assert.Contains(undated, kept.Candidates.Select(c => c.TitleId));
        Assert.Contains(tooLate, kept.Candidates.Select(c => c.TitleId));
    }

    [Fact]
    public async Task Without_a_judge_an_overview_only_hit_is_admitted_only_when_nothing_tagged_was_found()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // HREV5-RET-04: keyless "shark movies" showed Sarah's Oil ("greedy oil sharks") and Below (the
        // submarine USS Tiger Shark) as unchecked shark films beside the tagged ones. The overview is a
        // lead for a judge, not a match on its own.
        var topic = $"fin{Guid.NewGuid():N}"[..14];
        var accountId = await SeedAccountAsync();
        var tagged = await SeedTitleAsync(t => t.Keywords = [topic]);
        var named = await SeedTitleAsync(t => t.Name = $"Night of the {topic}");
        var prose = await SeedTitleAsync(t => t.Overview = $"Greedy oil {topic} barons circle a small Oklahoma town.");
        var hard = new HardConstraints([new ConceptTerm(topic, [], [])], [], null, null, MediaScope.Any);
        RetrievalQuery Query(HardConstraints h) => new(accountId, h, SoftPreferences.None, [], 50) { Arms = RetrievalArms.Catalogue };

        var unjudged = (await RetrieveAsync(Query(hard), judge: new StubJudgeModel(available: false))).Candidates.Select(c => c.TitleId).ToList();
        Assert.Contains(tagged, unjudged);
        Assert.Contains(named, unjudged);
        Assert.DoesNotContain(prose, unjudged);

        // A judge confirms or drops it, so it is a candidate as before — and with no judge registered.
        var judged = await RetrieveAsync(Query(hard), judge: new StubJudgeModel(available: true));
        Assert.Contains(prose, judged.Candidates.Select(c => c.TitleId));
        Assert.Equal(["fts:overview"], judged.Candidates.Single(c => c.TitleId == prose).Evidence);
        Assert.Contains(prose, (await RetrieveAsync(Query(hard))).Candidates.Select(c => c.TitleId));

        // Nothing tagged at all: the overview is all the catalogue has, so it still answers.
        var untagged = $"fin{Guid.NewGuid():N}"[..14];
        var onlyProse = await SeedTitleAsync(t => t.Overview = $"A lone {untagged} hunts the reef at night.");
        var proseOnlyHard = hard with { Concepts = [new ConceptTerm(untagged, [], [])] };
        var fallback = await RetrieveAsync(Query(proseOnlyHard), judge: new StubJudgeModel(available: false));
        Assert.Equal([onlyProse], fallback.Candidates.Select(c => c.TitleId));
    }

    [Fact]
    public async Task A_rejection_reaches_every_tag_the_same_word_asked_for_would()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // HREV5-ASK-04: "how many mafia titles are left?" counted the organized-crime, gangster and
        // mobster tags, yet "crime titles, no mafia" still showed The Wire, Snowfall and Godfather of
        // Harlem. A rejection now excludes every tag of the word's synonym group — tags only, never
        // prose, and never a label the group's short stem merely starts ("mobile phone").
        var topic = $"crimes{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        var organized = await SeedTitleAsync(t => t.Keywords = [topic, "organized crime"]);
        var gangster = await SeedTitleAsync(t => t.Keywords = [topic, "gangster"]);
        var mobster = await SeedTitleAsync(t => t.Keywords = [topic, "mobster", "crime boss"]);
        var mobBoss = await SeedTitleAsync(t => t.Keywords = [topic, "mob boss"]);
        var mafia = await SeedTitleAsync(t => t.Keywords = [topic, "italian mafia"]);
        var crimeBoss = await SeedTitleAsync(t => t.Keywords = [topic, "crime boss"]);
        var angryMob = await SeedTitleAsync(t => { t.Keywords = [topic]; t.Overview = "An angry mob gathers at the mill."; });
        var phone = await SeedTitleAsync(t => t.Keywords = [topic, "mobile phone"]);
        var hard = new HardConstraints([new ConceptTerm(topic, [], [])], [], null, null, MediaScope.Any);

        foreach (var rejected in new[] { "mafia", "mafias", "mafia stuff", "gangsters", "organized crime" })
        {
            var result = await RetrieveAsync(new RetrievalQuery(accountId, hard, SoftPreferences.None, [], 50)
            {
                Arms = RetrievalArms.Catalogue,
                ExcludeKeywords = [rejected],
            });
            var ids = result.Candidates.Select(c => c.TitleId).ToList();

            Assert.DoesNotContain(organized, ids);
            Assert.DoesNotContain(gangster, ids);
            Assert.DoesNotContain(mobster, ids);
            Assert.DoesNotContain(mobBoss, ids);
            Assert.DoesNotContain(mafia, ids);
            Assert.Contains(crimeBoss, ids);
            Assert.Contains(angryMob, ids);
            Assert.Contains(phone, ids);

            // The supply counts with the same reading, so the reply and the cards agree.
            Assert.Equal(3, result.Supply.MatchingInCatalogue);
        }

        // A word outside every synonym group is rejected exactly as before: by itself.
        var narrow = await RetrieveAsync(new RetrievalQuery(accountId, hard, SoftPreferences.None, [], 50)
        {
            Arms = RetrievalArms.Catalogue,
            ExcludeKeywords = ["crime boss"],
        });
        var narrowIds = narrow.Candidates.Select(c => c.TitleId).ToList();
        Assert.DoesNotContain(crimeBoss, narrowIds);
        Assert.DoesNotContain(mobster, narrowIds);
        Assert.Contains(organized, narrowIds);
        Assert.Contains(gangster, narrowIds);
        Assert.Equal(6, narrow.Supply.MatchingInCatalogue);

        // "Gangster films, no mafia": the request asks for the very group it rejects a word of, so
        // the rejection keeps its literal reading instead of emptying the answer.
        var gangsters = hard with { Concepts = [new ConceptTerm(topic, [], []), new ConceptTerm("gangster films", [], [])] };
        var both = await RetrieveAsync(new RetrievalQuery(accountId, gangsters, SoftPreferences.None, [], 50)
        {
            Arms = RetrievalArms.Catalogue,
            ExcludeKeywords = ["mafia"],
        });
        var bothIds = both.Candidates.Select(c => c.TitleId).ToList();
        Assert.All(new[] { organized, gangster, mobster, mobBoss }, id => Assert.Contains(id, bothIds));
        Assert.DoesNotContain(mafia, bothIds);
        Assert.Equal(4, both.Supply.MatchingInCatalogue);
    }

    [Fact]
    public async Task The_facet_arm_ranks_by_the_vote_shrunk_crowd_score()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Task 4 (D-12): a 10.0 on 2 votes ranks below a 7.0 on 5,000, and a widely-voted 5.0 below
        // both — most-voted-first would have led with it.
        var label = $"facetrank{Guid.NewGuid():N}"[..20];
        var accountId = await SeedAccountAsync();
        var thin = await SeedTitleAsync(t => { t.Keywords = [label]; t.TmdbVoteAverage = 10m; t.TmdbVoteCount = 2; });
        var solid = await SeedTitleAsync(t => { t.Keywords = [label]; t.TmdbVoteAverage = 7m; t.TmdbVoteCount = 5_000; });
        var popularButPoor = await SeedTitleAsync(t => { t.Keywords = [label]; t.TmdbVoteAverage = 5m; t.TmdbVoteCount = 9_000; });

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId, new HardConstraints([new ConceptTerm(label, [], [])], [], null, null, MediaScope.Any), SoftPreferences.None, [], 10)
        {
            Arms = RetrievalArms.Facet,
        });

        Assert.Equal([solid, thin, popularButPoor], result.Candidates.Select(c => c.TitleId));
    }

    [Fact]
    public async Task A_mood_without_a_concept_draws_the_semantic_neighbours_from_the_eligible_pool()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var mood = $"wistful {Guid.NewGuid():N}";
        var near = await SeedTitleAsync(t => t.Keywords = ["office"], StubVector(mood));
        var nearButWatched = await SeedTitleAsync(t => t.Keywords = ["office"], StubVector(mood));
        await SeedWatchAsync(accountId, nearButWatched);

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId, HardConstraints.None, new SoftPreferences([mood], [], []), [], 10)
        {
            Arms = RetrievalArms.Semantic,
        });

        Assert.Equal(near, result.Candidates[0].TitleId);
        Assert.True(result.Candidates[0].Similarity > 0.999);
        Assert.DoesNotContain(nearButWatched, result.Candidates.Select(c => c.TitleId));
    }

    [Fact]
    public async Task The_resolver_reaches_synonyms_plurals_and_genre_words_through_the_vocabulary()
    {
        if (!Fixture.Available)
        {
            return;
        }

        await SeedTitleAsync(t => t.Keywords = ["shinobi no mono", "ninja warrior", "time loop", "courtroom drama", "shipwreck", "heist", "android", "volcano"]);
        await using var db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);

        var ninja = await resolver.ResolveAsync(new ConceptTerm("Ninja movies", [], []), default);
        Assert.Contains("ninja warrior", ninja.ExactLabels!);
        Assert.Contains("shinobi no mono", ninja.ExactLabels!);
        Assert.Contains("shinobi", ninja.CatalogueLabels);

        var heists = await resolver.ResolveAsync(new ConceptTerm("heists", [], []), default);
        Assert.Contains("heist", heists.ExactLabels!);

        Assert.Contains("time loop", (await resolver.ResolveAsync(new ConceptTerm("a time loop film", [], []), default)).ExactLabels!);
        Assert.Contains("courtroom drama", (await resolver.ResolveAsync(new ConceptTerm("courtroom", [], []), default)).ExactLabels!);
        Assert.Contains("shipwreck", (await resolver.ResolveAsync(new ConceptTerm("survival at sea", [], []), default)).ExactLabels!);

        // A phrase no label carries falls back to its words and their synonyms.
        Assert.Contains("android", (await resolver.ResolveAsync(new ConceptTerm("ai robot movies", [], []), default)).ExactLabels!);
        Assert.Contains("volcano", (await resolver.ResolveAsync(new ConceptTerm("disaster", [], []), default)).ExactLabels!);

        var scifi = await resolver.ResolveAsync(new ConceptTerm("Sci-Fi & Fantasy", [], []), default);
        Assert.Equal(["science-fiction", "fantasy"], scifi.Genres);
    }

    [Fact]
    public async Task A_genre_word_is_matched_as_the_genre_never_as_a_keyword_or_name_containing_it()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // HREV1-RET-06: "romantic comedies" resolved "comedy" to every label containing the word, so How
        // to Build a Better Boy ({family, science-fiction}, keyword "teen comedy") was counted as the
        // founder's one "unseen comedy" while eligibility did not call it a comedy at all. A unique topic
        // concept scopes every count to this test's titles on the shared catalogue.
        var topic = $"genreword{Guid.NewGuid():N}";
        var tagged = await SeedTitleAsync(t => { t.Genres = ["comedy", "romance"]; t.Keywords = [topic]; });
        var teenComedy = await SeedTitleAsync(t =>
        {
            t.Genres = ["family", "science-fiction"];
            t.Keywords = [topic, "teen comedy", "teenage romance"];
        });
        var namedComedy = await SeedTitleAsync(t =>
        {
            t.Name = $"A Romantic Comedy of Errors {Guid.NewGuid():N}";
            t.Genres = ["drama"];
            t.Keywords = [topic];
        });
        // TV romance is judged as an exclusion reads it: the soap alias and the overview stand-in count;
        // the same overview on a FILM does not (TMDB tags films with romance itself).
        var soapShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["comedy", "soap"]; t.Keywords = [topic]; });
        var overviewShow = await SeedTitleAsync(t =>
        {
            t.MediaType = MediaType.Show;
            t.Genres = ["comedy", "drama"];
            t.Keywords = [topic];
            t.Overview = "Two exes rekindle an old romance.";
        });
        var overviewFilm = await SeedTitleAsync(t => { t.Genres = ["comedy", "drama"]; t.Keywords = [topic]; t.Overview = "Two exes rekindle an old romance."; });

        await using var db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);

        // The genre concepts the reducer builds (one per genre), and the plural / adjective spellings.
        foreach (var (words, genre) in new[] { ("comedy", "comedy"), ("comedies", "comedy"), ("romantic", "romance"), ("Romance movies", "romance") })
        {
            var resolved = await resolver.ResolveAsync(new ConceptTerm(words, [], words.Contains(' ') ? [] : [genre]), default);
            Assert.Equal([genre], resolved.Genres);
            Assert.Empty(resolved.ExactLabels!);
            Assert.Empty(HardConstraintFilter.LabelsOf(resolved));
        }

        // A phrase that is a TMDB keyword stays a keyword concept: "teen comedy" is a label, not a genre.
        var phrase = await resolver.ResolveAsync(new ConceptTerm("teen comedy", [], []), default);
        Assert.Contains("teen comedy", phrase.ExactLabels!);
        Assert.Empty(phrase.Genres);

        var hard = await resolver.ResolveAsync(
            HardConstraints.None with
            {
                Concepts = [new ConceptTerm(topic, [topic], []), new ConceptTerm("comedy", [], ["comedy"]), new ConceptTerm("romance", [], ["romance"])],
            },
            default);
        var accountId = await SeedAccountAsync();
        var eligibility = new EligibilityQueryBuilder(db);
        var supply = await new SupplyReporter(db, eligibility).ReportAsync(accountId, hard, CancellationToken.None);
        var matched = await HardConstraintFilter.Apply(db.Titles, db, hard).Select(t => t.Id).ToListAsync();
        Assert.Equal(new HashSet<Guid> { tagged, soapShow, overviewShow }, matched.ToHashSet());
        Assert.Equal(3, supply.MatchingInCatalogue);

        // Evidence names the genre, never a keyword or name that merely contains the genre word.
        var taggedTitle = await db.Titles.SingleAsync(t => t.Id == tagged);
        Assert.Equal(["keyword:" + topic, "genre:comedy", "genre:romance"], HardConstraintFilter.EvidenceFor(taggedTitle, hard));

        // The founder's conflict: comedy and romance are standing exclusions, so all three are held
        // out by conditions and nothing is "an unseen comedy". Each knockout alone unlocks nothing —
        // the other exclusion still binds — which is what lets the reply name the conflict instead.
        db.ContentFilters.AddRange(
            new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = FilterKind.ExcludeGenre, Value = "comedy", CreatedAt = DateTime.UtcNow },
            new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = FilterKind.ExcludeGenre, Value = "romance", CreatedAt = DateTime.UtcNow.AddSeconds(1) });
        await db.SaveChangesAsync();
        var conflicted = await new SupplyReporter(db, eligibility).ReportAsync(accountId, hard, CancellationToken.None);
        Assert.Equal(3, conflicted.MatchingInCatalogue);
        Assert.Equal(3, conflicted.ExcludedByConditions);
        Assert.Equal(0, conflicted.Eligible);
        Assert.DoesNotContain(conflicted.Knockouts, k => k.Kind == FilterKind.ExcludeGenre);
        _ = (teenComedy, namedComedy, overviewFilm);
    }

    [Theory]
    [InlineData("romantic comedy", new[] { "romance", "comedy" })]
    [InlineData("romantic comedies", new[] { "romance", "comedy" })]
    [InlineData("Romantic Comedy movies", new[] { "romance", "comedy" })]
    [InlineData("comedy romance", new[] { "comedy", "romance" })]
    [InlineData("horror western", new[] { "horror", "western" })]
    public async Task A_phrase_of_genre_words_no_label_carries_is_the_genres_never_their_keywords(string words, string[] genres)
    {
        if (!Fixture.Available)
        {
            return;
        }

        // HREV1-RET-06 (refuted round 2): "romantic comedy" — the singular, lower-case concept the planner
        // emits — reaches no label (TMDB says "romcom"), so the phrase fell back to its words and
        // "romantic" and "comedy" became keyword stems: on the founder's catalogue that was 109 labels
        // ("dark comedy", "teen comedy", "romantic") and Black Mirror among "6 unseen romantic comedy
        // titles" while comedy and romance were both standing exclusions. The vocabulary is pinned so
        // the phrase reaches nothing here whatever the shared catalogue holds, and carries exactly the
        // labels the bug reached.
        var resolver = PinnedVocabularyResolver(out var db, "romantic", "dark comedy", "teen comedy", "comedy club", "romcom", "western town");
        await using var owned = db;

        var resolved = await resolver.ResolveAsync(new ConceptTerm(words, [], []), default);

        Assert.Equal(genres, resolved.Genres);
        Assert.Empty(resolved.CatalogueLabels);
        Assert.Empty(resolved.ExactLabels!);
        Assert.Empty(HardConstraintFilter.LabelsOf(resolved));
    }

    [Fact]
    public async Task A_genre_word_inside_a_phrase_is_a_genre_the_title_must_also_carry()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // A phrase's genre words narrow it: a romantic comedy is a comedy AND a romance, as the reducer's
        // one-concept-per-genre plan reads it, never every comedy plus every romance. A phrase with a
        // genre word and a topic ("romantic heist") is a heist that is also a romance.
        var topic = $"phrasegenre{Guid.NewGuid():N}";
        var both = await SeedTitleAsync(t => { t.Genres = ["comedy", "romance"]; t.Keywords = [topic]; });
        var comedyOnly = await SeedTitleAsync(t => { t.Genres = ["comedy"]; t.Keywords = [topic, "romantic"]; });
        var romanceOnly = await SeedTitleAsync(t => { t.Genres = ["romance", "drama"]; t.Keywords = [topic, "dark comedy"]; });
        var soapComedy = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["comedy", "soap"]; t.Keywords = [topic]; });
        var neither = await SeedTitleAsync(t => { t.Genres = ["thriller"]; t.Keywords = [topic, "romantic", "dark comedy", "teen comedy"]; });
        var romanticHeist = await SeedTitleAsync(t => { t.Genres = ["romance", "crime"]; t.Keywords = [topic, "bank robbery"]; });
        var plainHeist = await SeedTitleAsync(t => { t.Genres = ["crime"]; t.Keywords = [topic, "heist"]; });

        var resolver = PinnedVocabularyResolver(out var db, topic, "romantic", "dark comedy", "teen comedy", "heist", "bank robbery", "robbery");
        await using var owned = db;

        var hard = await resolver.ResolveAsync(
            HardConstraints.None with { Concepts = [new ConceptTerm(topic, [topic], []), new ConceptTerm("romantic comedy", [], [])] },
            default);

        // The phrase's own words stay the topic the reply names; each further genre is its own requirement.
        Assert.Equal(3, hard.Concepts.Count);
        Assert.Equal("romantic comedy", hard.Concepts[1].UserWords);
        Assert.Equal(["romance"], hard.Concepts[1].Genres);
        Assert.Equal(["comedy"], hard.Concepts[2].Genres);
        Assert.All(hard.Concepts.Skip(1), c => Assert.Empty(HardConstraintFilter.LabelsOf(c)));

        var matched = (await HardConstraintFilter.Apply(db.Titles, db, hard).Select(t => t.Id).ToListAsync()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { both, soapComedy }, matched);
        var accountId = await SeedAccountAsync();
        var supply = await new SupplyReporter(db, new EligibilityQueryBuilder(db)).ReportAsync(accountId, hard, CancellationToken.None);
        Assert.Equal(2, supply.MatchingInCatalogue);

        // A planner that ALSO names the genres gives the same set, not a third count.
        var withGenres = await resolver.ResolveAsync(
            HardConstraints.None with
            {
                Concepts =
                [
                    new ConceptTerm(topic, [topic], []), new ConceptTerm("romantic comedy", [], []),
                    new ConceptTerm("comedy", [], ["comedy"]), new ConceptTerm("romance", [], ["romance"]),
                ],
            },
            default);
        Assert.Equal(matched, (await HardConstraintFilter.Apply(db.Titles, db, withGenres).Select(t => t.Id).ToListAsync()).ToHashSet());

        // Resolving the resolved constraints again (the Ask engine and the retriever both resolve)
        // changes nothing: the split concepts keep their one genre each.
        var again = await resolver.ResolveAsync(hard, default);
        Assert.Equal(hard.Concepts.Select(c => string.Join(',', c.Genres)), again.Concepts.Select(c => string.Join(',', c.Genres)));
        Assert.Equal(matched, (await HardConstraintFilter.Apply(db.Titles, db, again).Select(t => t.Id).ToListAsync()).ToHashSet());

        // "Or" asks for either genre: one concept, OR-ed.
        var either = await resolver.ResolveAsync(
            HardConstraints.None with { Concepts = [new ConceptTerm(topic, [topic], []), new ConceptTerm("comedy or romance", [], [])] },
            default);
        Assert.Equal(2, either.Concepts.Count);
        Assert.Equal(
            new HashSet<Guid> { both, comedyOnly, romanceOnly, soapComedy, romanticHeist },
            (await HardConstraintFilter.Apply(db.Titles, db, either).Select(t => t.Id).ToListAsync()).ToHashSet());

        var heist = await resolver.ResolveAsync(
            HardConstraints.None with { Concepts = [new ConceptTerm(topic, [topic], []), new ConceptTerm("romantic heist", [], [])] },
            default);
        Assert.Equal("romantic heist", heist.Concepts[1].UserWords);
        Assert.Contains("bank robbery", heist.Concepts[1].ExactLabels!);
        Assert.Empty(heist.Concepts[1].Genres);
        Assert.Equal(["romance"], heist.Concepts[2].Genres);
        Assert.Equal(
            new HashSet<Guid> { romanticHeist },
            (await HardConstraintFilter.Apply(db.Titles, db, heist).Select(t => t.Id).ToListAsync()).ToHashSet());
        _ = (comedyOnly, romanceOnly, neither, plainHeist);
    }

    [Theory]
    [InlineData("heists")]
    [InlineData("Heist movies")]
    [InlineData("robberies")]
    public async Task A_plural_concept_keeps_its_synonym_group(string words)
    {
        if (!Fixture.Available)
        {
            return;
        }

        // G1 risk: "heists" reached nothing, retried singular as "heist" alone, and lost "bank robbery"
        // and "robbery" — 258 founder titles where "heist" finds 522.
        var resolver = PinnedVocabularyResolver(out var db, "heist", "heist gone wrong", "bank robbery", "robbery", "train robbery");
        await using var owned = db;

        var plural = await resolver.ResolveAsync(new ConceptTerm(words, [], []), default);
        var singular = await resolver.ResolveAsync(new ConceptTerm("heist", [], []), default);

        Assert.Equal(singular.ExactLabels, plural.ExactLabels);
        Assert.Contains("bank robbery", plural.ExactLabels!);
        Assert.Empty(plural.Genres);
    }

    [Theory]
    [InlineData("romances", "romance")]
    [InlineData("horrors", "horror")]
    [InlineData("fantasies", "fantasy")]
    public async Task A_plural_genre_word_is_the_genre(string words, string genre)
    {
        if (!Fixture.Available)
        {
            return;
        }

        // "romances" is not a GenreWords key; read as a stem it reached every label containing
        // "romance" (the two-definitions defect by its plural).
        var resolver = PinnedVocabularyResolver(out var db, "romance", "teenage romance", "horror host", "fantasy world");
        await using var owned = db;

        var resolved = await resolver.ResolveAsync(new ConceptTerm(words, [], []), default);

        Assert.Equal([genre], resolved.Genres);
        Assert.Empty(resolved.ExactLabels!);
        Assert.Empty(HardConstraintFilter.LabelsOf(resolved));
    }

    /// <summary>A resolver over exactly <paramref name="labels"/> (the vocabulary cache pre-filled), on the fixture database.</summary>
    private ConceptResolver PinnedVocabularyResolver(out ReelDbContext db, params string[] labels)
    {
        db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(KeywordVocabulary.CacheKey, (IReadOnlyList<VocabularyLabel>)[.. labels.Select(l => new VocabularyLabel(l, 10))]);
        return new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);
    }

    [Fact]
    public async Task Keyword_neighbours_share_specific_evidence_and_say_which()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The Similar rail's seeding API: eligible titles sharing the anchor's SPECIFIC labels, the
        // most specific shared evidence first; nothing generic, nothing seen, never the anchor.
        var rare = $"safecracker{Guid.NewGuid():N}"[..24];
        var rarer = $"vaultcode{Guid.NewGuid():N}"[..22];
        var accountId = await SeedAccountAsync();
        var anchor = await SeedTitleAsync(t => t.Keywords = [rare, rarer]);
        var both = await SeedTitleAsync(t => t.Keywords = [rare, rarer]);
        var one = await SeedTitleAsync(t => t.Keywords = [rare]);
        var seen = await SeedTitleAsync(t => t.Keywords = [rare, rarer]);
        var unrelated = await SeedTitleAsync(t => t.Keywords = ["office"]);
        await SeedWatchAsync(accountId, seen);

        await using var db = Fixture.CreateContext(new AccountContext());
        var neighbours = await new KeywordNeighbours(db, new EligibilityQueryBuilder(db), new KeywordVocabulary(db, new MemoryCache(new MemoryCacheOptions())))
            .ForAsync(accountId, anchor, 10, [], default);

        Assert.Equal([both, one], neighbours.Select(n => n.TitleId));
        Assert.Contains($"keyword:{rare}", neighbours[1].Evidence);
        Assert.DoesNotContain(neighbours, n => n.TitleId == unrelated || n.TitleId == seen || n.TitleId == anchor);
    }

    [Fact]
    public async Task Rail_supply_counts_a_genre_rail_with_its_stand_ins()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // D-16: a genre rail's supply is the genre as exclusions read it — a TV romance carried only
        // by a stand-in keyword counts — and a title merely NAMED for the genre does not.
        var accountId = await SeedAccountAsync();
        await using var db = Fixture.CreateContext(new AccountContext());
        var supply = new SupplyReporter(db, new EligibilityQueryBuilder(db));
        var before = await supply.ForGenreRailAsync(accountId, "romance", MediaScope.Shows, [], default);

        await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["drama"]; t.Keywords = ["office romance"]; });
        await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["drama"]; t.Name = $"Romance Street {Guid.NewGuid():N}"; });

        var after = await supply.ForGenreRailAsync(accountId, "romance", MediaScope.Shows, [], default);
        Assert.Equal(1, after.MatchingInCatalogue - before.MatchingInCatalogue);
    }

    [Fact]
    public async Task Rail_supply_waives_an_include_genre_rule_as_the_rail_does_but_never_an_exclude()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // FD-6: a genre rail waives include-genre and never exclude-genre, so its "your conditions hide
        // N" must count the same way. Counted with the include rule applied, the rail's own genre
        // looked hidden by "only drama" — a rule the rail never applies.
        var genre = $"rail-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        var plain = await SeedTitleAsync(t => t.Genres = [genre]);
        var horror = await SeedTitleAsync(t => t.Genres = [genre, "horror"]);
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            seed.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = FilterKind.IncludeGenre,
                Value = "drama",
                CreatedAt = DateTime.UtcNow,
            });
            seed.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = FilterKind.ExcludeGenre,
                Value = "horror",
                CreatedAt = DateTime.UtcNow.AddSeconds(1),
            });
            await seed.SaveChangesAsync();
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var supply = new SupplyReporter(db, new EligibilityQueryBuilder(db));
        var applied = await supply.ReportAsync(
            accountId, new HardConstraints([new ConceptTerm(string.Empty, [], [genre], [])], [], null, null, MediaScope.Any), default);
        var rail = await supply.ForGenreRailAsync(accountId, genre, MediaScope.Any, [], default);

        Assert.Equal(2, applied.ExcludedByConditions);
        Assert.Equal(2, rail.MatchingInCatalogue);
        Assert.Equal(1, rail.ExcludedByConditions);
        Assert.Equal(1, rail.Eligible);
        Assert.DoesNotContain(rail.Knockouts, k => k.Kind == FilterKind.IncludeGenre);
        var exclude = Assert.Single(rail.Knockouts);
        Assert.Equal(FilterKind.ExcludeGenre, exclude.Kind);
        Assert.Equal(horror, Assert.Single(exclude.Sample).TitleId);
        Assert.NotEqual(plain, horror);
    }

    [Fact]
    public async Task A_mood_request_is_answered_by_titles_carrying_the_mood_not_the_best_rated_anything()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // WS-2's mood requests: "something cosy" had nothing to stand on but an uncached embedding,
        // so it was answered with the pool's best-rated titles of any kind.
        var accountId = await SeedAccountAsync();
        var warm = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.TmdbVoteAverage = 8.4m; t.TmdbVoteCount = 30_000; });
        await SeedAttributesAsync(warm, tone: "warm");
        var smallTown = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Keywords = ["small town"]; t.TmdbVoteAverage = 8.3m; t.TmdbVoteCount = 30_000; });
        var acclaimed = await SeedTitleAsync(t => { t.Genres = ["action"]; t.TmdbVoteAverage = 9.6m; t.TmdbVoteCount = 900_000; });
        await SeedAttributesAsync(acclaimed, tone: "gritty");

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId, HardConstraints.None, new SoftPreferences(["a cosy movie"], [], []), [], FacetRetriever.MaxWant)
        {
            Arms = RetrievalArms.Catalogue,
        });
        var byId = result.Candidates.ToDictionary(c => c.TitleId);

        Assert.Contains(warm, byId.Keys);
        Assert.Contains("mood:cosy", byId[warm].Evidence);
        Assert.Contains("tone:warm", byId[warm].Evidence);
        Assert.Contains(smallTown, byId.Keys);
        Assert.Contains("keyword:small town", byId[smallTown].Evidence);
        Assert.DoesNotContain(acclaimed, byId.Keys);
        Assert.All(result.Candidates, c => Assert.Contains("mood:cosy", c.Evidence));
    }

    [Fact]
    public async Task A_steer_re_ranks_a_references_neighbours_relative_to_the_reference_and_excludes_none()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // "Like X but funnier" (§4.6): the reference's neighbours, the ones funnier than X first. They
        // are its neighbours by what they share with it (a specific label), never by a stored vector.
        var accountId = await SeedAccountAsync();
        var vector = UniqueVector();
        var label = $"lighthousekeeper{Guid.NewGuid():N}"[..28];
        var reference = await SeedTitleAsync(t => t.Keywords = [label], vector);
        await SeedAttributesAsync(reference, humor: 0.40m);
        var funnier = await SeedTitleAsync(t => t.Keywords = [label], vector);
        await SeedAttributesAsync(funnier, humor: 0.70m);
        var graver = await SeedTitleAsync(t => t.Keywords = [label], vector);
        await SeedAttributesAsync(graver, humor: 0.20m);
        // Funnier than the midpoint but not than the reference: the pivot is the reference, not 0.5.
        var slightly = await SeedTitleAsync(t => t.Keywords = [label], vector);
        await SeedAttributesAsync(slightly, humor: 0.35m);

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId,
            HardConstraints.None,
            new SoftPreferences([], [new AttributeDirection("humor", AttributeTrend.More)], [reference]),
            [reference],
            FacetRetriever.MaxWant)
        {
            Arms = RetrievalArms.Semantic | RetrievalArms.Facet,
        });
        var order = result.Candidates.Select(c => c.TitleId).ToList();
        var byId = result.Candidates.ToDictionary(c => c.TitleId);

        Assert.DoesNotContain(reference, order);
        Assert.Contains(graver, order);
        Assert.Contains(slightly, order);
        Assert.True(order.IndexOf(funnier) < order.IndexOf(graver));
        Assert.True(order.IndexOf(funnier) < order.IndexOf(slightly));
        Assert.Contains("steer:humor+", byId[funnier].Evidence);
        Assert.DoesNotContain("steer:humor+", byId[slightly].Evidence);
        Assert.DoesNotContain("steer:humor+", byId[graver].Evidence);
    }

    /// <summary>
    /// HREV2-ASK-02: "more like the second one" resolved to Red-Eyed (heist, revenge) and came back
    /// Red Hill, Red Right Hand, Red (2008), Red White &amp; Blue and Red Road — the stored title
    /// vector leads with the name, so its nearest neighbours are titles CALLED alike. Here the
    /// reference's stored vector is shared by five "Red …" titles about something else entirely: a
    /// retriever that anchors on it returns them; one that reads what the reference is about returns
    /// the heist films — by the specific label they share, and by a description of the reference
    /// that never contains its name.
    /// </summary>
    [Fact]
    public async Task A_reference_is_searched_near_by_what_it_is_about_never_by_its_name()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        var heist = $"vaultjob{tag}";
        var nameVector = UniqueVector();
        var accountId = await SeedAccountAsync();
        Title shape = new()
        {
            Name = $"Red-Eyed {tag}",
            Genres = ["crime", "thriller"],
            Keywords = [heist, $"payback{tag}"],
            Overview = $"When a {tag} heist goes sideways, Cole goes back home and prepares to defend himself.",
        };
        var reference = await SeedTitleAsync(t => { t.Name = shape.Name; t.Genres = shape.Genres; t.Keywords = shape.Keywords; t.Overview = shape.Overview; }, nameVector);

        // Called alike, about nothing alike: the stored vector the old anchor read.
        var redAlikes = new List<Guid>();
        foreach (var name in new[] { "Red Hill", "Red Right Hand", "Red", "Red White & Blue", "Red Road" })
        {
            redAlikes.Add(await SeedTitleAsync(t => { t.Name = $"{name} {tag}"; t.Genres = ["drama"]; t.Keywords = ["office"]; }, nameVector));
        }

        // About alike: two share the reference's specific heist label (and lie near its description);
        // one shares nothing but what it is about (its stored vector IS the description's embedding).
        var description = StubVector(HybridRetriever.ReferenceText(shape));
        var crimeHeist = await SeedTitleAsync(t => { t.Genres = ["crime", "thriller"]; t.Keywords = [heist]; }, Near(description, 0.002f));
        var comedyHeist = await SeedTitleAsync(t => { t.Genres = ["comedy"]; t.Keywords = [heist]; }, Near(description, 0.004f));
        var aboutAlike = await SeedTitleAsync(t => t.Genres = ["crime"], description);

        var result = await RetrieveAsync(new RetrievalQuery(
            accountId, HardConstraints.None, new SoftPreferences([], [], [reference]), [reference], 40)
        {
            Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic,
        });
        var order = result.Candidates.Select(c => c.TitleId).ToList();
        var byId = result.Candidates.ToDictionary(c => c.TitleId);

        Assert.DoesNotContain(reference, order);
        Assert.Contains(crimeHeist, order);
        Assert.Contains(comedyHeist, order);
        Assert.Contains(aboutAlike, order);
        Assert.Contains($"keyword:{heist}", byId[crimeHeist].Evidence);
        Assert.True(byId[aboutAlike].Similarity > 0.999);

        // The golden bar: the head is the films about the same thing (only they carry evidence from two
        // arms or sit at the anchor), and a title merely CALLED alike ranks below every one of them —
        // never at the anchor (a small test catalogue lets the semantic arm's fixed k reach them at
        // all; the real one has 38k titles in between).
        var aboutLast = new[] { crimeHeist, comedyHeist, aboutAlike }.Max(order.IndexOf);
        Assert.Equal(2, aboutLast);
        Assert.All(redAlikes.Where(byId.ContainsKey), r =>
        {
            Assert.True(order.IndexOf(r) > aboutLast);
            Assert.True(byId[r].Similarity is null or < 0.999);
        });

        // The shared label ranks first, and the reference's own genres break the tie between the two.
        Assert.True(order.IndexOf(crimeHeist) < order.IndexOf(comedyHeist));
        Assert.Contains("genre:crime", byId[crimeHeist].Evidence);
        Assert.DoesNotContain(result.SkippedArms, a => a.StartsWith("semantic", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same request with no embedding provider (or a description that misses its budget): the
    /// reference's own labels' precomputed embeddings stand in for its description — still never
    /// the name-led stored vector — and the shared-label arm answers regardless.
    /// </summary>
    [Fact]
    public async Task Without_an_embedding_a_reference_stands_on_its_labels_never_its_stored_vector()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        var label = $"funeralhome{tag}";
        var nameVector = UniqueVector();
        var labelVector = UniqueVector();
        var accountId = await SeedAccountAsync();
        var reference = await SeedTitleAsync(
            t => { t.Name = $"After.Life {tag}"; t.Genres = ["drama", "horror"]; t.Keywords = [label]; t.Overview = "A woman wakes up being prepared for her own funeral."; },
            nameVector);
        var alike = await SeedTitleAsync(t => { t.Name = $"The Life Before Her Eyes {tag}"; t.Genres = ["drama"]; t.Keywords = ["office"]; }, nameVector);
        var sharesLabel = await SeedTitleAsync(t => { t.Genres = ["horror"]; t.Keywords = [label]; }, UniqueVector());
        var nearLabel = await SeedTitleAsync(t => t.Genres = ["horror"], labelVector);
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            seed.KeywordLabels.Add(new KeywordLabel
            {
                Label = label,
                TitleCount = 2,
                Embedding = new Vector(labelVector),
                EmbeddingModel = "text-embedding-3-small",
                CountedAt = DateTime.UtcNow,
                EmbeddedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        try
        {
            var result = await RetrieveAsync(
                new RetrievalQuery(accountId, HardConstraints.None, new SoftPreferences([], [], [reference]), [reference], 40)
                {
                    Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic,
                },
                new UnavailableEmbeddings());
            var order = result.Candidates.Select(c => c.TitleId).ToList();

            var byId = result.Candidates.ToDictionary(c => c.TitleId);
            Assert.Equal(1, result.Timings.ElapsedMs["semantic-reference-labels"]);
            Assert.Contains(sharesLabel, order);
            Assert.Contains(nearLabel, order);
            Assert.True(byId[nearLabel].Similarity > 0.999);
            Assert.Equal(1, new[] { sharesLabel, nearLabel }.Max(order.IndexOf));
            if (byId.TryGetValue(alike, out var called))
            {
                Assert.True(order.IndexOf(alike) > 1);
                Assert.True(called.Similarity is null or < 0.999);
            }
        }
        finally
        {
            await using var cleanup = Fixture.CreateContext(new AccountContext());
            await cleanup.KeywordLabels.Where(l => l.Label == label).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("Red-Eyed", "When a heist goes sideways, Cole goes back home in Red-Eyed fashion.", new[] { "heist" }, "crime, thriller. heist. When a heist goes sideways, Cole goes back home in fashion.")]
    [InlineData("After.Life", "AFTER.LIFE: a woman wakes before her funeral.", new string[0], "crime, thriller. : a woman wakes before her funeral.")]
    [InlineData("Red", null, new[] { "retired", "cia" }, "crime, thriller. retired, cia")]
    [InlineData("Red", "", new string[0], "")]
    public void A_reference_description_never_carries_its_name_and_a_bare_genre_list_is_none(
        string name, string? overview, string[] keywords, string expected)
    {
        var title = new Title { Name = name, Overview = overview, Keywords = keywords, Genres = ["crime", "thriller"] };
        Assert.Equal(expected, HybridRetriever.ReferenceText(title));
    }

    [Fact]
    public async Task Without_an_embedding_the_semantic_arm_stands_on_the_reached_labels_precomputed_embeddings()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var label = $"zephyrlull{Guid.NewGuid():N}"[..20];
        var vector = UniqueVector();
        var accountId = await SeedAccountAsync();
        var tagged = await SeedTitleAsync(t => t.Keywords = [label], vector);
        await using (var seed = Fixture.CreateContext(new AccountContext()))
        {
            seed.KeywordLabels.Add(new KeywordLabel
            {
                Label = label,
                TitleCount = 1,
                Embedding = new Vector(vector),
                EmbeddingModel = "text-embedding-3-small",
                CountedAt = DateTime.UtcNow,
                EmbeddedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        try
        {
            var result = await RetrieveAsync(
                new RetrievalQuery(accountId, HardConstraints.None, new SoftPreferences([label], [], []), [], 20)
                {
                    Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic,
                },
                new UnavailableEmbeddings());

            Assert.Equal(1, result.Timings.ElapsedMs["semantic-labels"]);
            Assert.DoesNotContain("semantic", result.SkippedArms);
            var hit = Assert.Single(result.Candidates, c => c.TitleId == tagged);
            Assert.NotNull(hit.Similarity);
            Assert.Contains($"keyword:{label}", hit.Evidence);
        }
        finally
        {
            await using var cleanup = Fixture.CreateContext(new AccountContext());
            await cleanup.KeywordLabels.Where(l => l.Label == label).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// R1-18: with a concept the semantic arm only re-ranks titles the catalogue's evidence admits — it
    /// can never add one — so retrieval never waits for its embedding: it takes the vector if it has
    /// arrived, else the reached labels' stand-in, and the call finishes in the background into the
    /// cache. A cold concept text takes 1.2–2.7 s to embed, so the 1.0 s wait was spent on nearly
    /// every turn for nothing (WS-2: retrieve ~1.0 s at the median, arm:semantic skipped). Filled or
    /// thin, the answer is the same.
    /// </summary>
    [Theory]
    [InlineData(4, 4)]
    [InlineData(2, 20)]
    public async Task A_concept_request_never_waits_for_the_semantic_embedding(int seeded, int want)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var label = $"semword{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < seeded; i++)
        {
            ids.Add(await SeedTitleAsync(t => t.Keywords = [label]));
        }

        var hard = new HardConstraints([new ConceptTerm(label, [], [])], [], null, null, MediaScope.Any);
        var result = await RetrieveWithScopesAsync(
            new RetrievalQuery(accountId, hard, SoftPreferences.None, [], want) { Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic },
            new SlowEmbeddings(TimeSpan.FromSeconds(5)),
            new SlowTmdbClient(TimeSpan.Zero));

        Assert.Equal(ids.Order(), result.Candidates.Select(c => c.TitleId).Order());
        Assert.True(result.Timings.ElapsedMs["semantic-wait"] < 250, $"semantic-wait {result.Timings.ElapsedMs["semantic-wait"]} ms");
        Assert.Contains("semantic-late", result.SkippedArms);
    }

    /// <summary>
    /// R1-18, the TMDB side: TMDB can add titles the catalogue lacks, so it is still waited for while
    /// the catalogue's evidence is thin — but not once it holds at least half the request
    /// (<see cref="HybridRetriever.SufficientShare"/>), where TMDB would only re-rank and add
    /// provisional titles the next turn can use. Filled, half-filled, and thin.
    /// </summary>
    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(2, 4, false)]
    [InlineData(1, 20, true)]
    public async Task Tmdb_is_waited_for_only_while_the_catalogue_is_thin(int seeded, int want, bool waits)
    {
        if (!Fixture.Available)
        {
            return;
        }

        var label = $"tmdbword{Guid.NewGuid():N}"[..16];
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < seeded; i++)
        {
            await SeedTitleAsync(t => t.Keywords = [label]);
        }

        var hard = new HardConstraints([new ConceptTerm(label, [], [])], [], null, null, MediaScope.Any);
        var result = await RetrieveWithScopesAsync(
            new RetrievalQuery(accountId, hard, SoftPreferences.None, [], want) { Arms = RetrievalArms.Catalogue | RetrievalArms.Tmdb, Message = $"{label} movies" },
            new SlowEmbeddings(TimeSpan.Zero),
            new SlowTmdbClient(waits ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(5)));

        Assert.Equal(seeded, result.Candidates.Count);
        if (waits)
        {
            Assert.DoesNotContain("tmdb-late", result.SkippedArms);
            Assert.True(result.Timings.ElapsedMs.ContainsKey("tmdb"), string.Join(", ", result.Timings.ElapsedMs.Keys));
        }
        else
        {
            Assert.True(result.Timings.ElapsedMs["tmdb-wait"] < 250, $"tmdb-wait {result.Timings.ElapsedMs["tmdb-wait"]} ms");
            Assert.Contains("tmdb-late", result.SkippedArms);
        }
    }

    /// <summary>
    /// The other side of R1-18: without a concept the semantic arm is a FINDING arm (a mood, a
    /// reference), so it keeps its budget, and an embedding that lands inside it is used.
    /// </summary>
    [Fact]
    public async Task A_mood_request_still_waits_for_an_embedding_inside_its_budget()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var mood = $"moodword{Guid.NewGuid():N}"[..16];
        var result = await RetrieveWithScopesAsync(
            new RetrievalQuery(accountId, HardConstraints.None, new SoftPreferences([mood], [], []), [], 20) { Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic },
            new SlowEmbeddings(TimeSpan.FromMilliseconds(700)),
            new SlowTmdbClient(TimeSpan.Zero));

        Assert.True(result.Timings.ElapsedMs.ContainsKey("semantic"), string.Join(", ", result.Timings.ElapsedMs.Keys));
        Assert.DoesNotContain("semantic", result.SkippedArms);
        Assert.DoesNotContain("semantic-late", result.SkippedArms);
    }

    /// <summary>The retriever with a container behind its own scopes, so the TMDB arm really runs on one.</summary>
    private async Task<RetrievalResult> RetrieveWithScopesAsync(RetrievalQuery query, Domain.Ports.IEmbeddingProvider provider, Domain.Ports.ITmdbClient tmdb)
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddDbContext<ReelDbContext>(o => o.UseNpgsql(Fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<Application.Persistence.IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());
        services.AddScoped<EligibilityQueryBuilder>();
        services.AddSingleton(tmdb);
        services.AddScoped<TmdbDiscoveryArm>();
        await using var container = services.BuildServiceProvider(validateScopes: true);

        var scoped = new AccountContext();
        scoped.SetAccount(query.AccountId);
        await using var db = Fixture.CreateContext(scoped);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var embeddings = new ConceptEmbeddings(provider, cache, NullLogger<ConceptEmbeddings>.Instance);
        var retriever = new HybridRetriever(
            db,
            eligibility,
            new SupplyReporter(db, eligibility),
            new ConceptResolver(new KeywordVocabulary(db, cache), embeddings, db, NullLogger<ConceptResolver>.Instance),
            new FullTextArm(db),
            embeddings,
            new KeywordNeighbours(db, eligibility, new KeywordVocabulary(db, cache)),
            container.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HybridRetriever>.Instance);
        return await retriever.RetrieveAsync(query, default);
    }

    /// <summary>Answers every text with a fixed vector, after <paramref name="delay"/>.</summary>
    private sealed class SlowEmbeddings(TimeSpan delay) : Domain.Ports.IEmbeddingProvider
    {
        public bool IsAvailable => true;

        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return [.. texts.Select(_ => UniqueVector())];
        }
    }

    /// <summary>A TMDB that answers everything empty, after <paramref name="delay"/> (or when cancelled).</summary>
    private sealed class SlowTmdbClient(TimeSpan delay) : Domain.Ports.ITmdbClient
    {
        public Task<Domain.Ports.Tmdb.TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => After<Domain.Ports.Tmdb.TmdbTitleDetails?>(null, ct);

        public Task<Domain.Ports.Tmdb.TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => After<Domain.Ports.Tmdb.TmdbTitleDetails?>(null, ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) => Empty(ct);

        public Task<Domain.Ports.Tmdb.TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            After(new Domain.Ports.Tmdb.TmdbDiscoverPage([], page, 0, 0), ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            After<IReadOnlyList<Domain.Ports.Tmdb.TmdbKeyword>>([], ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) => Empty(ct);

        public Task<Domain.Ports.Tmdb.TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => After<Domain.Ports.Tmdb.TmdbPerson?>(null, ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> GetPersonCreditsAsync(
            long personId, Domain.Ports.Tmdb.PersonCreditRole role, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null,
            int? maxRuntimeMinutes = null, Domain.Ports.Tmdb.TmdbDiscoverOptions? options = null, CancellationToken ct = default) => Empty(ct);

        public Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            After<IReadOnlyList<Domain.Ports.Tmdb.TmdbWatchProvider>>([], ct);

        private Task<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>> Empty(CancellationToken ct) =>
            After<IReadOnlyList<Domain.Ports.Tmdb.TmdbListItem>>([], ct);

        private async Task<T> After<T>(T value, CancellationToken ct)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            return value;
        }
    }

    /// <summary><paramref name="vector"/> nudged by a little random noise, re-normalised: near it, not on it.</summary>
    private static float[] Near(float[] vector, float noise)
    {
        var random = new Random(Guid.NewGuid().GetHashCode());
        var v = vector.Select(x => x + (noise * (float)(random.NextDouble() - 0.5))).ToArray();
        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        return [.. v.Select(x => x / norm)];
    }

    private static float[] UniqueVector()
    {
        var random = new Random(Guid.NewGuid().GetHashCode());
        var v = new float[1536];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(random.NextDouble() - 0.5);
        }

        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        return [.. v.Select(x => x / norm)];
    }

    private async Task SeedAttributesAsync(Guid titleId, string? tone = null, decimal humor = 0.5m)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.TitleAttributes.Add(new TitleAttributes
        {
            TitleId = titleId,
            Darkness = 0.5m,
            Pacing = 0.5m,
            Complexity = 0.5m,
            EmotionalIntensity = 0.5m,
            Humor = humor,
            Optimism = 0.5m,
            EnsembleVsSolo = 0.5m,
            Tone = tone,
            Themes = [],
            ExtractorModel = "fixture",
            ExtractorVersion = 1,
            Status = AttributeExtractionStatus.Done,
            ExtractedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class UnavailableEmbeddings : Domain.Ports.IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("No embedding provider.");
    }

    /// <summary>A judge model that is or is not configured; it is never asked to judge here.</summary>
    private sealed class StubJudgeModel(bool available) : Application.Search.AskReel.Engine.IRequestJudgeModel
    {
        public bool IsAvailable => available;

        public string ModelId => "stub-judge";

        public string CacheSalt => "stub-judge";

        public Task<Application.Search.AskReel.Engine.JudgeBatchResult> JudgeBatchAsync(
            Application.Search.AskReel.Engine.JudgeInput input, CancellationToken ct) =>
            throw new InvalidOperationException("Retrieval never judges.");
    }

    private async Task<RetrievalResult> RetrieveAsync(
        RetrievalQuery query,
        Domain.Ports.IEmbeddingProvider? provider = null,
        Application.Search.AskReel.Engine.IRequestJudgeModel? judge = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var embeddings = new ConceptEmbeddings(provider ?? new StubEmbeddingProvider(), cache, NullLogger<ConceptEmbeddings>.Instance);
        var retriever = new HybridRetriever(
            db,
            eligibility,
            new SupplyReporter(db, eligibility),
            new ConceptResolver(new KeywordVocabulary(db, cache), embeddings, db, NullLogger<ConceptResolver>.Instance),
            new FullTextArm(db),
            embeddings,
            new KeywordNeighbours(db, eligibility, new KeywordVocabulary(db, cache)),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HybridRetriever>.Instance,
            judge);
        return await retriever.RetrieveAsync(query, default);
    }

    /// <summary>The vector <see cref="StubEmbeddingProvider"/> returns for <paramref name="text"/>.</summary>
    private static float[] StubVector(string text) =>
        new StubEmbeddingProvider().EmbedAsync([text.Trim().ToLowerInvariant()]).GetAwaiter().GetResult()[0];

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"hybrid-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task SeedWatchAsync(Guid accountId, Guid titleId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            LastWatchedAt = DateTime.UtcNow,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTitleAsync(Action<Title> shape, float[]? vector = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"hybrid-fixture {Guid.NewGuid():N}",
            Language = "en",
            Genres = ["action"],
            Year = 2015,
            TmdbVoteAverage = 7.5m,
            TmdbVoteCount = 900,
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        shape(title);
        db.Add(title);
        if (vector is not null)
        {
            db.Add(new TitleEmbedding
            {
                TitleId = title.Id,
                Embedding = new Vector(vector),
                EmbeddingModel = "text-embedding-3-small",
                SourceTextHash = "fixture",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return title.Id;
    }
}
