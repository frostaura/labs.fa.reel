using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Llm;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// WS-1 task 3's measurement instrument: N real calls per purpose at the purpose's configured
/// reasoning effort, with each adapter's own prompt built from a slot database's real data, every
/// completion's token counts written out per call. PAID — so it is inert unless
/// <c>REEL_LLM_PROBE=1</c>, and it never runs in CI or in the ordinary suite.
///
/// <para><b>What it measures, and how it stays out of the way.</b> Every call bills under
/// <c>probe-&lt;purpose&gt;</c> (§7: a probe is a measurement of a purpose, never a use of it), so
/// it counts against no purpose's ceiling and no acceptance count, and lands on the slot's own
/// ledger (flushed at the end). The completion ceiling is lifted to <see cref="ProbeCeiling"/> so a
/// tail is measured rather than censored; a <c>length</c> finish is still recorded.</para>
///
/// <para>Environment: <c>REEL_PROBE_CONNECTION_STRING</c> (the slot DB, never <c>reel</c>),
/// <c>REEL_PROBE_OUT</c> (the JSON-lines file), optional <c>REEL_PROBE_PURPOSES</c> (comma list),
/// and the model key and base URL read the usual way.</para>
/// </summary>
public class LlmSizingProbe
{
    private const int ProbeCeiling = 4_000;

    private static readonly string[] Queries =
    [
        "ninja movies", "80s sci-fi but nothing too cheesy", "something like Heat but more recent",
        "a slow-burn thriller for tonight", "feel-good comedy under 100 minutes", "films Christopher Nolan directed",
        "no superheroes please, something grounded", "a mind-bending sci-fi series", "heist films with a twist",
        "dark Scandinavian crime show", "a war film that isn't just action", "animated films for adults",
        "something with Tom Hardy", "a courtroom drama", "cosy mystery series", "best of 90s action",
        "space exploration but realistic", "a horror film that isn't gory", "true crime documentary",
        "a western with moral grey areas", "Korean thriller", "a film like Arrival", "short binge, one season only",
        "revenge film, gritty", "political thriller from the 70s", "time loop movie", "survival story at sea",
        "comedy with Simon Pegg", "something hopeful after a long week", "the second one, but darker",
    ];

    /// <summary>
    /// Ask v2 plan follow-ups (R1-51): the message a person sends AFTER an answer is on screen. The
    /// first sizing pass only ever planned a conversation's opening turn, so a critique, a refine or
    /// an item question carried no earlier turns and no on-screen titles; these do. Every act, and
    /// lengths from one word to a five-constraint request.
    /// </summary>
    private static readonly string[] PlanFollowUps =
    [
        "none of the above, something darker", "only ones from the 90s", "is the third one scary?",
        "tell me more about T1.2", "thanks, that's great",
        "actually, forget that — I want a feel-good animated film for the kids, under 90 minutes, no musicals, nothing sad, ideally released after 2015 and not Pixar",
        "more like the first one", "no shows, only movies", "I've seen all of those",
        "something with less violence but the same vibe", "who directed the second one?", "show me more",
        "what about something by Denis Villeneuve instead?", "not that one, the one before it", "funnier, please",
        "why did you pick the last one?", "hmm", "ok what about Korean ones", "any of these under two hours?",
        "swap the horror ones for thrillers", "I loved T1.1, more like that",
        "something my wife would like too, she hates subtitles",
        "from the 70s or 80s, with Al Pacino or Robert De Niro, not gangster though, and not over 150 minutes",
        "which of these is the most hopeful?", "you're useless", "what's the fourth one about?",
        "start over: cosy British mysteries", "darker and slower, more like a novel", "hello?", "is T1.5 on Netflix?",
    ];

    /// <summary>
    /// Ask v2 item questions (R1-51): what the answerer is asked about one title. Answerable from the
    /// fact sheet, unanswerable from it (the prompt then says what it does know), and the two that
    /// push hardest on length ("tell me everything", a compare-and-list) so the tail is measured.
    /// </summary>
    private static readonly string[] ItemQuestions =
    [
        "Is it scary?", "Who directed it?", "How long is it?", "Is it OK for a 12-year-old?",
        "What's it about, without spoilers?", "Who's in it?", "Is it on Netflix?", "Does the dog die?",
        "Have I seen this one already?", "Is it based on a true story?", "Is it more of a comedy or a drama?",
        "Is it violent? I'm watching with my parents tonight and they hate gore, so tell me honestly",
        "Is there a sequel?", "What year did it come out?", "Is it slow?", "Is it in English?",
        "Would it suit a rainy Sunday afternoon?", "Is Tom Hardy in it?", "how many seasons", "Is it a remake?",
        "What's the tone like — bleak, or hopeful?", "Does it have subtitles?", "Is it romantic at all?",
        "Tell me everything you know about it",
        "Compare it with the director's other films and list the main cast with their roles",
        "Is the ending happy?", "Any sex scenes?", "What genre is it?", "Is it good?",
        "Why did you show me this one, and what else is it like?",
    ];

