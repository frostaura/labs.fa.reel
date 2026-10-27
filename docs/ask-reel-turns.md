# Ask Reel — the turn record

The server owns what a conversation is. A client sends a question, the conversation it belongs to,
and one id for this exchange; everything else — the history the model reads, the no-repeat set,
whether this is a new question or another page of the last one — is derived from rows the server
wrote. That inversion is the whole feature: a client-carried transcript is unverifiable, capped,
lost on reload and wrong the moment a second tab exists, and every one of those failures showed up
as the product forgetting what it had just said.

This page is the contract. The reasoning behind each decision lives in the doc-comments on
`SearchTurn`, `SearchTurnCard`, `SearchEndpoints.ReplayAsync` and `SearchConversationRetention`.

The same inversion governs what the user has *seen*, which is why the below-floor tray is a stored
row rather than a wire-only artefact — see *The tray*.

## The request

`POST /api/search/ask`

```jsonc
{
  "query": "something tense with a clever ending",    // 1..1000 chars, trimmed
  "conversationId": "1f2e3d4c-…",                     // optional UUID string; null mints a new conversation
  "clientTurnId": "9a8b7c6d-…",                       // REQUIRED UUID string; the idempotency key for this exchange
  "mediaTypes": ["movie"],                            // optional; "movie" | "movies" | "film" | "tv" | "show" | "series"
  "pageOf": "5c4b3a29-…"                              // optional UUID string; the assistant turn this is another page of
}
```

`history`, `shownTmdbIds` and `continuation` **do not exist**. They were removed, not renamed.

**Every id is a UUID, not an opaque string.** `SearchTurns.ClientTurnId`, `SearchTurns.Id` and
`SearchConversations.Id` are `uuid` columns, so a nanoid or a `${tabId}-${n}` key is unrepresentable
and is refused — `client_turn_id_invalid` / `conversation_id_invalid` / `page_of_invalid`, in the
same `{ error, code }` shape as every other refusal. They arrive as strings and are parsed by the
handler precisely so that refusal is a coded one: bound as `Guid` they failed in the JSON binder,
before any of the codes below could be reached, and the caller got a `text/plain` .NET exception with
no `code` at all. `"00000000-0000-0000-0000-000000000000"` is well-formed and still invalid — it is
the "no id" sentinel, and every turn would collide on it.

**The body is read as JSON and unpacked field by field, so a field of the wrong TYPE is refused the
same way a field of the wrong VALUE is.** `{"clientTurnId": 12345}` and
`{"clientTurnId": {"a":1}}` are `client_turn_id_invalid`; a non-string `query` is `query_invalid`;
a `mediaTypes` that is not an array is `media_type_invalid`. A body that is not a JSON **object** at
all — unparseable bytes, a bare string, an array, nothing — is `body_invalid`. Making the ids
strings alone was not enough: the binder is bypassed for a JSON *string*, so any other type still
failed inside it and returned five kilobytes of `text/plain` `BadHttpRequestException` naming an
internal type, on a route any signed-in account can reach.

**The one place `code` is not available is an unmatched ROUTE.** `GET /api/search/turns/not-a-uuid`
is a bodiless `404`: `{id:guid}` is a route constraint, so an unparseable id matches no route and
routing refuses it before any handler exists to be reached. That is deliberate — widening the
constraint would mean claiming a URL space this API does not own — and it is the stated exception to
"`code` is what a client switches on".

**Every `mediaTypes` token must be one of the six.** An unknown token is `media_type_invalid`, not a
silently dropped filter: a stale or typo'd token would otherwise be indistinguishable from no filter
at all, and the turn would be billed regardless.

The client mints one `clientTurnId` per user message and resends it verbatim on a retry — a dropped
connection, a StrictMode double-invoke, an impatient second tap. All three are answered from the
stored turn.

## The responses

| Status | Shape | When |
| --- | --- | --- |
| `200` `text/event-stream` | the SSE stream below | a live turn, or the replay of a finished one |
| `400` | `{ error, code }` | `body_invalid`, `empty_query`, `query_invalid`, `query_too_long`, `client_turn_id_required`, `client_turn_id_invalid`, `conversation_id_invalid`, `page_of_invalid`, `media_type_invalid` |
| `404` | `{ error, code }` | `conversation_not_found` — an id that is not the caller's, or is gone. **Never a fork.** · `page_of_not_found` — a `pageOf` turn that is not the caller's, is gone, or is not in this conversation |
| `409` | `{ error, code }` | `turn_in_progress` — this turn, or another one, is **genuinely** still running on this conversation; see *Stale turns* · `client_turn_id_reused` — this key already answered a **different** question · `page_of_not_completed` — the named turn did not finish, so there is no answer to extend · `page_query_mismatch` — the query is not the question that turn answers |

`code` is stable and is what a client switches on. The prose in `error` is the user's and may
change; matching on it is how a UI ends up rendering a crash message for an honest refusal.

## The stream

SSE, one frame per event, `id:` line first, blank line last. A `:keepalive` comment frame goes out
every 10 seconds — it carries no event and a parser must skip it, but a client's idle watchdog must
re-arm on its bytes. Whenever the stream has been silent for 5 seconds the server also repeats the
last `phase` frame with `heartbeat: true`: proof of life through the parser, never a new phase, and
never a reason to re-sort the cards on screen. No `phase` frame follows `done`. Ids start at 1; `id: 0` is never
written, because a client echoing it back as `Last-Event-ID` would reset its own cursor.

