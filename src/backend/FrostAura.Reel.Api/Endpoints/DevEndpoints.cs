using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// QA helpers — Development environment + QA_HELPERS_ENABLED=true only (fa.startup precedent).
/// Never mapped in production builds of the compose stack.
/// </summary>
public static class DevEndpoints
{
    public static void MapDevEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue("QA_HELPERS_ENABLED", false))
        {
            return;
        }

        // Links the configured dev profile using pre-provisioned tokens (TRAKT_ACCESS_TOKEN /
        // TRAKT_REFRESH_TOKEN from .env) — full pipeline + session without the browser consent
        // hop. The production path of record stays the OAuth callback.
        app.MapPost("/api/auth/dev/link", async (
            TraktOAuthService oauth,
            SessionService sessions,
            ITraktClient trakt,
            IReelDbContext db,
            HttpContext http,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
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

            // The dev profile is the founder account — everything unlocked during the beta.
            account.Tier = AccountTier.Founder;
            await db.SaveChangesAsync(ct);

            var session = await sessions.IssueAsync(account, http.Request.Headers.UserAgent.FirstOrDefault(), ct);
            http.Response.Cookies.Append("reel_at", session.AccessToken, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = session.AccessExpiresAt,
            });
            http.Response.Cookies.Append("reel_rt", session.RefreshToken, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/api/auth",
                Expires = session.RefreshExpiresAt,
            });

            return Results.Ok(account.ToSessionPayload());
        });

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
        app.MapGet("/api/dev/residual-replay", async (
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
        app.MapGet("/api/dev/lens-audit", async (
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
        app.MapGet("/api/dev/retrieval-recall", async (
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
        app.MapPost("/api/dev/seed-catalog", async (
            IReelDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null || account.Tier != AccountTier.Founder)
            {
                return Results.Forbid();
            }

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

        app.MapGet("/api/dev/seed-catalog", async (
            IReelDbContext db,
            HttpContext http,
            Application.Jobs.CatalogSeedProgressQuery progress,
            ITitleAttributeExtractor extractor,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null || account.Tier != AccountTier.Founder)
            {
                return Results.Forbid();
            }

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
    }
}