    [Fact]
    public async Task Measure_completion_tokens_per_purpose()
    {
        if (Environment.GetEnvironmentVariable("REEL_LLM_PROBE") != "1")
        {
            return;
        }

        var cs = Environment.GetEnvironmentVariable("REEL_PROBE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("REEL_PROBE_CONNECTION_STRING is required.");
        AssertIsolatedEvalDatabase(cs);
        var outPath = Environment.GetEnvironmentVariable("REEL_PROBE_OUT")
            ?? throw new InvalidOperationException("REEL_PROBE_OUT is required.");
        var only = (Environment.GetEnvironmentVariable("REEL_PROBE_PURPOSES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var calls = int.TryParse(Environment.GetEnvironmentVariable("REEL_PROBE_CALLS"), out var n) ? n : 30;

        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var options = new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(cs, o => o.UseVector()).Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new ReelDbContext(options, new AccountContext()));
        var recorder = new ApiUsageRecorder(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);

        var bootstrap = new ReelDbContext(options, new AccountContext());
        var accountId = await bootstrap.Accounts.Select(a => a.Id).FirstAsync();
        var account = new AccountContext();
        account.SetAccount(accountId);
        await using var db = new ReelDbContext(options, account);

        var http = new HttpClient
        {
            BaseAddress = new Uri((config["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1").TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
        var client = new OpenRouterChatClient(
            http, recorder, config, NullLogger.Instance, account, new LlmPurposeCatalog(config), new ModelCapabilities(config));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var features = new FeatureVectorBuilder(db);
        var dislikes = new DislikeSignal(db, cache);
        var now = DateTime.UtcNow;
        var taste = await features.BuildTasteStateAsync(accountId, now, CancellationToken.None);
        var persona = await new PersonalisationContext(db, dislikes).PersonaForAsync(accountId, taste, CancellationToken.None);
        var facts = await new TasteFactsBuilder(db, dislikes, features, cache).BuildAsync(accountId, taste, now, CancellationToken.None);
        var artifactId = await db.ModelArtifacts.Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version).Select(a => a.Id).FirstAsync();
        var scored = await db.TitleScores.Where(s => s.AccountId == accountId && s.ModelArtifactId == artifactId)
            .OrderByDescending(s => s.PredictedRating).Take(600)
            .Select(s => new { s.TitleId, s.PredictedRating }).ToListAsync();
        var ids = scored.Select(s => s.TitleId).ToList();
        var predictions = scored.ToDictionary(s => s.TitleId, s => s.PredictedRating);
        var titles = await db.Titles.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var contexts = await RerankContextBuilder.BuildAsync(db, ids, null, CancellationToken.None);
        RerankInput Input(Guid id) => new(
            id, titles[id].Name, titles[id].Year, titles[id].MediaType.ToString(), titles[id].Genres,
            titles[id].Overview, predictions[id], contexts.GetValueOrDefault(id));

        var tasteSummary = persona.Taste;
        await using var output = new StreamWriter(outPath, append: true);
        var rows = ids.Where(titles.ContainsKey).ToList();

        async Task Run(string purpose, int count, Func<int, (IReadOnlyList<LlmMessage> Messages, object? Schema, int Items)> build)
        {
            if (only.Count > 0 && !only.Contains(purpose))
            {
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var (messages, schema, items) = build(i);
                var clock = Stopwatch.StartNew();
                var result = await client.CompleteAsync(
                    new LlmChatRequest(ApiUsagePurpose.Probe(purpose), messages, schema, items, ProbeCeiling), CancellationToken.None);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    purpose,
                    i,
                    items,
                    model = result.Model,
                    effort = result.Effort,
                    finish = result.FinishReason,
                    failure = result.Failure?.ToString(),
                    prompt = result.PromptTokens,
                    completion = result.CompletionTokens,
                    reasoning = result.ReasoningTokens,
                    attempts = result.HttpAttempts,
                    ms = clock.ElapsedMilliseconds,
                }));
                await output.FlushAsync();
            }
        }

        await Run(ApiUsagePurpose.SearchAgent, calls, i => (
            [LlmMessage.System(OpenRouterSearchAgent.InterpretSystemPrompt),
                LlmMessage.User(OpenRouterSearchAgent.BuildInterpretPrompt(
                    i % 5 == 4 ? [new ChatTurn("user", Queries[(i + 7) % Queries.Length]), new ChatTurn("assistant", "Here are a few.")] : [],
                    Queries[i % Queries.Length], tasteSummary,
                    i % 5 == 4 ? rows.Skip(i).Take(6).Select(id => titles[id].Name).ToList() : [],
                    "never horror; English only"))],
            OpenRouterSearchAgent.InterpretSchema(), 0));

        await Run(ApiUsagePurpose.SearchInterpret, calls, i => (
            [LlmMessage.System(OpenRouterSearchInterpreter.SystemPrompt),
                LlmMessage.User(OpenRouterSearchInterpreter.BuildUserMessage(Queries[i % Queries.Length], "never horror", []))],
            null, 0));

        FeedRowKind[] feedRows = [FeedRowKind.Hero, FeedRowKind.BecauseYouLoved, FeedRowKind.DeepCuts, FeedRowKind.NewForYou];
        await Run(ApiUsagePurpose.RerankFeed, calls, i =>
        {
            var batch = rows.Skip(i * 9).Take(9).Select(Input).ToList();
            var row = feedRows[i % feedRows.Length];
            var anchor = row == FeedRowKind.BecauseYouLoved ? titles[rows[rows.Count - 1 - i]].Name : null;
            return (
                [LlmMessage.System(OpenRouterSearchAgent.RerankSystemPrompt),
                    LlmMessage.User(OpenRouterSearchAgent.BuildRerankPrompt(batch, FeedPaginator.QueryForRow(row), persona, anchor))],
                OpenRouterSearchAgent.RerankSchema(), batch.Count);
        });

        await Run(ApiUsagePurpose.RerankPage, calls, i =>
        {
            // From row 270 on, wrapping inside the 600-title shortlist, so thirty batches never run dry.
            var batch = rows.Skip(270 + (i * 9 % Math.Max(9, rows.Count - 279))).Take(9).Select(Input).ToList();
            return (
                [LlmMessage.System(OpenRouterSearchAgent.RerankSystemPrompt),
                    LlmMessage.User(OpenRouterSearchAgent.BuildRerankPrompt(batch, FeedPaginator.QueryForRow(FeedRowKind.DeepCuts), persona))],
                OpenRouterSearchAgent.RerankSchema(), batch.Count);
        });

        await Run(ApiUsagePurpose.RerankAsk, calls, i =>
        {
            var batch = rows.Skip(i * 6).Take(6).Select(Input).ToList();
            return (
                [LlmMessage.System(OpenRouterSearchAgent.AskRerankSystemPrompt),
                    LlmMessage.User(OpenRouterSearchAgent.BuildRerankPrompt(batch, Queries[i % Queries.Length], persona))],
                OpenRouterSearchAgent.AskRerankSchema(), batch.Count);
        });

        if (only.Count == 0 || only.Contains(ApiUsagePurpose.CrossReference))
        {
            var builder = new CrossReferenceCandidateBuilder(db, new NeighbourFinder(db));
            var xrefOptions = new CrossReferenceJudgeOptions(CrossReferencePromptVersions.Second);
            var batches = new List<CrossReferenceCandidateBuilder.Batch>();
            for (var i = 0; i < calls; i++)
            {
                batches.Add(await builder.BuildAsync(
                    accountId, rows.Skip(i * 10).Take(10).ToList(), predictions,
                    new NeighbourFinder.Options(NeighbourFinder.DefaultK, 0, null, false), CancellationToken.None));
            }

            await Run(ApiUsagePurpose.CrossReference, calls, i => (
                [LlmMessage.System(OpenRouterCrossReferenceJudge.BuildSystemPrompt(CrossReferenceRound.First, xrefOptions)),
                    LlmMessage.User(OpenRouterCrossReferenceJudge.BuildUserPrompt(batches[i].Candidates, persona, CrossReferenceRound.First, xrefOptions))],
                OpenRouterCrossReferenceJudge.ResultsSchema(batches[i].Candidates.Count, xrefOptions.PromptVersion),
                batches[i].Candidates.Count));
        }

        if (only.Count == 0 || only.Contains(ApiUsagePurpose.Lens))
        {
            var lensInputs = (await new LensInputBuilder(db, new RatingCalibrationService(db, cache))
                .BuildAsync(accountId, rows.Take(calls * 2).ToList(), CancellationToken.None)).Titles;
            await Run(ApiUsagePurpose.Lens, calls, i => (
                [LlmMessage.System(OpenRouterLensWriter.LensSystemPrompt),
                    LlmMessage.User(OpenRouterLensWriter.LensPrompt(lensInputs[i % lensInputs.Count], persona))],
                OpenRouterLensWriter.LensSchema(), 0));
        }

        if (Environment.GetEnvironmentVariable("REEL_PROBE_XREF_RUN") == "1")
        {
            // Task 6's acceptance batch: ONE real cross-reference batch of up to 20 candidates through
            // the production adapter (purpose cross-reference, the computed ceiling, the shared
            // client) with its verdicts written through the production service.
            var judge = new OpenRouterCrossReferenceJudge(
                new HttpClient { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromMinutes(5) },
                recorder, config, NullLogger<OpenRouterCrossReferenceJudge>.Instance, account);
            var verdicts = new CrossReferenceService(db, NullLogger<CrossReferenceService>.Instance);
            var unjudged = await verdicts.UnjudgedAsync(
                accountId, rows.Skip(400).ToList(), facts.ProfileHash, judge.PromptVersion, CrossReferenceRound.First, CancellationToken.None);
            var batch = await new CrossReferenceCandidateBuilder(db, new NeighbourFinder(db)).BuildAsync(
                accountId, unjudged.Take(20).ToList(), predictions,
                new NeighbourFinder.Options(NeighbourFinder.DefaultK, 0, null, false), CancellationToken.None);
            var clock = Stopwatch.StartNew();
            var result = await ((ICrossReferenceJudge)judge).JudgeAsync(
                batch.Candidates, persona, CrossReferenceRound.First, ApiUsagePurpose.CrossReference, CancellationToken.None);
            var written = await verdicts.RecordBatchAsync(
                accountId, facts.ProfileHash, judge.PromptVersion, CrossReferenceRound.First, judge.ModelId, artifactId, 0,
                result.Entries, batch.Neighbours, DateTime.UtcNow, CancellationToken.None);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                purpose = "xref-run",
                candidates = batch.Candidates.Count,
                succeeded = result.Succeeded,
                failure = result.Failure?.ToString(),
                verdicts = written,
                rejected = result.RejectedEntries,
                prompt = result.PromptTokens,
                completion = result.CompletionTokens,
                ms = clock.ElapsedMilliseconds,
            }));
        }

        // ── Ask v2 (R1-51). The plan: follow-up turns only — the opening turn was sized from live
        // turns — with one or two earlier exchanges and their cards on screen under real handles.
        await Run(ApiUsagePurpose.AskPlan, calls, i =>
        {
            var exchanges = (i % 3) + 1;
            var presented = new List<PresentedRef>();
            var turns = new List<ChatTurn>();
            for (var t = exchanges; t >= 1; t--)
            {
                var cards = rows.Skip((i * 17) + (t * 9)).Take(6 + ((i + t) % 7)).ToList();
                presented.AddRange(cards.Select((id, rank) =>
                    new PresentedRef(id, new TurnHandle(t, rank + 1), titles[id].Name, titles[id].Year, titles[id].MediaType)));
            }

            for (var t = 1; t <= exchanges; t++)
            {
                var asked = t == 1 ? Queries[i % Queries.Length] : PlanFollowUps[(i + t) % PlanFollowUps.Length];
                var lead = presented.First(p => p.Handle.Turn == t);
                turns.Add(new ChatTurn("user", asked));
                turns.Add(new ChatTurn(
                    "assistant",
                    string.Create(CultureInfo.InvariantCulture, $"Here are {presented.Count(p => p.Handle.Turn == t)} picks — {lead.Handle} {lead.Name} leads.")));
            }

            var state = new ConversationState(
                exchanges,
                new HardConstraints([new ConceptTerm(Queries[i % Queries.Length].Split(' ')[0], [], [])], [], null, null, MediaScope.Any),
                new SoftPreferences(i % 2 == 0 ? ["dark"] : [], [], []),
                i % 4 == 0 ? ["superheroes"] : [],
                [],
                [],
                presented.OrderByDescending(p => p.Handle.Turn).ThenBy(p => p.Handle.Rank).ToList());
            var input = new PlanInput(
                PlanFollowUps[i % PlanFollowUps.Length], state, turns,
                new StandingConditionsView(i % 2 == 0 ? "never horror; English only" : null), tasteSummary, MediaScope.Any);
            return (OpenRouterAskPlanner.Messages(input), OpenRouterAskPlanner.Schema(), 0);
        });

        // The answer: the production fact sheet (TitleFacts) for a spread of titles — the shortlist's
        // films and shows and some the account has rated, so "you have seen it" varies too.
        if (only.Count == 0 || only.Contains(ApiUsagePurpose.AskAnswer))
        {
            var seenIds = await db.UserRatings.Where(r => r.AccountId == accountId)
                .OrderBy(r => r.TitleId).Select(r => r.TitleId).Take(calls / 3).ToListAsync();
            var answerIds = rows.Where((_, k) => k % 19 == 0).Take(calls - seenIds.Count).Concat(seenIds).ToList();
            var titleFacts = new TitleFacts(db);
            var sheets = new List<TitleFactSheet>();
            foreach (var id in answerIds)
            {
                if (await titleFacts.GetAsync(accountId, id, CancellationToken.None) is { } sheet)
                {
                    sheets.Add(sheet);
                }
            }

            await Run(ApiUsagePurpose.AskAnswer, calls, i => (
                [LlmMessage.System(OpenRouterAskAnswerer.SystemPrompt),
                    LlmMessage.User(OpenRouterAskAnswerer.UserPrompt(
                        new AnswerInput(ItemQuestions[i % ItemQuestions.Length], sheets[i % sheets.Count])))],
                OpenRouterAskAnswerer.Schema(), 0));
        }

        await Run(ApiUsagePurpose.TasteNarrative, calls, _ => (
            [LlmMessage.System(OpenRouterLensWriter.NarrativeSystemPrompt), LlmMessage.User(OpenRouterLensWriter.NarrativePrompt(facts.Facts))],
            OpenRouterLensWriter.NarrativeSchema(), 0));

        await recorder.FlushAsync(CancellationToken.None);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"# done {DateTime.UtcNow:O}"));
    }

    /// <summary>
    /// The probe writes to the ledger and reads a whole account, so it may only ever point at the
    /// ISOLATED eval Postgres (:5434, loopback) and never at the live <c>reel</c> database — parsed,
    /// not substring-matched: "Database=Reel", a trailing space or a missing port all slipped past
    /// the old <c>Database=reel;</c> check, and the port was never checked at all.
    /// </summary>
    internal static void AssertIsolatedEvalDatabase(string connectionString)
    {
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        Assert.True(parsed.Port == 5434, $"The probe runs only against the eval Postgres on :5434, not :{parsed.Port}.");
        Assert.Contains(parsed.Host?.Trim(), new[] { "127.0.0.1", "localhost", "::1" });
        Assert.False(
            string.Equals(parsed.Database?.Trim(), "reel", StringComparison.OrdinalIgnoreCase),
            "The probe never runs against the live database name.");
        Assert.False(string.IsNullOrWhiteSpace(parsed.Database), "The probe needs an explicit slot database.");
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5433;Username=reel;Password=x;Database=reel_ws1")]
    [InlineData("Host=127.0.0.1;Port=5434;Username=reel;Password=x;Database=Reel")]
    [InlineData("Host=127.0.0.1;Port=5434;Username=reel;Password=x;Database= reel ")]
    [InlineData("Host=127.0.0.1;Username=reel;Password=x;Database=reel_ws1")]
    [InlineData("Host=db.example.com;Port=5434;Username=reel;Password=x;Database=reel_ws1")]
    public void The_probe_refuses_anything_but_a_slot_database_on_the_eval_postgres(string connectionString) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertIsolatedEvalDatabase(connectionString));

    [Fact]
    public void The_probe_accepts_a_slot_database_on_the_eval_postgres() =>
        AssertIsolatedEvalDatabase("Host=127.0.0.1;Port=5434;Username=reel;Password=x;Database=reel_ws1");
}
