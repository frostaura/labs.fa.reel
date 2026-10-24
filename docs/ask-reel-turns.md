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
data: {"results":[…],"streamed":10,"kept":8,"droppedByFloor":2,"belowFloor":[…],"nearMiss":[…],"mode":"Picks","supply":null,"degraded":[],"reason":"…","timingsMs":null,"debug":null}
```

**The interim gate (trust-v1, WS-2 task 1).** The top sixteen candidates are judged against the
REQUEST — at most three batches of six in flight at once — and each card is placed by the judge's
`satisfies` verdict and the account's For-you bar:

| Verdict | Clears the bar | Goes to | Stored `Tier` | Exposure |
| --- | --- | --- | --- | --- |
| `yes` | yes | `results` (a pick), emitted as its batch returns | `Pick` | yes |
| `yes` | no | `belowFloor` | `BelowFloor` | no |
| `partial` | either | `nearMiss`, a collapsed "close, but not quite" tray | `NearMiss` | no |
| `no` | either | nowhere — not shown, not stored | — | no |
| none (a failed batch, the stub) | yes | `results`, after every judged pick, in retrieval order, **no why-line, no fit** | `Pick` | yes, `WhySource=None` |

The floor **never relaxes**. A fallback verdict is never cached, never shown as model output and
never logged as `Llm`. The reply (`assistant-message`) goes out **once**, just before the first pick
— not as the turn's opening frame — and carries `degraded: true` when every attempt to read the
question failed (its text is then the degraded copy, which quotes nobody).

**`mode: "NoneFound"`** when nothing reached `results`. The reply is then composed deterministically
from the `supply` report (seen, saved or dismissed, held out by conditions, under the bar, unscored,
and the drop-one relaxations that would unlock something — "already seen" is never one of them);
`supply` rides `done` on that turn only and is `null` on every other. `degraded` lists what did not
run as designed: `plan`, `judge:{failed}/{batches}`.

The `conversation` frame is first and always present on a live turn: it is how the client learns
the conversation id to remember, the turn id to reconnect to, and where in the transcript it sits.

**Array order is the contract.** Every card carries `blend`, the server's own ranking value, so a
client can explain the order — but it must never re-sort by it: `blend` is rounded for display and
the server's ordering carries a tiebreak the rounded number cannot reproduce.

A **replay** (the same `clientTurnId` again) emits the same grammar — `conversation`, then `done` —
carrying **every key a live `done` carries**, plus three a live turn cannot state: `replayed: true`,
`removed` (how many stored cards the eligibility re-filter dropped since), and `failed` /
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
| `GET /api/search/turns/{id}` | `{ status, reason, conflict, failureCode, cards, belowFloor, removed }` — **`202` while `Pending`**, unless the row is stale; see *Stale turns* |
| `DELETE /api/search/conversations/{id}` | `204`. Cards, turns, conversation. **Never exposures.** |
| `GET /api/search/conversations/latest` | **deprecated** — the same payload for the newest conversation |

The resume payload:

```jsonc
{
  "id": "…", "title": "…", "lastCardsAt": null,
  "lastTurnAt": "2026-09-11T00:54:21.301Z",
  "turns":   [{ "id": "…", "sequence": 0, "role": "user", "text": "…",
                "kind": "ask", "status": "Completed", "failureCode": null }],
  "cards":       [ /* same card shape as done.results */ ],
  "belowFloor":  [ /* the same card shape again — the tray */ ],
  "reason":  "…",        // the newest replayed turn's reason, verbatim
  "conflict": {          // the collision notice that turn emitted, or null — see below
    "excludedGenres":   ["comedy"],
    "excludedKeywords": ["anime"],
    "minYear": 2000, "maxYear": null,
    "message": "Your conditions permanently exclude comedy and anime, …"
  },
  "removed": 0,          // stored cards the eligibility re-filter dropped, across BOTH lists
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
   the last three answers' cards as handles `T{answer}.{position}`, newest answer first, in screen
   order — the list "the second one" resolves against.
6. **Write the rows Pending** — a user row and an assistant row for an `ask`, the assistant row
   alone for a `page` — then the headers, then `conversation`.
7. **Stream.**
8. **Persist after `done`**, on a scope of its own, under `CancellationToken.None`, inside a
   try/catch that logs and never throws. The request's context carries the pipeline's catalogue
   upserts and is never saved after the stream.
   The row also records `Mode`, `DegradedFlags`, `StageTimingsJson`, `ModelIds` (`plan`, `judge`,
   `embedding`) and `ReasoningEffort` (`provider-default` while no effort parameter is sent).
9. **Failure** — a crash stamps the row `Failed` with a `FailureCode` and stores no cards. An
   abandoned stream (`client_aborted`) stores **what was already on the wire**: the reply and every
   card emitted, in emission order, so the next turn's history and no-repeat set contain them.

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
