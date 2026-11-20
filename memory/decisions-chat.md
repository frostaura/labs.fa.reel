---
name: fa-reel-decisions-chat
description: "Ask Reel was delegated 'as you see fit' and every founder lock was kept: no filter override, delete but no erasure, tier rungs designed not built. The turn is server-owned because a client transcript is a fabrication vector."
type: decision
last_verified: 2026-09-11
---

# Live decisions — the Ask Reel chat programme

Delivered 2026-09-11 in two waves. The founder delegated it as "implement as you see fit", and the programme read that
as **delegating the engineering, not the spec**: every question that touched a lock was answered the conservative way,
with the alternative written down so a reversal is one founder decision rather than an archaeology exercise. Each of
these is still open as a question — `questions.md`.

## The seven delegated questions, and how each was taken
- **No filter override in chat.** Naming the collision and showing the conditions needed no decision and shipped; a
  per-turn "just this once" override did not.
- **Conversation delete is built; whole-account erasure is not.** The design for erasure is pseudonymisation —
  rewriting the account id on exposure rows to a tombstone — which still touches an append-only row, so it wants a
  founder decision before the first external user.
- **Tier rungs and a chat budget were proposed as config, not built.** Free still gets the whole pipeline. This is the
  single largest unpaid debt on the surface — `watch.md`.
- **The programme left `REEL_SEARCH_MODEL` untouched**, because whether the founder's extraction-model lock reaches the
  rerank model was never stated. The 2026-10-22 `gpt-6-luna` commit changed its default anyway — open in `questions.md`.
- **No MCP surface** (it collides with "Sign in with Trakt only") and **no ad-hoc lists** (the spec defines one list
  per profile with an auto-removal contract; extra lists need one written).
- **Probe conversations are deleted through the endpoint, never SQL.**

## Engineering calls, each with its why
- **An Ask Reel turn is a server-owned record.** *Why:* a client-supplied transcript and no-repeat list are a
  fabrication vector, and both were lost on reload anyway. What is persisted and what the client may send is the
  invariant in [`../CLAUDE.md`](../CLAUDE.md); contract and refusal codes live in `docs/ask-reel-turns.md`.
- **A standing exclusion is airtight on every channel, and only the user's own words are negations** (rule in
  [`../CLAUDE.md`](../CLAUDE.md)). *Why:* the "outside your request" channel exists to keep a negation from emptying
  the screen, so it may relax only what the user typed.
- **A page is stated, not inferred.** *Why:* inferring "more like this" from the last user turn broke the moment a
  turn was stopped mid-stream.
- **Every `/ask` refusal is a coded `{error, code}`, and the progressive floor compares at badge precision.** *Why:* a
  refusal the client has to parse from prose is a refusal it will eventually mis-handle, and a floor that compares at
  a different precision than the badge shows the user a card the copy says is excluded.
- **One mount guard per hook instance, held in a ref, with every deliberate abort named; anything aborted and
  unclaimed is an error, not silence.** *Why:* the StrictMode double-guard hang — mechanism in
  [`gotchas-runtime.md`](gotchas-runtime.md), rule in [`../CLAUDE.md`](../CLAUDE.md).
- **A handed-off question is minted into a document-scoped registry and spent exactly once, with the router state
  cleared *before* the send.** *Why:* `history.state` survives a reload, so a re-sent question was being derived by
  the server into a *page* of an answer nobody asked for.
- **The interpretation cache key digests the whole transcript and the shown set.** *Why:* keyed on the question alone,
  it served one thread's history-shaped intent — byte-identical reply included — to a fresh thread. A whole-history
  digest can only cost a cache miss; guessing which slice the adapter sends would couple two layers.
- **Pending turns are closed by boot reconciliation and by a three-minute stale rule** (mechanism in
  [`../CLAUDE.md`](../CLAUDE.md)). *Why:* only the request pipeline could ever close a turn, so an API death
  mid-stream left it Pending forever and every retry answered 409. The stale row gives up its idempotency key so the partial unique index cannot turn a wedged thread into a 500 on the
  retry's insert. A live Pending inside the threshold still gets its 409.
