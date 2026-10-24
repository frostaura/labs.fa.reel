using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
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

    [Fact]
    public async Task Measure_completion_tokens_per_purpose()
    {
        if (Environment.GetEnvironmentVariable("REEL_LLM_PROBE") != "1")
        {
            return;
        }

        var cs = Environment.GetEnvironmentVariable("REEL_PROBE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("REEL_PROBE_CONNECTION_STRING is required.");
        Assert.DoesNotContain("Database=reel;", cs + ";", StringComparison.Ordinal);
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
            null, 0));

        await Run(ApiUsagePurpose.SearchInterpret, Math.Min(calls, 20), i => (
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

        await Run(ApiUsagePurpose.RerankPage, Math.Min(calls, 20), i =>
        {
            var batch = rows.Skip(270 + (i * 9)).Take(9).Select(Input).ToList();
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

        await Run(ApiUsagePurpose.TasteNarrative, Math.Min(calls, 15), _ => (
            [LlmMessage.System(OpenRouterLensWriter.NarrativeSystemPrompt), LlmMessage.User(OpenRouterLensWriter.NarrativePrompt(facts.Facts))],
            OpenRouterLensWriter.NarrativeSchema(), 0));

        await recorder.FlushAsync(CancellationToken.None);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"# done {DateTime.UtcNow:O}"));
    }
}
