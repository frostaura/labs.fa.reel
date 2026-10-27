using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.AskReel.Engine;
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
            AskRequest? request,
            HttpContext http,
            LiveSearchExpansionService expansion,
            IAccountContext accountContext,
            IReelDbContext db,
            IServiceScopeFactory scopeFactory,
            EligibilityQueryBuilder eligibility,
            OnDemandScorer scorer,
            ILoggerFactory loggerFactory,
            IConfiguration configuration,
            AskReelEngine engineV2,
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
            //
            // The body is read as a JsonDocument by AskRequest.BindAsync and every field is picked
            // out by hand, so a field of the WRONG JSON TYPE is a refusal here rather than a binder
            // failure upstream of this handler. `clientTurnId: 12345` used to return five kilobytes
            // of text/plain BadHttpRequestException naming this file's internal type, with no
            // `code` — the contract tells clients to switch on `code`, so they got `undefined` and
            // rendered a crash message for a type-confused field.
            if (request is null)
            {
                return AskError(
                    StatusCodes.Status400BadRequest,
                    "That request wasn't something I could read — refresh the page and try again.",
                    "body_invalid");
            }

            if (request.Malformed.Count > 0)
            {
                var (prose, code) = AskRequest.RefusalFor(request.Malformed[0]);
                return AskError(StatusCodes.Status400BadRequest, prose, code);
            }

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

            // The two ids are STRINGS on the wire and are parsed here, deliberately. Bound as
            // Guid / Guid? on the record they never reached this handler at all: a non-UUID, a
            // 300-character key, a JSON null or an empty string failed JSON binding first, and the
            // caller got five kilobytes of text/plain BadHttpRequestException — no `code` for a
            // client to switch on, and the internal type name on a route any signed-in account can
            // reach. A malformed id is this handler's refusal to make, in the house shape, like
            // every other refusal above it.
            if (string.IsNullOrWhiteSpace(request.ClientTurnId))
            {
                return AskError(
                    StatusCodes.Status400BadRequest,
                    "That request is missing its turn id — refresh the page and try again.",
                    "client_turn_id_required");
            }

            // All-zeros is well-formed and is nonetheless the "no id" sentinel, so it is INVALID
            // rather than missing: the client did send something, and it cannot be an idempotency
            // key — every retry of every turn would collide on it.
            if (!Guid.TryParse(request.ClientTurnId.Trim(), out var clientTurnId) || clientTurnId == Guid.Empty)
            {
                return AskError(
                    StatusCodes.Status400BadRequest,
                    "That request's turn id isn't a valid id — refresh the page and try again.",
                    "client_turn_id_invalid");
            }

            Guid? requestedConversationId = null;
            if (request.ConversationId is not null)
            {
                if (!Guid.TryParse(request.ConversationId.Trim(), out var parsedConversationId)
                    || parsedConversationId == Guid.Empty)
                {
                    return AskError(
                        StatusCodes.Status400BadRequest,
                        "That thread id isn't a valid id — start a new one.",
                        "conversation_id_invalid");
                }

                requestedConversationId = parsedConversationId;
            }

            // R3-02. A page is now EXPLICIT where the client knows which answer it is extending.
            Guid? requestedPageOf = null;
            if (request.PageOf is not null)
            {
                if (!Guid.TryParse(request.PageOf.Trim(), out var parsedPageOf) || parsedPageOf == Guid.Empty)
                {
                    return AskError(
                        StatusCodes.Status400BadRequest,
                        "That answer id isn't a valid id — ask the question again instead.",
                        "page_of_invalid");
                }

                requestedPageOf = parsedPageOf;
            }

            // Every media-type token has to be one this server can honour. An unknown token used to
            // be dropped in silence, so a typo'd or stale filter was indistinguishable from no
            // filter — and the turn was billed anyway. Refused before the spend, with the token
            // named, because the client cannot fix what it is not told.
            if (AskMediaTypes.Resolve(request.MediaTypes) is not { } mediaFilter)
            {
                return AskError(
                    StatusCodes.Status400BadRequest,
                    AskMediaTypes.Refusal(request.MediaTypes),
                    "media_type_invalid");
            }

            // ── 2. Resolve the conversation. An unknown id NEVER forks ──────────────────────
            // A stale localStorage entry, a deleted thread or another account's id used to mint a
            // brand-new conversation and answer into it, which is how one person's transcript
            // silently became two. The lookup is account-scoped, so "not mine" and "not there" are
            // the same 404 and neither leaks the difference.
            Domain.Search.SearchConversation? conversation = null;
            if (requestedConversationId is { } conversationId)
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

            // ── 2b. Resolve an EXPLICIT page, if the client named one ───────────────────────
            //
            // R3-02. `kind` used to be derived only by comparing the query to the conversation's
            // last USER turn, and after a Stop that is the stopped question — so pressing "Show me
            // more" on the kept answer was recorded as a brand-new ask. Three things then went
            // wrong at once: the transcript gained a question nobody typed, the stopped turn stopped
            // being the tail and lost its failure row, and the next resume returned only the newest
            // ask's cards — 42 of 66 on screen disappeared on reload. The client knows exactly which
            // answer its "Show me more" belongs to, because it is rendering it; `pageOf` is that
            // fact, stated rather than guessed.
            //
            // It is verified rather than trusted: the turn must be the caller's, must belong to this
            // conversation, must be Completed, and must answer the question being re-sent.
            Domain.Search.SearchTurn? pageOfTurn = null;
            if (requestedPageOf is { } pageOfId)
            {
                pageOfTurn = await db.SearchTurns
                    .FirstOrDefaultAsync(t => t.Id == pageOfId && t.AccountId == accountId && t.Role == "assistant", ct);

                // "Not yours", "not there" and "not in this thread" are one 404 and none of them
                // leaks which — the same rule the conversation lookup above follows.
                if (pageOfTurn is null || (conversation is not null && pageOfTurn.ConversationId != conversation.Id))
                {
                    return AskError(
                        StatusCodes.Status404NotFound,
                        "That answer isn't here any more — ask the question again.",
                        "page_of_not_found");
                }

                // A page EXTENDS an answer. A turn that failed or is still running has no answer to
                // extend, and appending to it would put cards under a row the transcript renders as
                // a failure — which is how a stopped turn lost its explanation in the first place.
                if (pageOfTurn.Status != Domain.Search.SearchTurnStatus.Completed)
                {
                    return AskError(
                        StatusCodes.Status409Conflict,
                        "That answer didn't finish, so there's nothing to show more of — ask again instead.",
                        "page_of_not_completed");
                }

                // The query must be the question that turn answers. A page writes no user row, so a
                // different question sent as a page would be a question the transcript never
                // records — invisible in the thread and unanswerable on the next resume.
                var pagedQuestion = await QuestionBehindAsync(db, pageOfTurn, ct);
                if (pagedQuestion is null
                    || !string.Equals(pagedQuestion.Trim(), message, StringComparison.Ordinal))
                {
                    return AskError(
                        StatusCodes.Status409Conflict,
                        "That's a different question from the one you're showing more of — send it as a new question.",
                        "page_query_mismatch");
                }

                // A `pageOf` with no `conversationId` is legitimate and cannot fork: the turn is
                // already account-scoped, so its own conversation is the only one it could mean.
                conversation ??= await db.SearchConversations.FirstOrDefaultAsync(
                    c => c.Id == pageOfTurn.ConversationId && c.AccountId == accountId, ct);
                if (conversation is null)
                {
                    return AskError(
                        StatusCodes.Status404NotFound,
                        "That answer isn't here any more — ask the question again.",
                        "page_of_not_found");
                }
            }

            var profile = await db.Accounts
                .Where(a => a.Id == accountId)
                .Select(a => new { a.Region, a.Tier })
                .FirstOrDefaultAsync(ct);

            // Founder-only diagnostics, opt-in per request. Not the RequireFounder group filter,
            // because this route is not founder-only — only one FIELD of its answer is.
            //
            // Resolved BEFORE the idempotency branch, because a replay answers on the same terms a
            // live turn does: the contract's `done` carries `timingsMs` and `debug` as keys that are
            // ALWAYS present and null when the caller is not entitled to them, and a replay that
            // omitted them gave the client a second shape to parse for one event name.
            var debug = EndpointHelpers.AllowsFounderDebug(http.Request.Query["debug"], profile?.Tier);

            // Which engine answers (§4.13): ASK_ENGINE, which a founder may override for one request
            // with ?engine=v1|v2 — gated exactly like ?debug, ignored for everyone else.
            var engine = AskReelEngineOptions.For(
                configuration,
                profile?.Tier == Domain.Tenancy.AccountTier.Founder ? http.Request.Query["engine"].ToString() : null);

            var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

            // ── 3. Idempotency, and one turn in flight per conversation ─────────────────────
            if (conversation is not null)
            {
                var existing = await db.SearchTurns.FirstOrDefaultAsync(
                    t => t.ConversationId == conversation.Id && t.ClientTurnId == clientTurnId, ct);

                // The key's own turn, dead. A Pending row older than StalePendingAfter belongs to a
                // process that is gone — nothing in THIS one is streaming it and nothing ever will —
                // so the 409 below would be a lie the thread never recovers from: the same key is
                // exactly what "Try again" resends, and it would be refused for as long as the row
                // exists. It is closed, and the retry is allowed to buy a real turn.
                //
                // The KEY is released with it. `(ConversationId, ClientTurnId)` is a partial UNIQUE
                // index, so leaving it on the corpse would collide with the retry's own assistant
                // row on insert and turn a wedged thread into a 500. The row itself stays in the
                // transcript as Failed/turn_timed_out: the question was asked, it was not answered,
                // and that is precisely what the transcript is for.
                if (existing is not null && ExpireIfStale(existing, DateTime.UtcNow))
                {
                    existing.ClientTurnId = null;
                    await db.SaveChangesAsync(ct);
                    existing = null;
                }

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

                    // A key is a key — but it is the key to ONE question, and a client that reuses
                    // it across two user messages gets the first message's answer for the second
                    // one, in thirty milliseconds, with nothing on the wire saying the question
                    // differed. That was measured: A1's key re-sent with "a completely different
                    // question about westerns" returned A1's crime dramas, `replayed: true`, and
                    // total confidence. Idempotency means "the same request twice is one request",
                    // and a different question is not the same request. Refusing is the only answer
                    // that cannot be wrong; answering the previous question is silently wrong.
                    var askedBefore = await QuestionBehindAsync(db, existing, ct);
                    if (askedBefore is not null
                        && !string.Equals(askedBefore.Trim(), message, StringComparison.Ordinal))
                    {
                        return AskError(
                            StatusCodes.Status409Conflict,
                            "That turn id has already answered a different question — start a new one.",
                            "client_turn_id_reused");
                    }

                    // Completed or Failed: replayed from the row. Zero LLM calls, zero new rows,
                    // zero exposures — the user is looking at the same serve again, not a new one.
                    return await ReplayTurnAsStreamAsync(
                        http, db, eligibility, scorer, accountId, conversation, existing, jsonOptions, debug, ct);
                }

                // A DIFFERENT turn is already in flight. The unique (ConversationId, Sequence)
                // index is the race backstop; this is the answer a person gets rather than a 500.
                //
                // Rows rather than AnyAsync, because the stale ones have to be CLOSED and not merely
                // counted. One Pending row whose process died holds the whole conversation shut
                // against every question anyone asks in it, forever — a new key does not help,
                // because this guard never looked at keys. The 409 survives for a turn that is
                // genuinely live, which is the case it was written for.
                var inFlight = await db.SearchTurns
                    .Where(t => t.ConversationId == conversation.Id
                        && t.Status == Domain.Search.SearchTurnStatus.Pending)
                    .ToListAsync(ct);
                if (inFlight.Count > 0)
                {
                    var askedAt = DateTime.UtcNow;
                    // Every stale row, not just the first: a conversation that outlived two restarts
                    // has two of them, and one left behind is the same wedge again.
                    var live = inFlight.Count(stuck => !ExpireIfStale(stuck, askedAt));
                    await db.SaveChangesAsync(ct);

                    if (live > 0)
                    {
                        return AskError(
                            StatusCodes.Status409Conflict,
                            "There's already a question in flight on this thread — one moment.",
                            "turn_in_progress");
                    }
                }
            }

            // ── 4. Everything the model reads, derived from rows ────────────────────────────
            // Read BEFORE this turn's own rows are written, so the history the model sees is the
            // conversation up to now and the question is not handed to it twice.
            var shown = new List<Guid>();
            string? lastUserText = null;
            var nextSequence = 0;
            if (conversation is not null)
            {
                // The no-repeat set: what this conversation has actually shown, from the server's
                // own record of it. One indexed read on (ConversationId, TitleId).
                //
                // DELIBERATELY UNFILTERED BY BelowFloor. The tray is on the glass — under the "Show
                // the N under your bar" fence, one tap away — so a title that appeared there has
                // been shown, and the founder-locked rule is about what the user has seen, not
                // about which half of the answer it sat in. When the tray was not stored at all,
                // this query could not contain it and the smoke run watched 13 of one turn's 14
                // tray titles come back as the next turn's top picks.
                shown = await db.SearchTurnCards
                    .Where(c => c.ConversationId == conversation.Id)
                    .Select(c => c.TitleId)
                    .Distinct()
                    .ToListAsync(ct);

                // The question the inferred fallback compares against: the last user turn that
                // actually got an ANSWER, not simply the last one typed.
                //
                // R3-02. Reading the last user row alone made a Stop change what "Show me more"
                // means: the stopped question became the tail, so re-sending the ANSWERED question
                // no longer matched and the page was recorded as a new ask — a question nobody
                // typed in the transcript, a stopped turn stripped of its failure row, and 42 of 66
                // cards gone on the next reload. A turn nobody waited for is still a turn that
                // happened, and it is still not the thing another page would be a page OF.
                var lastAnsweredAskSequence = await db.SearchTurns
                    .Where(t => t.ConversationId == conversation.Id
                        && t.Role == "assistant"
                        && t.Kind == Domain.Search.SearchTurnKind.Ask
                        && t.Status == Domain.Search.SearchTurnStatus.Completed)
                    .MaxAsync(t => (int?)t.Sequence, ct);

                lastUserText = await db.SearchTurns
                    .Where(t => t.ConversationId == conversation.Id
                        && t.Role == "user"
                        && (lastAnsweredAskSequence == null || t.Sequence <= lastAnsweredAskSequence))
                    .OrderByDescending(t => t.Sequence)
                    .Select(t => t.Text)
                    .FirstOrDefaultAsync(ct);

                nextSequence = await db.SearchTurns
                    .Where(t => t.ConversationId == conversation.Id)
                    .MaxAsync(t => (int?)t.Sequence, ct) is { } max ? max + 1 : 0;
            }

            // Kind is EXPLICIT where the client named the answer it is extending, and DERIVED
            // otherwise. A filter toggle, an infinite-scroll page and an impatient re-tap are
            // indistinguishable from the client's side, and the old client-supplied *flag* is why a
            // resumed transcript showed the same question five times — but `pageOf` is not that
            // flag: it names a row, and the row is verified to be the caller's, completed, in this
            // conversation, and answering exactly the question being re-sent. What is trusted is a
            // fact the client can prove, not a claim about its own intent.
            var kind = pageOfTurn is not null
                ? Domain.Search.SearchTurnKind.Page
                : Domain.Search.SearchTurnKind.Derive(lastUserText, message);

            // ── 5. The rows, before the stream ──────────────────────────────────────────────
            // Everything at or after this sequence is this turn's own; the history and the presented
            // handles, read once the stream is open, stop short of it.
            var priorSequences = nextSequence;
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
                ClientTurnId = clientTurnId,
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
            var lastFrameAt = System.Diagnostics.Stopwatch.StartNew();
            var doneSent = false;
            LiveSearchExpansionService.Emit emit = async (eventName, payload) =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions);
                await writeLock.WaitAsync(ct);
                try
                {
                    // `done` is the terminal frame. A heartbeat racing the end of the turn must not
                    // follow it: a `phase` after `done` would put a finished answer back into
                    // "streaming" on the client.
                    if (doneSent && eventName == "phase")
                    {
                        return;
                    }

                    doneSent |= eventName == "done";
                    var id = Interlocked.Increment(ref eventId);
                    // Never id: 0 — a client echoing that as Last-Event-ID would reset its cursor
                    // to the start of the stream. The counter is per-turn and starts at 1.
                    await http.Response.WriteAsync($"id: {id}\nevent: {eventName}\ndata: {json}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    lastFrameAt.Restart();
                }
                finally
                {
                    writeLock.Release();
                }
            };

            // What the turn has put on the wire, held here so a cancellation can persist it (B-3).
            var progress = new AskTurnProgress();

            await emit("conversation", new
            {
                id = conversation.Id,
                turnId = assistantTurn.Id,
                sequence = assistantTurn.Sequence,
                engine,
            });

            using var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var keepalive = KeepAliveAsync(http, writeLock, keepaliveCts.Token);
            var heartbeat = HeartbeatAsync(progress, emit, lastFrameAt, keepaliveCts.Token);

            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            LiveSearchExpansionService.TurnOutcome outcome;
            try
            {
                // Read AFTER the turn's row exists and the stream is open, so a client that walks
                // away during them still leaves a recorded turn (a cold host spends real time
                // compiling these queries), and bounded by sequence so this turn's own rows are
                // never read back to it.
                var presented = await PresentedAsync(db, conversation.Id, priorSequences, ct);
                var history = await HistoryAsync(db, conversation.Id, priorSequences, ct);
                var priorIntent = await PriorIntentAsync(db, conversation.Id, priorSequences, ct);
                var turnInput = new LiveSearchExpansionService.AskTurnInput(
                    message, history, shown, presented, profile?.Region, mediaFilter,
                    Domain.Sync.ApiUsagePurpose.RerankAsk, debug, assistantTurn.Id, progress, priorIntent);
                if (engine == AskReelEngineOptions.V2)
                {
                    // This answer's number, for its handles: the assistant rows before it, plus one.
                    var answerNumber = await db.SearchTurns.CountAsync(
                        t => t.ConversationId == conversation.Id && t.Role == "assistant" && t.Sequence < assistantTurn.Sequence, ct) + 1;
                    var threadId = conversation.Id;
                    var turnId = assistantTurn.Id;
                    outcome = await engineV2.StreamAsync(
                        turnInput with
                        {
                            ConversationId = threadId,
                            AnswerNumber = answerNumber,
                            PersistCards = cards => PersistCardsAsync(scopeFactory, logger, accountId, threadId, turnId, cards),
                        },
                        accountId,
                        emit,
                        ct);
                }
                else
                {
                    outcome = await expansion.StreamAsync(turnInput, emit, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // The client navigated away mid-stream — most often because the user typed a
                // follow-up over a live turn. Normal teardown for the response, and a recorded
                // failure for the row that keeps WHAT WAS SHOWN: the reply and every card already
                // on screen are persisted, so the next turn's history and no-repeat set contain them
                // and "none of the above" points at the cards it means (A-6, B-3).
                // The record keeps what a finished turn keeps — every degraded flag raised so far
                // (a failed judge batch included), the timings to the moment of the abort, the
                // models in play — so an interrupted turn is as diagnosable as a finished one (D9).
                var shownCards = progress.Cards;
                outcome = LiveSearchExpansionService.TurnOutcome.Crashed(
                    progress.AssistantText,
                    "the answer was interrupted",
                    Domain.Search.SearchTurnFailure.ClientAborted,
                    cards: shownCards,
                    degradedFlags: progress.DegradedFlags) with
                {
                    StageTimingsJson = progress.StageTimingsJson,
                    ModelIdsJson = progress.ModelIdsJson,
                    ReasoningEffort = progress.ReasoningEffort,
                    Mode = shownCards.Count > 0 ? Domain.Search.SearchTurnMode.Picks : null,
                };
            }
            finally
            {
                await keepaliveCts.CancelAsync();
                await keepalive;
                await heartbeat;
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

            // 202 means "ask again in a moment", so it is only honest while there is something to
            // wait for. A Pending row older than StalePendingAfter has no process behind it, and a
            // poller told 202 about it polls until the user closes the tab.
            //
            // The row is CLOSED here rather than merely reported closed. This is a read route that
            // writes, deliberately: the alternative is a turn the poller has been told is Failed
            // sitting Pending in the table, where it still reads as in-flight to everything else.
            // One rule, one state, and whichever surface notices first heals the conversation.
            if (turn.Status == Domain.Search.SearchTurnStatus.Pending)
            {
                if (!ExpireIfStale(turn, DateTime.UtcNow))
                {
                    return Results.Json(
                        new
                        {
                            status = turn.Status,
                            reason = (string?)null,
                            conflict = (object?)null,
                            failureCode = (string?)null,
                            cards = Array.Empty<object>(),
                            // Present-but-empty, so a poller renders one shape on both status codes.
                            belowFloor = Array.Empty<object>(),
                            nearMiss = Array.Empty<object>(),
                            @unchecked = Array.Empty<object>(),
                            mode = (string?)null,
                        },
                        statusCode: StatusCodes.Status202Accepted);
                }

                await db.SaveChangesAsync(ct);
            }

            var replay = await ReplayAsync(db, eligibility, scorer, accountId, [turn], ct);
            return Results.Ok(new
            {
                status = turn.Status,
                reason = replay.Reason,
                // Beside `reason`, on the same terms: a client that reconnects to a turn gets the
                // whole answer, and the notice is part of the answer.
                conflict = replay.Conflict,
                failureCode = turn.FailureCode,
                cards = replay.Cards,
                // A client that lost the stream lost the tray with it. Reconnecting has to restore
                // the whole answer, not the half of it that cleared the bar.
                belowFloor = replay.BelowFloor,
                nearMiss = replay.NearMiss ?? [],
                @unchecked = replay.Unchecked ?? [],
                mode = replay.Mode,
                removed = replay.Removed,
                removedEarlier = replay.RemovedEarlier,
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
    /// Everything a client is allowed to say about a turn.
    ///
    /// <para><b>What is deliberately absent is the point.</b> <c>History</c>, <c>ShownTmdbIds</c>
    /// and <c>Continuation</c> were all here and are all gone: the transcript, the no-repeat set
    /// and the ask-versus-page decision are now derived server-side from
    /// <c>SearchTurns</c> / <c>SearchTurnCards</c>. A client cannot carry state it loses on reload,
    /// cannot be trusted about what it has already been shown, and cannot tell a filter toggle from
    /// a new question — and every one of those showed up as the product forgetting itself.</para>
    ///
    /// <para><b>Both ids are strings, and that is not laziness.</b> Bound as <c>Guid?</c> /
    /// <c>Guid</c> they were parsed by the JSON binder, BEFORE the handler ran — so a non-UUID, an
    /// over-long key, a JSON <c>null</c> or an empty string skipped every coded refusal the
    /// contract documents and came back as a <c>text/plain</c> <c>BadHttpRequestException</c>
    /// naming this very type. A client switching on <c>code</c>, as the contract tells it to, got
    /// <c>undefined</c> and rendered a crash message for its own typo. Parsing here means the
    /// refusal is the handler's, in the house shape, and the UUID requirement is stated in
    /// <c>docs/ask-reel-turns.md</c> rather than discovered through a stack trace.</para>
    ///
    /// <para><b>And the body is bound BY HAND, for the same reason.</b> Making the ids strings only
    /// moved the leak: the JSON binder is bypassed for a string, and a <c>clientTurnId</c> of any
    /// other JSON type — a number, an object — still failed inside it and still returned five
    /// kilobytes of <c>text/plain</c> <c>BadHttpRequestException</c> with this type's internal name
    /// and no <c>code</c>. <see cref="BindAsync"/> reads the body as a <c>JsonDocument</c> and picks
    /// each field out itself, so every malformed SHAPE lands on the same coded 400 the contract
    /// documents instead of on a stack trace a type-confused client cannot act on.</para>
    /// </summary>
    /// <param name="Query">The question, 1–<see cref="AskRequestLimits.MaxQueryChars"/> characters trimmed.</param>
    /// <param name="ConversationId">The thread this belongs to, as a UUID string; null mints a new one. An unknown id is a 404, never a fork; an unparseable one is a 400.</param>
    /// <param name="ClientTurnId">The client's id for THIS exchange, as a UUID string — the idempotency key. Resent verbatim on a retry.</param>
    /// <param name="MediaTypes">An explicit UI filter, which outranks whatever the model infers from the prose. Every token must be one <see cref="AskMediaTypes"/> knows.</param>
    /// <param name="PageOf">
    /// The assistant turn this submission is another PAGE of, as a UUID string; null leaves the
    /// decision to the server's own comparison. It exists because the comparison cannot always be
    /// right: after a Stop, the conversation's last user turn is the stopped question, so "Show me
    /// more" on the kept answer was recorded as a new ask. The client is rendering the answer it is
    /// extending, so it knows; the server verifies rather than trusts.
    /// </param>
    /// <param name="Malformed">
    /// Fields whose JSON type the contract does not allow, in the order the handler refuses them.
    /// Populated by <see cref="BindAsync"/> and never by a client — the wire has no such field.
    /// </param>
    public record AskRequest(
        string? Query,
        string? ConversationId,
        string? ClientTurnId,
        List<string?>? MediaTypes,
        string? PageOf = null,
        IReadOnlyList<string>? Malformed = null)
    {
        /// <summary>The malformed-field list, never null, in the order the handler refuses them.</summary>
        public IReadOnlyList<string> Malformed { get; init; } = Malformed ?? [];

        /// <summary>
        /// The body, read as JSON and unpacked by hand.
        ///
        /// <para>Returns <c>null</c> only when the payload is not a JSON OBJECT at all — unparseable
        /// bytes, a bare string, an array — which the handler answers as <c>body_invalid</c>. Every
        /// other disagreement with the contract is carried on <see cref="Malformed"/> so the handler
        /// can refuse it in the documented order, with the documented code.</para>
        ///
        /// <para>An absent field and a JSON <c>null</c> are the same thing here, deliberately: both
        /// already have coded answers further down (<c>empty_query</c>,
        /// <c>client_turn_id_required</c>, a fresh conversation), and collapsing them keeps those
        /// answers reachable. A field of the WRONG TYPE is the new case, and it is the one that used
        /// to escape the handler entirely.</para>
        /// </summary>
        public static async ValueTask<AskRequest?> BindAsync(HttpContext context)
        {
            System.Text.Json.JsonDocument document;
            try
            {
                var parsed = await System.Text.Json.JsonDocument.ParseAsync(
                    context.Request.Body, cancellationToken: context.RequestAborted);
                document = parsed;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return null;
                }

                // Read in the order the handler refuses them, so the FIRST malformed field a client
                // is told about is the first one it would have been told about anyway.
                var malformed = new List<string>();
                var query = StringField(document.RootElement, "query", malformed);
                var clientTurnId = StringField(document.RootElement, "clientTurnId", malformed);
                var conversationId = StringField(document.RootElement, "conversationId", malformed);
                var pageOf = StringField(document.RootElement, "pageOf", malformed);
                var mediaTypes = MediaTypesField(document.RootElement, malformed);

                return new AskRequest(query, conversationId, clientTurnId, mediaTypes, pageOf, malformed);
            }
        }

        /// <summary>The prose and the stable code for one malformed field.</summary>
        public static (string Error, string Code) RefusalFor(string field) => field switch
        {
            "query" => ("That question wasn't text I could read — try typing it again.", "query_invalid"),
            "conversationId" => ("That thread id isn't a valid id — start a new one.", "conversation_id_invalid"),
            "clientTurnId" => (
                "That request's turn id isn't a valid id — refresh the page and try again.",
                "client_turn_id_invalid"),
            "pageOf" => ("That answer id isn't a valid id — ask the question again instead.", "page_of_invalid"),
            // Deliberately not AskMediaTypes.Refusal: that sentence names the offending TOKEN,
            // and there is no token here — the container itself was the wrong shape.
            "mediaTypes" => (
                $"The filter list has to be a list. Use any of: {string.Join(", ", AskMediaTypes.Tokens)}.",
                "media_type_invalid"),
            _ => ("That request wasn't something I could read — refresh the page and try again.", "body_invalid"),
        };

        /// <summary>
        /// One string field. Absent and <c>null</c> both answer null; anything that is not a JSON
        /// string is recorded as malformed, in the order the handler refuses fields.
        /// </summary>
        private static string? StringField(
            System.Text.Json.JsonElement root, string name, List<string> malformed)
        {
            if (!root.TryGetProperty(name, out var value)
                || value.ValueKind == System.Text.Json.JsonValueKind.Null
                || value.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                return null;
            }

            if (value.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                malformed.Add(name);
                return null;
            }

            return value.GetString();
        }

        /// <summary>
        /// <c>mediaTypes</c>, which must be an array. Its ELEMENTS are not validated here — a
        /// non-string element becomes a null entry and falls through to
        /// <see cref="AskMediaTypes"/>'s own refusal, which is already coded and already names the
        /// six tokens. Only the container's type is this method's business.
        /// </summary>
        private static List<string?>? MediaTypesField(
            System.Text.Json.JsonElement root, List<string> malformed)
        {
            if (!root.TryGetProperty("mediaTypes", out var value)
                || value.ValueKind == System.Text.Json.JsonValueKind.Null
                || value.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                return null;
            }

            if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                malformed.Add("mediaTypes");
                return null;
            }

            return value.EnumerateArray()
                .Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null)
                .ToList();
        }
    }

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
    /// informational: nothing the client sends back is trusted for it. It counts the TRAY, because
    /// a card under the fence is a card on the glass.</para>
    ///
    /// <para><c>belowFloor</c> restores that tray, separately from <c>cards</c> and never merged
    /// into it. <c>lastTurnAt</c> is beside <c>lastCardsAt</c> because a transcript-only thread has
    /// a null <c>lastCardsAt</c> by definition and would otherwise carry no date at all.</para>
    /// </summary>
    /// <summary>How many of a thread's newest answers a resume returns cards for, per turn.</summary>
    private const int ResumedTurnCards = 12;

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

        // The grid a resume restores is the newest SEARCH answer and its pages. An item question, a
        // count or a clarifying question (v2 `Answer` / `Clarify`) answers beside the grid and shows no
        // cards, so it never becomes the grid: resuming after "is the first one violent?" restores the
        // answer the question was about, exactly as the live screen kept it — not an empty grid.
        var lastAsk = turns.FindLastIndex(t =>
            t.Role == "assistant"
            && t.Kind == Domain.Search.SearchTurnKind.Ask
            && t.Status == Domain.Search.SearchTurnStatus.Completed
            && !Domain.Search.SearchTurnMode.LeavesGrid(t.Mode));
        var block = lastAsk < 0
            ? []
            : turns.Skip(lastAsk)
                .Where(t => t.Role == "assistant"
                            && t.Status == Domain.Search.SearchTurnStatus.Completed
                            && !Domain.Search.SearchTurnMode.LeavesGrid(t.Mode))
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

        // Every answer's own cards (§5.2 WS-2 task 3: "resume returns turns[].cards"), so a resumed
        // thread renders each turn's cards under its own bubble instead of only the newest block. Each
        // answer is replayed on its own through the same projection — the same eligibility re-filter,
        // the same tier split — so a card the account has since seen drops out of every turn alike.
        // The newest answers only: a long thread's oldest turns render as transcript.
        var perTurn = new Dictionary<Guid, ReplayedTurns>();
        foreach (var answer in turns
                     .Where(t => t.Role == "assistant"
                                 && (t.Status == Domain.Search.SearchTurnStatus.Completed
                                     || t.FailureCode == Domain.Search.SearchTurnFailure.ClientAborted))
                     .TakeLast(ResumedTurnCards))
        {
            perTurn[answer.Id] = await ReplayAsync(db, eligibility, scorer, accountId, [answer], ct);
        }

        return new
        {
            id = conversation.Id,
            title = conversation.Title,
            // Null says "transcript only" — this thread predates stored results, which is a
            // different fact from a turn that honestly returned nothing.
            lastCardsAt = conversation.LastCardsAt,
            // When this thread was last spoken in — the ONLY date a transcript-only thread has.
            // lastCardsAt is null for exactly those threads, so without this field a resumed
            // conversation that predates stored results cannot be dated at all, and the client was
            // reduced to rendering an undated seam. It is the same column the Recent list already
            // sorts and renders on, so the two views cannot disagree about a thread's age.
            lastTurnAt = conversation.LastTurnAt,
            turns = turns.Select(t => new
            {
                // The row's own id, so a transcript row is something the client can ASK about
                // rather than only render. A Pending row is the case that needs it: polling
                // GET /api/search/turns/{id} is the entire reconnect path, and without an id here
                // a resumed thread could see a turn still running and had no handle to name it.
                id = t.Id,
                sequence = t.Sequence,
                role = t.Role,
                text = t.Text,
                kind = t.Kind,
                status = t.Status,
                // Why it failed, and ONLY when it failed. `client_aborted` is the user's own stop
                // and reads as "you ended this"; anything else is a snag and reads as "try again".
                // Without the code both render identically, so the page either apologises for
                // something the user did deliberately or says nothing about a real failure.
                //
                // Read through the status rather than straight off the column, so a code can never
                // outlive the failure it described — a stale cause is worse than no cause, because
                // it reads as the current one.
                failureCode = t.Status == Domain.Search.SearchTurnStatus.Failed ? t.FailureCode : null,
                // Whether this row's reply is the degraded copy — a turn whose reading failed — so a
                // resumed thread can show it as degraded rather than as an ordinary answer.
                degraded = Domain.Search.SearchTurnDegraded.PlanFailed(t.DegradedFlags),
                // Whether a judge batch failed, so the screen can say some cards went unchecked.
                judgeDegraded = Domain.Search.SearchTurnDegraded.JudgeFailed(t.DegradedFlags),
                mode = t.Role == "assistant" ? Domain.Search.SearchTurnMode.Parse(t.Mode) : null,
                // Ask Reel v2's act (null on v1 rows and user rows).
                act = t.Act,
                // This answer's own cards and trays; null for a user row, a failure, or an answer
                // older than the newest ResumedTurnCards.
                cards = perTurn.TryGetValue(t.Id, out var own) ? own.Cards : null,
                // The trays NESTED under `trays`, as the chat UI reads them (R1-31): flat on the row,
                // `unchecked` collided with the row's own judge-degraded flag in the client's turn
                // shape, and the client — built against the nested contract — read nothing, so an
                // earlier answer lost "· N in its trays" on every resume.
                trays = perTurn.TryGetValue(t.Id, out var ownTrays)
                    ? new
                    {
                        belowFloor = ownTrays.BelowFloor,
                        nearMiss = ownTrays.NearMiss ?? [],
                        @unchecked = ownTrays.Unchecked ?? [],
                    }
                    : null,
                reason = perTurn.TryGetValue(t.Id, out var ownReason) ? ownReason.Reason : null,
            }),
            cards = replay.Cards,
            // The tray, restored as a tray. It is never merged into `cards` — these cards did not
            // clear the account's bar and the grid is not where they belong — but it has to come
            // back, because the user left it open and because the server now counts it in the
            // no-repeat set. A resume that dropped it would show a thread whose "Show the N under
            // your bar" fence had silently emptied.
            belowFloor = replay.BelowFloor,
            nearMiss = replay.NearMiss ?? [],
            @unchecked = replay.Unchecked ?? [],
            mode = replay.Mode,
            reason = replay.Reason,
            // The collision notice the answer was produced under, restored with the answer. Null on
            // a thread that had no collision, which is most of them. Without it a resumed screen
            // full of post-2000 dramas under the question "a 90s romantic comedy" had nothing left
            // on it saying why — the continuity failure the founder complained about, applied to
            // the explanation rather than to the cards.
            conflict = replay.Conflict,
            removed = replay.Removed,
            removedEarlier = replay.RemovedEarlier,
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
    /// <param name="Cards">The stored PICKS, re-filtered and re-scored, in stored order.</param>
    /// <param name="BelowFloor">
    /// The stored TRAY — the cards the account's rating floor put under the fence — re-filtered and
    /// re-scored on identical terms, in stored order. Kept separate all the way out: merging it into
    /// <paramref name="Cards"/> would promote cards the floor rejected into the grid, which is the
    /// one thing the floor exists to prevent.
    /// </param>
    /// <param name="Removed">
    /// How many stored cards the eligibility re-filter dropped, across BOTH lists. One number,
    /// because the user is owed one honest account of what disappeared from the answer, and a card
    /// that vanished out of the tray is as invisible as one that vanished out of the grid.
    /// </param>
    /// <param name="Reason">The reason line of the newest turn in the block, verbatim.</param>
    /// <param name="Conflict">
    /// The newest turn's stored collision notice, as JSON, or null where it had none. It travels
    /// beside <paramref name="Reason"/> and for the same reason: both are what the user was TOLD,
    /// and both would be wrong if re-derived from conditions that have since changed.
    /// </param>
    /// <param name="NearMiss">The stored near-miss tray, on the same terms as the floor tray.</param>
    /// <param name="Mode">The newest turn's mode — <c>Picks</c> or <c>NoneFound</c>.</param>
    /// <param name="Unchecked">The stored "not checked against your request" tray, on the same terms.</param>
    /// <param name="RemovedEarlier">
    /// How many of <paramref name="Removed"/> the account had ALREADY rated, watched, saved or
    /// dismissed when the answer was given — a person query shows seen titles (FD-3's lookup), so a
    /// resumed Nolan answer drops cards rated years before it. The client says "since" only of the
    /// rest (R1-11): "you've since rated" about a 2020 rating under a 2026 answer was false.
    /// </param>
    private sealed record ReplayedTurns(
        IReadOnlyList<object> Cards, IReadOnlyList<object> BelowFloor, int Removed, string? Reason,
        object? Conflict = null, IReadOnlyList<object>? NearMiss = null, string? Mode = null,
        IReadOnlyList<object>? Unchecked = null, int RemovedEarlier = 0);

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
    ///
    /// <para><b>The tray comes back as the tray.</b> Cards stored with
    /// <c>SearchTurnCard.BelowFloor</c> are partitioned out AFTER the shared sort and AFTER the
    /// eligibility re-filter, so they are governed by exactly the same founder-locked rule and one
    /// rank ordering — and they never reach <c>cards</c>. A restored tray is what makes a resumed
    /// screen the screen the user left; promoting it into the grid would show cards under a bar
    /// they did not clear.</para>
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
            return new ReplayedTurns([], [], 0, null, NearMiss: []);
        }

        var mode = Domain.Search.SearchTurnMode.Parse(turns[^1].Mode);

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
            return new ReplayedTurns([], [], 0, turns[^1].Reason, ConflictOf(turns[^1].ConflictJson), [], mode);
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
        var removedEarlier = removed == 0
            ? 0
            : await RemovedEarlierAsync(
                db, accountId, stored.Where(c => interacted.Contains(c.TitleId)).ToList(),
                turns.ToDictionary(t => t.Id, t => t.CreatedAt), ct);

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

        // ONE projection for both lists. A tray card renders with the same fields as a pick — it is
        // the same card, on the other side of a fence — and a second copy of this object shape is a
        // second place for the two to drift apart.
        object Project(Domain.Search.SearchTurnCard c)
        {
            {
                var title = titles[c.TitleId];
                return new
                {
                    titleId = title.Id,
                    mediaType = title.MediaType.ToString(),
                    tmdbId = title.TmdbId,
                    name = title.Name,
                    year = title.Year,
                    posterPath = title.PosterPath,
                    genres = title.Genres,
                    similarity = c.Similarity,
                    predictedRating = rescored.TryGetValue(c.TitleId, out var fresh) ? ForYouBar.Shown(fresh)
                        : c.PredictedRating is { } stored ? ForYouBar.Shown(stored) : (decimal?)null,
                    crowdRating = CrowdRating.For(title),
                    crowdVotes = CrowdRating.VotesFor(title),
                    // A fit is shown only where the model produced it. Rows older than FitSource carry
                    // no source and are shown as they were; a Fallback-sourced fit never is.
                    fit = c.LlmFit is { } llmFit && c.FitSource != Domain.Ports.ResultSource.Fallback.ToString()
                        ? (double?)llmFit
                        : null,
                    // The deterministic stub's sentence was stored as model output on thousands of
                    // historical rows (B-6), and lines written before today's guards still carry what
                    // a new turn discards (IR1S-05). Rows are never edited (FD-18); the replay simply
                    // stops showing a sentence a new turn would not show.
                    why = ReplayableWhy(c.Why, c.Satisfies),
                    seen = seen.Contains(c.TitleId),
                    blend = (double?)c.Blend,
                    outsideRequest = c.OutsideRequest,
                    satisfies = c.Satisfies,
                };
            }
        }

        // Partitioned AFTER the sort and AFTER the eligibility re-filter, so both lists carry one
        // ordering and one guard. `removed` deliberately spans both: it is the count of cards that
        // left the answer, and the tray is part of the answer.
        // By TIER, read through SearchTurnCardTier.Of so a row older than the column is read from
        // its BelowFloor flag and an unknown tier is never promoted into the grid.
        return new ReplayedTurns(
            survivors.Where(c => Domain.Search.SearchTurnCardTier.Of(c) == Domain.Search.SearchTurnCardTier.Pick)
                .Select(Project).ToList(),
            survivors.Where(c => Domain.Search.SearchTurnCardTier.Of(c) == Domain.Search.SearchTurnCardTier.BelowFloor)
                .Select(Project).ToList(),
            removed,
            turns[^1].Reason,
            ConflictOf(turns[^1].ConflictJson),
            survivors.Where(c => Domain.Search.SearchTurnCardTier.Of(c) == Domain.Search.SearchTurnCardTier.NearMiss)
                .Select(Project).ToList(),
            mode,
            survivors.Where(c => Domain.Search.SearchTurnCardTier.Of(c) == Domain.Search.SearchTurnCardTier.Unchecked)
                .Select(Project).ToList(),
            removedEarlier);
    }

    /// <summary>
    /// How many of the cards the re-filter dropped carry an interaction Reel knew of BEFORE the answer
    /// that showed them: a rating first seen or dated before it, a watch synced or dated before it, or
    /// a save, drop or not-interested made before it. Everything else was acted on since. Per card,
    /// against its own turn's time, because a block can span several answers.
    /// </summary>
    private static async Task<int> RemovedEarlierAsync(
        IReelDbContext db, Guid accountId, IReadOnlyList<Domain.Search.SearchTurnCard> removedCards,
        IReadOnlyDictionary<Guid, DateTime> turnTimes, CancellationToken ct)
    {
        var ids = removedCards.Select(c => c.TitleId).Distinct().ToList();
        var known = new Dictionary<Guid, DateTime>();
        void Earliest(Guid titleId, DateTime at)
        {
            known[titleId] = known.TryGetValue(titleId, out var seen) && seen <= at ? seen : at;
        }

        foreach (var r in await db.UserRatings
                     .Where(r => r.AccountId == accountId && ids.Contains(r.TitleId))
                     .Select(r => new { r.TitleId, r.RatedAt, r.FirstRatedAt })
                     .ToListAsync(ct))
        {
            Earliest(r.TitleId, r.RatedAt < r.FirstRatedAt ? r.RatedAt : r.FirstRatedAt);
        }

        foreach (var w in await db.WatchedTitles
                     .Where(w => w.AccountId == accountId && ids.Contains(w.TitleId))
                     .Select(w => new { w.TitleId, w.FirstSyncedAt, w.LastWatchedAt })
                     .ToListAsync(ct))
        {
            Earliest(w.TitleId, w.LastWatchedAt is { } watched && watched < w.FirstSyncedAt ? watched : w.FirstSyncedAt);
        }

        foreach (var x in await db.UserTitleReactions
                     .Where(x => x.AccountId == accountId && x.RevokedAt == null && ids.Contains(x.TitleId)
                         && (x.Kind == ReactionKind.NotInterested || x.Kind == ReactionKind.SaveForLater || x.Kind == ReactionKind.Dropped))
                     .Select(x => new { x.TitleId, x.CreatedAt })
                     .ToListAsync(ct))
        {
            Earliest(x.TitleId, x.CreatedAt);
        }

        return removedCards.Count(c =>
            known.TryGetValue(c.TitleId, out var at)
            && turnTimes.TryGetValue(c.TurnId, out var answeredAt)
            && at < answeredAt);
    }

    /// <summary>
    /// A stored why-line as a resumed thread may show it, or null where the card falls back to its
    /// own deterministic line — under exactly the rule a new turn's rerank applies
    /// (<c>OpenRouterSearchAgent.RejectsWhy</c>): the voice guard always, and the hedge guard
    /// wherever the line captions a match (a "yes" verdict, or a row from before verdicts existed).
    /// A partial or no verdict keeps its honest negative — on the near-miss and outside-request
    /// channels it is the point.
    ///
    /// <para><b>Why at replay (IR1S-05).</b> New turns were guarded and the golden eval scores only
    /// new turns, but 191 of 697 stored why-lines on one slot matched the hedge pattern, and
    /// opening Recent rendered them on pick cards — "…, though the show leans on mystery". The
    /// stored row is evidence and is never edited; the projection is where the screen is decided.</para>
    /// </summary>
    internal static string? ReplayableWhy(string? why, string? satisfies)
    {
        if (string.IsNullOrWhiteSpace(why) || LegacyStubWhy(why) || Domain.Ports.SecondPersonVoice.Rejects(why))
        {
            return null;
        }

        var captionsAMatch = satisfies is null || string.Equals(satisfies, "yes", StringComparison.OrdinalIgnoreCase);
        return captionsAMatch && Domain.Ports.SecondPersonVoice.SoundsHedged(why) ? null : why;
    }

    /// <summary>
    /// The deterministic stub's why-line — "A action pick aligned with your taste." — as it was stored,
    /// labelled model output, before the typed sources existed.
    /// </summary>
    private static bool LegacyStubWhy(string? why) =>
        why is { Length: > 0 }
        && why.StartsWith("A", StringComparison.Ordinal)
        && why.EndsWith(" pick aligned with your taste.", StringComparison.Ordinal);

    /// <summary>
    /// The idempotent retry's answer: the stored turn, on the wire, in the same SSE grammar a live
    /// turn uses — so the client has exactly one parser and one code path. Zero model calls, zero
    /// new rows, zero exposures.
    /// </summary>
    /// <summary>
    /// The QUESTION a stored assistant turn answers — the newest user row before it.
    ///
    /// <para>The assistant row's own <c>Text</c> is Reel's reply, not the question, and a
    /// <c>page</c> turn has no user row of its own at all: it answers the question already asked.
    /// So the lookup is "the last user turn at or before this sequence", which is the same rule the
    /// transcript renders by and the same one <c>SearchTurnKind.Derive</c> compares against. Null
    /// where the conversation has no user row — a shape nothing writes today, and a null is read as
    /// "cannot tell", which lets the replay proceed rather than refusing on a guess.</para>
    /// </summary>
    /// <summary>
    /// A stored <c>ConflictJson</c> as a JSON OBJECT on the wire, not as a string.
    ///
    /// <para>The column is <c>jsonb</c> and the client reads fields off it; handing back a string
    /// would make every consumer parse it a second time, and a double-encoded payload is the kind of
    /// thing that works until somebody's notice renders as a quoted blob. <c>Clone()</c> detaches
    /// the element from the document being disposed. A malformed row degrades to null rather than
    /// throwing — a resume must not fail because one historical row cannot be parsed.</para>
    /// </summary>
    private static object? ConflictOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject notice)
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }

            // R1-29: a notice stored before the fix calls the conditions PERMANENT, which they never
            // were (they are editable behind the PIN). The row is history and stays as it is; what a
            // resume hands back is the notice as the product states it now.
            if (notice["message"] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var message)
                && message.Contains(LegacyPermanentNotice, StringComparison.Ordinal))
            {
                notice["message"] = message.Replace(LegacyPermanentNotice, "Your conditions exclude", StringComparison.Ordinal);
            }

            return notice;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The pre-R1-29 opening of a stored conflict notice, rewritten on every read.</summary>
    private const string LegacyPermanentNotice = "Your conditions permanently exclude";

    /// <summary>How many earlier answers' cards the model may refer back to.</summary>
    private const int PresentedAnswers = 3;

    /// <summary>
    /// The cards earlier answers put on screen, each with its handle: the answer's number (1-based
    /// over assistant rows in sequence order) and the card's on-screen position. An answer with picks
    /// is referred to by its picks (and outside cards) — stored rank plus one — and its trays sit behind
    /// fences that "the second one" does not reach into. An answer that showed NO picks is referred to
    /// by its tray cards, in the order the screen renders the trays (<see cref="HandleCards"/>),
    /// because they are the only thing it showed: "more like the second one you showed me first time
    /// round" after a none-found answer means its second tray card, and with no handle for it the
    /// model searched for "you", "showed" and "round" instead (R1-04).
    ///
    /// <para>The last <see cref="PresentedAnswers"/> answers with picks, and separately the last
    /// <see cref="PresentedAnswers"/> tray-only answers, so a run of none-found answers can never
    /// push the last real picks out of the model's handles (D8).</para>
    ///
    /// <para>A turn the client aborted counts: its cards were on screen (B-3). A failed turn's
    /// cards never were, and none are stored.</para>
    /// </summary>
    private static async Task<IReadOnlyList<PresentedCard>> PresentedAsync(
        IReelDbContext db, Guid conversationId, int beforeSequence, CancellationToken ct)
    {
        var assistantRows = await db.SearchTurns
            .Where(t => t.ConversationId == conversationId && t.Role == "assistant" && t.Sequence < beforeSequence)
            .OrderBy(t => t.Sequence)
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (assistantRows.Count == 0)
        {
            return [];
        }

        var answerNumber = assistantRows.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index + 1);

        // The tier is read through SearchTurnCardTier.Of in memory — a row older than the column has
        // no tier to query on.
        var byAnswer = (await db.SearchTurnCards
                .Where(c => c.ConversationId == conversationId)
                .Join(db.Titles, c => c.TitleId, t => t.Id, (c, t) => new ShownCard(c, t.Name, t.Year, t.MediaType))
                .ToListAsync(ct))
            .Where(x => answerNumber.ContainsKey(x.Card.TurnId))
            .GroupBy(x => x.Card.TurnId)
            .Select(g => (Number: answerNumber[g.Key], HasPicks: g.Any(IsPick), Cards: HandleCards(g, answerNumber[g.Key])))
            .Where(a => a.Cards.Count > 0)
            .ToList();
        IEnumerable<PresentedCard> Recent(bool withPicks) => byAnswer
            .Where(a => a.HasPicks == withPicks)
            .OrderByDescending(a => a.Number)
            .Take(PresentedAnswers)
            .SelectMany(a => a.Cards);
        return Recent(withPicks: true)
            .Concat(Recent(withPicks: false))
            .OrderByDescending(p => p.Turn)
            .ThenBy(p => p.Rank)
            .ToList();
    }

    /// <summary>A stored card with what a handle line prints.</summary>
    private sealed record ShownCard(Domain.Search.SearchTurnCard Card, string Name, int? Year, MediaType Media);

    private static bool IsPick(ShownCard x) =>
        Domain.Search.SearchTurnCardTier.Of(x.Card) == Domain.Search.SearchTurnCardTier.Pick;

    /// <summary>The order the screen renders the trays in: the floor tray, the near misses, the unchecked.</summary>
    private static int TrayOrder(string tier) => tier switch
    {
        Domain.Search.SearchTurnCardTier.BelowFloor => 0,
        Domain.Search.SearchTurnCardTier.NearMiss => 1,
        _ => 2,
    };

    /// <summary>
    /// One answer's cards as the model may refer to them: its picks by stored rank when it has any,
    /// otherwise its tray cards in on-screen order — at most <see cref="AskReelHandles.MaxHistoryHandles"/>
    /// of those, so one long tray never crowds the prompt's handle list.
    /// </summary>
    private static List<PresentedCard> HandleCards(IEnumerable<ShownCard> ofOneAnswer, int answer)
    {
        var cards = ofOneAnswer.ToList();
        var picks = cards.Where(IsPick).ToList();
        if (picks.Count > 0)
        {
            return picks
                .Select(x => new PresentedCard(answer, x.Card.Rank + 1, x.Card.TitleId, x.Name, x.Year, x.Media))
                .ToList();
        }

        return cards
            .OrderBy(x => TrayOrder(Domain.Search.SearchTurnCardTier.Of(x.Card)))
            .ThenBy(x => x.Card.Rank)
            .Take(AskReelHandles.MaxHistoryHandles)
            .Select((x, index) => new PresentedCard(answer, index + 1, x.Card.TitleId, x.Name, x.Year, x.Media))
            .ToList();
    }

    /// <summary>
    /// The previous answer's reading of the request — the newest assistant row before this turn that
    /// stored one and whose plan did not fail — for what a follow-up inherits when its own words are
    /// silent (AskReelCarry). Null on a first turn or an unreadable row.
    /// </summary>
    private static async Task<Domain.Ports.SearchIntent?> PriorIntentAsync(
        IReelDbContext db, Guid conversationId, int beforeSequence, CancellationToken ct)
    {
        var rows = await db.SearchTurns
            .Where(t => t.ConversationId == conversationId
                && t.Role == "assistant"
                && t.Sequence < beforeSequence
                && t.IntentJson != null)
            .OrderByDescending(t => t.Sequence)
            .Select(t => new { t.IntentJson, t.DegradedFlags })
            .Take(AskHistoryTurns)
            .ToListAsync(ct);
        var row = rows.FirstOrDefault(r => !Domain.Search.SearchTurnDegraded.PlanFailed(r.DegradedFlags));
        return row is null ? null : AskReelCarry.Parse(row.IntentJson);
    }

    /// <summary>
    /// The transcript the model reads: the last <see cref="AskHistoryTurns"/> rows that said
    /// something true, oldest first, each assistant line carrying the handles of the cards it showed.
    ///
    /// <para><b>What is left out, and why.</b> A degraded reply is never read back (B-5): it is the
    /// degraded copy, not an answer, and feeding it to the next prompt taught the model that "I
    /// couldn't think that one through" is a thing it says. Neither is the legacy stub template
    /// ("Here are some action picks for “…”") that thousands of historical rows still carry — those
    /// rows are never edited (FD-18), only no longer quoted. A turn the CLIENT ended is read back,
    /// because its reply and its cards were on screen (B-3); any other failure said nothing.</para>
    /// </summary>
    private static async Task<List<Domain.Ports.ChatTurn>> HistoryAsync(
        IReelDbContext db, Guid conversationId, int beforeSequence, CancellationToken ct)
    {
        var rows = (await db.SearchTurns
                .Where(t => t.ConversationId == conversationId
                    && t.Sequence < beforeSequence
                    && t.Text != string.Empty
                    && (t.Status == Domain.Search.SearchTurnStatus.Completed
                        || (t.Status == Domain.Search.SearchTurnStatus.Failed
                            && t.FailureCode == Domain.Search.SearchTurnFailure.ClientAborted)))
                .OrderByDescending(t => t.Sequence)
                .Take(AskHistoryTurns * 2)
                .Select(t => new { t.Id, t.Role, t.Text, t.DegradedFlags, t.Sequence })
                .ToListAsync(ct))
            .Where(t => t.Role != "assistant"
                || (!Domain.Search.SearchTurnDegraded.PlanFailed(t.DegradedFlags) && !LegacyStubReply(t.Text)))
            .Take(AskHistoryTurns)
            .Reverse()
            .ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        // The handles each answer showed, by answer number — the same numbering PresentedAsync uses.
        var assistantRows = await db.SearchTurns
            .Where(t => t.ConversationId == conversationId && t.Role == "assistant" && t.Sequence < beforeSequence)
            .OrderBy(t => t.Sequence)
            .Select(t => t.Id)
            .ToListAsync(ct);
        var answerNumber = assistantRows.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index + 1);
        var assistantIds = rows.Where(r => r.Role == "assistant").Select(r => r.Id).ToList();
        // The same handles PresentedAsync gives each answer — its picks, or the trays of an answer
        // that showed none — so the transcript and the handle list never number a card differently.
        var shownByTurn = (await db.SearchTurnCards
                .Where(c => assistantIds.Contains(c.TurnId))
                .Join(db.Titles, c => c.TitleId, t => t.Id, (c, t) => new ShownCard(c, t.Name, t.Year, t.MediaType))
                .ToListAsync(ct))
            .GroupBy(x => x.Card.TurnId)
            .ToDictionary(g => g.Key, g => HandleCards(g, answerNumber.GetValueOrDefault(g.Key)));

        // Whitelisted roles: the role reaches the provider's messages array, and "system" there is a
        // different instruction set. The column holds only "user" and "assistant" today; this keeps
        // the guarantee local to the place the prompt is built.
        return rows
            .Select(t => t.Role == "assistant"
                ? new Domain.Ports.ChatTurn(
                    "assistant",
                    t.Text + (shownByTurn.TryGetValue(t.Id, out var cards) ? AskReelHandles.HistorySuffix(cards) : string.Empty))
                : new Domain.Ports.ChatTurn("user", t.Text))
            .ToList();
    }

    /// <summary>
    /// The exposures of a turn the client ended (R1-12). Decision: a pick that was streamed onto the
    /// screen IS an exposure, aborted or not. An exposure records that the product recommended a title
    /// at a rank, and that happened the moment the card went out; the same cards already enter the
    /// conversation's history and no-repeat set for exactly that reason (B-3). The live turn writes its
    /// exposures after <c>done</c>, which an aborted turn never reaches — so the aborted "Salting the
    /// Battlefield" pick was on screen and missing from the log.
    ///
    /// <para>Same rules as the finished turn: Pick tier only (no tray is a recommendation), a card
    /// with no prediction is not logged, the why-source is the typed one the card was stored with,
    /// the account's active artifact, keyed on the turn. Written only when the turn has no exposure
    /// rows yet, so a turn cancelled while its own write was in flight is never logged twice.</para>
    /// </summary>
    private static async Task RecordAbortedExposuresAsync(
        IServiceProvider services, IReelDbContext db, Guid accountId, Guid turnId,
        IReadOnlyList<LiveSearchExpansionService.TurnCard> cards)
    {
        var shown = cards
            .Where(c => c.Tier == Domain.Search.SearchTurnCardTier.Pick && c.PredictedRating is not null)
            .ToList();
        if (shown.Count == 0
            || await db.ExposureEvents.AnyAsync(e => e.SourceTurnId == turnId, CancellationToken.None))
        {
            return;
        }

        var artifactId = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(CancellationToken.None);
        if (artifactId is not { } activeArtifactId)
        {
            return;
        }

        await services.GetRequiredService<Application.Ml.ClosedLoop.ExposureLog>().RecordAsync(
            accountId,
            Domain.Feed.ExposureSurface.AskReel,
            feedSnapshotId: null,
            activeArtifactId,
            DateTime.UtcNow,
            shown.Select(c => new Application.Ml.ClosedLoop.ExposureCard(
                    c.TitleId,
                    c.Rank,
                    c.PredictedRating!.Value,
                    LlmFit: c.LlmFit,
                    WhySentence: c.Why,
                    WhySource: Application.Ml.ClosedLoop.ExposureWhySource.Parse(c.WhySource)))
                .ToList(),
            CancellationToken.None,
            turnId);
    }

    /// <summary>The legacy stub reply — "Here are some X picks for “…”" — as it was stored.</summary>
    private static bool LegacyStubReply(string text) =>
        text.StartsWith("Here are some ", StringComparison.Ordinal) && text.Contains(" picks for “", StringComparison.Ordinal);

    /// <summary>
    /// The `phase` heartbeat (A-6, B-10): whenever the stream has been silent for
    /// <see cref="HeartbeatAfter"/>, the last phase is sent again, marked <c>heartbeat: true</c> so the
    /// client re-arms its watchdog without re-sorting anything. The `:keepalive` comment keeps the
    /// proxies awake; this keeps the CLIENT awake, through the parser that comments never reach.
    /// </summary>
    private static async Task HeartbeatAsync(
        AskTurnProgress progress, LiveSearchExpansionService.Emit emit, System.Diagnostics.Stopwatch lastFrameAt,
        CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (lastFrameAt.Elapsed < HeartbeatAfter)
                {
                    continue;
                }

                // The last phase again, verbatim, plus the marker — so a client that renders the
                // phase's counts renders the same counts, and one that sorts at phase boundaries can
                // tell this is not one.
                var beat = new Dictionary<string, object?> { ["stage"] = "searching", ["found"] = 0, ["scored"] = 0 };
                if (progress.LastPhase is { } last)
                {
                    foreach (var property in System.Text.Json.JsonSerializer.SerializeToElement(last).EnumerateObject())
                    {
                        beat[property.Name] = property.Value.Clone();
                    }
                }

                beat["heartbeat"] = true;
                await emit("phase", beat);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException
                                   or InvalidOperationException)
        {
            // The turn ended or the client left — both are how this loop is meant to stop. A
            // heartbeat that cannot be written must never become the turn's failure.
        }
    }

    /// <summary>How long the stream may be silent before the heartbeat speaks. Well inside the 10 s contract.</summary>
    private static readonly TimeSpan HeartbeatAfter = TimeSpan.FromSeconds(5);

    private static async Task<string?> QuestionBehindAsync(
        IReelDbContext db, Domain.Search.SearchTurn turn, CancellationToken ct) =>
        await db.SearchTurns
            .Where(t => t.ConversationId == turn.ConversationId && t.Role == "user" && t.Sequence <= turn.Sequence)
            .OrderByDescending(t => t.Sequence)
            .Select(t => t.Text)
            .FirstOrDefaultAsync(ct);

    private static async Task<IResult> ReplayTurnAsStreamAsync(
        HttpContext http,
        IReelDbContext db,
        EligibilityQueryBuilder eligibility,
        OnDemandScorer scorer,
        Guid accountId,
        Domain.Search.SearchConversation conversation,
        Domain.Search.SearchTurn turn,
        System.Text.Json.JsonSerializerOptions jsonOptions,
        bool debug,
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
            // The same accounting a live turn emits, over what was stored: everything the turn
            // streamed is a pick, a tray card, or a card eligibility has since taken away.
            streamed = replay.Cards.Count + replay.BelowFloor.Count + replay.Removed,
            kept = replay.Cards.Count,
            droppedByFloor = replay.BelowFloor.Count,
            // The stored tray, not an empty array. A retry is the same answer as before, and the
            // tray was part of it — an idempotent replay that dropped it would make the second
            // delivery of one turn differ from the first.
            belowFloor = replay.BelowFloor,
            nearMiss = replay.NearMiss ?? [],
            @unchecked = replay.Unchecked ?? [],
            mode = replay.Mode,
            // A failed turn replays as the failure it was. Answering a retry with a cheerful empty
            // result set would tell the client the question succeeded and returned nothing.
            reason = replay.Reason,
            failed = turn.Status == Domain.Search.SearchTurnStatus.Failed,
            failureCode = turn.FailureCode,
            // The two things a replay must say out loud: this is the same answer as before, and
            // this many of its cards no longer belong on a suggestion surface.
            replayed = true,
            removed = replay.Removed,
            removedEarlier = replay.RemovedEarlier,
            // Present, and null. The contract's rule is that a client has ONE shape to render for
            // one event name, and these two keys carry that rule — a replay that omitted them made
            // `done` a three-shape event and handed every consumer an `undefined` to guard. There
            // is nothing honest to put in them: a replay makes no model calls and runs no stages,
            // so a timings block would be a fabrication and the stored row's own latency describes
            // a turn that happened minutes ago. `debug` is still gated exactly as it is live, so an
            // unentitled caller cannot learn anything from the key's presence.
            timingsMs = (object?)null,
            debug = debug ? (object?)new { replayed = true, storedTurnId = turn.Id, removed = replay.Removed } : null,
        });

        return Results.Empty;
    }

    /// <summary>
    /// The house error shape for <c>/api/search/*</c>, plus a stable <c>code</c>. The prose is the
    /// user's; the code is the client's, and it is what B2 switches on — matching on copy is how a
    /// UI ends up rendering a crash message for an honest refusal.
    /// </summary>
    /// <summary>
    /// Stamps a Pending turn that has outlived <c>StalePendingAfter</c> as
    /// <c>Failed/turn_timed_out</c>, in memory. Returns whether it did — the caller decides whether
    /// that changes its answer, and the caller saves.
    ///
    /// <para><b>One rule, three gates.</b> The stale test is asked at the ask gate (both the
    /// idempotent-replay arm and the one-in-flight arm) and at the reconnect poll, and all three
    /// have to agree: a row the poll calls Failed while <c>/ask</c> still calls it live is the wedge
    /// again, wearing a different status code. Keeping the transition in one place is also what
    /// keeps the code it writes in one place.</para>
    ///
    /// <para><b>It does not save.</b> Two of the three callers batch this into a write they were
    /// already making, and the third has exactly one row to flush; a helper that saved would make
    /// the ask gate write twice for one decision and would hide the write from the site that has to
    /// reason about ordering.</para>
    /// </summary>
    private static bool ExpireIfStale(Domain.Search.SearchTurn turn, DateTime utcNow)
    {
        if (!Domain.Search.SearchTurnFailure.IsStalePending(turn, utcNow))
        {
            return false;
        }

        turn.Status = Domain.Search.SearchTurnStatus.Failed;
        turn.FailureCode = Domain.Search.SearchTurnFailure.TurnTimedOut;
        turn.CompletedAt = utcNow;
        return true;
    }

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
    /// <summary>
    /// Ask Reel v2's write-on-emit (§4.6): the cards a <c>picks</c> or <c>tray</c> frame just put on
    /// screen, stored under the turn at once on a scope of their own, so a turn that dies mid-stream —
    /// a crash, not just a client abort — still holds what it showed, in history and in the
    /// no-repeat set. A failure here costs nothing: the final write stores whatever is missing.
    /// </summary>
    private static async Task PersistCardsAsync(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        Guid accountId,
        Guid conversationId,
        Guid turnId,
        IReadOnlyList<LiveSearchExpansionService.TurnCard> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IAccountContext>().SetAccount(accountId);
            var db = scope.ServiceProvider.GetRequiredService<IReelDbContext>();
            var ids = cards.Select(c => c.TitleId).ToList();
            var stored = await db.SearchTurnCards
                .Where(c => c.TurnId == turnId && ids.Contains(c.TitleId))
                .Select(c => c.TitleId)
                .ToListAsync(CancellationToken.None);
            var now = DateTime.UtcNow;
            db.SearchTurnCards.AddRange(cards.Where(c => !stored.Contains(c.TitleId)).Select(card => new Domain.Search.SearchTurnCard
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversationId,
                TurnId = turnId,
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
                BelowFloor = card.BelowFloor,
                Tier = card.Tier,
                Satisfies = card.Satisfies,
                FitSource = card.FitSource,
                SatisfiesEvidence = card.SatisfiesEvidence,
                CreatedAt = now,
            }));
            var conversation = await db.SearchConversations.FirstOrDefaultAsync(c => c.Id == conversationId, CancellationToken.None);
            if (conversation is not null)
            {
                conversation.LastCardsAt = now;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Ask Reel v2 could not store {Count} card(s) of turn {TurnId} as they were shown; the final write will.",
                cards.Count, turnId);
        }
    }

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
            // The notice the user read, verbatim, so a resume can restore it. Written on a FAILED
            // turn too: the frame went out before the crash, and a transcript that shows the answer
            // without the explanation for it is the half-record this column exists to prevent.
            turn.ConflictJson = outcome.ConflictJson;
            turn.ModelArtifactId = outcome.ModelArtifactId;
            turn.ModelId = outcome.ModelId;
            turn.PromptVersion = outcome.Act is null ? AskReelPromptVersion.Current : AskReelPromptVersion.V2;
            turn.LatencyMs = latencyMs;
            turn.CompletedAt = now;
            // J-13 / M-8: where the turn's seconds went, which models answered and at what effort,
            // what kind of answer it gave and which parts degraded — on the row, so a degraded turn
            // is a query rather than a log line that rotated away after thirty minutes.
            turn.StageTimingsJson = outcome.StageTimingsJson;
            turn.ModelIds = outcome.ModelIdsJson;
            turn.ReasoningEffort = outcome.ReasoningEffort;
            turn.Mode = outcome.Mode;
            turn.DegradedFlags = outcome.DegradedFlags;
            turn.Act = outcome.Act;
            turn.PlanSource = outcome.PlanSource;
            turn.TruncationCount = outcome.TruncationCount;

            // Cards are stored for every turn that SHOWED some — a completed answer, and a turn the
            // client ended after cards were already on screen (B-3). A crash stores none: its cards,
            // if any, never reached a `done`, and the client renders the failure instead.
            // A v2 turn writes its cards as it emits them (PersistCardsAsync); what is already stored is
            // not written twice.
            var stored = outcome.Cards.Count == 0
                ? []
                : await db.SearchTurnCards
                    .Where(c => c.TurnId == turnId)
                    .Select(c => c.TitleId)
                    .ToListAsync(CancellationToken.None);
            var unstored = outcome.Cards.Where(c => !stored.Contains(c.TitleId)).ToList();
            if (outcome.Cards.Count > 0
                && (!outcome.Failed || outcome.FailureCode == Domain.Search.SearchTurnFailure.ClientAborted))
            {
                db.SearchTurnCards.AddRange(unstored.Select(card => new Domain.Search.SearchTurnCard
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
                    // The tray rides the same table, flagged. It was on the glass, so it is in the
                    // conversation's no-repeat set; it was not recommended, so it is not in the
                    // exposure log and never replays into `cards`.
                    BelowFloor = card.BelowFloor,
                    Tier = card.Tier,
                    Satisfies = card.Satisfies,
                    FitSource = card.FitSource,
                    SatisfiesEvidence = card.SatisfiesEvidence,
                    CreatedAt = now,
                }));

                var conversation = await db.SearchConversations
                    .FirstOrDefaultAsync(c => c.Id == turn.ConversationId, CancellationToken.None);
                if (conversation is not null)
                {
                    conversation.LastCardsAt = now;
                }
            }

            // Ask Reel v2: the conversation's state after this turn (a cache of the turn record).
            if (outcome.ConversationStateJson is { } stateJson)
            {
                var conversation = await db.SearchConversations
                    .FirstOrDefaultAsync(c => c.Id == turn.ConversationId, CancellationToken.None);
                if (conversation is not null)
                {
                    conversation.StateJson = stateJson;
                    conversation.StateVersion = ConversationStateJson.Read(stateJson)?.Version ?? 0;
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);

            if (outcome.FailureCode == Domain.Search.SearchTurnFailure.ClientAborted)
            {
                await RecordAbortedExposuresAsync(scope.ServiceProvider, db, accountId, turnId, outcome.Cards);
            }
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
