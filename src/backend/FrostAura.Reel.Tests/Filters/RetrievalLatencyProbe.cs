using System.Diagnostics;
using System.Globalization;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The measurements behind WS-3's latency acceptance, against a real account's data: the cost meter
/// (p95 ≤ 150 ms over 20 uncached drafts). Each figure quoted in the code comments and the lane
/// report can be reproduced from here rather than taken on trust.
///
/// <para><b>Opt-in only, never in CI.</b> It does nothing unless <c>REEL_RETRIEVAL_PROBE=1</c>, and
/// then needs <c>REEL_PROBE_CONNECTION_STRING</c> (a SLOT copy of an account's data — never the live
/// database) and optionally <c>REEL_PROBE_ACCOUNT_ID</c> (default: the account with the most
/// ratings). It reads and writes nothing, and calls no paid provider. Run with
/// <c>--filter FullyQualifiedName~RetrievalLatencyProbe --logger "console;verbosity=detailed"</c>,
/// with nothing else loading the machine (latency figures are only valid unloaded).</para>
/// </summary>
public class RetrievalLatencyProbe(ITestOutputHelper output)
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("REEL_PROBE_CONNECTION_STRING");

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("REEL_RETRIEVAL_PROBE") == "1" && !string.IsNullOrWhiteSpace(ConnectionString);

    [Fact]
    public async Task Cost_meter_p95_over_twenty_uncached_drafts()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, saved) = await AccountAsync();
        var drafts = Drafts(saved);

        // One warm-up so connection setup is not billed to the first draft.
        await MeasureAsync(accountId, saved);
        var elapsed = new List<double>();
        foreach (var draft in drafts)
        {
            var clock = Stopwatch.StartNew();
            var report = await MeasureAsync(accountId, draft);
            elapsed.Add(clock.Elapsed.TotalMilliseconds);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{clock.Elapsed.TotalMilliseconds,7:F1} ms  eligible {report.Eligible,5} of {report.Total}  rules {report.ByRule.Count}"));
        }

        output.WriteLine(Summary("cost meter", elapsed));
    }

    /// <summary>
    /// The same twenty drafts on real data, for correctness rather than speed: each draft's eligible
    /// count and every rule's marginal must equal the subtractive counts (count, then re-count with
    /// that one rule relaxed), as <c>FilterCostMeterTests.Single_pass_marginals_equal_the_subtractive_counts</c>
    /// pins on fixtures. Drafts with two or more keyword exclusions run their keyword bits behind the
    /// AnyKeyword gate (R1-20), so this is where the gate meets a real catalogue.
    /// </summary>
    [Fact]
    public async Task Cost_meter_marginals_equal_the_subtractive_counts_on_every_draft()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, saved) = await AccountAsync();
        var gated = 0;
        foreach (var draft in Drafts(saved))
        {
            var context = new AccountContext();
            context.SetAccount(accountId);
            await using var db = Context(accountId);
            var builder = new EligibilityQueryBuilder(db);
            var report = await new FilterCostMeter(db, builder, context, new MemoryCache(new MemoryCacheOptions())).MeasureAsync(draft);
            var eligible = await builder.EligibleTitles(accountId, draft).CountAsync();
            Assert.Equal(eligible, report.Eligible);
            gated += draft.ExcludeKeywords.Count >= 2 ? 1 : 0;
            foreach (var rule in draft.Rules())
            {
                var without = await builder.EligibleTitles(accountId, draft.Without(rule)).CountAsync();
                var marginal = report.ByRule.Single(r => r.Kind == rule.Kind.ToString() && r.Value == rule.Value).Marginal;
                Assert.Equal((rule.Kind, rule.Value, Math.Max(0, without - eligible)), (rule.Kind, rule.Value, marginal));
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"20 drafts agree, {gated} of them behind the keyword gate"));
    }

    /// <summary>
    /// The drafts the cost-meter figure is taken over, always exactly twenty and all distinct: the saved
    /// set, each one-rule relaxation of it, edits a user makes in the editor, and then single-chip edits
    /// (one more genre or keyword excluded, a different crowd floor or year bound) until there are
    /// twenty. An account with few saved rules would otherwise be measured over fewer drafts than the
    /// acceptance names.
    /// </summary>
    internal static IReadOnlyList<FilterRuleSet> Drafts(FilterRuleSet saved)
    {
        const int Wanted = 20;
        var drafts = new List<FilterRuleSet> { saved };
        foreach (var rule in saved.Rules())
        {
            drafts.Add(saved.Without(rule));
        }

        drafts.Add(saved.WithExclusions(["horror"], null));
        drafts.Add(saved.WithExclusions(["thriller", "war"], ["gore"]));
        drafts.Add(saved.WithExclusions(null, ["zombie", "shark"]));
        drafts.Add(FilterRuleSet.Create(saved.ExcludeGenres, ["drama", "crime"], saved.ExcludeKeywords, saved.Languages, saved.MinCrowdRating, saved.MinYear, saved.MaxYear));

        string[] genrePads = ["romance", "documentary", "animation", "western", "musical", "family", "comedy", "fantasy", "science-fiction", "mystery", "history", "music"];
        string[] keywordPads = ["vampire", "clown", "spider", "nudity", "torture", "kidnapping", "cannibal", "exorcism"];
        decimal[] floorPads = [5.5m, 6m, 7m, 7.5m, 8m];
        int[] minYearPads = [1970, 1990, 2000, 2010, 2015];
        int[] maxYearPads = [1999, 2005, 2012, 2020];
        FilterRuleSet With(decimal? floor, int? minYear, int? maxYear) => FilterRuleSet.Create(
            saved.ExcludeGenres, saved.IncludeGenres, saved.ExcludeKeywords, saved.Languages, floor, minYear, maxYear);
        drafts.AddRange(genrePads.Select(g => saved.WithExclusions([g], null)));
        drafts.AddRange(keywordPads.Select(k => saved.WithExclusions(null, [k])));
        drafts.AddRange(floorPads.Select(f => With(f, saved.MinYear, saved.MaxYear)));
        drafts.AddRange(minYearPads.Select(y => With(saved.MinCrowdRating, y, saved.MaxYear)));
        drafts.AddRange(maxYearPads.Select(y => With(saved.MinCrowdRating, saved.MinYear, y)));

        var distinct = drafts.DistinctBy(d => d.CacheKey()).Take(Wanted).ToList();
        return distinct.Count == Wanted
            ? distinct
            : throw new InvalidOperationException($"Only {distinct.Count} distinct drafts; the acceptance is a p95 over {Wanted}.");
    }

    /// <summary>
    /// The acceptance is a p95 over TWENTY uncached drafts. An account with few saved rules used to
    /// yield fewer (16 on the eval account, 5 on an account with none), so the quoted p95 was taken
    /// over a smaller sample than the criterion names. This runs in CI: it touches no database.
    /// </summary>
    [Theory]
    [InlineData("nothing saved")]
    [InlineData("eval-shaped")]
    [InlineData("pads overlap the saved set")]
    [InlineData("more rules than twenty")]
    public void The_cost_meter_figure_is_taken_over_exactly_twenty_distinct_drafts(string label)
    {
        var saved = SavedSets[label];
        var drafts = Drafts(saved);

        Assert.True(drafts.Count == 20, $"{label}: {drafts.Count} drafts");
        Assert.Equal(20, drafts.Select(d => d.CacheKey()).Distinct().Count());
        Assert.Equal(saved.CacheKey(), drafts[0].CacheKey());
    }

    private static readonly Dictionary<string, FilterRuleSet> SavedSets = new()
    {
        ["nothing saved"] = FilterRuleSet.Create(null, null, null, null, null, null, null),
        ["eval-shaped"] = FilterRuleSet.Create(["horror", "animation"], null, ["gore", "zombie"], ["en", "fr"], 6.5m, 1990, null),
        ["pads overlap the saved set"] = FilterRuleSet.Create(["romance", "documentary", "animation"], null, ["vampire", "clown"], ["*"], 6m, 1990, 2020),
        ["more rules than twenty"] = FilterRuleSet.Create(
            ["horror", "thriller", "war", "romance", "documentary", "western", "musical", "family", "comedy", "fantasy", "mystery", "history"],
            null,
            ["gore", "zombie", "shark", "vampire", "clown", "spider", "nudity", "torture", "suicide", "rape"],
            ["en"],
            6m,
            1980,
            2024),
    };

    /// <summary>The concepts the acceptance names, plus the golden facet scenarios'.</summary>
    private static readonly string[] Concepts =
    [
        "ninja", "samurai", "heist", "time loop", "courtroom", "survival at sea",
        "martial arts", "mafia", "hitman", "kaiju", "spy", "zombie", "vampire", "werewolf", "shark", "space",
        "cyberpunk", "dinosaur", "submarine", "chess", "boxing", "time travel", "found footage", "serial killer",
    ];

    [Fact]
    public async Task Concepts_resolve_and_the_facet_arm_answers_inside_its_budget()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, _) = await AccountAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = Context();
        var resolver = new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new UnavailableEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);
        var eligibility = new EligibilityQueryBuilder(db);

        await resolver.ResolveAsync(new ConceptTerm("warm-up", [], []), default);
        var facet = new List<double>();
        foreach (var words in Concepts)
        {
            var concept = await resolver.ResolveAsync(new ConceptTerm(words, [], []), default);
            var hard = new HardConstraints([concept], [], null, null, MediaScope.Any);
            var clock = Stopwatch.StartNew();
            var ids = await CrowdVotes.BestShrunkFirst(HardConstraintFilter.Apply(eligibility.EligibleTitles(accountId), db, hard))
                .Select(t => t.Id).Take(HybridRetriever.ArmDepth).ToListAsync();
            facet.Add(clock.Elapsed.TotalMilliseconds);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{words,-16} labels {concept.ExactLabels?.Count.ToString(CultureInfo.InvariantCulture) ?? "regex",5}  stems [{string.Join(", ", concept.CatalogueLabels)}]  genres [{string.Join(", ", concept.Genres)}]  eligible facet hits {ids.Count,3}  {clock.Elapsed.TotalMilliseconds,6:F1} ms"));
        }

        output.WriteLine(Summary("facet SQL", facet));
    }

    [Fact]
    public async Task Founder_ninja_supply_for_the_sql_fixture()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, _) = await AccountAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = Context();
        var eligibility = new EligibilityQueryBuilder(db);
        var resolver = new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new UnavailableEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);
        var concept = await resolver.ResolveAsync(new ConceptTerm("ninja movies", [], []), default);
        var hard = new HardConstraints([concept], [], null, null, MediaScope.Movies);
        var clock = Stopwatch.StartNew();
        var report = await new SupplyReporter(db, eligibility).ReportAsync(accountId, hard, default);
        output.WriteLine($"labels: {string.Join(" | ", concept.ExactLabels ?? [])}");
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"matching {report.MatchingInCatalogue} seen {report.AlreadySeen} savedOrDismissed {report.SavedOrDismissed} excluded {report.ExcludedByConditions} eligible {report.Eligible} belowFloor {report.BelowFloor} unscored {report.Unscored} lowVote {report.LowVoteMatches} ({clock.Elapsed.TotalMilliseconds:F0} ms)"));
        foreach (var k in report.Knockouts)
        {
            output.WriteLine($"knockout {k.Kind}/{k.Value}: {k.WouldUnlock}");
        }
    }

    [Fact]
    public async Task Semantic_arm_returns_the_full_k_under_selective_filters()
    {
        if (!Enabled)
        {
            return;
        }

        // The anchor is a real title's stored embedding (a reference), so no paid call is made; the
        // question is only whether a filtered HNSW search returns min(k, pool) rows.
        var (accountId, _) = await AccountAsync();
        await using var db = Context();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var eligibility = new EligibilityQueryBuilder(db);
        var reference = await db.UserRatings.Where(r => r.AccountId == accountId).OrderByDescending(r => r.Rating)
            .Select(r => r.TitleId).FirstAsync();
        var embeddings = new ConceptEmbeddings(new UnavailableEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance);
        var retriever = new HybridRetriever(
            db, eligibility, new SupplyReporter(db, eligibility),
            new ConceptResolver(new KeywordVocabulary(db, cache), embeddings, db, NullLogger<ConceptResolver>.Instance),
            new FullTextArm(db), embeddings, new KeywordNeighbours(db, eligibility, new KeywordVocabulary(db, cache)),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HybridRetriever>.Instance);

        HardConstraints[] filters =
        [
            new([], [], new YearRange(1980, 1989), null, MediaScope.Movies),
            new([], [], new YearRange(2000, 2003), 30, MediaScope.Shows),
            new([], [], new YearRange(2024, 2026), 85, MediaScope.Movies),
            new([], [], new YearRange(1990, 1995), null, MediaScope.Shows),
            new([], [], null, null, MediaScope.Any),
        ];
        foreach (var hard in filters)
        {
            var pool = await HardConstraintFilter.Apply(eligibility.EligibleTitles(accountId), db, hard)
                .Where(t => db.TitleEmbeddings.Any(e => e.TitleId == t.Id) && t.Id != reference).CountAsync();
            var clock = Stopwatch.StartNew();
            var result = await retriever.RetrieveAsync(
                new RetrievalQuery(accountId, hard, new SoftPreferences([], [], [reference]), [reference], 200) { Arms = RetrievalArms.Semantic },
                default);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"years {hard.Years?.From}-{hard.Years?.To} runtime<={hard.MaxRuntimeMinutes} {hard.Media}: pool {pool,5}  returned {result.Candidates.Count,3} (k={HybridRetriever.SemanticK}, want min {Math.Min(pool, HybridRetriever.SemanticK)})  {clock.Elapsed.TotalMilliseconds:F0} ms  [{string.Join(", ", result.Timings.ElapsedMs.Select(kv => $"{kv.Key} {kv.Value}"))}]"));
        }
    }

    /// <summary>
    /// The hybrid retriever end to end through the REAL container (all four arms, live TMDB — free —
    /// and real concept embeddings, cached after the first call per text). Opt in separately with
    /// <c>REEL_RETRIEVAL_PROBE_LIVE=1</c> and the lane environment sourced, because it makes paid
    /// embedding calls (one per distinct concept text; 24 here).
    /// </summary>
    [Fact]
    public async Task Hybrid_retrieval_end_to_end_over_the_golden_concepts()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("REEL_RETRIEVAL_PROBE_LIVE") != "1")
        {
            return;
        }

        var (accountId, _) = await AccountAsync();
        // The process environment as configuration (the lane env sourced by the caller), the way the
        // host reads it: "__" separates sections. Values stay in memory; nothing is printed.
        var settings = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => ((string)e.Key).Replace("__", ":", StringComparison.Ordinal), e => (string?)e.Value);
        settings["ConnectionStrings:Postgres"] = ConnectionString;
        settings["Background:KeywordVocabulary"] = "false";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration);
        FrostAura.Reel.Infrastructure.DependencyInjection.AddReelInfrastructure(services, configuration);
        await using var provider = services.BuildServiceProvider();

        var total = new List<double>();
        var warm = new List<double>();
        var concepts = Concepts.Take(Environment.GetEnvironmentVariable("REEL_PROBE_CONCEPTS") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : Concepts.Length).ToList();
        foreach (var (words, round) in concepts.Select(c => (c, 1)).Concat(concepts.Select(c => (c, 2))))
        {
            using var scope = provider.CreateScope();
            ((AccountContext)scope.ServiceProvider.GetRequiredService<IAccountContext>()).SetAccount(accountId);
            var retriever = scope.ServiceProvider.GetRequiredService<IAskReelRetriever>();
            var hard = new HardConstraints([new ConceptTerm($"{words} movies", [], [])], [], null, null, MediaScope.Any);
            var clock = Stopwatch.StartNew();
            var result = await retriever.RetrieveAsync(
                new RetrievalQuery(accountId, hard, SoftPreferences.None, [], FrostAura.Reel.Application.Search.AskReel.Engine.AskReelEngine.RetrievalWant) { Message = $"{words} movies" }, default);
            (round == 1 ? total : warm).Add(clock.Elapsed.TotalMilliseconds);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"r{round} {words,-16} {clock.Elapsed.TotalMilliseconds,6:F0} ms  cands {result.Candidates.Count,3}  prov {result.Provisional.Count,3}  offerable {result.Supply.Offerable,4}  skipped [{string.Join(",", result.SkippedArms)}]  [{string.Join(", ", result.Timings.ElapsedMs.Select(kv => $"{kv.Key} {kv.Value}"))}]"));
        }

        output.WriteLine(Summary("hybrid retrieve, round 1 (cold TMDB, first embed per text)", total));
        output.WriteLine(Summary("hybrid retrieve, round 2 (warm)", warm));
    }

    /// <summary>
    /// Embeds the most-carried labels on the slot (<c>REEL_PROBE_EMBED_CAP</c>, default 0 = counts
    /// only) and then resolves words no synonym covers, through the nearest-label fallback. Paid:
    /// one call per 100 labels plus one per phrase.
    /// </summary>
    [Fact]
    public async Task Label_embeddings_and_the_nearest_label_fallback()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("REEL_RETRIEVAL_PROBE_LIVE") != "1")
        {
            return;
        }

        var settings = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => ((string)e.Key).Replace("__", ":", StringComparison.Ordinal), e => (string?)e.Value);
        settings["ConnectionStrings:Postgres"] = ConnectionString;
        settings["Background:KeywordVocabulary"] = "false";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
        FrostAura.Reel.Infrastructure.DependencyInjection.AddReelInfrastructure(services, configuration);
        await using var provider = services.BuildServiceProvider();

        var cap = int.TryParse(Environment.GetEnvironmentVariable("REEL_PROBE_EMBED_CAP"), CultureInfo.InvariantCulture, out var c) ? c : 0;
        using (var scope = provider.CreateScope())
        {
            var refresh = await scope.ServiceProvider.GetRequiredService<KeywordLabelRefresher>().RefreshAsync(cap, default);
            output.WriteLine($"refresh: counted {refresh.Counted}, embedded {refresh.Embedded} in {refresh.Calls} call(s), {refresh.Remaining} waiting");
        }

        foreach (var words in new[] { "stranded on a desert island", "bank job", "lumberjacks", "deep sea creature", "nuclear war", "ninja" })
        {
            using var scope = provider.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IConceptResolver>();
            var concept = await resolver.ResolveAsync(new ConceptTerm(words, [], []), default);
            if (concept.ExactLabels is { Count: 0 })
            {
                // The embedding call may have missed the 1 s budget cold; it is cached once it lands.
                await Task.Delay(2500);
                concept = await resolver.ResolveAsync(new ConceptTerm(words, [], []), default);
            }

            output.WriteLine($"{words,-28} exact [{string.Join(", ", concept.ExactLabels ?? ["(regex)"])}]");
        }
    }

    [Fact]
    public async Task Typeahead_local_leg_canonical_first_and_p95()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, _) = await AccountAsync();
        string[] canonical = ["heat", "alien", "godfather", "nolan", "keanu", "inception", "matrix", "amelie", "the notebook", "oppenheimer"];
        foreach (var term in canonical)
        {
            await using var db = Context();
            var local = await new FrostAura.Reel.Application.Search.Typeahead.TypeaheadSearch(db, new EligibilityQueryBuilder(db)).LocalAsync(accountId, term, default);
            output.WriteLine(
                $"{term,-13} titles [{string.Join(" | ", local.Titles.Take(3).Select(t => $"{t.Name} ({t.Year}) {t.Tier}"))}]  people [{string.Join(" | ", local.People.Take(2).Select(p => $"{p.Name} {p.Tier}{(p.Affinity is null ? string.Empty : " ♥")}"))}]  strong {local.StrongMatches} hidden {local.HiddenByConditions}");
        }

        // n = 120 over a spread of real terms, prefixes as typed, one context per call like a request.
        string[] terms = ["he", "hea", "heat", "ali", "alie", "alien", "go", "godf", "godfather", "no", "nol", "nolan", "ke", "kea", "keanu", "inc", "incep", "inception", "mat", "matrix", "bla", "blade", "blade runner", "dun", "dune", "int", "interstellar", "the dark", "breaking", "succession"];
        var elapsed = new List<double>();
        for (var i = 0; i < 120; i++)
        {
            await using var db = Context();
            var search = new FrostAura.Reel.Application.Search.Typeahead.TypeaheadSearch(db, new EligibilityQueryBuilder(db));
            var clock = Stopwatch.StartNew();
            await search.LocalAsync(accountId, terms[i % terms.Length], default);
            elapsed.Add(clock.Elapsed.TotalMilliseconds);
        }

        output.WriteLine(Summary("typeahead local leg", elapsed));
    }

    /// <summary>
    /// Task 11's acceptance: the resolved "ninja" concept's population (eligibility ignored — this is
    /// recall of the concept, not of what the founder may be shown) holds at least 0.9 of the 20-film
    /// list in <c>ninja-recall-list.json</c> (pending the founder's confirmation).
    /// </summary>
    [Fact]
    public async Task Ninja_recall_on_the_twenty_film_list()
    {
        if (!Enabled)
        {
            return;
        }

        var list = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(SourceDirectory(), "ninja-recall-list.json")));
        var films = list.RootElement.GetProperty("films").EnumerateArray()
            .Select(f => (TmdbId: f.GetProperty("tmdbId").GetInt64(), Name: f.GetProperty("name").GetString()!))
            .ToList();

        await using var db = Context();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new ConceptResolver(
            new KeywordVocabulary(db, cache),
            new ConceptEmbeddings(new UnavailableEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
            db,
            NullLogger<ConceptResolver>.Instance);
        var concept = await resolver.ResolveAsync(new ConceptTerm("ninja movies", [], []), default);
        var hard = new HardConstraints([concept], [], null, null, MediaScope.Movies);
        var tmdbIds = films.Select(f => (long?)f.TmdbId).ToList();
        var found = (await HardConstraintFilter.Apply(db.Titles, db, hard)
                .Where(t => tmdbIds.Contains(t.TmdbId))
                .Select(t => t.TmdbId!.Value)
                .ToListAsync())
            .ToHashSet();
        foreach (var (tmdbId, name) in films.Where(f => !found.Contains(f.TmdbId)))
        {
            output.WriteLine($"missed: {name} ({tmdbId})");
        }

        var recall = (double)films.Count(f => found.Contains(f.TmdbId)) / films.Count;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ninja recall {recall:F2} ({found.Count} of {films.Count})"));
        Assert.True(recall >= 0.9, $"ninja recall {recall:F2} < 0.9");
    }

    /// <summary>
    /// Recall@60 of the catalogue arms (facet + full text; the semantic and TMDB arms only re-rank
    /// or add admitted titles) against each open-set concept scenario's SQL truth, restricted to the
    /// titles the founder may be shown. The truth bounds precision and is not a judged recall set, so
    /// this approximates the program's "judged golden facet queries" until the founder's labels exist.
    /// </summary>
    [Fact]
    public async Task Facet_recall_at_60_against_the_open_concept_truth()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, _) = await AccountAsync();
        var truthDir = Path.GetFullPath(Path.Combine(SourceDirectory(), "../../../../eval/askreel/truth"));
        var skip = new[] { "_eligible", "french", "korean", "low-rated", "scifi-1980s", "heist-2010s", "title-scooby" };
        var recalls = new List<double>();
        foreach (var file in Directory.GetFiles(truthDir, "*.sql").Order(StringComparer.Ordinal))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (skip.Contains(key) || key.StartsWith("genre-", StringComparison.Ordinal) || key.StartsWith("person-", StringComparison.Ordinal))
            {
                continue;
            }

            var truthSql = string.Join('\n', (await File.ReadAllLinesAsync(file)).Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))).Trim().TrimEnd(';');
            await using var db = Context();
            var eligibleIds = new EligibilityQueryBuilder(db).EligibleTitles(accountId).Select(t => t.Id);
            // The truth file is a committed eval fixture, not input: concatenated, not interpolated.
            var wrapped = "SELECT x.\"Id\" AS \"Value\" FROM (" + truthSql + ") AS x";
            var truth = (await db.Database.SqlQueryRaw<Guid>(wrapped)
                    .Where(id => eligibleIds.Contains(id))
                    .ToListAsync())
                .ToHashSet();
            if (truth.Count == 0)
            {
                output.WriteLine($"{key,-16} no eligible truth titles");
                continue;
            }

            var cache = new MemoryCache(new MemoryCacheOptions());
            var embeddings = new ConceptEmbeddings(new UnavailableEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance);
            var eligibility = new EligibilityQueryBuilder(db);
            var retriever = new HybridRetriever(
                db, eligibility, new SupplyReporter(db, eligibility),
                new ConceptResolver(new KeywordVocabulary(db, cache), embeddings, db, NullLogger<ConceptResolver>.Instance),
                new FullTextArm(db), embeddings, new KeywordNeighbours(db, eligibility, new KeywordVocabulary(db, cache)),
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                NullLogger<HybridRetriever>.Instance);
            var words = key.Replace('-', ' ');
            var result = await retriever.RetrieveAsync(
                new RetrievalQuery(accountId, new HardConstraints([new ConceptTerm(words, [], [])], [], null, null, MediaScope.Any), SoftPreferences.None, [], 60)
                {
                    Arms = RetrievalArms.Catalogue,
                },
                default);
            var hits = result.Candidates.Count(c => truth.Contains(c.TitleId));
            var recall = (double)hits / Math.Min(60, truth.Count);
            recalls.Add(recall);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key,-16} truth {truth.Count,4}  returned {result.Candidates.Count,3}  in truth {hits,3}  recall@60 {recall:F2}"));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"mean recall@60 {recalls.Average():F3} over {recalls.Count} concepts; {recalls.Count(r => r >= 0.85)} at or above 0.85"));
    }

    /// <summary>
    /// The mood arm and keyless search on the slot (no paid call: no embedding provider). Prints each
    /// mood's head with its evidence and the time per request, so a person can judge whether "cosy"
    /// reads as cosy; asserts only that every mood is answered and every candidate carries the mood.
    /// </summary>
    [Fact]
    public async Task Moods_and_keyless_search_on_the_slot()
    {
        if (!Enabled)
        {
            return;
        }

        var (accountId, _) = await AccountAsync();
        var elapsed = new List<double>();
        foreach (var mood in new[] { "cosy", "tense", "mind-bending", "feel-good", "funny", "dark", "relaxing", "scary" })
        {
            await using var db = Context();
            var cache = new MemoryCache(new MemoryCacheOptions());
            var embeddings = new ConceptEmbeddings(new UnavailableEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance);
            var eligibility = new EligibilityQueryBuilder(db);
            var retriever = new HybridRetriever(
                db, eligibility, new SupplyReporter(db, eligibility),
                new ConceptResolver(new KeywordVocabulary(db, cache), embeddings, db, NullLogger<ConceptResolver>.Instance),
                new FullTextArm(db), embeddings, new KeywordNeighbours(db, eligibility, new KeywordVocabulary(db, cache)),
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                NullLogger<HybridRetriever>.Instance);
            var clock = Stopwatch.StartNew();
            var result = await retriever.RetrieveAsync(
                new RetrievalQuery(accountId, new HardConstraints([], [], null, null, MediaScope.Movies), new SoftPreferences([mood], [], []), [], 24)
                {
                    Arms = RetrievalArms.Catalogue | RetrievalArms.Semantic,
                },
                default);
            elapsed.Add(clock.Elapsed.TotalMilliseconds);
            var ids = result.Candidates.Take(8).Select(c => c.TitleId).ToList();
            var names = await db.Titles.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{mood}: {result.Candidates.Count} in {clock.Elapsed.TotalMilliseconds:F0} ms [{string.Join(", ", result.Timings.ElapsedMs.Select(kv => $"{kv.Key} {kv.Value}"))}]"));
            foreach (var c in result.Candidates.Take(8))
            {
                output.WriteLine($"   {names[c.TitleId]}  [{string.Join(", ", c.Evidence)}]");
            }

            Assert.NotEmpty(result.Candidates);
            Assert.All(result.Candidates, c => Assert.Contains(c.Evidence, e => e.StartsWith("mood:", StringComparison.Ordinal)));
        }

        output.WriteLine(Summary("mood retrieval", elapsed));

        var keyless = new List<double>();
        foreach (var query in new[] { "medevil fun", "ninja", "heist", "time loop", "survival at sea", "cosy christmas", "scarry zombies" })
        {
            await using var db = Context();
            var search = KeylessTextSearch.Offline(db, new EligibilityQueryBuilder(db));
            var clock = Stopwatch.StartNew();
            var hits = await search.SearchAsync(accountId, query, 24, default);
            keyless.Add(clock.Elapsed.TotalMilliseconds);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"keyless \"{query}\": {hits.Count} in {clock.Elapsed.TotalMilliseconds:F0} ms: {string.Join(" | ", hits.Take(6).Select(h => $"{h.Title.Name} [{string.Join(", ", h.MatchedOn.Take(3))}]"))}"));
        }

        output.WriteLine(Summary("keyless search", keyless));
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;

    internal static string Summary(string what, IReadOnlyList<double> elapsed)
    {
        var sorted = elapsed.Order().ToList();
        double At(double q) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{what}: n={sorted.Count} p50 {At(0.5):F1} ms  p95 {At(0.95):F1} ms  max {sorted[^1]:F1} ms");
    }

    internal static ReelDbContext Context(Guid? accountId = null)
    {
        var context = new AccountContext();
        if (accountId is { } id)
        {
            context.SetAccount(id);
        }

        var options = new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(ConnectionString!, n => n.UseVector());
        if (Environment.GetEnvironmentVariable("REEL_PROBE_SQL") is { Length: > 0 } sqlFile)
        {
            // Writes every statement to a file for EXPLAIN ANALYZE; values are the probe's own drafts.
            options.LogTo(
                line => File.AppendAllText(sqlFile, line + Environment.NewLine),
                [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted]);
            options.EnableSensitiveDataLogging();
        }

        return new ReelDbContext(options.Options, context);
    }

    internal static async Task<(Guid AccountId, FilterRuleSet Saved)> AccountAsync()
    {
        await using var db = Context();
        var configured = Environment.GetEnvironmentVariable("REEL_PROBE_ACCOUNT_ID");
        var accountId = Guid.TryParse(configured, out var id)
            ? id
            : await db.UserRatings.GroupBy(r => r.AccountId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        var rows = await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync();
        return (accountId, FilterRuleSet.FromRows(rows));
    }

    private static async Task<FilterCostReport> MeasureAsync(Guid accountId, FilterRuleSet rules)
    {
        await using var db = Context(accountId);
        var context = new AccountContext();
        context.SetAccount(accountId);
        var meter = new FilterCostMeter(db, new EligibilityQueryBuilder(db), context, new MemoryCache(new MemoryCacheOptions()));
        return await meter.MeasureAsync(rules);
    }

    /// <summary>An embedding provider that is not configured: the probe never pays for a call.</summary>
    private sealed class UnavailableEmbeddings : FrostAura.Reel.Domain.Ports.IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float[]>());
    }
}