```
id: 1
event: conversation
data: {"id":"…","turnId":"…","sequence":3}

id: 2
event: phase
data: {"stage":"searching","found":0,"scored":0}

…candidate / candidate-scored / candidate-reranked / conflict…

id: 9
event: done
data: {"results":[…],"streamed":10,"kept":8,"droppedByFloor":2,"belowFloor":[…],"nearMiss":[…],"unchecked":[],"mode":"Picks","supply":null,"degraded":[],"reason":"…","timingsMs":null,"debug":null}
```

**The interim gate (trust-v1, WS-2 task 1).** The top sixteen candidates are judged against the
REQUEST — the top four first, then batches of six, at most three in flight at once — and each card is
placed by the judge's `satisfies` verdict and the account's For-you bar. A card is *judged* only when
the model returned both its fit and a `satisfies` value; a skipped entry in an answered batch is as
unjudged as a failed batch:

| Verdict | Clears the bar | Goes to | Stored `Tier` | Exposure |
| --- | --- | --- | --- | --- |
| `yes` | yes | `results` (a pick), emitted as its batch returns | `Pick` | yes |
| `yes` | no, or no score while a bar is set | `belowFloor`, the "held back by your bar" tray — judged matches **only** | `BelowFloor` | no |
| `partial` | either | `nearMiss`, a collapsed "close, but not quite" tray | `NearMiss` | no |
| `no` | either | nowhere — not shown, not stored | — | no |
| none (a failed batch, a skipped entry, the stub), on a turn with at least one `yes` pick | yes | `results`, after every judged pick, in retrieval order, **no why-line, no fit** | `Pick` | yes, `WhySource=None` |
| none, on a turn with **no** `yes` pick — or under the bar on any turn | either | `unchecked`, the "not checked against your request" tray; with no `yes` pick the turn is `NoneFound` | `Unchecked` | no |

