# Ask Reel — the turn record

The server owns what a conversation is. A client sends a question, the conversation it belongs to,
and one id for this exchange; everything else — the history the model reads, the no-repeat set,
whether this is a new question or another page of the last one — is derived from rows the server
wrote. That inversion is the whole feature: a client-carried transcript is unverifiable, capped,
lost on reload and wrong the moment a second tab exists, and every one of those failures showed up
as the product forgetting what it had just said.

This page is the contract. The reasoning behind each decision lives in the doc-comments on
`SearchTurn`, `SearchTurnCard`, `SearchEndpoints.ReplayAsync` and `SearchConversationRetention`.

## The request

`POST /api/search/ask`

```jsonc
{
  "query": "something tense with a clever ending",  // 1..1000 chars, trimmed
  "conversationId": "…",                            // optional; null mints a new conversation
  "clientTurnId": "…",                              // REQUIRED; the idempotency key for this exchange
  "mediaTypes": ["movie"]                           // optional; "movie" | "movies" | "film" | "tv" | "show" | "series"
}
```

`history`, `shownTmdbIds` and `continuation` **do not exist**. They were removed, not renamed.

The client mints one `clientTurnId` per user message and resends it verbatim on a retry — a dropped
connection, a StrictMode double-invoke, an impatient second tap. All three are answered from the
stored turn.

## The responses

| Status | Shape | When |
| --- | --- | --- |
| `200` `text/event-stream` | the SSE stream below | a live turn, or the replay of a finished one |
| `400` | `{ error, code }` | `empty_query`, `query_too_long`, `client_turn_id_required` |
| `404` | `{ error, code }` | `conversation_not_found` — an id that is not the caller's, or is gone. **Never a fork.** |
| `409` | `{ error, code }` | `turn_in_progress` — this turn, or another one, is still running on this conversation |

`code` is stable and is what a client switches on. The prose in `error` is the user's and may
change; matching on it is how a UI ends up rendering a crash message for an honest refusal.

## The stream

SSE, one frame per event, `id:` line first, blank line last. A `:keepalive` comment frame goes out
every 10 seconds — it carries no event and a parser must skip it. Ids start at 1; `id: 0` is never
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
data: {"results":[…],"streamed":24,"kept":18,"droppedByFloor":6,"belowFloor":[…],"reason":"…","timingsMs":null,"debug":null}
```

The `conversation` frame is first and always present on a live turn: it is how the client learns
the conversation id to remember, the turn id to reconnect to, and where in the transcript it sits.

**Array order is the contract.** Every card carries `blend`, the server's own ranking value, so a
client can explain the order — but it must never re-sort by it: `blend` is rounded for display and
the server's ordering carries a tiebreak the rounded number cannot reproduce.

A **replay** (the same `clientTurnId` again) emits the same grammar — `conversation`, then `done` —
with three extra fields: `replayed: true`, `removed` (how many stored cards the eligibility
re-filter dropped since), and `failed` / `failureCode` when the stored turn was a failure. A replay
makes zero model calls, writes zero rows and writes **no exposures**.

## The read routes

| Route | Answers |
| --- | --- |
| `GET /api/search/conversations?limit=20` | `[{ id, title, turnCount, lastTurnAt, lastCardsAt }]` |
| `GET /api/search/conversations/{id}` | the resume payload below; `404` if it is not the caller's |
| `GET /api/search/turns/{id}` | `{ status, reason, failureCode, cards, removed }` — **`202` while `Pending`** |
| `DELETE /api/search/conversations/{id}` | `204`. Cards, turns, conversation. **Never exposures.** |
| `GET /api/search/conversations/latest` | **deprecated** — the same payload for the newest conversation |

The resume payload:

```jsonc
{
  "id": "…", "title": "…", "lastCardsAt": null,
  "turns":   [{ "sequence": 0, "role": "user", "text": "…", "kind": "ask", "status": "Completed" }],
  "cards":   [ /* same card shape as done.results */ ],
  "reason":  "…",        // the newest replayed turn's reason, verbatim
  "removed": 0,          // stored cards the eligibility re-filter dropped
  "lastQuery": "…",      // the last user turn
  "mediaTypes": ["tv"],  // the filter that turn ran under, in request tokens
  "shown": [603, 27205]  // the whole conversation's no-repeat set
}
```

`lastCardsAt: null` means **transcript only** — a conversation that predates stored results. Render
that as its own state; it is a different fact from a turn that honestly returned nothing.

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
5. **Derive** history (last 8 completed turns by `Sequence`), `shown` (distinct `TmdbId` over the
   conversation's cards) and `kind`, all read *before* this turn's own rows exist.
6. **Write the rows Pending** — a user row and an assistant row for an `ask`, the assistant row
   alone for a `page` — then the headers, then `conversation`.
7. **Stream.**
8. **Persist after `done`**, on a scope of its own, under `CancellationToken.None`, inside a
   try/catch that logs and never throws. The request's context carries the pipeline's catalogue
   upserts and is never saved after the stream.
9. **Failure** — a crash or an abandoned stream stamps the row `Failed` with a `FailureCode`
   (`client_aborted` on an abort) and stores no cards. A recoverable record, not an orphan.

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

## Retention

`ASK_RETENTION_DAYS`, default **90**. A conversation whose last turn is older than that is
hard-deleted — cards, then turns, then the conversation — by the nightly reconcile, beside the
enrichment sweep. Date-swept rather than depth-capped: a feed snapshot is replaced every build, a
person's transcript is not, and age is the only honest bound.

Neither retention nor the user-facing delete touches `ExposureEvents`. The log is append-only, every
accuracy number is computed from it, and a dangling `ExposureEvent.SourceTurnId` afterwards is the
designed state — the product did serve those cards, and that stays true after the transcript is
gone.
