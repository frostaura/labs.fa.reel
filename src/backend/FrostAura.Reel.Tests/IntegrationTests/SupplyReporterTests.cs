using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// SupplyReport v0 and the facet retriever on real Postgres. Every test tags its titles with a
/// keyword no other fixture uses, so the population is exactly what the test seeded and the counts
/// can be asserted to ±0 on a shared catalogue — the same bar the founder's ninja scenario is held to.
///
/// What is pinned, and why each one matters to an honest "none left":
/// 1. the partition is exact — seen, saved/dismissed, held out by a condition, eligible, and inside
///    eligible unscored / under the bar / clearing it — so no title is counted twice or lost;
/// 2. the vote floor removes low-vote titles from every count and reports them once;
/// 3. "already seen" is never a relaxation, and a knockout counts only footprint-free titles;
/// 4. one account's footprint never leaks into another account's supply;
/// 5. the retriever applies eligibility inside, honours Exclude and carries topical evidence;
/// 6. the API container serves the contract.
/// </summary>
[Collection("postgres")]
public class SupplyReporterTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public void The_container_serves_the_contract_with_one_reporter_per_scope()
    {
        if (!fixture.Available)
        {
            return;
        }

        using var scope = api.Services.CreateScope();
        Assert.IsType<HybridRetriever>(scope.ServiceProvider.GetRequiredService<IAskReelRetriever>());
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<SupplyReporter>(),
            scope.ServiceProvider.GetRequiredService<ISupplyReporter>());
    }

    [Fact]
    public async Task Supply_partitions_every_matching_title_into_exactly_one_bucket()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");
        await SeedFilterAsync(accountId, FilterKind.Language, "en");
        await SeedFilterAsync(accountId, FilterKind.MinCrowdRating, "6.0");
        await SeedFilterAsync(accountId, FilterKind.MinYear, "2000");
        await SeedFilterAsync(accountId, FilterKind.MinPredictedRating, "6.0");
        var activeArtifact = await SeedArtifactAsync(accountId, ArtifactStatus.Active, version: 2);

        // Footprint: two seen (one watched, one only rated), two saved/dismissed.
        var watched = await SeedTitleAsync(topic);
        var rated = await SeedTitleAsync(topic);
        var watchedComedy = await SeedTitleAsync(topic, t => t.Genres = ["comedy"]);
        var saved = await SeedTitleAsync(topic);
        var notInterested = await SeedTitleAsync(topic);
        await SeedWatchAsync(accountId, watched);
        await SeedWatchAsync(accountId, watchedComedy);
        await SeedRatingAsync(accountId, rated);
        await SeedReactionAsync(accountId, saved, ReactionKind.SaveForLater);
        await SeedReactionAsync(accountId, notInterested, ReactionKind.NotInterested);

        // Held out by one standing condition each.
        var comedy = await SeedTitleAsync(topic, t => t.Genres = ["comedy", "action"]);
        var french = await SeedTitleAsync(topic, t => t.Language = "fr");
        var weak = await SeedTitleAsync(topic, t => t.TmdbVoteAverage = 4.2m);
        var old = await SeedTitleAsync(topic, t => t.Year = 1984);

        // Eligible: clears the bar, under it, exactly on it at badge precision, scored only by an
        // older artifact (so unscored for the active one), never scored.
        var clears = await SeedTitleAsync(topic);
        var under = await SeedTitleAsync(topic);
        var roundsUp = await SeedTitleAsync(topic);
        var staleScore = await SeedTitleAsync(topic);
        var neverScored = await SeedTitleAsync(topic);
        await SeedScoreAsync(accountId, clears, activeArtifact, 7.4m);
        await SeedScoreAsync(accountId, under, activeArtifact, 5.94m);
        await SeedScoreAsync(accountId, roundsUp, activeArtifact, 5.95m);
        await SeedScoreAsync(accountId, staleScore, Guid.NewGuid(), 8.0m);

        // Below the vote floor: would be eligible, and is counted only as a low-vote match.
        await SeedTitleAsync(topic, t => t.TmdbVoteCount = 12);

        // Off-topic: eligible but not part of the request at all.
        await SeedTitleAsync(UniqueTopic());

        var report = await ReportAsync(accountId, Concept(topic));

        Assert.Equal(14, report.MatchingInCatalogue);
        Assert.Equal(3, report.AlreadySeen);
        Assert.Equal(2, report.SavedOrDismissed);
        Assert.Equal(4, report.ExcludedByConditions);
        Assert.Equal(5, report.Eligible);
        Assert.Equal(1, report.BelowFloor);
        Assert.Equal(2, report.Unscored);
        Assert.Equal(2, report.ClearingFloor);
        Assert.Equal(1, report.LowVoteMatches);
        Assert.Equal(CrowdVotes.MinRelaxVotes, report.MinVotes);
        Assert.Equal(6.0m, report.PredictedFloor);
        Assert.Equal(
            report.MatchingInCatalogue,
            report.AlreadySeen + report.SavedOrDismissed + report.ExcludedByConditions + report.Eligible);

        // Each rule unlocks exactly its own title. The watched comedy is NOT unlocked by relaxing
        // comedy: "already seen" is never a relaxation, under any rule.
        Assert.Equal(
            new (FilterKind, string, int)[]
            {
                (FilterKind.ExcludeGenre, "comedy", 1),
                (FilterKind.Language, "en", 1),
                (FilterKind.MinCrowdRating, "6.0", 1),
                (FilterKind.MinYear, "2000", 1),
            },
            report.Knockouts.Select(k => (k.Kind, k.Value, k.WouldUnlock)).ToArray());
        Assert.Equal([comedy], Single(report, FilterKind.ExcludeGenre).Sample.Select(s => s.TitleId));
        Assert.Equal([french], Single(report, FilterKind.Language).Sample.Select(s => s.TitleId));
        Assert.Equal([weak], Single(report, FilterKind.MinCrowdRating).Sample.Select(s => s.TitleId));
        Assert.Equal([old], Single(report, FilterKind.MinYear).Sample.Select(s => s.TitleId));
        Assert.Equal("English only", Single(report, FilterKind.Language).Label);
        Assert.All(report.Knockouts, k => Assert.NotNull(k.ContentFilterId));
        Assert.True(report.KnockoutsMeasured);

        // The predicate path a broad request takes counts exactly the same titles.
        var byPredicate = await ReportAsync(accountId, Concept(topic), idNarrowingLimit: 0);
        Assert.Equal(Shape(report), Shape(byPredicate));
    }

    [Fact]
    public async Task Relaxations_are_skipped_only_for_a_broad_request_with_plenty_clearing_the_bar()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");
        await SeedFilterAsync(accountId, FilterKind.MinPredictedRating, "6.0");
        var artifact = await SeedArtifactAsync(accountId, ArtifactStatus.Active, version: 1);
        for (var i = 0; i < SupplyReporter.ThinSupply; i++)
        {
            await SeedScoreAsync(accountId, await SeedTitleAsync(topic), artifact, 7.0m);
        }

        await SeedTitleAsync(topic, t => t.Genres = ["comedy"]);

        // Small population: measured, whatever the supply.
        var small = await ReportAsync(accountId, Concept(topic));
        Assert.True(small.KnockoutsMeasured);
        Assert.Equal(1, Single(small, FilterKind.ExcludeGenre).WouldUnlock);

        // Broad (forced) with exactly ThinSupply clearing: skipped, and says so.
        var broad = await ReportAsync(accountId, Concept(topic), idNarrowingLimit: 0);
        Assert.Equal(SupplyReporter.ThinSupply, broad.ClearingFloor);
        Assert.False(broad.KnockoutsMeasured);
        Assert.Empty(broad.Knockouts);

        // One fewer clearing the bar makes it thin again: measured on the broad path too.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var one = db.TitleScores.First(s => s.AccountId == accountId);
            one.PredictedRating = 3.0m;
            await db.SaveChangesAsync();
        }

        var thin = await ReportAsync(accountId, Concept(topic), idNarrowingLimit: 0);
        Assert.Equal(SupplyReporter.ThinSupply - 1, thin.ClearingFloor);
        Assert.Equal(1, thin.BelowFloor);
        Assert.True(thin.KnockoutsMeasured);
        Assert.Equal(1, Single(thin, FilterKind.ExcludeGenre).WouldUnlock);
    }

    [Fact]
    public async Task Vote_floor_counts_the_source_the_badge_uses_and_can_be_lowered()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync();

        // Trakt has a crowd (≥ 50 votes), so the badge reads Trakt and ITS count decides — 9,000.
        await SeedTitleAsync(topic, t => { t.TmdbVoteCount = 10; t.TraktRating = 7.0m; t.TraktVotes = 9_000; });
        // A thin Trakt sample yields to TMDB, whose 10 votes are under the floor.
        await SeedTitleAsync(topic, t => { t.TmdbVoteCount = 10; t.TraktRating = 7.0m; t.TraktVotes = 40; });
        // No TMDB rating: Trakt's rating and Trakt's votes decide.
        await SeedTitleAsync(topic, t => { t.TmdbVoteAverage = null; t.TmdbVoteCount = 0; t.TraktRating = 7.0m; t.TraktVotes = 80; });
        await SeedTitleAsync(topic, t => { t.TmdbVoteAverage = null; t.TmdbVoteCount = 0; t.TraktRating = 7.0m; t.TraktVotes = 49; });
        // Exactly on the floor counts.
        await SeedTitleAsync(topic, t => t.TmdbVoteCount = CrowdVotes.MinRelaxVotes);

        var floored = await ReportAsync(accountId, Concept(topic));
        Assert.Equal(3, floored.MatchingInCatalogue);
        Assert.Equal(2, floored.LowVoteMatches);
        Assert.Equal(3, floored.Eligible);

        var unfloored = await ReportAsync(accountId, Concept(topic), minVotes: 0);
        Assert.Equal(5, unfloored.MatchingInCatalogue);
        Assert.Equal(0, unfloored.LowVoteMatches);
        Assert.Equal(0, unfloored.MinVotes);
    }

    [Fact]
    public async Task Hard_constraints_match_by_containment_and_conjunction()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var second = UniqueTopic();
        var accountId = await SeedAccountAsync();

        var contained = await SeedTitleAsync(topic, t => t.Keywords = [$"female {topic}"]);
        var both = await SeedTitleAsync(topic, t => t.Keywords = [topic, second]);
        var show = await SeedTitleAsync(topic, t => t.MediaType = MediaType.Show);
        var nineties = await SeedTitleAsync(topic, t => t.Year = 1995);
        var undated = await SeedTitleAsync(topic, t => { t.Year = null; t.ReleasedAt = null; t.FirstAiredAt = null; });
        var longOne = await SeedTitleAsync(topic, t => t.RuntimeMinutes = 150);
        var noRuntime = await SeedTitleAsync(topic, t => t.RuntimeMinutes = null);
        var plural = await SeedTitleAsync(topic, t => t.Keywords = [$"{topic}s"]);

        // A label inside another word is not the topic: "heist" once matched the keyword "atheist".
        await SeedTitleAsync(topic, t => t.Keywords = [$"a{topic}"]);
        await SeedTitleAsync(topic, t => t.Keywords = [$"the a{topic} debate"]);

        Assert.Equal(8, (await ReportAsync(accountId, Concept(topic))).MatchingInCatalogue);

        // Two concepts AND together.
        var twoConcepts = HardConstraints.None with
        {
            Concepts = [new ConceptTerm(topic, [topic], []), new ConceptTerm(second, [second], [])],
        };
        Assert.Equal(1, (await ReportAsync(accountId, twoConcepts)).MatchingInCatalogue);

        // Media scope.
        Assert.Equal(7, (await ReportAsync(accountId, Concept(topic) with { Media = MediaScope.Movies })).MatchingInCatalogue);
        Assert.Equal(1, (await ReportAsync(accountId, Concept(topic) with { Media = MediaScope.Shows })).MatchingInCatalogue);

        // An inclusive year window lets the undated title through, as the standing year rule does.
        var window = Concept(topic) with { Years = new YearRange(1990, 1999) };
        Assert.Equal(2, (await ReportAsync(accountId, window)).MatchingInCatalogue);

        // A runtime ceiling needs a known runtime.
        var shortOnes = Concept(topic) with { MaxRuntimeMinutes = 120 };
        Assert.Equal(6, (await ReportAsync(accountId, shortOnes)).MatchingInCatalogue);

        // A concept whose labels are empty falls back to the user's words.
        Assert.Equal(8, (await ReportAsync(accountId, HardConstraints.None with
        {
            Concepts = [new ConceptTerm(topic, [], [])],
        })).MatchingInCatalogue);

        // A genre concept matches by slug, AND-ed with the topic.
        HardConstraints WithGenre(string genre) => HardConstraints.None with
        {
            Concepts = [new ConceptTerm(topic, [topic], []), new ConceptTerm(genre, [], [genre])],
        };
        Assert.Equal(8, (await ReportAsync(accountId, WithGenre("drama"))).MatchingInCatalogue);
        Assert.Equal(0, (await ReportAsync(accountId, WithGenre("horror"))).MatchingInCatalogue);

        _ = (contained, both, show, nineties, undated, longOne, noRuntime, plural);
    }

    [Fact]
    public async Task A_title_carrying_the_topic_only_in_its_name_counts_as_a_whole_word()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The founder's ninja report missed "3 Ninjas", "Alien vs. Ninja" and "Batman Ninja vs.
        // Yakuza League": TMDB tags none of them "ninja". These fixtures carry the topic only in the
        // name, in each of the shapes those titles take, plus the near-misses the arm must refuse.
        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync();
        var untagged = new[] { "unrelated" };
        var plural = await SeedTitleAsync(topic, t => { t.Name = $"3 {topic}s"; t.Keywords = untagged; });
        var trailing = await SeedTitleAsync(topic, t => { t.Name = $"Alien vs. {topic.ToUpperInvariant()}"; t.Keywords = untagged; });
        var middle = await SeedTitleAsync(topic, t => { t.Name = $"Batman {topic} vs. Yakuza League"; t.Keywords = untagged; });
        var seenByName = await SeedTitleAsync(topic, t => { t.Name = $"{topic}s Kick Back"; t.Keywords = untagged; });
        await SeedWatchAsync(accountId, seenByName);
        var both = await SeedTitleAsync(topic, t => t.Name = $"{topic} Scroll");
        var keywordOnly = await SeedTitleAsync(topic);
        await SeedTitleAsync(topic, t => { t.Name = $"{topic}baby"; t.Keywords = untagged; });
        await SeedTitleAsync(topic, t => { t.Name = $"The Lego {topic}go Movie"; t.Keywords = untagged; });
        await SeedTitleAsync(topic, t => { t.Name = $"x{topic}"; t.Keywords = untagged; });
        var showByName = await SeedTitleAsync(topic, t => { t.Name = $"American {topic} Warrior"; t.Keywords = untagged; t.MediaType = MediaType.Show; });
        await SeedTitleAsync(topic, t => { t.Name = $"Cumbia {topic}"; t.Keywords = untagged; t.TmdbVoteCount = 12; });

        var report = await ReportAsync(accountId, Concept(topic));

        // plural, trailing, middle, seenByName, both, keywordOnly, showByName — "both" once.
        Assert.Equal(7, report.MatchingInCatalogue);
        Assert.Equal(1, report.AlreadySeen);
        Assert.Equal(6, report.Eligible);
        Assert.Equal(1, report.LowVoteMatches);
        Assert.Equal(6, (await ReportAsync(accountId, Concept(topic) with { Media = MediaScope.Movies })).MatchingInCatalogue);

        // The predicate path a broad request takes counts the same titles.
        Assert.Equal(Shape(report), Shape(await ReportAsync(accountId, Concept(topic), idNarrowingLimit: 0)));

        // A label with regex metacharacters is matched literally, never as a pattern.
        var dotted = $"{topic}.v2";
        var literal = await SeedTitleAsync(topic, t => { t.Name = $"Return of {dotted}"; t.Keywords = untagged; });
        await SeedTitleAsync(topic, t => { t.Name = $"Return of {topic}xv2"; t.Keywords = untagged; });
        Assert.Equal(1, (await ReportAsync(accountId, HardConstraints.None with
        {
            Concepts = [new ConceptTerm(dotted, [dotted], [])],
        })).MatchingInCatalogue);

        // The retriever serves the name-only titles, each with evidence naming the label.
        var result = await RetrieveAsync(new RetrievalQuery(accountId, Concept(topic), SoftPreferences.None, [], Want: 20));
        var evidence = result.Candidates.ToDictionary(c => c.TitleId, c => c.Evidence);
        Assert.Equal([$"name:{topic}"], evidence[plural]);
        Assert.Equal([$"name:{topic}"], evidence[trailing]);
        Assert.Equal([$"name:{topic}"], evidence[middle]);
        Assert.Equal([$"keyword:{topic}", $"name:{topic}"], evidence[both]);
        Assert.Equal([$"keyword:{topic}"], evidence[keywordOnly]);
        Assert.False(evidence.ContainsKey(seenByName));
        _ = (showByName, literal);
    }

    [Fact]
    public async Task Titles_already_shown_here_are_cut_from_the_offerable_supply_and_from_every_knockout()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");
        await SeedFilterAsync(accountId, FilterKind.MinPredictedRating, "6.0");
        var artifact = await SeedArtifactAsync(accountId, ArtifactStatus.Active, version: 1);

        var shownClears = await SeedTitleAsync(topic);
        var freshClears = await SeedTitleAsync(topic);
        var shownUnder = await SeedTitleAsync(topic);
        var shownUnscored = await SeedTitleAsync(topic);
        var shownSeen = await SeedTitleAsync(topic);
        var shownComedy = await SeedTitleAsync(topic, t => t.Genres = ["comedy"]);
        var freshComedy = await SeedTitleAsync(topic, t => t.Genres = ["comedy"]);
        var shownOffTopic = await SeedTitleAsync(UniqueTopic());
        await SeedScoreAsync(accountId, shownClears, artifact, 7.0m);
        await SeedScoreAsync(accountId, freshClears, artifact, 6.5m);
        await SeedScoreAsync(accountId, shownUnder, artifact, 5.0m);
        await SeedScoreAsync(accountId, shownOffTopic, artifact, 9.0m);
        await SeedWatchAsync(accountId, shownSeen);
        Guid[] shown = [shownClears, shownUnder, shownUnscored, shownSeen, shownComedy, shownOffTopic];

        var none = await ReportAsync(accountId, Concept(topic));
        var report = await ReportAsync(accountId, Concept(topic), shown: shown);

        // Only a shown title that was offerable supply (eligible, scored, clearing the bar) is cut;
        // shown titles under the bar, unscored, seen or off-topic stay in their own buckets.
        Assert.Equal(0, none.AlreadyShown);
        Assert.Equal(2, none.Offerable);
        Assert.Equal(1, report.AlreadyShown);
        Assert.Equal(1, report.Offerable);
        Assert.Equal(none.ClearingFloor, report.ClearingFloor);
        Assert.Equal(
            (none.MatchingInCatalogue, none.AlreadySeen, none.Eligible, none.BelowFloor, none.Unscored),
            (report.MatchingInCatalogue, report.AlreadySeen, report.Eligible, report.BelowFloor, report.Unscored));

        // Relaxing "comedy" would unlock two comedies, but one was already shown: it unlocks one.
        Assert.Equal(2, Single(none, FilterKind.ExcludeGenre).WouldUnlock);
        var comedy = Single(report, FilterKind.ExcludeGenre);
        Assert.Equal(1, comedy.WouldUnlock);
        Assert.Equal([freshComedy], comedy.Sample.Select(s => s.TitleId));

        // The predicate path counts the same.
        var byPredicate = await ReportAsync(accountId, Concept(topic), idNarrowingLimit: 0, shown: shown);
        Assert.Equal((Shape(report), report.AlreadyShown), (Shape(byPredicate), byPredicate.AlreadyShown));

        // With no bar set, every scored eligible title is offerable, so a shown one is cut.
        var noBar = await SeedAccountAsync();
        var noBarArtifact = await SeedArtifactAsync(noBar, ArtifactStatus.Active, version: 1);
        var low = await SeedTitleAsync(topic, t => t.Keywords = [$"{topic} nobar"]);
        await SeedScoreAsync(noBar, low, noBarArtifact, 2.0m);
        var noBarConcept = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [$"{topic} nobar"], [])] };
        var noBarReport = await ReportAsync(noBar, noBarConcept, shown: [low]);
        Assert.Equal((1, 1, 0), (noBarReport.ClearingFloor, noBarReport.AlreadyShown, noBarReport.Offerable));

        // The no-shown overload on the port reports nothing shown.
        await using var db = fixture.CreateContext(new AccountContext());
        ISupplyReporter port = new SupplyReporter(db, new EligibilityQueryBuilder(db));
        Assert.Equal(0, (await port.ReportAsync(accountId, Concept(topic), CancellationToken.None)).AlreadyShown);
        Assert.Equal(1, (await port.ReportAsync(accountId, Concept(topic), shown, CancellationToken.None)).AlreadyShown);
        _ = freshClears;
    }

    [Fact]
    public async Task People_constraints_match_credits_by_id_or_name_and_role()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync();
        var personId = Guid.NewGuid();
        var name = $"Director {Guid.NewGuid():N}";
        var directed = await SeedTitleAsync(topic);
        var actedIn = await SeedTitleAsync(topic);
        await SeedTitleAsync(topic);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.Persons.Add(new Person { Id = personId, Name = name, TmdbId = Random.Shared.NextInt64(1, long.MaxValue) });
            db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = directed, PersonId = personId, Role = CreditRole.Director });
            db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = actedIn, PersonId = personId, Role = CreditRole.Actor, CastOrder = 0 });
            await db.SaveChangesAsync();
        }

        HardConstraints With(PersonConstraint person) => Concept(topic) with { People = [person] };
        Assert.Equal(1, (await ReportAsync(accountId, With(new PersonConstraint(name, personId, PersonRole.Director)))).MatchingInCatalogue);
        Assert.Equal(1, (await ReportAsync(accountId, With(new PersonConstraint(name.ToUpperInvariant(), null, PersonRole.Cast)))).MatchingInCatalogue);
        Assert.Equal(2, (await ReportAsync(accountId, With(new PersonConstraint(name, null, PersonRole.Any)))).MatchingInCatalogue);
        Assert.Equal(0, (await ReportAsync(accountId, With(new PersonConstraint("Nobody At All", null, PersonRole.Any)))).MatchingInCatalogue);
    }

    [Fact]
    public async Task Default_english_only_is_a_knockout_with_no_row_and_maturity_holds_titles_out()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync(maturityCeiling: "PG-13");
        var korean = await SeedTitleAsync(topic, t => { t.Language = "ko"; t.Certification = "PG"; });
        var restricted = await SeedTitleAsync(topic, t => t.Certification = "R");
        var restrictedKorean = await SeedTitleAsync(topic, t => { t.Language = "ko"; t.Certification = "R"; });
        var fine = await SeedTitleAsync(topic, t => t.Certification = "PG");

        var report = await ReportAsync(accountId, Concept(topic));

        Assert.Equal(4, report.MatchingInCatalogue);
        Assert.Equal(1, report.Eligible);
        Assert.Equal(3, report.ExcludedByConditions);
        // No active artifact: every eligible title is unscored, and none is "under the bar".
        Assert.Equal(1, report.Unscored);
        Assert.Equal(0, report.BelowFloor);
        Assert.Null(report.PredictedFloor);

        // Relaxing the language default unlocks the Korean title but not the R-rated Korean one: the
        // maturity ceiling is not a relaxable condition and still binds.
        var language = Assert.Single(report.Knockouts);
        Assert.Equal(FilterKind.Language, language.Kind);
        Assert.Null(language.ContentFilterId);
        Assert.Equal([korean], language.Sample.Select(s => s.TitleId));
        _ = (restricted, restrictedKorean, fine);
    }

    [Fact]
    public async Task One_accounts_footprint_never_reaches_another_accounts_supply()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        var shared = await SeedTitleAsync(topic);
        await SeedWatchAsync(theirs, shared);
        await SeedFilterAsync(theirs, FilterKind.ExcludeGenre, "drama");

        var report = await ReportAsync(mine, Concept(topic));

        Assert.Equal(1, report.Eligible);
        Assert.Equal(0, report.AlreadySeen);
        Assert.Empty(report.Knockouts);
    }

    [Fact]
    public async Task Retriever_returns_only_eligible_topical_titles_with_evidence_and_honours_exclude()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync();
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");

        var popular = await SeedTitleAsync(topic, t => t.TmdbVoteCount = 9_000);
        var middling = await SeedTitleAsync(topic, t => t.TmdbVoteCount = 4_000);
        var niche = await SeedTitleAsync(topic, t => { t.TmdbVoteCount = 300; t.Keywords = [$"{topic} clan"]; });
        var shown = await SeedTitleAsync(topic, t => t.TmdbVoteCount = 8_000);
        var seen = await SeedTitleAsync(topic, t => t.TmdbVoteCount = 7_000);
        await SeedTitleAsync(topic, t => { t.TmdbVoteCount = 6_000; t.Genres = ["comedy"]; });
        await SeedWatchAsync(accountId, seen);

        var result = await RetrieveAsync(new RetrievalQuery(accountId, Concept(topic), SoftPreferences.None, [shown], Want: 10));

        Assert.Equal([popular, middling, niche], result.Candidates.Select(c => c.TitleId));
        Assert.Equal(
            result.Candidates.Select(c => c.FusedScore).OrderByDescending(s => s),
            result.Candidates.Select(c => c.FusedScore));
        Assert.Equal(1d / (FacetRetriever.RrfK + 1), result.Candidates[0].FusedScore);
        Assert.All(result.Candidates, c => Assert.Null(c.Similarity));
        Assert.Equal([$"keyword:{topic}"], result.Candidates[0].Evidence);
        Assert.Equal([$"keyword:{topic} clan"], result.Candidates[2].Evidence);

        // The supply is the request's, not the turn's: the excluded-by-turn title is still eligible.
        Assert.Equal(4, result.Supply.Eligible);
        Assert.True(result.Timings.ElapsedMs.ContainsKey("supply"));
        Assert.True(result.Timings.ElapsedMs.ContainsKey("facet"));

        var capped = await RetrieveAsync(new RetrievalQuery(accountId, Concept(topic), SoftPreferences.None, [], Want: 2));
        Assert.Equal([popular, shown], capped.Candidates.Select(c => c.TitleId));

        var none = await RetrieveAsync(new RetrievalQuery(accountId, Concept(topic), SoftPreferences.None, [], Want: 0));
        Assert.Empty(none.Candidates);
        Assert.Equal(4, none.Supply.Eligible);
    }

    [Fact]
    public async Task Retriever_applies_the_maturity_ceiling_inside()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = UniqueTopic();
        var accountId = await SeedAccountAsync(maturityCeiling: "PG");
        var allowed = await SeedTitleAsync(topic, t => { t.Certification = "PG"; t.TmdbVoteCount = 100; });
        await SeedTitleAsync(topic, t => { t.Certification = "R"; t.TmdbVoteCount = 9_000; });

        var result = await RetrieveAsync(new RetrievalQuery(accountId, Concept(topic), SoftPreferences.None, [], Want: 5));

        Assert.Equal([allowed], result.Candidates.Select(c => c.TitleId));
    }

    private static ConditionKnockout Single(SupplyReport report, FilterKind kind) =>
        Assert.Single(report.Knockouts, k => k.Kind == kind);

    private static string UniqueTopic() => $"supplytopic{Guid.NewGuid():N}";

    private static HardConstraints Concept(string topic) =>
        HardConstraints.None with { Concepts = [new ConceptTerm(topic, [topic], [])] };

    private static string Shape(SupplyReport report) =>
        string.Join(
            "|",
            report.MatchingInCatalogue, report.AlreadySeen, report.SavedOrDismissed, report.ExcludedByConditions,
            report.Eligible, report.BelowFloor, report.Unscored, report.LowVoteMatches,
            string.Join(",", report.Knockouts.Select(k => $"{k.Kind}/{k.Value}/{k.WouldUnlock}/{string.Join("+", k.Sample.Select(s => s.TitleId))}")));

    private async Task<SupplyReport> ReportAsync(
        Guid accountId,
        HardConstraints hard,
        int minVotes = CrowdVotes.MinRelaxVotes,
        int idNarrowingLimit = SupplyReporter.IdNarrowingLimit,
        IReadOnlyCollection<Guid>? shown = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new SupplyReporter(db, new EligibilityQueryBuilder(db))
            .ReportAsync(accountId, hard, minVotes, idNarrowingLimit, shown ?? [], CancellationToken.None);
    }

    private async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var eligibility = new EligibilityQueryBuilder(db);
        return await new FacetRetriever(db, eligibility, new SupplyReporter(db, eligibility))
            .RetrieveAsync(query, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync(string? maturityCeiling = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"supply-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        account.Settings.MaturityCeiling = maturityCeiling;
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// A title that passes every rule the tests configure unless <paramref name="shape"/> says
    /// otherwise: English, 2010, crowd 7.5 on 1,000 TMDB votes, drama, 100 minutes, tagged with the topic.
    /// </summary>
    private async Task<Guid> SeedTitleAsync(string topic, Action<Title>? shape = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"supply-fixture {Guid.NewGuid():N}",
            Language = "en",
            Year = 2010,
            RuntimeMinutes = 100,
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            Keywords = [topic],
            TmdbVoteAverage = 7.5m,
            TmdbVoteCount = 1_000,
            // Bottom of the popularity order, so these fixtures never displace another integration
            // test's top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        shape?.Invoke(title);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedFilterAsync(Guid accountId, FilterKind kind, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWatchAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = 7,
            RatedAt = DateTime.UtcNow,
            FirstRatedAt = DateTime.UtcNow,
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedReactionAsync(Guid accountId, Guid titleId, ReactionKind kind)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedArtifactAsync(Guid accountId, ArtifactStatus status, int version)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var id = Guid.NewGuid();
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = id,
            AccountId = accountId,
            Version = version,
            Status = status,
            // The schema version must be current or the global query filter hides the artifact.
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            TrainingRunId = Guid.NewGuid(),
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedScoreAsync(Guid accountId, Guid titleId, Guid artifactId, decimal predicted)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleScores.Add(new TitleScore
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            ModelArtifactId = artifactId,
            PredictedRating = predicted,
            ScoredAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
