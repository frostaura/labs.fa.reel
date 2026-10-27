using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// R1-01 (J-11): every scope WS-3's code opens says whose data it reads, so Ask Reel and the keyword
/// vocabulary keep working when <c>Tenancy:UnscopedReads</c> becomes Throw.
///
/// <para>The retriever runs its supply count and its TMDB arm on their own DI scopes so they overlap
/// the catalogue arms. A fresh scope has a fresh, unpinned <see cref="IAccountContext"/>, so each must
/// pin the turn's account before it resolves anything that reads. The turn runs through the real
/// endpoint with the REAL embedding and retrieval registrations — the shared harness swaps the
/// embedding provider for a stub, which is exactly what hid a root-scope capture (CR-B) on the same
/// chain — and the provider stays keyless, so nothing is spent.</para>
/// </summary>
[Collection("postgres")]
public class RetrievalTenancyTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    public static TheoryData<string, UnscopedReadMode> EnginesAndModes => new()
    {
        { AskReelEngineOptions.V1, UnscopedReadMode.Throw },
        { AskReelEngineOptions.V2, UnscopedReadMode.Throw },
        { AskReelEngineOptions.V1, UnscopedReadMode.Warn },
        { AskReelEngineOptions.V2, UnscopedReadMode.Warn },
    };

    [Theory]
    [MemberData(nameof(EnginesAndModes))]
    public async Task An_ask_turn_reads_nothing_unscoped(string engine, UnscopedReadMode mode)
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = $"tenancytopic{Guid.NewGuid():N}"[..20];
        var log = new CapturingLoggerProvider();
        var planner = new TopicPlanner(topic);
        using var factory = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(AskReelEngineOptions.Setting, engine);
            builder.UseSetting(UnscopedReadPolicy.ConfigurationKey, mode.ToString());
            builder.ConfigureLogging(logging => logging.AddProvider(log));
            builder.ConfigureServices(services =>
            {
                // The production registration, keyless: unavailable, so the semantic arm stands on
                // label embeddings — but resolved through the same typed client and scopes as live.
                services.RemoveAll<IEmbeddingProvider>();
                services.AddHttpClient<IEmbeddingProvider, OpenAiEmbeddingProvider>(client =>
                    client.BaseAddress = new Uri("https://embeddings.invalid/"));
                services.RemoveAll<IAskReelPlanner>();
                services.AddSingleton<IAskReelPlanner>(planner);
                services.RemoveAll<IRequestJudgeModel>();
                services.AddSingleton<IRequestJudgeModel, YesJudge>();
                services.RemoveAll<ITmdbClient>();
                services.AddSingleton<ITmdbClient, SilentTmdbClient>();
            });
        });

        var accountId = await api.SeedAccountAsync($"tenancy-{engine}-{Guid.NewGuid():N}");
        await SeedTopicalAsync(topic, 6);
        using var client = SignedIn(factory, accountId);

        var stream = await AskAsync(client, new { query = $"{topic} movies", clientTurnId = Guid.NewGuid() });

        var done = stream.Last(f => f.Event == "done").Data;
        Assert.False(done.TryGetProperty("failed", out var failed) && failed.GetBoolean(), done.ToString());
        Assert.Equal(engine, stream.Single(f => f.Event == "conversation").Data.GetProperty("engine").GetString());
        if (engine == AskReelEngineOptions.V2)
        {
            // The retriever really ran — the planner was asked, and its concept found the seeded titles.
            Assert.NotEmpty(planner.Calls);
            Assert.NotEmpty(stream.Where(f => f.Event == "picks").SelectMany(f => f.Data.GetProperty("cards").EnumerateArray()));
        }

        // Late arms finish on their own scope after the stream closes; give them their deadline.
        await Task.Delay(TimeSpan.FromMilliseconds(1_600));

        var refused = log.Entries.Where(e => Chain(e.Exception).Any(x => x is UnscopedTenantQueryException)).ToList();
        Assert.True(refused.Count == 0, "Refused unscoped reads: " + string.Join(" | ", refused.Select(e => e.Category + ": " + e.Message)));
        var warned = log.Entries.Where(e => e.Message.StartsWith("Unscoped tenant query", StringComparison.Ordinal)).ToList();
        Assert.True(warned.Count == 0, "Unscoped reads: " + string.Join(" | ", warned.Select(e => e.Message)));
    }

    /// <summary>
    /// The keyword vocabulary's warm-up and daily refresh read the whole catalogue for every account,
    /// so each pass opens a system scope — declared, not inherited from a permissive default.
    /// </summary>
    [Fact]
    public async Task The_keyword_label_refresh_runs_each_pass_in_a_system_scope()
    {
        if (!fixture.Available)
        {
            return;
        }

        var seen = new ConcurrentBag<(string Pass, bool System)>();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddMemoryCache();
        services.AddScoped<IAccountContext>(_ => new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw)));
        services.AddDbContext<ReelDbContext>(o => o.UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<Application.Persistence.IReelDbContext>(sp => sp.GetRequiredService<ReelDbContext>());
        services.AddSingleton<IEmbeddingProvider>(new UnavailableEmbeddings());
        services.AddScoped(sp =>
        {
            seen.Add(("warm", sp.GetRequiredService<IAccountContext>().IsSystemScope));
            return new KeywordVocabulary(sp.GetRequiredService<Application.Persistence.IReelDbContext>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());
        });
        services.AddScoped(sp =>
        {
            seen.Add(("refresh", sp.GetRequiredService<IAccountContext>().IsSystemScope));
            return new KeywordLabelRefresher(
                sp.GetRequiredService<Application.Persistence.IReelDbContext>(),
                sp.GetRequiredService<IEmbeddingProvider>(),
                NullLogger<KeywordLabelRefresher>.Instance);
        });
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var service = new KeywordLabelRefreshService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<KeywordLabelRefreshService>.Instance);

        await service.WarmAsync(CancellationToken.None);
        var refreshed = await service.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal([("refresh", true), ("warm", true)], seen.OrderBy(s => s.Pass, StringComparer.Ordinal));
        Assert.NotNull(refreshed);
    }

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }
        }
    }

    private async Task SeedTopicalAsync(string topic, int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            db.Titles.Add(new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue),
                TraktSlug = string.Empty,
                Name = $"Tenancy {topic} {i}",
                Language = "en",
                Year = 2010,
                RuntimeMinutes = 100,
                PosterPath = "/p.jpg",
                Genres = ["action"],
                Keywords = [topic],
                TmdbVoteAverage = 7.5m,
                TmdbVoteCount = 5_000 - i,
                TmdbPopularity = 0m,
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private HttpClient SignedIn(WebApplicationFactory<Program> factory, Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = db.Accounts.IgnoreQueryFilters().First(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
        var cookies = new CookieContainerHandler();
        cookies.Container.Add(factory.ClientOptions.BaseAddress, new Cookie("reel_at", token, "/", factory.ClientOptions.BaseAddress.Host));
        return factory.CreateDefaultClient(cookies);
    }

    private sealed record SseFrame(string Event, JsonElement Data);

    private static async Task<List<SseFrame>> AskAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        return raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(chunk => !chunk.StartsWith(':'))
            .Select(chunk => Regex.Match(chunk, @"^(id: \d+\n)?event: (?<event>[a-z][a-z-]*)\ndata: (?<data>\{.*\})$", RegexOptions.Singleline))
            .Where(m => m.Success)
            .Select(m => new SseFrame(m.Groups["event"].Value, JsonDocument.Parse(m.Groups["data"].Value).RootElement.Clone()))
            .ToList();
    }

    /// <summary>A new search for the seeded topic, so the facet, full-text, supply and TMDB arms all run.</summary>
    private sealed class TopicPlanner(string topic) : IAskReelPlanner
    {
        public ConcurrentBag<PlanInput> Calls { get; } = [];

        public bool IsAvailable => true;

        public string ModelId => "tenancy-plan";

        public Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct)
        {
            Calls.Add(input);
            return Task.FromResult(new PlanResult(
                TurnAct.NewRequest, ConversationStateDelta.None with { Concepts = [topic] }, [], [new ToolRequest(AskTool.SearchCatalog)],
                null, [], PlanSource.Llm, new LlmCallMeta("tenancy-plan", "none", false, 1, 5)));
        }
    }

    private sealed class YesJudge : IRequestJudgeModel
    {
        public bool IsAvailable => true;

        public string ModelId => "tenancy-judge";

        public string CacheSalt => $"tenancy-judge-{Guid.NewGuid():N}";

        public Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct) =>
            Task.FromResult(new JudgeBatchResult(
                [.. input.Titles.Select(t => new JudgeVerdict(
                    t.TitleId, Satisfies.Yes, 0.8, "You get the thing you asked for.", $"keyword: {t.Keywords.FirstOrDefault()}",
                    ResultSource.Llm, ResultSource.Llm))],
                new LlmCallMeta("tenancy-judge", "none", false, 1, 3),
                false));
    }

    private sealed class UnavailableEmbeddings : IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("No embedding provider in this fixture.");
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. entries];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner.entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
                }
            }
        }
    }
}
