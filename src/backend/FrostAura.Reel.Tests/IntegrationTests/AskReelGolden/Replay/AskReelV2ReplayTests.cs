using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden.Replay;

/// <summary>
/// The Ask Reel v2 replay suite (program §4.12 "replay" mode, WS-7 task 6): recorded plans and judge
/// verdicts drive the REAL v2 executor and composer — retrieval, eligibility, placement, trays, state,
/// handles, persistence and the endpoint's lifecycle — through <c>POST /api/search/ask</c> with
/// <c>ASK_ENGINE=v2</c>, deterministically and with zero paid calls (the model ports are replaced by the
/// recording; <see cref="ReelApiFactory"/> refuses live keys).
///
/// <para>Each fixture in <c>eval/askreel/replay/v2/</c> is a small world (titles, the account's standing
/// conditions and footprint) and a conversation: per turn the planner's plan, the judge's verdict per title
/// (by name; "*" for every title) and, for an item question, the answer. Expectations are the golden eval's
/// machine checks: the mode, the exact picks, minimum picks, titles that must never appear (every title the
/// world marks "out" is added: a standing exclusion or the footprint), no repeat of an earlier turn's cards,
/// the handle an answer resolves, and two grounding checks on the reply (it names no title that is not on
/// screen, and does not echo the user's message).</para>
///
/// <para>Fixtures are authored until the eval key exists; once it does, <c>askreel.mjs live --engine v2</c>
/// recordings are the source and <c>provenance</c> says which. Every name and topic word is suffixed with a
/// per-run token, so a fixture sees only its own world on the shared test database.</para>
/// </summary>
[Collection("postgres")]
[Trait("Suite", "Replay")]
public class AskReelV2ReplayTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(FixtureDir(), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Fact]
    public void The_replay_set_covers_the_turn_kinds_the_engine_executes()
    {
        var fixtures = Directory.EnumerateFiles(FixtureDir(), "*.json").Select(f => Load(Path.GetFileNameWithoutExtension(f))).ToList();
        var acts = fixtures.SelectMany(f => f.Turns).Select(t => t.Plan.Act).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string>(["NewRequest", "Refine", "CritiqueSet", "ItemQuestion"], StringComparer.Ordinal), acts);
        Assert.Contains(fixtures, f => f.Turns.Any(t => t.Expect.Mode == "NoneFound"));
        Assert.Contains(fixtures, f => f.Turns.Any(t => t.Expect.NoRepeatTurns is { Count: > 0 }));
        Assert.Contains(fixtures, f => f.Titles.Any(t => t.Out is not null));
        Assert.All(fixtures, f => Assert.False(string.IsNullOrWhiteSpace(f.Provenance), $"{f.Id} does not say where its plans came from"));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task A_recorded_conversation_replays_through_the_v2_engine_to_its_expectations(string name)
    {
        if (!fixture.Available)
        {
            return;
        }

        var fx = Load(name);
        var script = new ReplayScript(fx, $"r{Guid.NewGuid():N}"[..9]);
        using var factory = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(AskReelEngineOptions.Setting, AskReelEngineOptions.V2);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAskReelPlanner>();
                services.AddSingleton<IAskReelPlanner>(script);
                services.RemoveAll<IRequestJudgeModel>();
                services.AddSingleton<IRequestJudgeModel>(script);
                services.RemoveAll<IAskReelAnswerer>();
                services.AddSingleton<IAskReelAnswerer>(script);
                services.RemoveAll<ITmdbClient>();
                services.AddSingleton<ITmdbClient, SilentTmdbClient>();
            });
        });
        var (accountId, client) = await SignInAsync(factory, $"replay-{script.Token}");
        using var _ = client;
        var ids = await SeedWorldAsync(fx, script, accountId);
        var names = ids.ToDictionary(kv => kv.Value, kv => kv.Key);
        var outNames = fx.Titles.Where(t => t.Out is not null).Select(t => t.Name).ToList();

        var failures = new List<string>();
        var shownByTurn = new List<List<(string Name, Guid Id, string? Handle)>>();
        string? conversationId = null;
        for (var i = 0; i < fx.Turns.Count; i++)
        {
            var turn = fx.Turns[i];
            script.Turn = i;
            var frames = await AskAsync(client, new { query = turn.Message, conversationId, clientTurnId = Guid.NewGuid() });
            var label = $"{fx.Id} turn {i + 1} (\"{turn.Message}\")";
            conversationId ??= frames.FirstOrDefault(f => f.Event == "conversation")?.Data.GetProperty("id").GetString();
            var done = frames.LastOrDefault(f => f.Event == "done")?.Data;
            if (done is not { } d)
            {
                failures.Add($"{label}: no done frame ({string.Join(", ", frames.Select(f => f.Event))})");
                shownByTurn.Add([]);
                continue;
            }

            var picks = Cards(d, "results", names);
            var shown = picks
                .Concat(new[] { "belowFloor", "nearMiss", "unchecked" }.SelectMany(k => Cards(d, k, names)))
                .Concat(frames.Where(f => f.Event is "picks" or "tray").SelectMany(f => Cards(f.Data, "cards", names)))
                .DistinctBy(c => c.Id)
                .ToList();
            shownByTurn.Add(shown);

            var mode = d.TryGetProperty("mode", out var m) ? m.ToString() : null;
            if (turn.Expect.Mode is { } wantMode && mode != wantMode)
            {
                failures.Add($"{label}: mode {mode}, expected {wantMode}");
            }

            if (turn.Expect.Picks is { } wantPicks
                && !picks.Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(wantPicks))
            {
                failures.Add($"{label}: picks [{string.Join(", ", picks.Select(p => p.Name))}], expected [{string.Join(", ", wantPicks)}]");
            }

            if (turn.Expect.MinPicks is { } min && picks.Count < min)
            {
                failures.Add($"{label}: {picks.Count} pick(s), expected at least {min} (earlier turns showed {string.Join(", ", shownByTurn.Take(i).Select(s => s.Count))} card(s))");
            }

            foreach (var never in outNames.Concat(turn.Expect.NeverShown ?? []))
            {
                if (shown.Any(c => c.Name == never))
                {
                    failures.Add($"{label}: showed \"{never}\", which must never appear ({fx.Titles.FirstOrDefault(t => t.Name == never)?.Out ?? "judged not a match"})");
                }
            }

            foreach (var earlier in turn.Expect.NoRepeatTurns ?? [])
            {
                var repeats = picks.Where(p => shownByTurn[earlier - 1].Any(e => e.Id == p.Id)).Select(p => p.Name).ToList();
                if (repeats.Count > 0)
                {
                    failures.Add($"{label}: repeated turn {earlier}'s {string.Join(", ", repeats)}");
                }
            }

            if (turn.Expect.AnswerHandle is { } handle)
            {
                var answer = frames.FirstOrDefault(f => f.Event == "answer")?.Data;
                var parsed = TurnHandle.Parse(handle)!.Value;
                var target = shownByTurn[parsed.Turn - 1].FirstOrDefault(c => c.Handle == handle);
                if (answer is not { } a)
                {
                    failures.Add($"{label}: no answer frame");
                }
                else if (a.GetProperty("handle").GetString() != handle || target.Id == Guid.Empty
                    || !a.TryGetProperty("titleId", out var about) || about.ValueKind != JsonValueKind.String || about.GetGuid() != target.Id)
                {
                    failures.Add($"{label}: answered about {a.GetProperty("handle")} / {(a.TryGetProperty("titleId", out var t) ? t.ToString() : "nothing")}, expected {handle} ({target.Name})");
                }
            }

            var reply = frames.LastOrDefault(f => f.Event == "assistant-message")?.Data.GetProperty("text").GetString() ?? string.Empty;
            var onScreen = shownByTurn.SelectMany(s => s).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var title in fx.Titles.Where(t => !onScreen.Contains(t.Name) && !turn.Message.Contains(t.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (reply.Contains(title.Name, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{label}: the reply names \"{title.Name}\", which is not on screen");
                }
            }

            if (turn.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 5
                && reply.Contains(turn.Message, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{label}: the reply echoes the user's message");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(fx.Turns.Count, script.PlansServed);
    }

    // ── The recording ───────────────────────────────────────────────────────────────────────

    private sealed record ReplayFixture(
        string Id, string Scenario, string Provenance, ReplayAccount Account, List<ReplayTitle> Titles, List<ReplayTurn> Turns);

    private sealed record ReplayAccount(List<List<string>> Filters);

    private sealed record ReplayTitle(
        string Name, List<string> Keywords, int Votes, List<string>? Genres = null, string? Language = null, string? Media = null,
        bool Seen = false, int? Rated = null, string? Reaction = null, string? Out = null);

    private sealed record ReplayTurn(string Message, ReplayPlan Plan, Dictionary<string, string>? Verdicts, string? Answer, ReplayExpect Expect);

    private sealed record ReplayPlan(
        string Act, List<string>? Concepts = null, string? Media = null, List<string>? Negations = null, List<string>? References = null,
        List<string>? Tools = null, string? Question = null);

    private sealed record ReplayExpect(
        string? Mode = null, List<string>? Picks = null, int? MinPicks = null, List<string>? NeverShown = null,
        List<int>? NoRepeatTurns = null, string? AnswerHandle = null);

    private static string FixtureDir() => Path.Combine(RepoRoot(), "eval", "askreel", "replay", "v2");

    private static ReplayFixture Load(string name) =>
        JsonSerializer.Deserialize<ReplayFixture>(File.ReadAllText(Path.Combine(FixtureDir(), $"{name}.json")), Json)
        ?? throw new InvalidOperationException($"replay fixture {name} is empty");

    /// <summary>
    /// The recorded model side of one conversation: the planner returns the turn's recorded plan, the judge
    /// the recorded verdict per title (by name; unrecorded titles are judged "no"), the answerer the recorded
    /// answer. Topic words carry the run's token so retrieval sees only this world.
    /// </summary>
    private sealed class ReplayScript(ReplayFixture fixture, string token) : IAskReelPlanner, IRequestJudgeModel, IAskReelAnswerer
    {
        private int _plans;

        public string Token { get; } = token;

        public int Turn { get; set; }

        public int PlansServed => Volatile.Read(ref _plans);

        public bool IsAvailable => true;

        public string ModelId => "replay";

        public string CacheSalt => $"replay-{Token}-{Turn}";

        public string Word(string word) => word + Token;

        public string Named(string name) => $"{name} {Token}";

        public string Unnamed(string name) => name.EndsWith($" {Token}", StringComparison.Ordinal) ? name[..^(Token.Length + 1)] : name;

        public Task<PlanResult> PlanAsync(PlanInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _plans);
            var p = fixture.Turns[Turn].Plan;
            var delta = ConversationStateDelta.None with
            {
                Concepts = [.. (p.Concepts ?? []).Select(Word)],
                Media = p.Media is null ? null : Enum.Parse<MediaScope>(p.Media),
                Negations = [.. (p.Negations ?? []).Select(Word)],
            };
            return Task.FromResult(new PlanResult(
                Enum.Parse<TurnAct>(p.Act),
                delta,
                [.. (p.References ?? []).Select(h => TurnHandle.Parse(h)!.Value)],
                [.. (p.Tools ?? ["SearchCatalog"]).Select(t => new ToolRequest(Enum.Parse<AskTool>(t)))],
                null,
                [],
                PlanSource.Llm,
                new LlmCallMeta("replay", "none", false, 1, 1),
                p.Question));
        }

        public Task<JudgeBatchResult> JudgeBatchAsync(JudgeInput input, CancellationToken ct)
        {
            var recorded = fixture.Turns[Turn].Verdicts ?? [];
            var verdicts = input.Titles.Select(t =>
            {
                var plain = Unnamed(t.Name);
                var said = recorded.GetValueOrDefault(plain) ?? recorded.GetValueOrDefault("*") ?? "No";
                var satisfies = Enum.Parse<Satisfies>(said);
                return new JudgeVerdict(
                    t.TitleId, satisfies, satisfies == Satisfies.Yes ? 0.85 : 0.4, "Recorded verdict.",
                    $"keyword: {t.Keywords.FirstOrDefault()}", ResultSource.Llm, ResultSource.Llm);
            }).ToList();
            return Task.FromResult(new JudgeBatchResult(verdicts, new LlmCallMeta("replay", "none", false, 1, 1), false));
        }

        public Task<AnswerResult> AnswerAsync(AnswerInput input, CancellationToken ct) =>
            Task.FromResult(new AnswerResult(
                fixture.Turns[Turn].Answer ?? "Recorded answer.", new LlmCallMeta("replay", "none", false, 1, 1), null));
    }

    // ── The world ───────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, Guid>> SeedWorldAsync(ReplayFixture fx, ReplayScript script, Guid accountId)
    {
        await using var db = fixture.CreateContext(new Application.Tenancy.AccountContext());
        foreach (var filter in fx.Account.Filters)
        {
            db.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = Enum.Parse<FilterKind>(filter[0]),
                Value = filter[1],
                CreatedAt = DateTime.UtcNow,
            });
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var t in fx.Titles)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = t.Media is null ? MediaType.Movie : Enum.Parse<MediaType>(t.Media),
                TmdbId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue),
                TraktSlug = string.Empty,
                Name = script.Named(t.Name),
                Language = t.Language ?? "en",
                Year = 2010,
                RuntimeMinutes = 100,
                PosterPath = "/p.jpg",
                Genres = [.. t.Genres ?? ["action"]],
                Keywords = [.. t.Keywords.Select(script.Word)],
                TmdbVoteAverage = 7.5m,
                TmdbVoteCount = t.Votes,
                TmdbPopularity = 0m,
                CreatedAt = DateTime.UtcNow,
            };
            db.Titles.Add(title);
            ids[t.Name] = title.Id;
            if (t.Seen)
            {
                db.WatchedTitles.Add(new WatchedTitle
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    Plays = 1,
                    IsFullyWatched = true,
                    LastWatchedAt = DateTime.UtcNow,
                    FirstSyncedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            if (t.Rated is { } rating)
            {
                db.UserRatings.Add(new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    SubjectType = RatingSubjectType.Movie,
                    Rating = (short)rating,
                    RatedAt = DateTime.UtcNow,
                    FirstRatedAt = DateTime.UtcNow,
                });
            }

            if (t.Reaction is { } reaction)
            {
                db.UserTitleReactions.Add(new UserTitleReaction
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    Kind = Enum.Parse<ReactionKind>(reaction),
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private static List<(string Name, Guid Id, string? Handle)> Cards(JsonElement frame, string property, Dictionary<Guid, string> names) =>
        frame.TryGetProperty(property, out var cards) && cards.ValueKind == JsonValueKind.Array
            ? [.. cards.EnumerateArray().Select(c =>
            {
                var id = c.GetProperty("titleId").GetGuid();
                var handle = c.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                return (names.GetValueOrDefault(id) ?? $"(outside the world: {c.GetProperty("name").GetString()})", id, handle);
            })]
            : [];

    // ── The wire ────────────────────────────────────────────────────────────────────────────

    private sealed record SseFrame(string Event, JsonElement Data);

    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(WebApplicationFactory<Program> factory, string slug)
    {
        var accountId = await api.SeedAccountAsync(slug);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
        var cookies = new CookieContainerHandler();
        cookies.Container.Add(factory.ClientOptions.BaseAddress, new Cookie("reel_at", token, "/", factory.ClientOptions.BaseAddress.Host));
        return (accountId, factory.CreateDefaultClient(cookies));
    }

    private static async Task<List<SseFrame>> AskAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        return [.. raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(chunk => !chunk.StartsWith(':'))
            .Select(chunk => Regex.Match(chunk, @"^(id: \d+\n)?event: (?<event>[a-z][a-z-]*)\ndata: (?<data>\{.*\})$", RegexOptions.Singleline))
            .Where(m => m.Success)
            .Select(m => new SseFrame(m.Groups["event"].Value, JsonDocument.Parse(m.Groups["data"].Value).RootElement.Clone()))];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("fa.reel.slnx not found above the test assembly.");
    }
}
