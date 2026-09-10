using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").RequireAccount();

        // Typeahead with the personal lens: watched titles appear (badged), content filters and the
        // maturity ceiling are airtight, and the shortlist is scored LIVE against the Active model so
        // the best-for-you matches float up rather than the merely-popular ones.
        group.MapGet("/typeahead", async (
            string q, IReelDbContext db, IAccountContext accountContext,
            EligibilityQueryBuilder eligibility, OnDemandScorer scorer, ITmdbClient tmdb,
            CancellationToken ct, string? global = null) =>
        {
            var accountId = accountContext.AccountId!.Value;
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            {
                return Results.Ok(new
                {
                    titles = Array.Empty<object>(),
                    people = Array.Empty<object>(),
                    global = Array.Empty<GlobalTypeaheadRow>(),
                });
            }

            var term = q.Trim();
            var maturityCeiling = await eligibility.MaturityCeilingAsync(accountId, ct);

            // ContentFilteredTitles (not EligibleTitles): watched titles legitimately appear here
            // badged — but every exclusion still applies, airtight. Over-fetch, then apply the
            // in-memory maturity gate (certifications don't order in SQL) and keep 8, so a
            // restricted profile still lands a full shortlist instead of a starved one.
            //
            // Match on substring OR trigram similarity. Substring alone is exact-spelling-only:
            // "inceptoin" and "spiderman" both returned literally nothing, because ILIKE '%term%'
            // cannot see past a transposed letter or a missing hyphen. pg_trgm has been installed
            // and declared in every migration since the project started — and is half the reason
            // this project deviates from the house postgres image to pgvector/pgvector:pg16 — but
            // nothing ever queried it. Exact substring still wins the ordering, so a correctly
            // spelled query is unaffected; fuzzy matches only fill in behind it.
            var matched = await eligibility.ContentFilteredTitles(accountId)
                .Where(t => EF.Functions.ILike(t.Name, $"%{term}%") || EF.Functions.TrigramsAreSimilar(t.Name, term))
                .Where(t => !db.UserTitleReactions.Any(r =>
                    r.AccountId == accountId && r.TitleId == t.Id && r.Kind == ReactionKind.NotInterested && r.RevokedAt == null))
                .OrderByDescending(t => EF.Functions.ILike(t.Name, $"%{term}%"))
                .ThenByDescending(t => EF.Functions.TrigramsSimilarity(t.Name, term))
                .ThenByDescending(t => t.TmdbPopularity)
                .Take(24)
                .ToListAsync(ct);

            var candidates = EligibilityQueryBuilder.WithinMaturity(matched, maturityCeiling).Take(8).ToList();
            var candidateIds = candidates.Select(t => t.Id).ToList();

            // Relevance tier per candidate, recomputed here because the SQL ordering is lost the
            // moment these are re-sorted by predicted rating below. 2 = the term appears verbatim,
            // 1 = a close fuzzy match, 0 = a distant one.
            //
            // This exists because fuzzy matching widened the candidate set: before trigram search,
            // every row was an exact substring match and sorting the survivors purely by predicted
            // rating was harmless. Now a loosely-similar title can outrank the one the user
            // actually typed — "spiderman" surfaced Superman above Spider-Man, because Superman
            // simply scored better for this account. Relevance has to dominate for a LOOKUP;
            // the personal lens is the tiebreak within a tier, not the primary key.
            var tiers = candidates.ToDictionary(
                t => t.Id,
                t => t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ? 2
                    : TitleSimilarity(t.Name, term) >= 0.5 ? 1
                    : 0);

            // Live per-user scoring of the ≤8 survivors against the Active artifact (cheap: one model
            // load + one taste build). No model yet ⇒ empty map ⇒ order falls back to popularity.
            var predicted = await scorer.ScoreAsync(accountId, candidateIds, DateTime.UtcNow, ct);
            var watchedFull = (await db.WatchedTitles
                .Where(w => w.AccountId == accountId && candidateIds.Contains(w.TitleId) && w.IsFullyWatched)
                .Select(w => w.TitleId)
                .ToListAsync(ct)).ToHashSet();

            var titles = candidates
                .OrderByDescending(t => tiers[t.Id])
                .ThenByDescending(t => predicted.TryGetValue(t.Id, out var p) ? p : decimal.MinValue)
                .ThenByDescending(t => t.TmdbPopularity ?? 0)
                .Select(t => new
                {
                    titleId = t.Id,
                    mediaType = t.MediaType.ToString(),
                    tmdbId = t.TmdbId,
                    name = t.Name,
                    year = t.Year,
                    posterPath = t.PosterPath,
                    isFullyWatched = watchedFull.Contains(t.Id),
                    predictedRating = predicted.TryGetValue(t.Id, out var p) ? (decimal?)p : null,
                    crowdRating = CrowdRating.For(t),
                    crowdVotes = CrowdRating.VotesFor(t),
                })
                .ToList();

            var people = await db.Persons
                .Where(p => EF.Functions.ILike(p.Name, $"%{term}%") || EF.Functions.TrigramsAreSimilar(p.Name, term))
                .OrderByDescending(p => EF.Functions.ILike(p.Name, $"%{term}%"))
                .ThenByDescending(p => EF.Functions.TrigramsSimilarity(p.Name, term))
                .Take(4)
                .Select(p => new { personId = p.Id, name = p.Name, knownFor = p.KnownForDepartment, profilePath = p.ProfilePath })
                .ToListAsync(ct);

            // The global leg — LOOKUP beyond the ingested catalogue, opt-in via ?global=1. "1"
            // rather than bool binding because the frontend sends &global=1 and minimal-API bool
            // parsing rejects it. Runs AFTER the local shortlist so its (isMovie, tmdbId) pairs can
            // be deduped away, and deliberately applies NO eligibility/footprint filtering: this is
            // the same precedent that lets typeahead show watched titles — a lookup answers "does
            // this exist", not "should you watch it".
            IReadOnlyList<GlobalTypeaheadRow> globalRows = global is "1" or "true"
                ? await GlobalLookupAsync(
                    tmdb,
                    term,
                    candidates.Where(t => t.TmdbId is not null)
                        .Select(t => (IsMovie: t.MediaType == MediaType.Movie, TmdbId: t.TmdbId!.Value))
                        .ToHashSet(),
                    ct)
                : [];

            return Results.Ok(new { titles, people, global = globalRows });
        });

        // Live "Ask Reel": pulls matching titles from TMDB on the fly, embeds + personally scores
        // them, and STREAMS the experience (SSE: conversation → phase → candidate →
        // candidate-scored → done) so an engaging, live-updating UI appears instantly. Consumed via
        // fetch + ReadableStream.
        //
        // ── The lifecycle, and why it is shaped this way ────────────────────────────────────
        // The SERVER owns what a conversation is. The client sends a question, the conversation it
        // belongs to, and one id for this exchange; everything else — the history the model reads,
        // the no-repeat set, whether this is a new question or another page of the last one — is
        // derived here from rows the server wrote. That inversion is the whole feature: a
        // client-carried transcript is unverifiable, capped, lost on reload and wrong the moment a
        // second tab exists, and every one of those failures showed up as the product forgetting
        // what it had just said.
        //
        // The turn is written PENDING before the stream opens and stamped Completed after `done`,
        // on a scope of its own. A crash mid-stream therefore leaves a row that says so rather than
        // nothing at all, and a retry of the same ClientTurnId is answered from that row instead of
        // buying a second full-price turn.
        group.MapPost("/ask", async (
            AskRequest request,
            HttpContext http,
            LiveSearchExpansionService expansion,
            IAccountContext accountContext,
            IReelDbContext db,
            IServiceScopeFactory scopeFactory,
            EligibilityQueryBuilder eligibility,
            OnDemandScorer scorer,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("FrostAura.Reel.Api.AskReel");
            if (accountContext.AccountId is not { } accountId)
            {
                return Results.Unauthorized();
            }

            // ── 1. Validate, before a header is written ─────────────────────────────────────
            // A malformed request is refused as a request. Opening a stream to tell a client its
            // own bug is a stream it then has to parse to learn nothing happened, and B2's client
            // maps 400 / 404 / 409 to three different things it can actually do.
            var message = (request.Query ?? string.Empty).Trim();
            if (message.Length == 0)
            {
                return AskError(StatusCodes.Status400BadRequest, "Ask me something first.", "empty_query");
            }

            // The pre-spend ceiling, kept as A3 wrote it — including its prose, which is the half a
            // person can act on. History and the shown set no longer ride on the request at all, so
            // only the query bound can still be tripped.
            if (AskRequestLimits.Refuse(message, historyTexts: null, shownCount: 0) is { } refusal)
            {
                return AskError(StatusCodes.Status400BadRequest, refusal, "query_too_long");
            }

            if (request.ClientTurnId == Guid.Empty)
            {
                return AskError(
                    StatusCodes.Status400BadRequest,
                    "That request is missing its turn id — refresh the page and try again.",
                    "client_turn_id_required");
            }

            // ── 2. Resolve the conversation. An unknown id NEVER forks ──────────────────────
            // A stale localStorage entry, a deleted thread or another account's id used to mint a
            // brand-new conversation and answer into it, which is how one person's transcript
            // silently became two. The lookup is account-scoped, so "not mine" and "not there" are
            // the same 404 and neither leaks the difference.
            Domain.Search.SearchConversation? conversation = null;
            if (request.ConversationId is { } conversationId)
            {
                conversation = await db.SearchConversations
                    .FirstOrDefaultAsync(c => c.Id == conversationId && c.AccountId == accountId, ct);
                if (conversation is null)
                {
                    return AskError(
                        StatusCodes.Status404NotFound,
                        "That conversation isn't here any more — start a new one.",
                        "conversation_not_found");
                }
            }

            var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

            // ── 3. Idempotency, and one turn in flight per conversation ─────────────────────
            if (conversation is not null)
            {
                var existing = await db.SearchTurns.FirstOrDefaultAsync(
                    t => t.ConversationId == conversation.Id && t.ClientTurnId == request.ClientTurnId, ct);
                if (existing is not null)
                {
                    // Still running: 409 rather than a second stream. Two streams for one turn
                    // would both write cards under the same turn id and collide on (TurnId, Rank).
                    if (existing.Status == Domain.Search.SearchTurnStatus.Pending)
                    {
                        return AskError(
                            StatusCodes.Status409Conflict,
                            "That question is still being answered — one moment.",
                            "turn_in_progress");
                    }

                    // Completed or Failed: replayed from the row. Zero LLM calls, zero new rows,
                    // zero exposures — the user is looking at the same serve again, not a new one.
                    return await ReplayTurnAsStreamAsync(
                        http, db, eligibility, scorer, accountId, conversation, existing, jsonOptions, ct);
                }

                // A DIFFERENT turn is already in flight. The unique (ConversationId, Sequence)
                // index is the race backstop; this is the answer a person gets rather than a 500.
                if (await db.SearchTurns.AnyAsync(
                        t => t.ConversationId == conversation.Id && t.Status == Domain.Search.SearchTurnStatus.Pending, ct))
                {
                    return AskError(
                        StatusCodes.Status409Conflict,
                        "There's already a question in flight on this thread — one moment.",
                        "turn_in_progress");
                }
            }

            var profile = await db.Accounts
                .Where(a => a.Id == accountId)
                .Select(a => new { a.Region, a.Tier })
                .FirstOrDefaultAsync(ct);

            // Founder-only diagnostics, opt-in per request. Not the RequireFounder group filter,
            // because this route is not founder-only — only one FIELD of its answer is.
            var debug = EndpointHelpers.AllowsFounderDebug(http.Request.Query["debug"], profile?.Tier);

            // ── 4. Everything the model reads, derived from rows ────────────────────────────
            // Read BEFORE this turn's own rows are written, so the history the model sees is the
            // conversation up to now and the question is not handed to it twice.
            var history = new List<Domain.Ports.ChatTurn>();
            var shown = new List<long>();
            string? lastUserText = null;
            var nextSequence = 0;
            if (conversation is not null)
            {
                history = (await db.SearchTurns
                        .Where(t => t.ConversationId == conversation.Id
                            && t.Status == Domain.Search.SearchTurnStatus.Completed
                            && t.Text != string.Empty)
                        .OrderByDescending(t => t.Sequence)
                        .Take(AskHistoryTurns)
                        .Select(t => new { t.Role, t.Text })
                        .ToListAsync(ct))
                    .AsEnumerable()
                    .Reverse()
                    .Select(t => new Domain.Ports.ChatTurn(t.Role, t.Text))
                    .ToList();

                // The no-repeat set: what this conversation has actually shown, from the server's
                // own record of it. One indexed read on (ConversationId, TmdbId).
                shown = await db.SearchTurnCards
                    .Where(c => c.ConversationId == conversation.Id && c.TmdbId != null)
                    .Select(c => c.TmdbId!.Value)
                    .Distinct()
                    .ToListAsync(ct);

                lastUserText = await db.SearchTurns
                    .Where(t => t.ConversationId == conversation.Id && t.Role == "user")
                    .OrderByDescending(t => t.Sequence)
                    .Select(t => t.Text)
                    .FirstOrDefaultAsync(ct);

                nextSequence = await db.SearchTurns
                    .Where(t => t.ConversationId == conversation.Id)
                    .MaxAsync(t => (int?)t.Sequence, ct) is { } max ? max + 1 : 0;
            }

            // Kind is DERIVED, never sent. A filter toggle, an infinite-scroll page and an
            // impatient re-tap are indistinguishable from the client's side, and the old
            // client-supplied flag is why a resumed transcript showed the same question five times.
            var kind = lastUserText is not null
                && string.Equals(lastUserText.Trim(), message, StringComparison.OrdinalIgnoreCase)
                    ? Domain.Search.SearchTurnKind.Page
                    : Domain.Search.SearchTurnKind.Ask;

            var mediaFilter = (request.MediaTypes ?? [])
                .Select(m => m?.Trim().ToLowerInvariant())
                .Select(m => m is "movie" or "movies" or "film" ? MediaType.Movie
                    : m is "tv" or "show" or "series" ? MediaType.Show : (MediaType?)null)
                .Where(m => m is not null)
                .Select(m => m!.Value)
                .Distinct()
                .ToList();

            // ── 5. The rows, before the stream ──────────────────────────────────────────────
            var now = DateTime.UtcNow;
            if (conversation is null)
            {
                conversation = new Domain.Search.SearchConversation
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Title = message.Length > 80 ? message[..80] : message,
                    CreatedAt = now,
                    LastTurnAt = now,
                };
                db.SearchConversations.Add(conversation);
            }

            // A page adds an assistant row to a question that was already asked; an ask adds both.
            if (kind == Domain.Search.SearchTurnKind.Ask)
            {
                db.SearchTurns.Add(new Domain.Search.SearchTurn
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ConversationId = conversation.Id,
                    Role = "user",
                    Text = message,
                    CreatedAt = now,
                    Sequence = nextSequence++,
                    Kind = kind,
                    Status = Domain.Search.SearchTurnStatus.Completed,
                    CompletedAt = now,
                });
            }

            var assistantTurn = new Domain.Search.SearchTurn
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                Role = "assistant",
                Text = string.Empty,
                CreatedAt = now,
                Sequence = nextSequence,
                ClientTurnId = request.ClientTurnId,
                Kind = kind,
                Status = Domain.Search.SearchTurnStatus.Pending,
                MediaFilter = mediaFilter.Count > 0
                    ? string.Join(',', mediaFilter.Select(m => m.ToString()))
                    : null,
            };
            db.SearchTurns.Add(assistantTurn);
            conversation.LastTurnAt = now;
            conversation.TurnCount += kind == Domain.Search.SearchTurnKind.Ask ? 2 : 1;
            await db.SaveChangesAsync(ct);

            // ── 6. The stream ───────────────────────────────────────────────────────────────
            // Connection: keep-alive is deliberately absent (it is a hop-by-hop header HTTP/2
            // rejects); no-transform stops a proxy re-encoding the body out of frame shape.
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache, no-transform";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.Body.FlushAsync(ct);

            // ONE writer at a time. The keepalive timer below writes to the same body from another
            // task, and concurrent writes to a response body are not supported — the failure mode
            // is a torn frame, which a client parser reports as corrupt JSON somewhere unrelated.
            using var writeLock = new SemaphoreSlim(1, 1);
            var eventId = 0;
            LiveSearchExpansionService.Emit emit = async (eventName, payload) =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions);
                var id = Interlocked.Increment(ref eventId);
                await writeLock.WaitAsync(ct);
                try
                {
                    // Never id: 0 — a client echoing that as Last-Event-ID would reset its cursor
                    // to the start of the stream. The counter is per-turn and starts at 1.
                    await http.Response.WriteAsync($"id: {id}\nevent: {eventName}\ndata: {json}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
                finally
                {
                    writeLock.Release();
                }
            };

            await emit("conversation", new
            {
                id = conversation.Id,
                turnId = assistantTurn.Id,
                sequence = assistantTurn.Sequence,
            });

            using var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var keepalive = KeepAliveAsync(http, writeLock, keepaliveCts.Token);

            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            LiveSearchExpansionService.TurnOutcome outcome;
            try
            {
                outcome = await expansion.StreamAsync(
                    message, history, shown, profile?.Region, mediaFilter, emit, ct, debug, assistantTurn.Id);
            }
            catch (OperationCanceledException)
            {
                // The client navigated away mid-stream. Normal teardown for the response, and a
                // recorded failure for the row: a turn nobody waited for is still a turn that
                // happened, and the transcript has to be able to say so.
                outcome = LiveSearchExpansionService.TurnOutcome.Crashed(
                    assistantText: null, "the answer was interrupted", "client_aborted");
            }
            finally
            {
                await keepaliveCts.CancelAsync();
                await keepalive;
            }

            // ── 7. Persist, on a scope of its own ───────────────────────────────────────────
            await PersistTurnAsync(
                scopeFactory, logger, accountId, assistantTurn.Id, outcome, (int)startedAt.ElapsedMilliseconds);

            return Results.Empty;
        })
        // The transport half of the ceiling: a body past this never reaches the handler, so work the
        // handler cannot inspect stays bounded too. Route metadata rather than a Kestrel-wide limit,
        // because Program.cs speaks for every endpoint and only this one streams an unbounded prompt.
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AskRequestLimits.MaxBodyBytes));

        // ── The read side ───────────────────────────────────────────────────────────────────
        // Four routes and one deprecated alias, all backed by the single ReplayAsync projection so
        // the founder-locked eligibility rule is applied in exactly one place.

        /// The conversation list — enough to render a "Recent" menu and nothing more.
        group.MapGet("/conversations", async (
            IReelDbContext db, IAccountContext accountContext, CancellationToken ct, int limit = 20) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var conversations = await db.SearchConversations
                .Where(c => c.AccountId == accountId)
                .OrderByDescending(c => c.LastTurnAt)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(c => new
                {
                    id = c.Id,
                    title = c.Title,
                    turnCount = c.TurnCount,
                    lastTurnAt = c.LastTurnAt,
                    // Null is "transcript only" — a thread that predates stored results. The client
                    // renders that as its own state rather than as an empty grid, which is the
                    // difference between "this is old" and "we found nothing for you".
                    lastCardsAt = c.LastCardsAt,
                })
                .ToListAsync(ct);
            return Results.Ok(conversations);
        });

        /// One conversation, resumed: the transcript plus the answer as it should look NOW.
        group.MapGet("/conversations/{id:guid}", async (
            Guid id, IReelDbContext db, IAccountContext accountContext,
            EligibilityQueryBuilder eligibility, OnDemandScorer scorer, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var conversation = await db.SearchConversations
                .FirstOrDefaultAsync(c => c.Id == id && c.AccountId == accountId, ct);
            return conversation is null
                ? AskError(StatusCodes.Status404NotFound, "That conversation isn't here any more.", "conversation_not_found")
                : Results.Ok(await ResumeAsync(db, eligibility, scorer, accountId, conversation, ct));
        });

        /// The reconnect path. A client that lost the stream asks the TURN what happened to it.
        ///
        /// This is what replaces Last-Event-ID replay: the events are gone, but the turn is a row,
        /// and a row answers the only question the client actually has — is it done, and what did
        /// it say. 202 while it is still running, so a poller can tell "not yet" from "nothing".
        group.MapGet("/turns/{id:guid}", async (
            Guid id, IReelDbContext db, IAccountContext accountContext,
            EligibilityQueryBuilder eligibility, OnDemandScorer scorer, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var turn = await db.SearchTurns.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId, ct);
            if (turn is null)
            {
                return AskError(StatusCodes.Status404NotFound, "That turn isn't here any more.", "turn_not_found");
            }

            if (turn.Status == Domain.Search.SearchTurnStatus.Pending)
            {
                return Results.Json(
                    new { status = turn.Status, reason = (string?)null, failureCode = (string?)null, cards = Array.Empty<object>() },
                    statusCode: StatusCodes.Status202Accepted);
            }

            var replay = await ReplayAsync(db, eligibility, scorer, accountId, [turn], ct);
            return Results.Ok(new
            {
                status = turn.Status,
                reason = replay.Reason,
                failureCode = turn.FailureCode,
                cards = replay.Cards,
                removed = replay.Removed,
            });
        });

        /// Erasure, for one conversation.
        ///
        /// Cards, then turns, then the conversation — children first, because none of those
        /// references is a foreign key and the wrong order leaves rows nothing can ever reach again.
        /// It NEVER touches ExposureEvents: the exposure log is append-only, its rows are the
        /// evidence every accuracy number is computed from, and a dangling SourceTurnId afterwards
        /// is the designed state. Deleting a conversation removes what the user can see, not the
        /// record that the product once served those cards.
        group.MapDelete("/conversations/{id:guid}", async (
            Guid id, IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var exists = await db.SearchConversations.AnyAsync(c => c.Id == id && c.AccountId == accountId, ct);
            if (!exists)
            {
                return AskError(StatusCodes.Status404NotFound, "That conversation isn't here any more.", "conversation_not_found");
            }

            await db.SearchTurnCards.Where(c => c.ConversationId == id && c.AccountId == accountId).ExecuteDeleteAsync(ct);
            await db.SearchTurns.Where(t => t.ConversationId == id && t.AccountId == accountId).ExecuteDeleteAsync(ct);
            await db.SearchConversations.Where(c => c.Id == id && c.AccountId == accountId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        /// DEPRECATED — a thin alias for the newest conversation, kept only so a client that has
        /// not yet learned to remember its conversation id still resumes. It answers the same shape
        /// as /conversations/{id}; new callers should use that with a stored id, which is also what
        /// stops a second device stealing the first one's thread.
        group.MapGet("/conversations/latest", async (
            IReelDbContext db, IAccountContext accountContext,
            EligibilityQueryBuilder eligibility, OnDemandScorer scorer, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var conversation = await db.SearchConversations
                .Where(c => c.AccountId == accountId)
                .OrderByDescending(c => c.LastTurnAt)
                .FirstOrDefaultAsync(ct);
            return conversation is null
                ? Results.Ok(new { id = (Guid?)null, turns = Array.Empty<object>() })
                : Results.Ok(await ResumeAsync(db, eligibility, scorer, accountId, conversation, ct));
        })
        .WithSummary("Deprecated: use GET /api/search/conversations/{id} with a remembered id.");
    }

    /// <summary>
    /// The typeahead's global TMDB lookup — one movie-search and one show-search page, unioned,
    /// minus anything the local shortlist already shows, poster-less rows dropped (a bare text row
    /// in a poster list reads as broken), popularity-ordered, capped at six. Failure shape is part
    /// of the contract: TMDB being down degrades to an empty global list, never to a failed
    /// typeahead — the local results always survive.
    /// </summary>
    internal static async Task<IReadOnlyList<GlobalTypeaheadRow>> GlobalLookupAsync(
        ITmdbClient tmdb, string term, IReadOnlySet<(bool IsMovie, long TmdbId)> localKeys, CancellationToken ct)
    {
        try
        {
            var batches = await Task.WhenAll(
                tmdb.SearchTitlesAsync(true, term, 1, ct),
                tmdb.SearchTitlesAsync(false, term, 1, ct));
            return batches
                .SelectMany(b => b)
                .GroupBy(i => (i.IsMovie, i.Id))
                .Select(g => g.First())
                .Where(i => !localKeys.Contains((i.IsMovie, i.Id)))
                .Where(i => !string.IsNullOrEmpty(i.PosterPath))
                .OrderByDescending(i => i.Popularity)
                .Take(6)
                .Select(i => new GlobalTypeaheadRow(
                    Source: "tmdb",
                    MediaType: i.IsMovie ? "movie" : "show",
                    TmdbId: i.Id,
                    Name: i.Name,
                    Year: i.ReleasedAt?.Year,
                    PosterPath: i.PosterPath,
                    CrowdRating: i.VoteAverage == 0m ? null : i.VoteAverage,
                    PredictedRating: null))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lookup beyond the catalogue is a bonus, never a dependency — swallow and degrade.
            return [];
        }
    }

    /// <summary>
    /// One "beyond your catalogue" typeahead row. A record rather than an anonymous type so tests
    /// can assert on it; serializes camelCase like every other minimal-API payload here.
    /// </summary>
    internal sealed record GlobalTypeaheadRow(
        string Source,
        string MediaType,
        long TmdbId,
        string Name,
        int? Year,
        string? PosterPath,
        decimal? CrowdRating,
        decimal? PredictedRating);

    /// <summary>
    /// Trigram similarity between a title and a search term, mirroring how pg_trgm computes it:
    /// lowercase, split on non-alphanumerics, pad each word with two leading spaces and one
    /// trailing, then |shared trigrams| / |union|. Reimplemented in memory only because the SQL
    /// ordering is lost once the shortlist is re-sorted by predicted rating — the database remains
    /// the authority on which rows MATCH; this only re-establishes how well, for the tiebreak.
    /// </summary>
    public static double TitleSimilarity(string? candidate, string term)
    {
        var a = Trigrams(candidate);
        var b = Trigrams(term);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        var shared = a.Intersect(b).Count();
        var union = a.Count + b.Count - shared;
        return union == 0 ? 0d : (double)shared / union;
    }

    private static HashSet<string> Trigrams(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        // Every non-alphanumeric is a SEPARATOR, not something to strip out of a word. That
        // distinction is the whole behaviour: pg_trgm reads "Spider-Man" as two words, which is why
        // the live database scores it 0.615 against "spiderman" rather than 1.0. Stripping the
        // hyphen instead would make this helper disagree with the query that selected the rows.
        foreach (var word in value.ToLowerInvariant().Split(
                     value.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = "  " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                result.Add(padded.Substring(i, 3));
            }
        }

        return result;
    }

    /// <summary>
    /// Everything a client is allowed to say about a turn.
    ///
    /// <para><b>What is deliberately absent is the point.</b> <c>History</c>, <c>ShownTmdbIds</c>
    /// and <c>Continuation</c> were all here and are all gone: the transcript, the no-repeat set
    /// and the ask-versus-page decision are now derived server-side from
    /// <c>SearchTurns</c> / <c>SearchTurnCards</c>. A client cannot carry state it loses on reload,
    /// cannot be trusted about what it has already been shown, and cannot tell a filter toggle from
    /// a new question — and every one of those showed up as the product forgetting itself.</para>
    /// </summary>
    /// <param name="Query">The question, 1–<see cref="AskRequestLimits.MaxQueryChars"/> characters trimmed.</param>
    /// <param name="ConversationId">The thread this belongs to; null mints a new one. An unknown id is a 404, never a fork.</param>
    /// <param name="ClientTurnId">The client's id for THIS exchange — the idempotency key. Resent verbatim on a retry.</param>
    /// <param name="MediaTypes">An explicit UI filter, which outranks whatever the model infers from the prose.</param>
    public record AskRequest(
        string Query,
        Guid? ConversationId,
        Guid ClientTurnId,
        List<string>? MediaTypes);

    /// <summary>How many prior turns the interpret prompt reads. The tail is what carries context.</summary>
    private const int AskHistoryTurns = 8;

    /// <summary>
    /// One conversation, resumed: the transcript, plus the answer as it should look now.
    ///
    /// <para><b>Which turns' cards come back.</b> The last completed <c>ask</c>, plus every
    /// completed <c>page</c> after it — that block IS the answer the user was last looking at,
    /// because a page adds cards to the question already on screen. Anything older is transcript.
    /// Ordered <c>(Sequence, Rank)</c>, never re-sorted, so a resumed grid is the grid that was
    /// there.</para>
    ///
    /// <para><c>shown</c> is the whole conversation's no-repeat set, not the block's — it is what
    /// the SERVER will exclude on the next turn, and the client renders it to explain why. It is
    /// informational: nothing the client sends back is trusted for it.</para>
    /// </summary>
    private static async Task<object> ResumeAsync(
        IReelDbContext db,
        EligibilityQueryBuilder eligibility,
        OnDemandScorer scorer,
        Guid accountId,
        Domain.Search.SearchConversation conversation,
        CancellationToken ct)
    {
        var turns = await db.SearchTurns
            .Where(t => t.ConversationId == conversation.Id)
            .OrderBy(t => t.Sequence)
            .ToListAsync(ct);

        var lastAsk = turns.FindLastIndex(t =>
            t.Role == "assistant"
            && t.Kind == Domain.Search.SearchTurnKind.Ask
            && t.Status == Domain.Search.SearchTurnStatus.Completed);
        var block = lastAsk < 0
            ? []
            : turns.Skip(lastAsk)
                .Where(t => t.Role == "assistant" && t.Status == Domain.Search.SearchTurnStatus.Completed)
                .ToList();

        var replay = await ReplayAsync(db, eligibility, scorer, accountId, block, ct);

        // The whole conversation's no-repeat set, straight off the index that computes it for the
        // next turn — one definition, so the client can never be told a different set than the one
        // the server will actually apply.
        var shown = await db.SearchTurnCards
            .Where(c => c.ConversationId == conversation.Id && c.TmdbId != null)
            .Select(c => c.TmdbId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var lastQuery = turns.LastOrDefault(t => t.Role == "user")?.Text;
        var mediaFilter = block.Count > 0 ? block[^1].MediaFilter : null;

        return new
        {
            id = conversation.Id,
            title = conversation.Title,
            // Null says "transcript only" — this thread predates stored results, which is a
            // different fact from a turn that honestly returned nothing.
            lastCardsAt = conversation.LastCardsAt,
            turns = turns.Select(t => new
            {
                sequence = t.Sequence,
                role = t.Role,
                text = t.Text,
                kind = t.Kind,
                status = t.Status,
            }),
            cards = replay.Cards,
            reason = replay.Reason,
            removed = replay.Removed,
            lastQuery,
            mediaTypes = MediaTypeTokens(mediaFilter),
            shown,
        };
    }

    /// <summary>
    /// The stored media filter as the tokens the request accepts, so a resumed page can send the
    /// filter straight back without a translation table of its own. <c>Show</c> becomes <c>tv</c>
    /// because that is what the client's own filter emits.
    /// </summary>
    private static string[] MediaTypeTokens(string? mediaFilter) =>
        string.IsNullOrWhiteSpace(mediaFilter)
            ? []
            : mediaFilter
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(m => m == nameof(MediaType.Show) ? "tv" : "movie")
                .ToArray();

    /// <summary>A replayed block of turns: the cards as they should be rendered NOW, and what changed.</summary>
    /// <param name="Cards">The stored cards, re-filtered and re-scored, in stored order.</param>
    /// <param name="Removed">How many stored cards the eligibility re-filter dropped.</param>
    /// <param name="Reason">The reason line of the newest turn in the block, verbatim.</param>
    private sealed record ReplayedTurns(IReadOnlyList<object> Cards, int Removed, string? Reason);

    /// <summary>
    /// THE read projection. Every route that hands a stored turn back to a client goes through
    /// here, so the eligibility guard exists in exactly one place — and the eligibility rule is
    /// founder-locked and names Ask Reel explicitly, so a second copy of it is a second chance to
    /// get it wrong.
    ///
    /// <para><b>Three things happen to a stored card on its way out.</b> It is dropped if the user
    /// has since interacted with the title in any way — rated it, watched it, saved it, dropped it,
    /// marked it not-interested — and the drops are COUNTED, because a card that silently
    /// disappears between two views of the same answer is indistinguishable from a bug. Its
    /// prediction is re-scored when the turn's artifact is not the account's current Active one:
    /// null, superseded, or simply unresolvable — <c>ModelArtifact</c>'s global filter hides
    /// artifacts trained against a different feature-schema version, so "does not resolve" and
    /// "differs" are the same query and get the same answer. And <c>seen</c> is recomputed rather
    /// than stored, because it is a fact about the person, not about the card.</para>
    ///
    /// <para><b>What does NOT happen: an exposure row.</b> Replaying is the user looking at the same
    /// serve again; logging it would inflate the denominator of every accuracy number computed from
    /// the log. The ORDER is also never re-derived — stored rank is the position the answer had,
    /// and re-sorting a replay by a rounded blend is how the same question comes back in a
    /// different order.</para>
    /// </summary>
    private static async Task<ReplayedTurns> ReplayAsync(
        IReelDbContext db,
        EligibilityQueryBuilder eligibility,
        OnDemandScorer scorer,
        Guid accountId,
        IReadOnlyList<Domain.Search.SearchTurn> turns,
        CancellationToken ct)
    {
        if (turns.Count == 0)
        {
            return new ReplayedTurns([], 0, null);
        }

        var sequenceByTurn = turns.ToDictionary(t => t.Id, t => t.Sequence);
        var turnIds = sequenceByTurn.Keys.ToList();
        var stored = (await db.SearchTurnCards
                .Where(c => turnIds.Contains(c.TurnId))
                .ToListAsync(ct))
            .OrderBy(c => sequenceByTurn[c.TurnId])
            .ThenBy(c => c.Rank)
            .ToList();
        if (stored.Count == 0)
        {
            return new ReplayedTurns([], 0, turns[^1].Reason);
        }

        var titleIds = stored.Select(c => c.TitleId).Distinct().ToList();
        var titles = await db.Titles.Where(t => titleIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        // The founder-locked eligibility rule, applied to a stored answer. A title that has since
        // picked up ANY interaction footprint stops being a suggestion.
        var interacted = await eligibility.InteractedTitleIdsAsync(accountId, titleIds, ct);
        var survivors = stored
            .Where(c => !interacted.Contains(c.TitleId) && titles.ContainsKey(c.TitleId))
            .ToList();
        var removed = stored.Count - survivors.Count;

        // Null, superseded, or hidden by the schema filter — all three are "not the artifact behind
        // the numbers this account is shown today", and all three re-score.
        var activeArtifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        var stale = turns.Any(t => t.ModelArtifactId is null || t.ModelArtifactId != activeArtifactId);
        IReadOnlyDictionary<Guid, decimal> rescored = new Dictionary<Guid, decimal>();
        if (stale && survivors.Count > 0)
        {
            // An empty map is the honest answer for an account with no model at all, and it falls
            // through to the stored number rather than blanking the badge.
            rescored = await scorer.ScoreAsync(
                accountId, survivors.Select(c => c.TitleId).Distinct().ToList(), DateTime.UtcNow, ct);
        }

        var survivorIds = survivors.Select(c => c.TitleId).Distinct().ToList();
        var watched = await db.WatchedTitles
            .Where(w => w.AccountId == accountId && survivorIds.Contains(w.TitleId))
            .Select(w => w.TitleId)
            .ToListAsync(ct);
        var rated = await db.UserRatings
            .Where(r => r.AccountId == accountId && survivorIds.Contains(r.TitleId))
            .Select(r => r.TitleId)
            .ToListAsync(ct);
        var seen = watched.Concat(rated).ToHashSet();

        var cards = survivors
            .Select(c =>
            {
                var title = titles[c.TitleId];
                return (object)new
                {
                    titleId = title.Id,
                    mediaType = title.MediaType.ToString(),
                    tmdbId = title.TmdbId,
                    name = title.Name,
                    year = title.Year,
                    posterPath = title.PosterPath,
                    genres = title.Genres,
                    similarity = c.Similarity,
                    predictedRating = rescored.TryGetValue(c.TitleId, out var fresh) ? fresh : c.PredictedRating,
                    crowdRating = CrowdRating.For(title),
                    crowdVotes = CrowdRating.VotesFor(title),
                    fit = c.LlmFit is { } llmFit ? (double?)llmFit : null,
                    why = c.Why,
                    seen = seen.Contains(c.TitleId),
                    blend = (double?)c.Blend,
                    outsideRequest = c.OutsideRequest,
                };
            })
            .ToList();

        return new ReplayedTurns(cards, removed, turns[^1].Reason);
    }

    /// <summary>
    /// The idempotent retry's answer: the stored turn, on the wire, in the same SSE grammar a live
    /// turn uses — so the client has exactly one parser and one code path. Zero model calls, zero
    /// new rows, zero exposures.
    /// </summary>
    private static async Task<IResult> ReplayTurnAsStreamAsync(
        HttpContext http,
        IReelDbContext db,
        EligibilityQueryBuilder eligibility,
        OnDemandScorer scorer,
        Guid accountId,
        Domain.Search.SearchConversation conversation,
        Domain.Search.SearchTurn turn,
        System.Text.Json.JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        var replay = await ReplayAsync(db, eligibility, scorer, accountId, [turn], ct);

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var eventId = 0;
        async Task Emit(string eventName, object payload)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions);
            await http.Response.WriteAsync($"id: {++eventId}\nevent: {eventName}\ndata: {json}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }

        await Emit("conversation", new { id = conversation.Id, turnId = turn.Id, sequence = turn.Sequence });
        await Emit("done", new
        {
            results = replay.Cards,
            streamed = replay.Cards.Count + replay.Removed,
            kept = replay.Cards.Count,
            droppedByFloor = 0,
            belowFloor = Array.Empty<object>(),
            // A failed turn replays as the failure it was. Answering a retry with a cheerful empty
            // result set would tell the client the question succeeded and returned nothing.
            reason = replay.Reason,
            failed = turn.Status == Domain.Search.SearchTurnStatus.Failed,
            failureCode = turn.FailureCode,
            // The two things a replay must say out loud: this is the same answer as before, and
            // this many of its cards no longer belong on a suggestion surface.
            replayed = true,
            removed = replay.Removed,
        });

        return Results.Empty;
    }

    /// <summary>
    /// The house error shape for <c>/api/search/*</c>, plus a stable <c>code</c>. The prose is the
    /// user's; the code is the client's, and it is what B2 switches on — matching on copy is how a
    /// UI ends up rendering a crash message for an honest refusal.
    /// </summary>
    private static IResult AskError(int statusCode, string error, string code) =>
        Results.Json(new { error, code }, statusCode: statusCode);

    /// <summary>
    /// A comment frame every 10 seconds so intermediaries do not reap an idle stream — Ask Reel can
    /// legitimately go quiet for tens of seconds during discovery, and nginx's <c>/api/</c> location
    /// is not the one with the 24-hour read timeout.
    ///
    /// <para>It shares <paramref name="writeLock"/> with <c>emit</c> and that is mandatory, not
    /// tidiness: two tasks writing one response body interleave mid-frame, and the resulting torn
    /// JSON surfaces to the client as a parse error on whatever frame happened to be in flight.</para>
    /// </summary>
    private static async Task KeepAliveAsync(HttpContext http, SemaphoreSlim writeLock, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await writeLock.WaitAsync(ct);
                try
                {
                    await http.Response.WriteAsync(":keepalive\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
                finally
                {
                    writeLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The turn ended or the client left — both are how this loop is meant to stop.
        }
    }

    /// <summary>
    /// Stamps the turn with what it turned out to be, and stores its cards.
    ///
    /// <para><b>On a scope of its own</b>, for two reasons. The request-scoped context has just
    /// carried the whole pipeline's catalogue upserts and must never be saved after the stream —
    /// doing so would commit a pile of unrelated tracked changes as a side effect of logging a
    /// turn. And the request's <c>CancellationToken</c> is already cancelled whenever the client
    /// went away, which is precisely the case this write exists for, so it saves under
    /// <see cref="CancellationToken.None"/>.</para>
    ///
    /// <para>It never throws. The response is committed by the time it runs — there is no
    /// exception handler left to catch anything — and a turn nobody managed to record is a worse
    /// outcome than a 500 the user cannot see, but not worse than losing the answer they already
    /// have. Failure is logged at Error, loudly, because a silent one leaves the whole feature
    /// looking like it works while nothing persists.</para>
    /// </summary>
    private static async Task PersistTurnAsync(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        Guid accountId,
        Guid turnId,
        LiveSearchExpansionService.TurnOutcome outcome,
        int latencyMs)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountContext>();
            accounts.SetAccount(accountId);
            var db = scope.ServiceProvider.GetRequiredService<IReelDbContext>();

            var turn = await db.SearchTurns.FirstOrDefaultAsync(t => t.Id == turnId, CancellationToken.None);
            if (turn is null)
            {
                logger.LogError(
                    "Ask Reel turn {TurnId} vanished between the stream and the write; the answer is not recorded.",
                    turnId);
                return;
            }

            var now = DateTime.UtcNow;
            turn.Text = outcome.AssistantText ?? string.Empty;
            turn.Reason = outcome.Reason;
            turn.Status = outcome.Failed
                ? Domain.Search.SearchTurnStatus.Failed
                : Domain.Search.SearchTurnStatus.Completed;
            turn.FailureCode = outcome.Failed ? outcome.FailureCode : null;
            turn.Relaxation = outcome.Relaxation;
            turn.FloorRelaxed = outcome.FloorRelaxed;
            turn.DiscoveredCount = outcome.DiscoveredCount;
            turn.IntentJson = outcome.IntentJson;
            turn.ModelArtifactId = outcome.ModelArtifactId;
            turn.ModelId = outcome.ModelId;
            turn.PromptVersion = AskReelPromptVersion.Current;
            turn.LatencyMs = latencyMs;
            turn.CompletedAt = now;

            // A failed turn stores no cards. There is nothing to replay, and a half-answer replayed
            // as a whole one is the transcript lying about what happened.
            if (!outcome.Failed && outcome.Cards.Count > 0)
            {
                db.SearchTurnCards.AddRange(outcome.Cards.Select(card => new Domain.Search.SearchTurnCard
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    ConversationId = turn.ConversationId,
                    TurnId = turn.Id,
                    TitleId = card.TitleId,
                    TmdbId = card.TmdbId,
                    Rank = card.Rank,
                    OutsideRequest = card.OutsideRequest,
                    Similarity = card.Similarity,
                    PredictedRating = card.PredictedRating,
                    LlmFit = card.LlmFit,
                    Blend = card.Blend,
                    Why = card.Why,
                    WhySource = card.WhySource,
                    CreatedAt = now,
                }));

                var conversation = await db.SearchConversations
                    .FirstOrDefaultAsync(c => c.Id == turn.ConversationId, CancellationToken.None);
                if (conversation is not null)
                {
                    conversation.LastCardsAt = now;
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Ask Reel could not record turn {TurnId} for account {AccountId}. The answer was delivered; the "
                + "transcript is now short by one turn and a resume will not show it.",
                turnId, accountId);
        }
    }

}
