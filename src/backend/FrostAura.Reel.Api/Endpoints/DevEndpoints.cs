using FrostAura.Reel.Api.Security;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// QA helpers — Development environment + QA_HELPERS_ENABLED=true only (fa.startup precedent).
/// Never mapped in production builds of the compose stack.
///
/// <para><b>Three locks, not one.</b> Mapping them at all needs Development plus the flag. Then
/// every route is loopback-only (<see cref="LoopbackGuard"/>): dev/link mints a Founder session, so
/// a founder check alone protected nothing on a stack reachable from the LAN (J-1). And every
/// <c>/api/dev/*</c> route additionally needs a signed-in Founder, in that order — an anonymous
/// caller gets 401, a non-founder 403, a founder calling from another machine 403
/// <c>loopback_only</c>.</para>
/// </summary>
public static class DevEndpoints
{
    /// <summary>The prefix every founder diagnostic lives under.</summary>
    public const string DevPrefix = "/api/dev";

    /// <summary>The QA sign-in route. Outside <see cref="DevPrefix"/> because it is pre-auth.</summary>
    public const string DevLinkRoute = "/api/auth/dev/link";

    /// <summary>
    /// Prefix written onto the stored <c>RefreshSessions.UserAgent</c> of every session dev/link
    /// issues, so QA sessions can be excluded from the founder's data by identity rather than by
    /// clock (FD-18). Rotation carries it to every successor (<see cref="SessionService.QaSessionMarker"/>).
    /// </summary>
    public const string DevLinkSessionMarker = SessionService.QaSessionMarker;

