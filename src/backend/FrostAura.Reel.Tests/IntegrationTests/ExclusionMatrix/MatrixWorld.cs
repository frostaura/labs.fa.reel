using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.ExclusionMatrix;

/// <summary>
/// The world the exclusion matrix (program WS-7 task 7) is measured in: one small catalogue in a
/// database of its own, accounts in pairs — a guarded account carrying an exclusion and a control
/// account with the same history and no exclusion — and every surface fetched through the real API
/// for each of them. <see cref="MatrixCells"/> reads the verdicts off what each account saw.
///
/// <para><b>Why a database of its own.</b> Feed, rails, Similar and the digest RANK the catalogue. On
/// the shared test database they would rank every other class's leftovers too, and a violator could
/// be crowded out of a surface for reasons that have nothing to do with the exclusion — which reads
/// as a pass. Here the catalogue is only the matrix's own titles, so a surface shows most of it.</para>
///
/// <para><b>Why a control account.</b> "The violator is not on the surface" proves nothing unless the
/// surface would have shown it. A cell is exercised only when the control's surface DOES show the
/// violator, and an unexercised cell fails rather than passing.</para>
///
/// <para><b>Three kinds of pair.</b> Broad pairs (one per exclusion family: rules, footprint, the
/// For-you bar) fetch every surface over the whole catalogue. Surfaces that show only their top few —
/// tonight's picks (5), the digest (10) — cannot show every violator at once, so each exclusion kind
/// also gets a focus pair whose account has watched everything except that kind's violators and one
/// clean title per medium: what is left fits the capped surfaces, so they can show all of it. The
/// Watch-tonight pin shows ONE title, so each (kind, medium) gets a pin pair whose queue holds only
/// that kind's violator.</para>
///
/// <para><b>Standing and late.</b> A standing exclusion exists before the feed and the digest are
/// built. A late one is added after, with no rebuild: the materialised build, the rails and the
/// digest must still honour it. That is the timestamp-aware half of task 7 — nothing may be served
/// after the moment a title entered the footprint or an exclusion.</para>
/// </summary>
public sealed class MatrixWorld
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MatrixWorld? _shared;

    private MatrixWorld()
    {
    }

    /// <summary>
    /// The one world every cell reads, built on first use. It lives in the <c>postgres</c> collection
    /// (its fixture decides whether integration tests run at all), and it is built whole and then torn
    /// down — host disposed, database dropped — before any cell is read, because everything a verdict
    /// needs is captured in memory while the world is up.
    /// </summary>
    public static async Task<MatrixWorld> SharedAsync(PostgresFixture fixture)
    {
        await Gate.WaitAsync();
        try
        {
            if (_shared is null)
            {
                var world = new MatrixWorld();
                if (fixture.Available)
                {
                    await world.BuildAsync();
                }

                _shared = world;
            }

            return _shared;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The surfaces the matrix reads, in report order.</summary>
    public static readonly string[] Surfaces =
    [
        "feed-hero", "feed-row", "more", "genre-rail", "similar", "digest", "ask", "typeahead", "person", "saved", "watch-tonight",
    ];

    /// <summary>Surfaces that show only their top few: read from focus pairs, not broad ones.</summary>
    public static readonly string[] CappedSurfaces = ["feed-hero", "digest"];

    /// <summary>The exclusion families: the account's content rules, its interaction footprint, its For-you bar.</summary>
    public enum Family
    {
        Rules,
        Footprint,
        Bar,
    }

    /// <summary>
    /// The rule exclusions under test. <c>genre-standin</c> is the TV half of an ExcludeGenre of horror,
    /// which TMDB's TV taxonomy cannot tag (GenreStandIns): a show carrying "zombie" is excluded; a film
    /// carrying it is not (films have a horror id), so its movie violator must STAY visible.
    /// </summary>
    public static readonly string[] RuleKinds = ["genre", "genre-standin", "keyword", "language", "crowd"];

    /// <summary>The footprint kinds under test.</summary>
    public static readonly string[] FootprintKinds = ["watched", "rated", "not-interested", "dropped", "saved"];

    public static readonly MediaType[] Media = [MediaType.Movie, MediaType.Show];

    /// <summary>The keyword every matrix title shares — distinctive evidence for the Similar gate, and Ask's topic.</summary>
    public const string Topic = "xmatrixtopic";

    private readonly string? _sharedDatabase = Environment.GetEnvironmentVariable(PostgresFixtureGate.ConnectionStringVariable);
    private string _connectionString = string.Empty;
    private WebApplicationFactory<Program>? _api;
    private byte[] _artifactBytes = [];
    private string[] _featureNames = [];

    public bool Available { get; private set; }

    /// <summary>The violator of each (kind, medium): a title that breaks exactly that exclusion and nothing else.</summary>
    public Dictionary<(string Kind, MediaType Media), Title> Violators { get; } = [];

    /// <summary>The For-you bar the bar family runs at: the broad control's median served prediction.</summary>
    public decimal Bar { get; private set; }

    /// <summary>What each account saw, per surface: the title ids, in served order (duplicates kept).</summary>
    public Dictionary<string, Dictionary<string, List<Guid>>> Seen { get; } = [];

    /// <summary>The Watch-tonight pin of each account (title id and reason), when there was one.</summary>
    public Dictionary<string, (Guid TitleId, string? Reason)?> Pins { get; } = [];

    /// <summary>The active-artifact prediction of every scored title, per account, read after its fetch.</summary>
    public Dictionary<string, Dictionary<Guid, decimal>> Predictions { get; } = [];

    /// <summary>The SSE event names Ask answered with, per account (what the card frames were read from).</summary>
    public Dictionary<string, List<string>> AskEvents { get; } = [];

    /// <summary>
    /// Ask's labelled trays per account (below the For-you bar, near misses): shown, but marked as not
    /// meeting the request. A standing exclusion or the footprint may not reach them either; the bar may.
    /// </summary>
    public Dictionary<string, List<Guid>> AskTrays { get; } = [];

    /// <summary>The feed's own disclosure per account (a relaxed For-you floor says so here), or null.</summary>
    public Dictionary<string, string?> FeedDegraded { get; } = [];

    /// <summary>Anything that went wrong while building the world, surfaced by the tests rather than thrown.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>Wall time of the whole world, for the task 7 acceptance (under 10 minutes).</summary>
    public TimeSpan BuildTime { get; private set; }

    public MediaType MediaOf(Guid titleId) => _catalogue.First(t => t.Id == titleId).MediaType;

    /// <summary>A title's name, for evidence lines.</summary>
    public string NameOf(Guid titleId) => _catalogue.FirstOrDefault(t => t.Id == titleId)?.Name ?? titleId.ToString();

    private readonly List<Title> _catalogue = [];
    private Person _person = null!;
    private StreamingProvider _provider = null!;

    private async Task BuildAsync()
    {
        var started = DateTime.UtcNow;
        var shared = new NpgsqlConnectionStringBuilder(_sharedDatabase!);
        _connectionString = new NpgsqlConnectionStringBuilder(_sharedDatabase!) { Database = $"{shared.Database}_xmatrix" }.ConnectionString;
        if (PostgresFixtureGate.LiveTargetRefusal(_connectionString) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        await using (var db = Context())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        _api = ReelApiFactory.ForDatabase(_connectionString).WithWebHostBuilder(host => host.ConfigureServices(services =>
        {
            // Offline: the matrix's catalogue is the database. A TMDB discovery or hydration call would
            // add titles the matrix never seeded, and Ask's live expansion would reach the network.
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient, SilentTmdbClient>();
        }));

        await SeedCatalogueAsync();
        await TrainArtifactAsync();
        try
        {
            await BuildAccountsAsync();
        }
        finally
        {
            await _api.DisposeAsync();
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }

        BuildTime = DateTime.UtcNow - started;
        Available = true;
    }

    private ReelDbContext Context() =>
        new(new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(_connectionString, n => n.UseVector()).Options, new AccountContext());

    // ── The catalogue ──────────────────────────────────────────────────────────────────────────

    /// <summary>The violator kinds seeded, per medium. The saved-* ones sit on every broad queue.</summary>
    private static IEnumerable<string> SeededKinds() =>
        RuleKinds.Concat(FootprintKinds).Concat(RuleKinds.Select(k => $"saved-{k}"));

    private async Task SeedCatalogueAsync()
    {
        var now = DateTime.UtcNow;
        _person = new Person { Id = Guid.NewGuid(), TmdbId = 9_100_001, Name = "Matrix Person", KnownForDepartment = "Acting" };
        _provider = new StreamingProvider { Id = Guid.NewGuid(), TmdbProviderId = 9_100_001, Name = "MatrixFlix", DisplayPriority = 1 };

        // Loved titles: the taste every account shares. Drama and crime, three or more each, so both are
        // offered as genre rails. Released years ago, so they are never the digest's news.
        var loved = Enumerable.Range(0, 12).Select(i => NewTitle($"loved {i}", MediaType.Movie, 1m)).ToList();
        foreach (var t in loved)
        {
            t.ReleasedAt = now.AddYears(-3);
            t.Year = now.Year - 3;
        }

        // Clean candidates: fill every surface around the violators.
        var clean = Media.SelectMany(m => Enumerable.Range(0, 8).Select(i => NewTitle($"clean {i}", m, 40m - i))).ToList();

        // Decoys: clean titles the model rates above everything, so where a focus-row account keeps
        // them they take tonight's picks and the violators fall to the rows below.
        var decoys = Media.SelectMany(m => Enumerable.Range(0, 5).Select(i => NewTitle($"decoy {i}", m, 95m - i))).ToList();

        // One violator per (kind, medium): drama and crime like the taste, the topic and the person like
        // everything else, the most popular in the catalogue — so nothing but the exclusion keeps it out.
        foreach (var media in Media)
        {
            foreach (var kind in SeededKinds())
            {
                var v = NewTitle(kind, media, 90m);
                switch (kind.Replace("saved-", string.Empty, StringComparison.Ordinal))
                {
                    case "genre":
                        v.Genres = ["drama", "crime", "horror"];
                        break;
                    case "genre-standin":
                        v.Keywords = [Topic, "zombie"];
                        break;
                    case "keyword":
                        v.Keywords = [Topic, "killer clown"];
                        break;
                    case "language":
                        v.Language = "fr";
                        break;
                    case "crowd":
                        v.TmdbVoteAverage = 4.5m;
                        v.TraktRating = 4.5m;
                        break;
                }

                Violators[(kind, media)] = v;
            }
        }

        // Filler: far away in embedding space and not renderable (no poster), so it never reaches a
        // card, but it gives every anchor the 50 neighbours the Similar gate's floor needs.
        var filler = Enumerable.Range(0, 60).Select(i =>
        {
            var t = NewTitle($"filler {i}", MediaType.Movie, 0.1m);
            t.PosterPath = null;
            t.Genres = ["documentary"];
            t.Keywords = [];
            t.ReleasedAt = now.AddYears(-5);
            return t;
        }).ToList();

        _catalogue.AddRange(loved);
        _catalogue.AddRange(clean);
        _catalogue.AddRange(decoys);
        _catalogue.AddRange(Violators.Values);
        _catalogue.AddRange(filler);

        await using var db = Context();
        db.Add(_person);
        db.Add(_provider);
        db.Titles.AddRange(_catalogue);
        var rng = new Random(7);
        foreach (var t in _catalogue)
        {
            var isFiller = filler.Contains(t);
            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = t.Id,
                Embedding = new Pgvector.Vector(Direction(rng, isFiller ? 1 : 0)),
                EmbeddingModel = "matrix",
                SourceTextHash = $"matrix-{t.Id:N}",
                CreatedAt = now,
            });
            if (isFiller)
            {
                continue;
            }

            // The person page lists forty titles, rated ones first: credit only the titles a person page
            // cell reads (violators and clean titles), so none of them is cut by the forty.
            if (!t.Name.StartsWith("Xm loved", StringComparison.Ordinal) && !t.Name.StartsWith("Xm saved-", StringComparison.Ordinal))
            {
                db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = t.Id, PersonId = _person.Id, Role = CreditRole.Actor, CastOrder = 1 });
            }
            db.TitleAvailabilities.Add(new TitleAvailability
            {
                Id = Guid.NewGuid(),
                TitleId = t.Id,
                Region = "ZA",
                ProviderId = _provider.Id,
                Kind = AvailabilityKind.Flatrate,
                FetchedAt = now,
                FirstSeenAt = now.AddDays(-1),
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A unit vector near axis <paramref name="axis"/>: the matrix titles cluster on axis 0, the filler on axis 1.</summary>
    private static float[] Direction(Random rng, int axis)
    {
        var v = new float[1536];
        v[axis] = 1f;
        for (var i = 2; i < 40; i++)
        {
            v[i] = (float)(rng.NextDouble() * 0.2);
        }

        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }

    private static Title NewTitle(string label, MediaType media, decimal popularity)
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        return new Title
        {
            Id = id,
            MediaType = media,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"Xm {label} {(media == MediaType.Movie ? "film" : "series")} {id.ToString("N")[..6]}",
            Overview = $"A {label} story.",
            Language = "en",
            PosterPath = "/matrix.jpg",
            Genres = ["drama", "crime"],
            Keywords = [Topic],
            Year = now.Year,
            RuntimeMinutes = 95,
            TraktRating = 7.6m,
            TraktVotes = 900,
            TmdbVoteAverage = 7.6m,
            TmdbVoteCount = 1_500,
            TmdbPopularity = popularity,
            // Inside the digest's trailing week, so every candidate is "new" for the digest to consider.
            ReleasedAt = media == MediaType.Movie ? now.AddDays(-2) : null,
            FirstAiredAt = media == MediaType.Show ? now.AddDays(-2) : null,
            LastMetadataRefreshAt = now,
            CreatedAt = now,
        };
    }

    // ── The accounts ───────────────────────────────────────────────────────────────────────────

    /// <summary>The broad pairs' labels, the focus pairs' and the pin pairs'.</summary>
    public static string Broad(Family family, bool guarded, bool late) =>
        !guarded ? "control" : $"{family}{(late ? "-late" : string.Empty)}".ToLowerInvariant();

    public static string Focus(string kind, bool guarded, bool late, bool rows = false) =>
        $"focus{(rows ? "-row" : string.Empty)}-{kind}{(guarded ? string.Empty : "-control")}{(late ? "-late" : string.Empty)}";

    /// <summary>What an account's fetch reads: everything, the focus surfaces, or the focus-row ones.</summary>
    private enum Scope
    {
        Broad,
        Focus,
        FocusRow,
    }

    public static string Pin(string kind, MediaType media, bool guarded) =>
        $"pin-{kind}-{media}{(guarded ? string.Empty : "-control")}".ToLowerInvariant();

    /// <summary>The kinds with focus pairs: every rule and footprint kind, and the bar.</summary>
    public static IEnumerable<string> FocusKinds() => RuleKinds.Concat(FootprintKinds).Append("bar");

    /// <summary>The kinds with pin pairs: the rule kinds (their saved-* violators), consumed saves, and the bar.</summary>
    public static IEnumerable<string> PinKinds() => RuleKinds.Concat(["watched", "rated", "bar"]);

    private async Task BuildAccountsAsync()
    {
        // Control first: its served predictions set the bar the bar family runs at.
        await BroadAsync(Broad(Family.Rules, guarded: false, late: false), family: null, late: false);
        Bar = MedianServedPrediction("control");

        foreach (var family in Enum.GetValues<Family>())
        {
            await BroadAsync(Broad(family, true, false), family, late: false);
            await BroadAsync(Broad(family, true, true), family, late: true);
        }

        foreach (var kind in FocusKinds())
        {
            foreach (var rows in new[] { false, true })
            {
                await FocusAsync(kind, guarded: false, late: false, rows);
                await FocusAsync(kind, guarded: true, late: false, rows);
                await FocusAsync(kind, guarded: true, late: true, rows);
            }
        }

        foreach (var kind in PinKinds())
        {
            foreach (var media in Media)
            {
                await PinAsync(kind, media, guarded: false);
                await PinAsync(kind, media, guarded: true);
            }
        }
    }

    private decimal MedianServedPrediction(string label)
    {
        var served = Seen[label].Where(kv => kv.Key is "feed-hero" or "feed-row" or "more")
            .SelectMany(kv => kv.Value).Distinct()
            .Select(id => Predictions[label].TryGetValue(id, out var p) ? p : (decimal?)null)
            .OfType<decimal>().Order().ToList();
        if (served.Count == 0)
        {
            Problems.Add("the control account was served no predicted card, so the For-you bar could not be set");
            return 6.0m;
        }

        return Math.Round(served[served.Count / 2], 1);
    }

    private async Task BroadAsync(string label, Family? family, bool late)
    {
        var accountId = await SeedAccountAsync(label, savedRuleViolators: true);
        if (family is { } standing && !late)
        {
            await ApplyAsync(accountId, standing, kind: null);
        }

        await RunJobAsync(accountId, JobKind.BuildFeed);
        await RunJobAsync(accountId, JobKind.BuildDigest);
        if (family is { } added && late)
        {
            await ApplyAsync(accountId, added, kind: null);
        }

        await FetchAsync(label, accountId, Scope.Broad);
    }

    private async Task FocusAsync(string kind, bool guarded, bool late, bool rows)
    {
        var label = Focus(kind, guarded, late, rows);
        var accountId = await SeedAccountAsync(label, savedRuleViolators: false);

        // Narrow the pool to this kind's violators and clean titles: watch the rest. A focus account's
        // pool must FIT tonight's picks (5), or the hero chooses among the violators and one of them can
        // fall out for a reason that is not the exclusion — an unexercised cell. So it keeps one clean
        // title per medium: 2 violators + 2 clean = 4. (With two per medium it was 6 for 5 seats, and the
        // hero, which seats crowd-backed titles first, left out the sub-floor crowd series on the
        // control: crowd|Show|feed-hero went unexercised once WS-5's bar-to-the-badge build merged.) A
        // focus-row account keeps two clean titles per medium for its rows, and the decoys, which
        // outrank everything and take tonight's picks.
        var keep = Media.SelectMany(m => (kind == "bar"
                ? _catalogue.Where(t => t.MediaType == m && t.Name.StartsWith("Xm clean", StringComparison.Ordinal)).Select(t => t.Id)
                : _catalogue.Where(t => t.MediaType == m && t.Name.StartsWith("Xm clean", StringComparison.Ordinal)).Take(rows ? 2 : 1).Select(t => t.Id)
                    .Append(Violators[(kind, m)].Id))
                .Concat(rows ? _catalogue.Where(t => t.MediaType == m && t.Name.StartsWith("Xm decoy", StringComparison.Ordinal)).Select(t => t.Id) : []))
            .ToHashSet();
        await WatchAllButAsync(accountId, keep);

        var family = FootprintKinds.Contains(kind) ? Family.Footprint : kind == "bar" ? Family.Bar : Family.Rules;
        if (guarded && !late)
        {
            await ApplyAsync(accountId, family, kind);
        }

        await RunJobAsync(accountId, JobKind.BuildFeed);
        await RunJobAsync(accountId, JobKind.BuildDigest);
        if (guarded && late)
        {
            await ApplyAsync(accountId, family, kind);
        }

        await FetchAsync(label, accountId, rows ? Scope.FocusRow : Scope.Focus);
    }

    private async Task PinAsync(string kind, MediaType media, bool guarded)
    {
        var label = Pin(kind, media, guarded);
        var accountId = await SeedAccountAsync(label, savedRuleViolators: false);
        var now = DateTime.UtcNow;
        await using (var db = Context())
        {
            // The queue holds ONE title, so the pin is that title or nothing.
            var title = kind switch
            {
                "watched" or "rated" => Violators[(kind, media)],
                "bar" => LowestPredicted(media),
                _ => Violators[($"saved-{kind}", media)],
            };
            db.UserTitleReactions.Add(Reaction(accountId, title.Id, ReactionKind.SaveForLater, now.AddDays(-3)));
            await db.SaveChangesAsync();
        }

        if (guarded)
        {
            var family = kind is "watched" or "rated" ? Family.Footprint : kind == "bar" ? Family.Bar : Family.Rules;
            await ApplyAsync(accountId, family, kind);
        }

        await FetchAsync(label, accountId, Scope.Focus);
    }

    /// <summary>The clean title of <paramref name="media"/> the control predicts lowest — the pin pair for the bar saves it.</summary>
    private Title LowestPredicted(MediaType media)
    {
        var control = Predictions["control"];
        return _catalogue
            .Where(t => t.MediaType == media && t.Name.StartsWith("Xm clean", StringComparison.Ordinal) && control.ContainsKey(t.Id))
            .OrderBy(t => control[t.Id])
            .FirstOrDefault()
            ?? _catalogue.First(t => t.MediaType == media && t.Name.StartsWith("Xm clean", StringComparison.Ordinal));
    }

    private async Task WatchAllButAsync(Guid accountId, HashSet<Guid> keep)
    {
        var now = DateTime.UtcNow;
        await using var db = Context();
        foreach (var t in _catalogue.Where(t => t.PosterPath != null && !keep.Contains(t.Id) && !t.Name.StartsWith("Xm loved", StringComparison.Ordinal)))
        {
            db.WatchedTitles.Add(Watched(accountId, t.Id, now.AddDays(-20)));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Applies one family's exclusions — all of them (<paramref name="kind"/> null) or one kind's. The
    /// rules family's language rule is "English only"; every other account opts every language in, so
    /// the French violator is eligible for them (an account with no language row is English-only).
    /// </summary>
    private async Task ApplyAsync(Guid accountId, Family family, string? kind)
    {
        await using var db = Context();
        var now = DateTime.UtcNow;
        switch (family)
        {
            case Family.Rules:
                var rules = new Dictionary<string, (FilterKind Kind, string Value)>
                {
                    ["genre"] = (FilterKind.ExcludeGenre, "horror"),
                    ["genre-standin"] = (FilterKind.ExcludeGenre, "horror"),
                    ["keyword"] = (FilterKind.ExcludeKeyword, "clown"),
                    ["language"] = (FilterKind.Language, "en"),
                    ["crowd"] = (FilterKind.MinCrowdRating, "6.0"),
                };
                var wanted = kind is null ? rules.Values.Distinct() : [rules[kind]];
                foreach (var (filterKind, value) in wanted)
                {
                    if (filterKind == FilterKind.Language)
                    {
                        // English only replaces "every language".
                        await db.ContentFilters.Where(f => f.AccountId == accountId && f.Kind == FilterKind.Language).ExecuteDeleteAsync();
                    }

                    db.ContentFilters.Add(new ContentFilter { Id = Guid.NewGuid(), AccountId = accountId, Kind = filterKind, Value = value, CreatedAt = now });
                }

                break;
            case Family.Footprint:
                foreach (var media in Media)
                {
                    foreach (var k in kind is null ? FootprintKinds : [kind])
                    {
                        var id = Violators[(k, media)].Id;
                        switch (k)
                        {
                            case "watched":
                                db.WatchedTitles.Add(Watched(accountId, id, now));
                                if (kind is null)
                                {
                                    db.UserTitleReactions.Add(Reaction(accountId, id, ReactionKind.SaveForLater, now.AddDays(-3)));
                                }

                                break;
                            case "rated":
                                db.UserRatings.Add(new UserRating
                                {
                                    Id = Guid.NewGuid(),
                                    AccountId = accountId,
                                    TitleId = id,
                                    Rating = 6,
                                    SubjectType = media == MediaType.Movie ? RatingSubjectType.Movie : RatingSubjectType.Show,
                                    RatedAt = now,
                                    FirstRatedAt = now,
                                    SyncedAt = now,
                                });
                                if (kind is null)
                                {
                                    db.UserTitleReactions.Add(Reaction(accountId, id, ReactionKind.SaveForLater, now.AddDays(-3)));
                                }

                                break;
                            case "not-interested":
                                db.UserTitleReactions.Add(Reaction(accountId, id, ReactionKind.NotInterested, now));
                                break;
                            case "dropped":
                                db.UserTitleReactions.Add(Reaction(accountId, id, ReactionKind.Dropped, now));
                                break;
                            case "saved":
                                db.UserTitleReactions.Add(Reaction(accountId, id, ReactionKind.SaveForLater, now));
                                break;
                        }
                    }
                }

                break;
            case Family.Bar:
                db.ContentFilters.Add(new ContentFilter
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Kind = FilterKind.MinPredictedRating,
                    Value = Bar.ToString("0.0", CultureInfo.InvariantCulture),
                    CreatedAt = now,
                });
                break;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A pin pair's footprint: for consumed saves the WATCH or RATING lands after the save (applied as the guard).</summary>
    private static WatchedTitle Watched(Guid accountId, Guid titleId, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        Plays = 1,
        IsFullyWatched = true,
        LastWatchedAt = at,
        FirstSyncedAt = at,
        UpdatedAt = at,
    };

    private static UserTitleReaction Reaction(Guid accountId, Guid titleId, ReactionKind kind, DateTime at) =>
        new() { Id = Guid.NewGuid(), AccountId = accountId, TitleId = titleId, Kind = kind, CreatedAt = at };

    private async Task<Guid> SeedAccountAsync(string label, bool savedRuleViolators, bool artifact = true)
    {
        var now = DateTime.UtcNow;
        await using var db = Context();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xmatrix-{label}",
            TraktUsername = $"xmatrix-{label}",
            DisplayName = label,
            Region = "ZA",
            Tier = AccountTier.Founder,
            PipelineStage = PipelineStage.FeedReady,
            PipelineStageChangedAt = now,
            CreatedAt = now,
            LastSeenAt = now,
        };
        db.Accounts.Add(account);
        db.ContentFilters.Add(new ContentFilter { Id = Guid.NewGuid(), AccountId = account.Id, Kind = FilterKind.Language, Value = "*", CreatedAt = now });

        // The shared taste: the twelve loved titles, rated 8–10.
        var loved = _catalogue.Where(t => t.Name.StartsWith("Xm loved", StringComparison.Ordinal)).ToList();
        for (var i = 0; i < loved.Count; i++)
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = loved[i].Id,
                Rating = (short)(8 + (i % 3)),
                SubjectType = RatingSubjectType.Movie,
                RatedAt = now.AddDays(-30 - i),
                FirstRatedAt = now.AddDays(-30 - i),
                SyncedAt = now,
            });
        }

        // A broad queue holds every rule kind's saved violator, so the Saved shelf has something the
        // rules family must hide and the control must show.
        if (savedRuleViolators)
        {
            foreach (var media in Media)
            {
                foreach (var kind in RuleKinds)
                {
                    db.UserTitleReactions.Add(Reaction(account.Id, Violators[($"saved-{kind}", media)].Id, ReactionKind.SaveForLater, now.AddMinutes(-1)));
                }
            }
        }

        if (artifact)
        {
            db.ModelArtifacts.Add(new ModelArtifact
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Version = 1,
                Algo = "FastTree",
                ArtifactBytes = _artifactBytes,
                FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = _featureNames }),
                TrainingRunId = Guid.NewGuid(),
                Status = ArtifactStatus.Active,
                TrainedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// One trained model, shared by every account, so a control and its guarded twin predict alike. It
    /// is trained on the catalogue's REAL feature vectors (a model trained on random vectors predicts one
    /// number for every title, and a For-you bar over one number cannot be exercised): each violator is
    /// labelled high (8.5–9), so nothing but its exclusion keeps it off a surface, and the clean titles
    /// are spread 4.5–8.5, so a bar at their median holds some of them out.
    /// </summary>
    private async Task TrainArtifactAsync()
    {
        var probe = await SeedAccountAsync("probe", savedRuleViolators: false, artifact: false);
        var ids = _catalogue.Where(t => t.PosterPath != null).Select(t => t.Id).ToList();
        await using var db = Context();
        var builder = new FeatureVectorBuilder(db);
        var now = DateTime.UtcNow;
        var vectors = await builder.BuildAsync(await builder.BuildTasteStateAsync(probe, now, CancellationToken.None), ids, now, CancellationToken.None);
        var violators = Violators.Values.Select(v => v.Id).ToHashSet();
        var decoys = _catalogue.Where(t => t.Name.StartsWith("Xm decoy", StringComparison.Ordinal)).Select(t => t.Id).ToHashSet();
        var clean = ids.Where(id => !violators.Contains(id) && !decoys.Contains(id)).ToList();
        var label = new Dictionary<Guid, float>();
        for (var i = 0; i < clean.Count; i++)
        {
            label[clean[i]] = 4.5f + (4f * i / Math.Max(1, clean.Count - 1));
        }

        var spread = new Random(11);
        foreach (var id in violators.Where(vectors.ContainsKey).Order())
        {
            label[id] = 8.5f + (float)(spread.NextDouble() * 0.5);
        }

        foreach (var id in decoys.Where(vectors.ContainsKey))
        {
            label[id] = 9.6f;
        }

        var rng = new Random(23);
        var rows = new List<float[]>();
        var labels = new List<float>();
        foreach (var (id, vector) in vectors.Where(kv => label.ContainsKey(kv.Key)))
        {
            for (var copy = 0; copy < 6; copy++)
            {
                rows.Add([.. vector.Select(x => x + (float)((rng.NextDouble() - 0.5) * 1e-3))]);
                labels.Add(label[id]);
            }
        }

        _featureNames = FeatureSchema.AllNames();
        _artifactBytes = new FastTreeModelEngine().Train(
            new TrainingMatrix([.. rows], [.. labels], _featureNames),
            """{"Trees":60,"Leaves":16,"MinExamplesPerLeaf":3,"LearningRate":0.3}""").ArtifactBytes;
    }

    // ── Builds, through the host's own handlers ────────────────────────────────────────────────

    /// <summary>
    /// Runs one job the way the job runner does: a scope, the account pinned before the handler is
    /// resolved, the handler, then the scope's context saved.
    /// </summary>
    private async Task RunJobAsync(Guid accountId, JobKind kind)
    {
        using var scope = _api!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var job = new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Priority = 1,
            EnqueuedAt = DateTime.UtcNow,
            TriggerReason = "exclusion-matrix",
        };
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IAccountContext>().SetAccount(accountId);
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().First(h => h.Kind == kind);
        try
        {
            await handler.ExecuteAsync(job, CancellationToken.None);
            job.Status = JobStatus.Succeeded;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Problems.Add($"{kind} failed for {accountId}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Every surface ──────────────────────────────────────────────────────────────────────────

    private async Task FetchAsync(string label, Guid accountId, Scope scope)
    {
        var broad = scope == Scope.Broad;
        var rowsToo = scope is Scope.Broad or Scope.FocusRow;
        var client = await SignedInClientAsync(accountId);
        var seen = Surfaces.ToDictionary(s => s, _ => new List<Guid>());
        Seen[label] = seen;

        var feed = await GetAsync(client, "/api/feed", label);
        FeedDegraded[label] = feed?.TryGetProperty("degradedReason", out var degraded) == true && degraded.ValueKind == JsonValueKind.String
            ? degraded.GetString()
            : null;
        var hero = Ids(feed, "hero").ToList();
        seen["feed-hero"].AddRange(hero);
        seen["digest"].AddRange(Ids(await GetAsync(client, "/api/digest/current", label), "items"));
        await ReadSavedAsync(client, label, seen);

        if (rowsToo)
        {
            var rows = feed?.TryGetProperty("rows", out var r) == true ? r.EnumerateArray().ToList() : [];
            foreach (var row in rows)
            {
                var items = Ids(row, "items").ToList();
                seen["feed-row"].AddRange(items);
                var kind = row.GetProperty("kind").GetString();
                var anchor = row.TryGetProperty("anchorTitleId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                seen["more"].AddRange(Ids(await PostAsync(client, "/api/feed/more", new { row = kind, anchorTitleId = anchor, excludeTitleIds = items, limit = 30 }, label), "items"));
            }

            seen["more"].AddRange(Ids(await PostAsync(client, "/api/feed/more", new { row = "tonights-picks", excludeTitleIds = hero, limit = 30 }, label), "items"));
            foreach (var slug in new[] { "new-for-you", "deep-cuts" })
            {
                seen["more"].AddRange(Ids(await PostAsync(client, "/api/feed/more", new { row = slug, excludeTitleIds = Array.Empty<Guid>(), limit = 30 }, label), "items"));
            }

            // Genre rails: every genre offered, and drama and crime by direct request (a rail can be
            // opened without being offered, and every violator carries both).
            var offered = await GetAsync(client, "/api/feed/genres", label);
            var slugs = (offered?.TryGetProperty("genres", out var g) == true ? g.EnumerateArray().Select(x => x.GetProperty("slug").GetString()!).ToList() : [])
                .Concat(["drama", "crime"]).Distinct();
            foreach (var slug in slugs)
            {
                seen["genre-rail"].AddRange(Ids(await PostAsync(client, "/api/feed/more", new { row = "genre", genre = slug, excludeTitleIds = Array.Empty<Guid>(), limit = 30 }, label), "items"));
            }

            await AskAsync(client, label, seen);
        }

        if (scope == Scope.Focus && !label.StartsWith("pin-", StringComparison.Ordinal))
        {
            await AskAsync(client, label, seen);
        }

        if (broad)
        {
            // Similar: anchored on every clean title, so every violator is some anchor's neighbour.
            foreach (var anchor in _catalogue.Where(t => t.Name.StartsWith("Xm clean", StringComparison.Ordinal)))
            {
                seen["similar"].AddRange(Ids(await PostAsync(client, "/api/feed/more", new { row = "similar", anchorTitleId = anchor.Id, excludeTitleIds = Array.Empty<Guid>(), limit = 30 }, label), "items"));
            }

            // Typeahead: each violator by its own name (the shortlist is eight long).
            foreach (var v in Violators.Values)
            {
                seen["typeahead"].AddRange(Ids(await GetAsync(client, $"/api/search/typeahead?q={Uri.EscapeDataString(v.Name)}", label), "titles"));
            }

            seen["person"].AddRange(Ids(await GetAsync(client, $"/api/people/{_person.Id}", label), "filmography"));
        }

        await using var read = Context();
        Predictions[label] = (await read.TitleScores
                .Where(s => s.AccountId == accountId)
                .Join(read.ModelArtifacts.Where(m => m.Status == ArtifactStatus.Active), s => s.ModelArtifactId, m => m.Id, (s, m) => s)
                .Select(s => new { s.TitleId, s.PredictedRating, s.ScoredAt })
                .ToListAsync())
            .GroupBy(s => s.TitleId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(s => s.ScoredAt).First().PredictedRating);
    }

    private async Task ReadSavedAsync(HttpClient client, string label, Dictionary<string, List<Guid>> seen)
    {
        var saved = await GetAsync(client, "/api/saved", label);
        seen["saved"].AddRange(Ids(saved, "items"));
        Pins[label] = null;
        if (saved?.TryGetProperty("items", out var items) != true)
        {
            return;
        }

        foreach (var item in items.EnumerateArray().Where(i => i.TryGetProperty("watchTonight", out var w) && w.GetBoolean()))
        {
            var id = item.GetProperty("titleId").GetGuid();
            seen["watch-tonight"].Add(id);
            Pins[label] = (id, item.TryGetProperty("watchTonightReason", out var reason) ? reason.GetString() : null);
        }
    }

    /// <summary>
    /// One Ask turn on the matrix topic, with the deterministic stub model the harness forces. Every
    /// title id in a card-bearing frame counts as shown.
    /// </summary>
    private async Task AskAsync(HttpClient client, string label, Dictionary<string, List<Guid>> seen)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", new { query = $"{Topic} crime drama", clientTurnId = Guid.NewGuid() });
        if (!response.IsSuccessStatusCode)
        {
            Problems.Add($"{label}: POST /api/search/ask → {(int)response.StatusCode}");
            return;
        }

        var events = new List<string>();
        AskEvents[label] = events;
        var tray = new List<Guid>();
        AskTrays[label] = tray;
        foreach (var chunk in (await response.Content.ReadAsStringAsync()).Split("\n\n"))
        {
            var lines = chunk.Split('\n');
            var name = lines.FirstOrDefault(l => l.StartsWith("event: ", StringComparison.Ordinal))?[7..];
            var data = lines.FirstOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal))?[6..];
            if (name is null || data is null)
            {
                continue;
            }

            events.Add(name);
            var payload = JsonDocument.Parse(data).RootElement;
            if (name is "candidate" or "candidate-scored" or "picks")
            {
                seen["ask"].AddRange(TitleIdsIn(payload));
            }
            else if (name == "tray")
            {
                tray.AddRange(TitleIdsIn(payload));
            }
            else if (name == "done")
            {
                // v1 settles the turn's cards in its terminal frame: the answer ("results") and the
                // cards it could not check ("unchecked") are both rendered as cards; the below-floor and
                // near-miss lists are the labelled trays.
                foreach (var list in new[] { "results", "unchecked" })
                {
                    if (payload.TryGetProperty(list, out var cards))
                    {
                        seen["ask"].AddRange(TitleIdsIn(cards));
                    }
                }

                foreach (var list in new[] { "belowFloor", "nearMiss" })
                {
                    if (payload.TryGetProperty(list, out var cards))
                    {
                        tray.AddRange(TitleIdsIn(cards));
                    }
                }
            }
        }
    }

    private static IEnumerable<Guid> TitleIdsIn(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    if (p.Name == "titleId" && p.Value.ValueKind == JsonValueKind.String && Guid.TryParse(p.Value.GetString(), out var id))
                    {
                        yield return id;
                    }
                    else
                    {
                        foreach (var inner in TitleIdsIn(p.Value))
                        {
                            yield return inner;
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in TitleIdsIn(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    /// <summary>A client carrying the account's <c>reel_at</c> cookie, minted the way <see cref="ReelApiFactory.CreateSignedInClientAsync"/> mints it.</summary>
    private async Task<HttpClient> SignedInClientAsync(Guid accountId)
    {
        using var scope = _api!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
        var cookies = new CookieContainerHandler();
        cookies.Container.Add(_api.ClientOptions.BaseAddress, new System.Net.Cookie("reel_at", token, "/", _api.ClientOptions.BaseAddress.Host));
        return _api.CreateDefaultClient(cookies);
    }

    private async Task<JsonElement?> GetAsync(HttpClient client, string path, string label)
    {
        var response = await client.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            Problems.Add($"{label}: GET {path} → {(int)response.StatusCode}");
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement?> PostAsync(HttpClient client, string path, object body, string label)
    {
        var response = await client.PostAsJsonAsync(path, body);
        if (!response.IsSuccessStatusCode)
        {
            Problems.Add($"{label}: POST {path} {JsonSerializer.Serialize(body)} → {(int)response.StatusCode}");
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<Guid> Ids(JsonElement? element, string property)
    {
        if (element is not { } e || !e.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray()
            .Select(x => x.TryGetProperty("titleId", out var id) ? id : x.TryGetProperty("id", out var alt) ? alt : default)
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetGuid())
            .ToList();
    }
}
