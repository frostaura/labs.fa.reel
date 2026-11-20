---
name: labs-fa-reel-gotchas-runtime
description: "A filtered HNSW search silently returns a fraction of the rows it asks for and the LINQ cannot save you; StrictMode remounts hang the search at Thinking…; a dead backend still renders the app because the frontend is a PWA."
type: gotcha
last_verified: 2026-09-11
---

# Gotchas — runtime, scoring and the front end

## Retrieval and scoring
- **A filtered vector search silently returns a FRACTION of the rows it asks for.** An HNSW index scan collects a
  fixed candidate window and the query's own `WHERE` is applied to that window *afterwards*; at pgvector's default the
  scan then stops. So an ordered `LIMIT 40` filtered by eligibility returns *the eligible members of the 40 nearest
  titles overall*, not the 40 nearest eligible ones — and Reel's eligibility rule removes exactly what a heavy
  watcher's catalogue is made of, so the arm starves further the more the product is used. **Writing the predicate
  "before" the ordering does nothing**; Postgres pushes the ordered limit into the index either way, which is why the
  symptom is intermittent at fixture scale and permanent at catalogue scale. A query with a second ordering key
  usually escapes by making the planner sort — luck, not a guarantee. Fixed per connection; the rule is in
  [`../CLAUDE.md`](../CLAUDE.md).
- **A calibrator fit on pairs with no rank signal is a constant.** Isotonic regression over pairs with near-zero
  correlation maps everything to the mean, which is honest and useless — hence the bias-shift fallback below a
  correlation threshold. De-mean per artifact too: pooling pairs across artifacts whose score levels swing a point
  between retrains blurs the map.
- **The residual head auto-resets on load, not on write.** After a version bump the stored row keeps showing the *old*
  algorithm version, bias and update count until the next observation, while the served head is already the zero
  state. Never read that row as the live head.

## Front end
- **StrictMode mounts, unmounts and remounts every effect, and refs survive the remount.** A mount guard created
  inside an effect body therefore exists twice, and the first instance's deferred abort fires after the second has
  mounted — cancelling the request the page itself just sent and leaving the UI at "Thinking…" forever, because the
  reducer treats an abort as benign. Found by instrumenting, not by reasoning. The rule is in
  [`../CLAUDE.md`](../CLAUDE.md).
- **RTK Query's `data` is the last result the hook saw and survives `skip`** — a deleted thread's transcript came
  straight back after starting a new one, until the resume effect was gated on the same condition as the query.
- **Never invalidate the `Feed` RTK tag from a per-title reaction.** Home keys every infinite row on the snapshot's
  timestamp, so a refetch remounts the rows and discards the pages the user scrolled — acting on one card used to
  reset the whole list mid-triage. Reactions prune locally; a fresher snapshot is *held* and surfaced as "New picks
  ready".
- **A dead stack still renders the app.** The frontend is a PWA with a precaching service worker, so the cached shell
  loads normally and the only symptom of a dead backend is the sign-in error. Confirm something is serving (`curl -s
  http://localhost:5173/health`) before debugging OAuth — `000` means the stack is down, not that Trakt is broken.
- **The stack must be served on 5173, not the compose default.** The Trakt app registration's only redirect URI names
  that port, and the registration — not the app — is authoritative; moving the stack silently breaks sign-in after
  consent. When the docker gateway owns 5173, do not also start the dev server.
- **A UI filter the server never sees will empty the screen.** The media filter rides on the request and narrows
  discovery; the "done" reason derives from the final count so the footer can never describe a result set the user
  cannot see; discovery widens through relax levels before an empty answer is treated as a verdict.

## Search behaviour
- **Ask Reel has TWO interpretation prompts, and the live one is the agent's**, not the standalone interpreter's,
  which is only a fallback. A whole session was lost adding a field everywhere except the agent prompt. Whenever a
  field is added to the intent, update both and verify against the running stack.
- **Negation is a design contract, not an implementation detail** — three parts hold it together and undoing any one
  of them quietly stops negations working. The contract and the reasoning behind each part:
  [`decisions-retrieval.md`](decisions-retrieval.md).

_Layout-shift traps — what scores CLS on a results grid and what does not — are in [`gotchas-layout.md`](gotchas-layout.md)._