    public static void MapDevEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue("QA_HELPERS_ENABLED", false))
        {
            return;
        }

        var dev = app.MapGroup(DevPrefix)
            .RequireAccount()
            .RequireFounder()
            .RequireLoopback();

        // Links the configured dev profile using pre-provisioned tokens (TRAKT_ACCESS_TOKEN /
        // TRAKT_REFRESH_TOKEN from .env) — full pipeline + session without the browser consent
        // hop. The production path of record stays the OAuth callback.
        //
        // `{"noCascade": true}` issues a session for the ALREADY-linked dev account and does nothing
        // else: no Trakt call, no token write, no tier write, no job. A link that falls through to
        // LinkAccountAsync enqueues a FullIngest/DeltaSync, and that chain ends in feed builds and
        // re-rank calls that bill the account's model budget (D-17); QA that only needs a cookie
        // should never start it.
        app.MapPost(DevLinkRoute, async (
            TraktOAuthService oauth,
            SessionService sessions,
            ITraktClient trakt,
            IReelDbContext db,
            HttpContext http,
            IConfiguration configuration,
            DevLinkRequest? request,
            CancellationToken ct) =>
        {
            var noCascade = request?.NoCascade == true;
            // Preferred QA path: the dev profile is normally already linked — its Account and
            // TraktConnection live in the DB and self-heal their Trakt tokens on 401 during sync. A
            // session only needs the Account, so we don't re-link through Trakt (which would depend on
            // the pre-provisioned .env tokens, and single-use refresh tokens rotate and go stale).
            // Issue a session for the existing account directly — robust regardless of .env freshness.
            var devProfile = configuration["TRAKT_DEV_PROFILE"];
            Account? account = null;
            if (!string.IsNullOrWhiteSpace(devProfile))
            {
                account = await db.Accounts
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.TraktUserSlug == devProfile || a.TraktUsername == devProfile, ct);
            }

            if (account is null && noCascade)
            {
                return Results.Json(
                    new { error = "dev_account_not_linked", detail = "noCascade issues sessions for an already-linked dev account only; link once without it." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Fresh QA database — nothing linked yet. Fall back to linking via the pre-provisioned
            // tokens, and surface a clean 400 (not a 500) if those tokens are stale/revoked.
            if (account is null)
            {
                var accessToken = configuration["TRAKT_ACCESS_TOKEN"];
                var refreshToken = configuration["TRAKT_REFRESH_TOKEN"];
                if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
                {
                    return Results.BadRequest(new { error = "No linked dev account, and TRAKT_ACCESS_TOKEN / TRAKT_REFRESH_TOKEN not configured." });
                }

                // Unknown provenance: try the access token; if stale, rotate via refresh first.
                var tokens = new TraktTokenResponse(
                    accessToken, refreshToken, "public write",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), (long)TimeSpan.FromDays(60).TotalSeconds);
                try
                {
                    await trakt.GetUserSettingsAsync(accessToken, ct);
                }
                catch (Exception)
                {
                    try
                    {
                        tokens = await trakt.RefreshTokenAsync(refreshToken, ct);
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { error = "Pre-provisioned Trakt tokens are stale/revoked — sign in through the OAuth flow once to re-link the dev profile.", detail = ex.Message });
                    }
                }

                var result = await oauth.LinkAccountAsync(tokens, countryHint: null, ct);
                account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == result.Account.Id, ct);
            }

            // The dev profile is the founder account — everything unlocked during the beta. A
            // no-cascade call writes nothing but the session row.
            if (!noCascade && account.Tier != AccountTier.Founder)
            {
                account.Tier = AccountTier.Founder;
                await db.SaveChangesAsync(ct);
            }

            // The rest of this request acts as the account it just signed in (J-11): the session
            // payload reads scoped tables, and a stale cookie for another account must not narrow them.
            http.RequestServices.GetRequiredService<IAccountContext>().SetAccount(account.Id);
            var session = await sessions.IssueQaAsync(account, http.Request.Headers.UserAgent.FirstOrDefault(), ct);
            SessionCookies.Write(http, session);
            return Results.Ok(await account.ToSessionPayloadAsync(http, ct));
        }).RequireLoopback();

        // ── Residual-head dry run ───────────────────────────────────────────────────────
        // The offline replay the plan requires BEFORE RESIDUAL_OBSERVE_TRAKT_RATINGS is turned on:
        // it runs the account's whole rating history through the v2 head in order, writing nothing,
        // and reports where the head lands. Read `residual.finalBias` and `residual.weightsAtClamp`
        // first — a bias near zero with nothing on the clamp means the head is learning shape rather
        // than level, which is the only condition under which bulk-teaching it is safe. The same
        // response carries the fitted calibrator, so the two mechanisms can be compared on one page:
        //
        //   curl -s localhost:5080/api/dev/residual-replay | jq
        //
        // Optional ?slug= picks an account when more than one is linked; otherwise TRAKT_DEV_PROFILE
        // and then "the only account" are tried, in that order.
        dev.MapGet("/residual-replay", async (
            string? slug,
            IReelDbContext db,
            ResidualReplay replay,
            RatingCalibrationService calibration,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var wanted = slug ?? configuration["TRAKT_DEV_PROFILE"];
            var accounts = db.Accounts.IgnoreQueryFilters();
            var account = wanted is null
                ? await accounts.OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(ct)
                : await accounts.FirstOrDefaultAsync(a => a.TraktUserSlug == wanted || a.TraktUsername == wanted, ct);
            if (account is null)
            {
                return Results.NotFound(new { error = "No account to replay." });
            }

            var report = await replay.RunAsync(account.Id, sampleWeight: 1.0, ct);
            var pairs = await calibration.LoadPairsAsync(account.Id, ct);
            var calibrator = await calibration.FitAsync(account.Id, ct);

            return Results.Ok(new
            {
                account = account.TraktUserSlug,
                residual = report,
                calibration = new
                {
                    kind = calibrator.Kind.ToString(),
                    // What the selection rule was allowed to choose from, and what it turned on.
                    mode = calibration.Mode.ToString(),
                    correlation = calibrator.Correlation,
                    activeArtifactId = await calibration.ActiveArtifactIdAsync(account.Id, ct),
                    // `pairs` is every closed-loop pair on record; `fittedPairs` is what survived
                    // artifact selection, and is the set every metric below is measured over.
                    pairs = pairs.Count,
                    fittedPairs = calibrator.PairCount,
                    // The plan's Phase 0 gate: MAE over the closed-loop pairs must fall toward ≤ 1.2.
                    maeBefore = calibrator.MaeBefore,
                    maeAfter = calibrator.MaeAfter,
                    // The band the UI renders beside a prediction — "7.4, ±1.9".
                    expectedAbsoluteError = calibrator.ExpectedAbsoluteError,
                    biasBefore = pairs.Count == 0 ? 0d : pairs.Average(p => p.Predicted - p.Actual),
                    biasAfter = pairs.Count == 0 ? 0d : pairs.Average(p => calibrator.Calibrate(p.Predicted) - p.Actual),
                    // A quick look at the map itself, so the shape is legible without a chart. The
                    // spread across it is what the minimum-slope guard exists to keep off the floor.
                    curve = Enumerable.Range(0, 11)
                        .Select(i => 5d + (i * 0.5d))
                        .Select(x => new { predicted = x, calibrated = Math.Round(calibrator.Calibrate(x), 3) }),
                    curveRange = Math.Round(
                        calibrator.Calibrate(RatingCalibrator.RangeProbeHigh) - calibrator.Calibrate(RatingCalibrator.RangeProbeLow), 3),
                },
            });
        });

        // ── The lens audit ──────────────────────────────────────────────────────────────────
        // Founder-only, and the reason it is founder-only is that it SPENDS: it re-reads the last
        // N generated notes and asks the same model family to score each against a frozen rubric.
        //
        // How to read it: compare MEANS between prompt versions, never absolute values — the judge
        // shares the writer's blind spots, so a level is close to meaningless and a difference is
        // not. `rejectedByGuards` is worth more than any mean, because it counts notes that were
        // actually refused rather than opinions about them. The failure mode to watch for is
        // specificity rising while grounding falls: that is what "more confident and less true"
        // looks like, and it is exactly what a prompt tweak aimed at livelier copy tends to buy.
        //
        // ?score=false returns the same rows without paying for a judgement, which is the right
        // call when you only want to read fifty generations yourself.
        dev.MapGet("/lens-audit", async (
            Application.Lens.LensAudit audit,
            Application.Tenancy.IAccountContext accountContext,
            FeatureVectorBuilder featureBuilder,
            int? sample,
            bool? score,
            CancellationToken ct) =>
        {
            if (accountContext.AccountId is not { } accountId)
            {
                return Results.Unauthorized();
            }

            var taste = await featureBuilder.BuildTasteStateAsync(accountId, DateTime.UtcNow, ct);
            var report = await audit.RunAsync(
                accountId,
                taste,
                sample ?? Application.Lens.LensAudit.DefaultSampleSize,
                score ?? true,
                ct);
            return Results.Ok(report);
        });

        // ── The offline retrieval-recall test ───────────────────────────────────────────────
        // Read-only, persists nothing, and answers the one question every modelling iteration so
        // far has been unable to see past: of the titles this account went on to LOVE, how many
        // would the first-pass pool have even contained on the day before they were rated?
        //
        //   curl -s 'localhost:5080/api/dev/retrieval-recall?window=180' | jq
        //
        // How to read the answer — and the order matters, because two different failures wear the
        // same "we missed it" shirt and want opposite fixes:
        //
        //   1. `loved.rates.inCatalogue` is the SUPPLY ceiling. Titles below it were not in the
        //      catalogue that day, so no retrieval budget on earth would have returned them; that
        //      is a discovery problem. `notReachable[].reason` names it per title.
        //   2. `loved.recallGivenReachable` is the RETRIEVAL number — of the titles that existed
        //      and were eligible, what share the arms returned. This is the only one a bigger pool
        //      or a new arm can move, and it is the one the plan's decision rule reads: a
        //      public-corpus collaborative arm is worth its licensing cost only if THIS is low.
        //   3. `perCluster` is the B1 verdict, and it is the block to read before the headline moved.
        //      Multi-centroid retrieval claims to reach the pockets one mean vector points away
        //      from, so the recall rise has to CONCENTRATE in the low-weight clusters. A rise spread
        //      evenly across every ordinal means the extra slots bought it, not the partition — in
        //      which case raise `FirstPassAnnPool` and delete the clusterer.
        //   4. `disliked` is the same computation over titles rated <= 4 — the false-positive side.
        //      A high union there is a RANKING problem, not a supply one.
        //
        // `popCount` / `annCount` default to the feed's own first-pass budgets; raise them and
        // re-run to answer "would a bigger pool have caught these?" without shipping anything.
        // `budget.exhausted` true means the rates describe a SAMPLE of the window, not the window.
        //
        // Optional ?slug= picks an account when more than one is linked; otherwise TRAKT_DEV_PROFILE
        // and then "the only account" are tried, in that order — same resolution as the replay above.
        dev.MapGet("/retrieval-recall", async (
            string? slug,
            int? window,
            int? popCount,
            int? annCount,
            int? seconds,
            IReelDbContext db,
            Application.Ranking.RetrievalRecallProbe probe,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var wanted = slug ?? configuration["TRAKT_DEV_PROFILE"];
            var accounts = db.Accounts.IgnoreQueryFilters();
            var account = wanted is null
                ? await accounts.OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(ct)
                : await accounts.FirstOrDefaultAsync(a => a.TraktUserSlug == wanted || a.TraktUsername == wanted, ct);
            if (account is null)
            {
                return Results.NotFound(new { error = "No account to measure." });
            }

            // Clamped rather than validated-and-rejected: this is a diagnostic run by hand, and a
            // fat-fingered window=100000 should give a long window, not a 400. The 60s cap is the
            // one that is NOT negotiable upward — the probe rebuilds a full taste state per distinct
            // rating day, and an unbounded run against a live database is a self-inflicted outage.
            var report = await probe.RunAsync(
                account.Id,
                Math.Clamp(window ?? 180, 1, 3650),
                Math.Clamp(popCount ?? Application.Ranking.RetrievalRecallProbe.DefaultPopCount, 0, 5000),
                Math.Clamp(annCount ?? Application.Ranking.RetrievalRecallProbe.DefaultAnnCount, 0, 5000),
                TimeSpan.FromSeconds(Math.Clamp(seconds ?? 60, 1, 60)),
                DateTime.UtcNow,
                ct);

            return Results.Ok(new { account = account.TraktUserSlug, recall = report });
        });

        // ── The catalogue bulk seed ─────────────────────────────────────────────────────────
        // Founder-only, and it is founder-only because it commits the system to tens of thousands
        // of new rows and the weeks of hydration and LLM extraction that follow them. POST enqueues
        // or RESUMES — those are the same call, because a run that stopped at its page budget left a
        // cursor and the next job continues from it. Calling it twice is safe: one Pending/Running
        // seed job exists at a time, and a second POST returns the one already in flight.
        //
        //   curl -s -X POST localhost:5080/api/dev/seed-catalog | jq
        //   curl -s localhost:5080/api/dev/seed-catalog | jq
        //
        // GET is free and read-only. Read it in this order: `progress` says how far the WALK has
        // got, `backlog` says how much of what it fetched is still unusable, and `estimate` turns
        // the second into days at the current ceilings. `ceilings.extractTokensPerTitle` is the
        // figure the estimate divides by — measured from the last week of the token ledger where
        // there is one, and a stated assumption where there is not, so an estimate that looks wrong
        // can be checked rather than argued with.
        dev.MapPost("/seed-catalog", async (
            IReelDbContext db,
            CancellationToken ct) =>
        {
            // Founder and loopback are enforced on the group (RequireFounder, RequireLoopback).
            var (queued, job) = await Application.Jobs.CatalogSeedScheduler.EnqueueAsync(db, DateTime.UtcNow, ct);
            var cursor = Application.Jobs.CatalogSeedCursor.Read(job.CursorJson);
            return Results.Accepted(value: new
            {
                queued,
                jobId = job.Id,
                status = job.Status.ToString(),
                // False on the second POST, and that is the answer rather than an error: the seed is
                // already running or already queued, so the caller's intent is satisfied.
                resumedFrom = cursor is null
                    ? null
                    : new { cursor.Media, cursor.Year, cursor.Page, cursor.TitlesAdded },
            });
        });

        dev.MapGet("/seed-catalog", async (
            Application.Jobs.CatalogSeedProgressQuery progress,
            ITitleAttributeExtractor extractor,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            // Founder and loopback are enforced on the group (RequireFounder, RequireLoopback).
            var report = await progress.RunAsync(extractor.Version, DateTime.UtcNow, ct);
            return Results.Ok(new
            {
                job = new
                {
                    id = report.JobId,
                    status = report.Status,
                    enqueuedAt = report.EnqueuedAt,
                    startedAt = report.StartedAt,
                    completedAt = report.CompletedAt,
                    attemptCount = report.AttemptCount,
                    error = report.Error,
                    message = report.ProgressMessage,
                },
                plan = new
                {
                    firstYear = Application.Jobs.CatalogSeedPlan.FirstYear,
                    yearsPerMedium = report.YearsPerMedium,
                    partitions = report.PartitionsTotal,
                    // The EFFECTIVE floors, not the compiled-in defaults: these are the two settings
                    // that decide how big the corpus is, and reporting the default while the
                    // environment overrides it is how a seed gets re-run to fix a number it already
                    // has. Measured 2026-09-10, the defaults yield ~9,300 titles and 50/50 yields
                    // ~34,800 — see CatalogSeedPlan for the table.
                    movieMinVoteCount = Application.Jobs.CatalogSeedPlan.MinVoteCount(true, configuration),
                    showMinVoteCount = Application.Jobs.CatalogSeedPlan.MinVoteCount(false, configuration),
                    maxDiscoverPage = Application.Jobs.CatalogSeedPlan.MaxDiscoverPage,
                },
                cursor = report.Media is null
                    ? null
                    : new { media = report.Media, year = report.Year, page = report.Page },
                progress = new
                {
                    done = report.Done,
                    movieYearsDone = report.MovieYearsDone,
                    showYearsDone = report.ShowYearsDone,
                    yearsTotalPerMedium = report.YearsPerMedium,
                    partitionsDone = report.PartitionsDone,
                    partitionsTotal = report.PartitionsTotal,
                    titlesAdded = report.TitlesAdded,
                    pagesFetched = report.PagesFetched,
                    pagesUsedToday = report.PagesUsedToday,
                },
                backlog = new
                {
                    hydration = report.HydrationBacklog,
                    missingEmbedding = report.Enrichment.MissingEmbedding,
                    missingAttributes = report.Enrichment.MissingAttributes,
                    staleExtractorVersion = report.Enrichment.StaleExtractorVersion,
                    lastEnrichAt = report.Enrichment.LastRunAt,
                },
                ceilings = new
                {
                    hydrateTitlesPerDay = report.HydrationAllowance.Limit,
                    hydrateTitlesUsedToday = report.HydrationAllowance.Used,
                    extractDailyTokens = report.ExtractAllowance.Limit,
                    extractTokensUsedToday = report.ExtractAllowance.Used,
                    embedDailyTokens = report.EmbedAllowance.Limit,
                    embedTokensUsedToday = report.EmbedAllowance.Used,
                    extractTokensPerTitle = report.ExtractTokensPerTitle,
                },
                estimate = new
                {
                    daysToHydrated = report.EstimatedDaysToHydrated,
                    daysToAttributed = report.EstimatedDaysToAttributed,
                    // The two stages run concurrently, so this is the max of the pair and never
                    // their sum. Null means there is nothing outstanding — or, where a ceiling is
                    // set to zero, that the answer is "never at this setting" rather than a number.
                    daysToFullyEnriched = report.EstimatedDaysToEnriched,
                },
            });
        });

        // ── The cross-reference backtest ────────────────────────────────────────────────────
        // The only thing that may promote a cross-reference verdict from "demotes a title" to
        // "hides a title", and the reason the instrument ships before the job does.
        //
        //   curl -s -X POST localhost:5080/api/dev/cross-reference-eval | jq
        //   curl -s -X POST 'localhost:5080/api/dev/cross-reference-eval?promptVersion=2' | jq
        //   curl -s localhost:5080/api/dev/cross-reference-eval | jq
        //   curl -s 'localhost:5080/api/dev/cross-reference-eval?runId=<id>&skip=0&take=200' | jq
        //
        // How to read the answer, and the ORDER matters:
        //
        //   1. `gatingDecision` on the `mean` row is the verdict. "gating" means an Out may bar a
        //      hero card; "demote-only" means an Out still halves the rank key so the closed loop
        //      can observe it, but hides nothing — and the band on the feed has to say so in words,
        //      with the real false-rule-out rate in it.
        //   2. `wilsonHigh`, not `falseRuleOutRate`, is what the gate reads. At n around 55 an
        //      observed 9% has a 95% upper bound near 20%; reading the point estimate as "under
        //      15%" is reading sampling noise as a result.
        //   3. `unsureRate` is the useless-but-honest check. A judge that answers "unsure" to 90% of
        //      the pool has a perfect false-rule-out rate and no value — the same failure mode the
        //      calibrator collapse guard exists for.
        //   4. `noEmbeddingCount` is coverage. Those titles are in NO denominator on this row, so
        //      any public claim about how much of the pool was cross-referenced has to subtract it.
        //
        //   5. The DISCRIMINATION block — `keepLovedRate` against `outLovedRate`, and `auc` — is
        //      what the first live run could not report and needed most. A false-rule-out rate of
        //      12.0% reads as a cautious instrument right up until you put it beside a true-rule-out
        //      rate of 10.1%, at which point it reads as a coin. `auc` says the same thing on a
        //      scale that does not move with the Out threshold: 0.5 is chance.
        //
        // POST runs it (~35 provider calls under `cross-reference-eval`, deliberately a different
        // purpose from `cross-reference` so measuring the judge cannot eat the budget the judge runs
        // on). `?promptVersion=2` runs the EXPERIMENT rather than the generation the job serves —
        // the default stays 1, and a generation becomes current by being measured against the one it
        // replaces, never by a caller selecting it here. GET is free: with no query it returns the
        // newest run PER prompt version, so v1 and v2 sit side by side; with `?runId=` it returns
        // that run's per-title items so a human can read the actual misses.
        dev.MapPost("/cross-reference-eval", async (
            HttpContext http,
            Application.CrossReference.CrossReferenceBacktest backtest,
            int? promptVersion,
            CancellationToken ct) =>
        {
            // Founder and loopback are enforced on the group (RequireFounder, RequireLoopback).
            var account = http.GatedAccount();

            // Exactly the two generations that exist. An unbounded int here would write rows under a
            // version nothing can render and nothing will ever judge against again.
            var version = promptVersion ?? Domain.CrossReference.CrossReferencePromptVersions.First;
            if (!Domain.CrossReference.CrossReferencePromptVersions.IsKnown(version))
            {
                return Results.BadRequest(new { error = "promptVersion must be 1 or 2." });
            }

            var report = await backtest.RunAsync(account.Id, version, ct);
            return report is null
                ? Results.BadRequest(new { error = "Not enough rating history to split — the backtest needs a holdout." })
                : Results.Ok(new
                {
                    runId = report.RunId,
                    mean = Describe(report.Mean),
                    origins = report.Origins.Select(Describe).ToList(),
                });
        });

        dev.MapGet("/cross-reference-eval", async (
            IReelDbContext db,
            HttpContext http,
            Guid? runId,
            int? skip,
            int? take,
            CancellationToken ct) =>
        {
            // Founder and loopback are enforced on the group (RequireFounder, RequireLoopback).
            var account = http.GatedAccount();

            return runId is { } requested
                ? await ItemsAsync(db, account.Id, requested, skip ?? 0, take ?? MaxEvaluationItemsPerPage, ct)
                : await LatestRunsAsync(db, account.Id, ct);
        });
    }

    /// <summary>Per-title items returned in one page. A page is for reading, not for exporting.</summary>
    private const int MaxEvaluationItemsPerPage = 200;

    /// <summary>
    /// The newest run PER prompt version — so a v1 baseline and a v2 experiment on the same account
    /// sit beside each other and the comparison is a read rather than a spreadsheet.
    ///
    /// <para>The newest RUN, never the newest rows: a run is up to five origins plus a mean, and a
    /// naive "last six rows" splices two runs together the moment one origin produces no evaluable
    /// holdout.</para>
    /// </summary>
    private static async Task<IResult> LatestRunsAsync(
        IReelDbContext db, Guid accountId, CancellationToken ct)
    {
        var latestPerVersion = await db.CrossReferenceEvaluations
            .Where(e => e.AccountId == accountId)
            .GroupBy(e => e.PromptVersion)
            .Select(g => new { PromptVersion = g.Key, EvaluatedAt = g.Max(e => e.EvaluatedAt) })
            .ToListAsync(ct);
        if (latestPerVersion.Count == 0)
        {
            return Results.Ok(new { runs = Array.Empty<object>() });
        }

        var stamps = latestPerVersion.Select(v => v.EvaluatedAt).ToList();
        var rows = await db.CrossReferenceEvaluations
            .Where(e => e.AccountId == accountId && stamps.Contains(e.EvaluatedAt))
            .ToListAsync(ct);

        var runs = latestPerVersion
            .OrderBy(v => v.PromptVersion)
            .Select(v =>
            {
                var runRows = rows
                    .Where(r => r.PromptVersion == v.PromptVersion && r.EvaluatedAt == v.EvaluatedAt)
                    .ToList();
                return new
                {
                    promptVersion = v.PromptVersion,
                    runId = runRows.Select(r => (Guid?)r.RunId).FirstOrDefault(),
                    mean = runRows.Where(r => r.SplitFraction is null).Select(Describe).FirstOrDefault(),
                    origins = runRows
                        .Where(r => r.SplitFraction is not null)
                        .OrderBy(r => r.SplitFraction)
                        .Select(Describe)
                        .ToList(),
                };
            })
            .ToList();

        return Results.Ok(new { runs });
    }

    /// <summary>
    /// One run's per-title items — the verdict, the confidence, the evidence it was shown and what
    /// the account went on to rate it.
    ///
    /// <para>This is the surface the whole items table exists for: a rule-out rate says how often
    /// the judge was wrong and cannot say what it was wrong ABOUT. Ordered by origin then title so
    /// paging is stable, and capped at 200 a page because it is for reading.</para>
    /// </summary>
    private static async Task<IResult> ItemsAsync(
        IReelDbContext db, Guid accountId, Guid runId, int skip, int take, CancellationToken ct)
    {
        var evaluationIds = await db.CrossReferenceEvaluations
            .Where(e => e.AccountId == accountId && e.RunId == runId && e.SplitFraction != null)
            .Select(e => e.Id)
            .ToListAsync(ct);
        if (evaluationIds.Count == 0)
        {
            return Results.NotFound(new { error = "No cross-reference evaluation run with that id on this account." });
        }

        var query = db.CrossReferenceEvaluationItems
            .Where(i => i.AccountId == accountId && evaluationIds.Contains(i.EvaluationId));
        var total = await query.CountAsync(ct);

        var page = await query
            .OrderBy(i => i.SplitFraction).ThenBy(i => i.TitleId)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxEvaluationItemsPerPage))
            .Join(
                db.Titles,
                i => i.TitleId,
                t => t.Id,
                (i, t) => new
                {
                    i.TitleId,
                    t.Name,
                    t.Year,
                    i.SplitFraction,
                    i.Verdict,
                    i.ReasonCode,
                    i.Confidence,
                    i.LaterRating,
                    i.NeighbourIdsJson,
                    i.BatchIndex,
                })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            runId,
            total,
            skip = Math.Max(0, skip),
            take = Math.Clamp(take, 1, MaxEvaluationItemsPerPage),
            items = page.Select(i => new
            {
                titleId = i.TitleId,
                name = i.Name,
                year = i.Year,
                splitFraction = i.SplitFraction,
                verdict = i.Verdict.ToString(),
                reason = i.ReasonCode,
                confidence = i.Confidence,
                laterRating = i.LaterRating,
                neighbourIds = i.NeighbourIdsJson,
                batchIndex = i.BatchIndex,
            }).ToList(),
        });
    }

    /// <summary>
    /// One evaluation row as JSON. Every number is rendered as stored — no rounding up, no
    /// prettifying — because the whole value of this surface is that the founder can compare what
    /// it says with what the band on the feed will say.
    /// </summary>
    private static object Describe(Domain.CrossReference.CrossReferenceEvaluation e) => new
    {
        splitFraction = e.SplitFraction,
        splitAt = e.SplitFraction is null ? (DateTime?)null : e.SplitAt,
        judged = e.HoldoutJudged,
        loved = e.LovedCount,
        disliked = e.DislikedCount,
        falseRuleOuts = e.FalseRuleOuts,
        trueRuleOuts = e.TrueRuleOuts,
        falseRuleOutRate = e.FalseRuleOutRate,
        trueRuleOutRate = e.TrueRuleOutRate,
        wilsonLow = e.WilsonLow,
        wilsonHigh = e.WilsonHigh,
        unsureRate = e.UnsureRate,
        noEmbedding = e.NoEmbeddingCount,
        // Does the answer carry information? `keepLovedRate` beside `outLovedRate` is the pair to
        // read first, and `auc` is the one number that does not move with the Out threshold.
        discrimination = new
        {
            keepCount = e.KeepCount,
            outCount = e.OutCount,
            unsureCount = e.UnsureCount,
            meanLaterRatingKeep = e.MeanLaterRatingKeep,
            meanLaterRatingOut = e.MeanLaterRatingOut,
            meanLaterRatingUnsure = e.MeanLaterRatingUnsure,
            keepLovedRate = e.KeepLovedRate,
            keepDislikedRate = e.KeepDislikedRate,
            outLovedRate = e.OutLovedRate,
            outDislikedRate = e.OutDislikedRate,
            spearmanConfidenceVsRating = e.SpearmanConfidenceVsRating,
            auc = e.AucKeepVsOut,
        },
        gatingDecision = e.GatingDecision,
        caveat = e.Caveat,
        model = e.ModelId,
        promptVersion = e.PromptVersion,
        promptTokens = e.PromptTokens,
        completionTokens = e.CompletionTokens,
        evaluatedAt = e.EvaluatedAt,
    };
}

/// <summary>Body of <c>POST /api/auth/dev/link</c>. Every field is optional; <c>{}</c> keeps the old behaviour.</summary>
/// <param name="NoCascade">Issue a session for the already-linked dev account and do nothing else.</param>
public sealed record DevLinkRequest(bool? NoCascade);