An unjudged card is **never** in the floor tray (R1-01): that tray, its caption, the reply and the
reason line all read as "matches your bar held back", and the samurai turn once told the founder
"2 held back by your 6.0 bar" about two films nobody had checked. An outside card (one that trips the
user's own negation) under the bar goes to `unchecked` for the same reason. Trays are stored, and
rendered, in one order: picks, the floor tray, the near misses, the unchecked — so a tray card's
stored rank is its place on screen.

The floor **never relaxes**, and a card with no prediction never clears a set bar (§4.8: unscored
cards are never picks) — the same definition `SupplyReport.ClearingFloor` counts by. Unjudged cards
never make an answer on their own: sixteen of them under the model's pre-retrieval sentence is the
original "cards that don't match" failure. Every non-grid tier also stores `BelowFloor = true`, so an
image older than the tier never promotes it; an unknown tier reads as `NearMiss`.

The judge rules on the request **in its conversation** — the message, the conversation's last two
user asks, and the resolved reading (media, genres, topic, years, exclusions) — and its verdict cache
is keyed on that composed text, so a verdict is reused only where the same thing was asked in the
same context. Judging has a 25 s deadline from the start of the stage; a batch still out by then is a
failed batch. A fallback verdict is never cached, never shown as model output and
never logged as `Llm`. The reply (`assistant-message`) goes out **once**, just before the first pick
— not as the turn's opening frame — and carries `degraded: true` when every attempt to read the
question failed (its text is then the degraded copy, which quotes nobody).

**The topic is resolved once** (R1-02). The model's keywords are alternative pulls and it widens them
on its own ("martial arts" beside "ninja"); discovery may use all of them, but the topic the supply
COUNTS and the reply NAMES is the subset the user said — in this message, else in the conversation's
earlier asks, newest first. When the user's words anchor none of them, the whole list is the topic
and the reply names every keyword it counted ("swordplay or warrior movies"). A request with a named
topic never widens past it: one discovery pass, no popularity last resort (§4.8); mood and genre
requests still widen, keeping their genres on every rung.

**`mode: "Clarify"`** when the message points at a card by position ("the second one", "the last
one", "#3") and no answer in the conversation shows that many cards (R1-04). It is answered before any
model call, with no search and no cards: the reply says what is on screen and asks what was meant.
`intentJson` is null, so the next turn inherits nothing from it. A pointer some answer CAN satisfy is
always left to the model.

**Keyless turns** (no embedding provider, R1-10) answer `NoneFound` with the lexical engine's keyword
matches in `unchecked` — inside every standing condition and the media the message names (else the
previous answer's), the bar's verdict on every card and counted in the reply — with `degraded:
["no-model"]`, a degraded reply, stage timings and no exposure. Nothing reaches `results`.

**`mode: "NoneFound"`** when no card was judged `yes` (unjudged cards then sit in `unchecked`). The reply is then composed deterministically
from the `supply` report (seen, saved or dismissed, held out by conditions, under the bar, unscored,
and the drop-one relaxations that would unlock something — "already seen" is never one of them);
`supply` rides `done` on that turn only and is `null` on every other. `degraded` lists what did not
run as designed: `plan`; `judge:{failed}/{batches}` (batches that ruled on nothing);
`judge-missing:{missing}/{judged}` (every title sent to the judge that came back without a request
verdict, failed batches included — raised by a single skipped entry); and `no-model` (a keyless
turn: nothing read the request, nothing checked a card). Any of the last three marks the reply
"some cards unchecked". On a none-found turn the reply names the near misses ("Close, but not quite:
Blade of the 47 Ronin — tucked away below."), three at most. Every enum inside `supply` is written as its
name (`"kind": "Language"`), never a number. A follow-up that names no media inherits the previous
reading's single media type. A client-aborted turn stores its degraded flags, stage timings, model ids
and mode exactly as a finished turn does.

The `conversation` frame is first and always present on a live turn: it is how the client learns
the conversation id to remember, the turn id to reconnect to, and where in the transcript it sits.

**Array order is the contract.** Every card carries `blend`, the server's own ranking value, so a
client can explain the order — but it must never re-sort by it: `blend` is rounded for display and
the server's ordering carries a tiebreak the rounded number cannot reproduce.

A **replay** (the same `clientTurnId` again) emits the same grammar — `conversation`, then `done` —
carrying **every key a live `done` carries**, plus four a live turn cannot state: `replayed: true`,
`removed` (how many stored cards the eligibility re-filter dropped since), `removedEarlier` (how many
of those the account had already rated, watched, saved or dismissed when the answer was given — a
person query shows seen titles, so the client says "since" only of the rest), and `failed` /
`failureCode` when the stored turn was a failure. `timingsMs` and `debug` are present and `null`,
under the same founder gating as a live turn: one event name, one shape, always. A replay makes zero
model calls, writes zero rows and writes **no exposures**.

**A replay answers the same question or it answers nothing.** The key identifies one exchange, so
re-sending it with a different `query` (compared trimmed and ordinal against the question the stored
turn answers) is `409 client_turn_id_reused` rather than the previous question's answer. Whitespace
differs freely — that is a retry; anything else is a new question and needs a new key.

## Asks and pages

An **ask** writes two rows — the user's question and Reel's answer. A **page** writes the assistant
row alone: it is another page of a question that was already asked, so there is no second question
to record. Which one a submission is has two sources, and they are tried in this order.

**1 — `pageOf`, when the client sends it.** `pageOf` is the **assistant** turn this submission is
another page of, as a UUID string. It is how a client that is *rendering* an answer says which
answer its "Show me more" belongs to, rather than leaving the server to infer it. The server
verifies rather than trusts it, and all four checks are refusals a client can act on:

| Check | Failure |
| --- | --- |
| parses as a non-zero UUID | `400 page_of_invalid` |
| is an assistant turn, is the caller's, and is in this conversation | `404 page_of_not_found` |
| is `Completed` | `409 page_of_not_completed` |
| `query` equals the question that turn answers, **trimmed and ordinal** | `409 page_query_mismatch` |

On success the turn is recorded `Kind='page'` extending **that** turn: **no user row is written**,
the assistant row takes the conversation's next `Sequence`, and the no-repeat set is derived exactly
as it is for any other turn — the whole conversation's distinct `TmdbId`s, tray included.

`conversationId` may be omitted when `pageOf` is present: the turn is already account-scoped, so its
own conversation is the only one it could mean, and resolving it that way cannot fork. When both are
sent they must agree, or it is `page_of_not_found`.

**2 — the comparison, when `pageOf` is absent.** The query is compared **trimmed and ORDINAL**, case
included, against the question of the **last `Completed` ask** in the conversation — failed and
pending tails are skipped. A question retyped in different capitals is a new ask and gets its own
user row; only a byte-identical repeat is another page.

**Why the comparison skips failed tails, and why `pageOf` exists at all.** It used to compare
against the conversation's last **user** turn, full stop. Stop a turn and the stopped question
becomes that tail, so pressing "Show me more" on the kept answer no longer matched and was recorded
as a new **ask**. Three things broke at once: the transcript gained a question the user never typed,
the stopped turn stopped being the tail and lost its failure row along with "That one didn't finish
before you left", and the next resume returned only the newest ask's block — 42 of the 66 cards on
screen vanished on reload. A turn nobody waited for is still a turn that happened, and it is still
not the thing another page would be a page *of*.

## The read routes

| Route | Answers |
| --- | --- |
| `GET /api/search/conversations?limit=20` | `[{ id, title, turnCount, lastTurnAt, lastCardsAt }]` |
| `GET /api/search/conversations/{id}` | the resume payload below; `404` if it is not the caller's |
| `GET /api/search/turns/{id}` | `{ status, reason, conflict, failureCode, cards, belowFloor, nearMiss, unchecked, mode, removed, removedEarlier }` — **`202` while `Pending`**, unless the row is stale; see *Stale turns* |
| `DELETE /api/search/conversations/{id}` | `204`. Cards, turns, conversation. **Never exposures.** |
| `GET /api/search/conversations/latest` | **deprecated** — the same payload for the newest conversation |

The resume payload:

```jsonc
{
  "id": "…", "title": "…", "lastCardsAt": null,
  "lastTurnAt": "2026-09-11T00:54:21.301Z",
  "turns":   [{ "id": "…", "sequence": 0, "role": "user", "text": "…",
                "kind": "ask", "status": "Completed", "failureCode": null,
                "degraded": false, "judgeDegraded": false, "mode": null,
                "act": null,          // v2's act on an assistant row; null on v1 rows and user rows
                // This answer's OWN cards and trays, replayed through the same projection as the
                // top-level lists — null on a user row, a failure, or an answer older than the
                // newest 12. A client renders each turn's cards under its own bubble from these.
                // The trays are NESTED under `trays`, so their names never collide with the row's
                // own flags (`unchecked` is also the client's judge-degraded marker).
                "cards": null,
                "trays": null,        // or { "belowFloor": [card], "nearMiss": [card], "unchecked": [card] }
                "reason": null }],
  "cards":       [ /* same card shape as done.results */ ],
  "belowFloor":  [ /* the same card shape again — the tray */ ],
  "nearMiss":    [ /* the near-miss tray */ ],
  "unchecked":   [ /* the not-checked tray */ ],
  "mode":        "Picks",
  "reason":  "…",        // the newest replayed turn's reason, verbatim
  "conflict": {          // the collision notice that turn emitted, or null — see below
    "excludedGenres":   ["comedy"],
    "excludedKeywords": ["anime"],
    "minYear": 2000, "maxYear": null,
    "message": "Your conditions exclude comedy and anime, …"
  },
  "removed": 0,          // stored cards the eligibility re-filter dropped, across BOTH lists
  "removedEarlier": 0,   // of those, already acted on when the answer was given (never "since")
  "lastQuery": "…",      // the last user turn
  "mediaTypes": ["tv"],  // the filter that turn ran under, in request tokens
  "shown": [603, 27205]  // the whole conversation's no-repeat set, tray included
}
```

Each turn carries its own `id` and, when it failed, its `failureCode`. The id is what makes a
transcript row addressable: a `Pending` row is polled through `GET /api/search/turns/{id}`, which is
the whole reconnect path, and without an id a resumed thread could see a turn still running and have
no handle to name it. The code is `null` on every status but `Failed` — read through the status
rather than off the column, so a cause can never outlive the failure it described. It is what
separates a turn the user stopped themselves (`client_aborted`) from one that hit a snag; without
it, both render the same and the page either apologises for a deliberate stop or stays quiet about a
real failure. `server_restarted` and `turn_timed_out` join it from the two arms described under
*Stale turns* — neither is written by anything in the request pipeline, because the rows they close
are exactly the rows no request can reach.

`conflict` is the `conflict` frame that turn emitted, **stored on the turn** and handed back with
it — `null` on a thread that had no collision, which is most of them. It restores the notice and the
"under your conditions" pill, which were stream-only: reload a thread that asked for a 90s romantic
comedy against standing exclusions on both genres and it came back as four post-2000 dramas under
that question with nothing left on the page saying why. The cards are restored from rows and the
reason line from a column; the explanation was the one part of the turn the server forgot.

**Which list a colliding term appears in is decided by the list the account's RULE lives in**, never
by the bucket the interpreter happened to put the word in. An account holding `anime` as an
`ExcludeGenre` gets it back under `excludedGenres` even though the turn's words read as a keyword;
an account holding `ghosts` as an `ExcludeKeyword` gets it back under `excludedKeywords`. The two
lead to different rows in the conditions editor, so a client keying them to different affordances
has to be told the truth about where to send the user. Both lists are **ordinal-sorted**: row order
out of Postgres is not guaranteed, and a notice that named its two collisions one way live and the
other way on the next resume reads as two different answers to one question.

The collision is computed against **everything the turn asked for** — the model's intent, the
free deterministic reading of the user's own sentence, and the raw sentence's own tokens — each
expanded through the genre alias table, so "romantic" collides with a standing rule on `romance`.
Without that expansion "a 90s romantic comedy" named only comedy on the frame while the prose in the
same bubble named romance and comedy.

It is **stored, not recomputed**, for the same reason `reason` is: the account's conditions can
change between the turn and the resume — that is the entire point of the Conditions screen the
notice points at — and a notice re-derived from today's rules would describe an answer produced
under yesterday's.

`poolSize` and `catalogueSize` are on the live frame and deliberately **not** in the stored object:
both count the catalogue at one instant and stop being true the moment the next title is ingested,
so replaying them on a resume would state a stale number as a current one. `minYear` / `maxYear` are
present only when the **year** is what collided — an account with a standing window has one on every
turn, so storing them unconditionally would put a year chip on a screen whose collision was purely a
genre.

`lastCardsAt: null` means **transcript only** — a conversation that predates stored results. Render
that as its own state; it is a different fact from a turn that honestly returned nothing.

`lastTurnAt` is when the thread was last spoken in — the same column the `Recent` list sorts and
renders on, so the two views cannot disagree about a thread's age. It is present because a
transcript-only thread has a null `lastCardsAt` **by definition**, and therefore carried no date at
all: the resumed seam had nothing to say but "picked up where you left off".

`belowFloor` is the tray, restored as a tray. It is never merged into `cards` — see *The tray*
below.

`GET /turns/{id}` is what replaces `Last-Event-ID` replay. The events are gone, but the turn is a
row, and a row answers the only question a disconnected client has: is it done, and what did it
say.

## Lifecycle

1. **Validate** — before a header is written.
2. **Resolve the conversation** — account-scoped, so "not yours" and "not there" are the same 404.
3. **Idempotency** on `(ConversationId, ClientTurnId)` — `Completed` or `Failed` replays, `Pending`
   is a 409. Backed by a partial unique index, not a check-then-act.
4. **One turn in flight** per conversation — a second is a 409; the unique `(ConversationId,
   Sequence)` index is the race backstop.
2b. **Resolve `pageOf`**, when one was sent — see *Asks and pages*. Its refusals come before the
   idempotency check, so a malformed page costs nothing.
5. **Derive** `shown` (distinct **`TitleId`** over the conversation's cards, **tray included** — see
   *The tray*; TMDB's movie and TV id spaces overlap, so a `TmdbId` key hid unrelated titles) and
   `kind` (explicit where `pageOf` was sent, compared otherwise), *before* this turn's own rows exist.
   History and the presented handles are read once the stream is open, bounded by sequence to the
   rows before this turn: the last 8 turns that said something true — `Completed`, or `Failed` /
   `client_aborted` (its reply and cards were on screen) — never a degraded reply nor the legacy stub
   template, each assistant line suffixed `[Shown: T2.1 Ballerina (2025) · …]`. The model is shown
   handles `T{answer}.{position}`, newest answer first, in screen order — the list "the second one"
   resolves against: the picks of the last three answers that had picks, and the tray cards (at most
   eight each, floor tray then near misses then unchecked) of the last three answers that showed no
   picks, because a none-found answer's trays are the only thing it showed. The two are cut
   separately, so a run of none-found answers never pushes the last real picks out.
6. **Write the rows Pending** — a user row and an assistant row for an `ask`, the assistant row
   alone for a `page` — then the headers, then `conversation`.
7. **Stream.**
8. **Persist after `done`**, on a scope of its own, under `CancellationToken.None`, inside a
   try/catch that logs and never throws. The request's context carries the pipeline's catalogue
   upserts and is never saved after the stream.
   The row also records `Mode`, `DegradedFlags`, `StageTimingsJson`, `ModelIds` (`plan`, `judge`,
   `embedding`) and `ReasoningEffort` — the effort the purpose catalog sends at, `none` today;
   `plan/judge` when the two differ, `provider-default` where no effort parameter is sent, and null
   on a turn that made no model call (keyless, Clarify). `StageTimingsJson` carries the sequential
   stages plus breakdowns: `discover.tmdb`, `discover.facet`, `discover.knn`, `embed.titles`
   (embedding titles that had no vector), `embed.seed` (the part of the concept embedding the arms
   did not hide), `score.hydrate` (TMDB hydration of never-refreshed titles), `rerank.first` (the
   first judge batch) and `firstCard` (turn start to the first pick on the wire). A breakdown whose
   work did not happen is absent, not zero. All three middle ones sit on the first card's critical
   path, and §4.11's retrieve budget has no line for any of them.
9. **Failure** — a crash stamps the row `Failed` with a `FailureCode` and stores no cards. An
   abandoned stream (`client_aborted`) stores **what was already on the wire**: the reply and every
   card emitted, in emission order, so the next turn's history and no-repeat set contain them — and
   logs those picks as exposures (Pick tier with a prediction, the typed why-source, keyed on the
   turn, written only when the turn has none yet): a pick that reached the screen was recommended,
   stopped or not (R1-12).

**Before step 1 of anything — boot.** Every row still `Pending` from the previous process is stamped
`Failed` / `server_restarted` with a `CompletedAt`, before the API serves its first request. It is
not a step of a turn, which is the point: it closes the rows no turn can reach. See *Stale turns*.

## Stale turns

Steps 8 and 9 both live in the **request pipeline**. Kill the process mid-stream — a crash, an OOM,
a deploy restart — and neither runs: the row stays `Pending`, `CompletedAt` null, `FailureCode`
null, with no code path anywhere that would ever close it. Step 4 then refuses every new turn on
that conversation, so the thread is not degraded but **finished**: every retry, every "Try again"
and every new question answers `409 turn_in_progress`, and `GET /turns/{id}` answers `202` forever.
Reproduced by killing the API 20 seconds into a turn; the conversation never accepted another
question.

Two arms close it, and they write **different codes on purpose**.

| Code | Written by | Means |
| --- | --- | --- |
| `server_restarted` | the boot sweep, step 0 | the process that was streaming this turn is gone. A **fact**: a process that has just started owns no streams, so every `Pending` row it finds belongs to a dead one. No clock is consulted and no live turn can be caught, which is why this arm sweeps regardless of age and across **every** account. |
| `turn_timed_out` | the `/ask` gate and `GET /turns/{id}` | the row was still `Pending` **3 minutes** after it was written. An **inference** — the threshold is an order of magnitude above measured turns of 8–30 s, deliberately, and is not a latency budget. |

Both arms are needed. A boot that never happens sweeps nothing, and a clock cannot tell a dead
process from a slow one at the instant it dies. Collapsing the two codes would lose the only
distinction worth having when a stuck conversation is read back.

Where the gate finds a stale row it **closes it and carries on**, rather than answering 409:

- **A different turn in flight** — every stale `Pending` row on the conversation is closed, not just
  the first, and the new turn proceeds. A conversation that outlived two restarts has two of them.
- **The same `clientTurnId`** — the retry **proceeds** rather than replaying the corpse or refusing.
  The dead row also **gives up its key**: `(ConversationId, ClientTurnId)` is a partial unique index,
  so a corpse that kept it would collide with the retry's own assistant row and turn a wedged thread
  into a `500`. The row stays in the transcript as `Failed` / `turn_timed_out`.
- **The reconnect poll** — a stale row answers `200` as a `Failed` turn with that code, never `202`.
  It is *closed* by the poll, not merely reported closed, so the table and the poller agree; this is
  the one read route that writes, and it writes exactly one row.

A `Pending` row **inside** the threshold is untouched and still gets the `409`. That refusal is not
a nicety: two streams on one turn id would both write cards and collide on the unique
`(TurnId, Rank)`.

## Eligibility on replay

The founder-locked rule — *never show things I've already seen* — names Ask Reel explicitly, and it
applies to a **stored** answer as much as to a live one. Every route that hands cards back goes
through one private `ReplayAsync`, so the guard exists in exactly one place. Three things happen to
a stored card on its way out:

- **Dropped** if the account has since interacted with the title in any way — rated, watched, saved,
  dropped, marked not-interested — and the drops are **counted** into `removed`, because a card that
  silently disappears between two views of the same answer is indistinguishable from a bug. A
  *revoked* reaction is not an interaction: it is a statement the user took back.
- **Re-scored** when the turn's `ModelArtifactId` is not the account's current Active artifact.
  Null, superseded and *unresolvable* are the same query and get the same answer — `ModelArtifact`'s
  global filter hides artifacts trained against a different feature-schema version. No model at all
  yields an empty map, which falls through to the stored number rather than blanking the badge.
- **`seen` recomputed**, never stored: it is a fact about the person, not about the card.

The **order is never re-derived**. Stored rank is the position the answer had.

## The tray

A card the account's rating floor pushed under the *"Show the N under your bar"* fence is stored as
a `SearchTurnCard` with **`Tier = BelowFloor`** and **`BelowFloor = true`**; a near miss is stored
`Tier = NearMiss` and **also** `BelowFloor = true`, so an image older than the tier column can never
replay it as a pick. Readers go through `SearchTurnCardTier.Of`: no tier reads from the flag, an
unknown tier reads as `NearMiss`, never as a pick, and the rank counter runs across both blocks — the
picks take `0..n-1` and the tray continues at `n`, so the unique `(TurnId, Rank)` holds.

**Why it is stored: the user saw it.** The founder-locked rule is *never show things I've already
seen*, and the no-repeat set is `SELECT DISTINCT "TmdbId"` over the conversation's cards. While the
tray was emitted and then forgotten, that query could not contain it — and a measured smoke run
watched turn 1 put 14 titles under the bar and turn 2 serve **13 of those same 14** as its
top-of-grid picks, the user looking at the same titles twice on one screen. The `shown` set is
therefore **deliberately not filtered by `BelowFloor`**: under the fence is still on the glass.

**It is not a pick, and three things keep knowing that.**

- **Replay keeps it separate.** `ReplayAsync` partitions *after* the shared `(Sequence, Rank)` sort
  and *after* the eligibility re-filter, so both lists carry one ordering and one guard — and the
  tray never reaches `cards`. `removed` spans both, because a card that vanished out of the tray is
  as invisible as one that vanished out of the grid.
- **No exposure row, ever.** An `ExposureEvent` is evidence that the product *recommended* a title
  at a rank; a card the account's own floor pushed under the fence was displayed and explicitly not
  recommended. Logging it would put titles the floor rejected into the denominator of every accuracy
  number computed from an append-only log, irreversibly. Storing the tray moved the exposure count
  by exactly zero.
- **The reason line is unchanged.** Its counts come from the floor's own verdict, never from a row
  count over `SearchTurnCards`.

A **replay** (`done`) and `GET /turns/{id}` both hand the stored tray back in `belowFloor`, so an
idempotent retry and a lost-stream reconnect restore the whole answer rather than the half of it
that cleared the bar. On a replay `streamed = kept + droppedByFloor + removed`.

## Retention

`ASK_RETENTION_DAYS`, default **90**. A conversation whose last turn is older than that is
hard-deleted — cards, then turns, then the conversation — by the nightly reconcile, beside the
enrichment sweep. Date-swept rather than depth-capped: a feed snapshot is replaced every build, a
person's transcript is not, and age is the only honest bound.

Neither retention nor the user-facing delete touches `ExposureEvents`. The log is append-only, every
accuracy number is computed from it, and a dangling `ExposureEvent.SourceTurnId` afterwards is the
designed state — the product did serve those cards, and that stays true after the transcript is
gone.

## Engine v2 (`ASK_ENGINE=v2`)

The request, the refusals, the rows written before the stream, idempotency, replay, the read routes,
stale turns, the tray and retention are **unchanged** — only what runs between the `conversation`
frame and `done` differs. `ASK_ENGINE` is `v1` (default) or `v2`; any other value reads as `v1`. A
founder may force either engine for one request with `?engine=v1|v2` (gated like `?debug`; ignored for
everyone else). Every live turn's `conversation` frame now carries `engine: "v1" | "v2"`; a client
uses it only to know which frames to expect.

### The pipeline

1. **Plan** — one model call (`ask-plan`, JSON schema with only `act` required, effort none — the
   planner writes only the fields that carry something, since output tokens are what the stage waits
   on; a missing field reads as empty or unchanged, and a genre outside the slug list is dropped). It classifies the message
   as one **act** — `NewRequest`, `Refine`, `CritiqueSet`, `CritiqueItem`, `ItemQuestion` or
   `Chitchat` — and returns a delta to the conversation's state, the handles it refers to, and the
   tools it wants. A `length` finish or a timeout is retried once at twice the budget; if the retry
   fails too the previous state is **carried forward**, only additive words from the new message are
   added (never ordinals, never a negation phrase as a topic), and the turn is degraded (`plan`). On
   a conversation's **first** turn there is nothing to carry: the user's positive words are searched
   as the **phrases** they wrote ("time travel", "liam neeson" — never each word as its own
   requirement), and the reply leads "I couldn't fully read that, so I searched for …", never "as
   before". A plan no model wrote (carried forward, or keyless) takes the media the message names.
   **Media:** the user's own media word outranks a plan that left media unset **or answered
   `any`** ("samurai films" is films); `any` stands only where the words name both media or neither,
   ask for one in addition ("shows too") or rule one out ("no tv shows" asks for films).
   **Pointers:** an ordinal no answer on screen can satisfy ("the third one" after one pick, or
   with nothing shown) is asked back **before any model call** (`clarify`, R1-04); one some answer
   could satisfy but that nothing resolved to a card (no handle, and the newest answer has no card
   there, or "from before" without saying which) is asked back after planning — a search never runs
   on an unresolved pointer. **"More like the third one"** (a `Refine` whose reference resolved, in
   a message asking for likeness) searches **near that card**: the earlier person and topics stop
   being hard filters unless the message restates them; media, years, runtime and the user's own
   negations still hold.
   A spent plan allowance (`ask_budget_spent`) or an open provider breaker (`provider_unavailable`)
   ends the turn **before any work**: `Failed` with that code and the cause's degraded copy.
   **Speculative retrieval:** while the plan runs, a plain topic request's keyless reading
   (concepts only — no person, mood or pointer) is retrieved on a scope of its own; the plan's
   retrieval uses it only when the plan asks for the identical query, else it is cancelled. Stored
   timings then carry `retrieve.spec` (its own length) and `retrieve` is only the wait after the plan.
   Stage timings also record `load`, `retrieve.titles` and `judge.prep`.
2. **Execute** — deterministic tools only; no tool output goes back to the model except the fact
   sheet an `ItemQuestion` answer is written from. `search_catalog` passes the user's own rejections
   (genres, and keywords in every spelling), the message and the account's region into retrieval.
   An arm that contributed nothing is stored on the row as `arm:{name}` (`arm:semantic`,
   `arm:tmdb-late`, `arm:provisional-n/m` …) and is **never** sent in `done.degraded` — the cards were
   fused without it and are still what they claim (§4.10). When the catalogue leaves the judged slice
   short, up to 6 TMDB-only titles are hydrated beside the first judge pass (2.5 s), admitted through
   eligibility, this turn's rejections and the hard constraints, and judged as one more pass.
   `conditions()` answers with the pool the standing rules leave and the three rules costing the most,
   each with its marginal (the Conditions editor's own numbers).
3. **Judge** — the top **16** candidates in retrieval order, batches of at most **6**, at most
   **3** in flight, an Ask-only prompt (`ask-judge`). Each batch is placed and **emitted as it
   returns**.
4. **Rank** — `yes` and clearing the bar → picks, appended per batch, **never reordered once
   shown**; `partial` → the near-miss tray; `yes` under the bar → the floor tray; `no` → dropped;
   a failed batch → its cards rank after every judged pick, in retrieval order, with no why-line, or
   go to `unchecked` when the turn has no judged pick. **The bar binds first (XM-06):** a card nobody
   judged that the bar holds back (under it, or unscored while one is set) goes to `belowFloor` with
   `satisfies: null`, never to `unchecked` — so `unchecked` only ever holds cards that clear the bar.
   Only cards something judged (the judge, or a person's credits) are named or said to "fit" under
   the bar; the rest are counted as unchecked.
5. **Compose** — the reply is written by templates from the supply report, the handles and the
   facts. **No model writes a search turn's reply.** No markdown, never "pulled", never an echo of
   the message. A none-found reply claims a check only for **verdicts that came back** — a person's
   credits, a turn with no model and a judge whose every batch failed checked nothing — and of the
   tagged supply only the part the judge saw ("I checked 16 of them … the other 4 weren't among the
   16 titles I checked"); titles this conversation already showed are named as shown. An
   `ItemQuestion` answer names the title and states the answer; one that cites Reel's inputs ("the
   runtime fact says…", "the keywords identify…") is discarded for the fact line, which names the title.
6. **Persist** — cards are written **as they are emitted** (each `picks` / `tray` frame is a row
   write), so a turn that dies mid-stream still holds what it showed; the turn's act, mode, plan
   source, degraded flags, timings, models and effort are written at the end, and the conversation's
   state is saved beside it.

### Frames, in order

| Frame | When | Payload |
| --- | --- | --- |
| `conversation` | first, always | `{ id, turnId, sequence, engine: "v2" }` |
| `phase` | at every stage, and repeated with `heartbeat: true` after 5 s of silence | `{ stage, label }` — `stage` is `planning` · `searching` · `judging` · `answering` · `composing`; `label` is a plain sentence a narration line can show as-is ("Checking 16 titles against your request") |
| `plan` | once, after planning | `{ act, concepts: ["ninja"], people: [{ name, role: "Director" \| "Cast" \| "Any" }], references: ["T2.3"], media: "Any" \| "Movies" \| "Shows", source: "Llm" \| "LlmRetry" \| "CarriedForward" }` |
| `conflict` | when the request collides with a standing condition | the v1 shape, unchanged |
| `picks` | once per judge batch that produced at least one pick | `{ batch, cards: [card] }` — append in arrival order; never re-sort. `batch` is the batch's ordinal within its judge pass (a turn that judges TMDB-only titles runs a second pass, numbered from 0 again): informational, never a key |
| `tray` | once per non-empty tray, after judging | `{ kind: "nearMiss" \| "belowFloor" \| "unchecked", cards: [card] }` — the whole tray in one frame |
| `none-found` | a search act with no pick | `{ supply }` — the `done.supply` shape |
| `degraded` | when anything the client is told about did not run as designed: before `answer` / `clarify`, else before `assistant-message` | `{ what: [flag] }` — the `done.degraded` tokens (`plan`, `plan-cause:{code}`, `judge:n/m`, `judge-missing:n/m`, `no-model`, `answer`); never `arm:` notes |
| `answer` | `ItemQuestion`, `Chitchat`, a supply or conditions question | `{ text, handle, titleId }` — **the turn's reply**: no `assistant-message` follows it. `handle` and `titleId` are null when the answer is about no single title |
| `clarify` | the planner found two readings that differ in their hard constraints (at most once per conversation), an item question names no title, or a pointer ("the third one") resolves to no card — before planning when no answer on screen has that many cards (then no `plan` frame precedes it) | `{ question, options: [string] }` — **the turn's reply**: no `assistant-message` follows it |
| `assistant-message` | search and refused turns only, once, after the trays and just before `done` | `{ text, degraded }` — the composed reply. A client must accept it before or after the turn's `picks` (the chat UI's own fixtures send it first) |
| `done` | last, always | every v1 key, plus `engine: "v2"`, `act`, `planSource`, `answer` (string or null) and `clarify` (object or null). `mode` is `Picks` · `NoneFound` · `Clarify` · `Answer` |

A v2 turn emits **no** `candidate`, `candidate-scored` or `candidate-reranked` frames; `done.results`
still carries every pick, so a client that only reads `done` keeps working.

**A card** is the v1 card plus three keys: `handle` (`"T3.2"` — answer 3, rank 2, the label the
user and the planner both use), `tier` (`Pick` · `NearMiss` · `BelowFloor` · `Unchecked`) and
`evidence` (the judge's one-line evidence for its verdict, or null). `satisfies` is `"yes"` /
`"partial"` / null as in v1.

**An answer turn** (`mode: "Answer"`) leaves the grid exactly as it was: no cards, no judge call, no
exposure. Its text is at most two sentences, every claim in it taken from the fact sheet it was
written from, and it is the turn's assistant text. A `Clarify` turn leaves the grid the same way. On
**resume**, the top-level `cards`, `belowFloor`, `nearMiss`, `unchecked`, `mode` and `reason` are the
newest *search* answer and its pages — an `Answer` or `Clarify` turn after it never becomes the grid
(`SearchTurnMode.LeavesGrid`); its own `turns[].cards` is empty. A client therefore keeps the grid
under the search answer it belongs to, not under the question asked beside it.

**Frame order, as the chat UI is built against it.** Search turn: `conversation` → `phase` →
`plan` → `phase`… → [`conflict`] → `picks`… (per batch) → `tray`… (`belowFloor`, `nearMiss`,
`unchecked`, each once, non-empty only) → [`none-found`] → [`degraded`] → `assistant-message` →
`done`. Answer / clarify turn: `conversation` → `phase` → `plan` → [`phase`] → [`degraded`] →
`answer` | `clarify` → `done`. Refused turn (spent allowance, open breaker): `conversation` → `phase` →
`degraded` → `assistant-message` (the cause's copy, `degraded: true`) → `done` (`failed: true`).

### Acts and what they do to the state

The conversation's state (`SearchConversations.StateJson`, `StateVersion`) is derived from the turn
record and never accepted from the client.

| Act | State | Grid |
| --- | --- | --- |
| `NewRequest` | hard constraints and soft preferences **replaced** | new picks |
| `Refine` | delta **merged** into the current state | new picks |
| `CritiqueSet` ("none of these are X") | every shown handle → rejected; the concept becomes hard | new picks, none of the rejected |
| `CritiqueItem` ("more heart than T2.1") | the critiqued titles → rejected; the direction shifts the soft preferences | new picks |
| `ItemQuestion` | unchanged | unchanged — `answer` |
| `Chitchat` | unchanged | unchanged — `answer` |

Negations come only from the user's words; standing conditions are never written into the state.
**People and seen titles (FD-3, lock-compliant default):** a person request returns only titles the
account has not seen; the reply counts the seen ones and never names them. A person's credits are
their own evidence (`person:Christopher Nolan/director`), so a pure person request is not sent to
the judge — its cards carry `satisfies: "yes"` with `FitSource = Facet`.

### Pre-v2 conversations

A conversation with no `StateJson` is rebuilt from its stored turns on its first v2 turn: the first
answer reads as `NewRequest`, later ones as `Refine`, every answer as `Picks`; stored cards become
`Tier = Pick` with `Satisfies` null (unjudged — never re-shown as judged); handles come from stored
rank; a legacy card with no `TitleId` is translated through `Titles` by `TmdbId`, and one that cannot
be translated stays excluded by its TMDB id.

### Stored, beyond v1

| Row | Column | Meaning |
| --- | --- | --- |
| `SearchConversations` | `StateJson`, `StateVersion` | the state after the newest turn; the version increments once per v2 turn |
| `SearchTurns` | `Act` | the planner's act, as its name; unknown values read as `NewRequest` |
| `SearchTurns` | `PlanSource` | `Llm` · `LlmRetry` · `CarriedForward`; unknown values read as `CarriedForward` |
| `SearchTurns` | `TruncationCount` | how many of the turn's calls finished on `length` |
| `SearchTurnCards` | `SatisfiesEvidence` | the judge's evidence line for the verdict |
