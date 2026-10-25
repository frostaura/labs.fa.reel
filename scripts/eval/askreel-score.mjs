// Deterministic scorer for Ask Reel golden runs. Pure: it reads a recording (with its frozen truth
// facts), the scenario definitions, the price table and the gate table, and returns metrics. No API,
// no database, no paid call — so `replay` is exactly this function, and CI can run it.
//
// Metric definitions are pre-registered in docs/ask-reel-eval.md; change them there first, and record
// every change made after a baseline existed in eval/askreel/gates.json "amendments".
import { createHash } from "node:crypto";

// Template families the v1 pipeline emits when a model call failed. Matching one is a fallback, and a
// stub why-line on a card is ALSO a grounding violation, because it is invented copy shown as an
// explanation (labelled Llm on v1).
export const STUB_WHY = /^an? [\p{L}\p{N} &'’-]+ pick aligned with your taste\.?$/iu;
export const STUB_REPLY = /^here are some .+ picks for [“"']/iu;
// A why-line that argues against its own pick ("isn't a ninja film", "not quite …", "though its mystery
// focus may be less to your taste", "though … is a series rather than your usual film pick").
export const HEDGE_WHY = new RegExp([
  String.raw`\b(?:isn['’]t|is not|aren['’]t|not (?:really |quite |exactly )?(?:a|an|about)|doesn['’]t (?:have|feature|involve|include)|lacks?|though it['’]s not|not strictly)\b`,
  // A concession clause that then argues against the pick: "though … less …", "but … rather than …".
  String.raw`\b(?:though|although|even if|while|but|yet)\b[^.;]*\b(?:less|rather than|weaker|not (?:your|quite|really|exactly|as)|may not|might not|isn['’]t|won['’]t|may (?:feel|test|land)|outweighs?|lower for you|than (?:you (?:prefer|favou?r|usually)|your usual)|(?:pace|it) is slow|more [\w-]+(?: [\w-]+)? than|over (?:brisk|your)|dominates)\b`,
  String.raw`\bless (?:aligned|suited|to your taste|your usual|certain)\b`,
  String.raw`\bnot your (?:usual|typical)\b`,
].join("|"), "iu");
// English copy that slipped into another script mid-sentence (e.g. "your film-first偏好").
export const NON_LATIN = /(?!\p{Script=Latin})(?!\p{Script=Common})(?!\p{Script=Inherited})\p{L}/u;

// Typed sources that say a card's copy is not model output. "Templater" is a fallback only on an
// in-request pick (the model returned no why for a card it ranked); on an outside-request card it is the
// deterministic by-design caption stating which exclusion the card trips. "Generic" is the captionless
// filmography list, by design.
const FALLBACK_SOURCES = new Set(["Fallback", "Stub"]);
const isFallbackSource = (src, card) => Boolean(src) && (FALLBACK_SOURCES.has(src) || (src === "Templater" && !card.outsideRequest));
// A turn that failed or never ran is charged the client's cap: a user who got no answer waited that long.
export const DEFAULT_TURN_CAP_MS = 300000;
const COMMON_WORDS = new Set(["the", "a", "an", "and", "or", "of", "in", "on", "out", "to", "for", "with", "it", "is", "me", "you", "we", "us", "one", "other", "this", "that", "what", "more", "less", "all", "up", "down", "here", "there", "now", "then", "back", "home", "love", "time", "night", "day", "life", "man", "woman", "good", "bad", "new", "old", "big", "little", "first", "last", "same", "something", "nothing", "everything"]);

const norm = (s) => (s ?? "").toLowerCase().normalize("NFKC").replace(/[“”"‘’'`]/g, "").replace(/\s+/g, " ").trim();

// An echo is the reply quoting the user's message back (the v1 `StubReply` shape: “<message>”), or
// repeating a message of five or more words verbatim. Restating a two-word topic ("romantic comedies")
// is not an echo.
export function isEcho(query, reply) {
  const q = norm(query);
  if (q.length < 4 || !reply) return false;
  const quoted = (reply ?? "").toLowerCase().normalize("NFKC").replace(/\s+/g, " ");
  const raw = (query ?? "").toLowerCase().normalize("NFKC").replace(/\s+/g, " ").trim();
  for (const [open, close] of [["“", "”"], ['"', '"'], ["‘", "’"], ["'", "'"]]) {
    if (quoted.includes(`${open}${raw}${close}`)) return true;
  }
  return q.split(" ").length >= 5 && norm(reply).includes(q);
}

function pct(values, p) {
  const v = values.filter((x) => typeof x === "number" && Number.isFinite(x)).sort((a, b) => a - b);
  if (v.length === 0) return null;
  const idx = Math.min(v.length - 1, Math.max(0, Math.ceil((p / 100) * v.length) - 1));
  return v[idx];
}

const mean = (xs) => (xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null);
const rate = (num, den) => (den ? num / den : null);

function mediaScope(media) {
  if (!media || media.length === 0) return "Any";
  const tv = media.some((m) => ["tv", "show", "series"].includes(m));
  const mv = media.some((m) => ["movie", "movies", "film"].includes(m));
  if (tv && mv) return "Any";
  return tv ? "Show" : "Movie";
}

function costOf(ledger, prices) {
  let total = 0;
  const rows = [];
  for (const r of ledger) {
    const p = prices.models[r.model] ?? null;
    let usd = null;
    if (p) {
      const prompt = r.promptTokens || (r.completionTokens ? 0 : r.tokensUsed);
      usd = (prompt * p.inputPerMillion + r.completionTokens * (p.outputPerMillion ?? 0)) / 1e6;
      total += usd;
    }
    rows.push({ ...r, usd });
  }
  return { total, rows, unpriced: rows.filter((r) => r.usd === null && r.provider === "OpenRouter").map((r) => r.model) };
}

function referencedTitles(turn, earlierTurns) {
  // v2: handles "T2.3" (or {turn, rank}) resolved against the recording's own display order.
  if (Array.isArray(turn.references)) {
    return turn.references.map((h) => {
      const m = typeof h === "string" ? /^T(\d+)\.(\d+)$/.exec(h) : null;
      const tn = m ? Number(m[1]) : h?.turn;
      const rk = m ? Number(m[2]) : h?.rank;
      return earlierTurns[tn - 1]?.cards[rk - 1]?.titleId ?? null;
    }).filter(Boolean);
  }
  return null;
}

// opts.actObservable: the engine emits an act (v2). v1 emits none, so act is "no data" for v1 rather
// than a property of turn position. opts.turnCapMs: what a failed or unrun turn costs in latency.
export function evaluateTurn(def, t, index, scenarioTurns, facts, opts = {}) {
  const e = def?.expect ?? {};
  const turnCapMs = opts.turnCapMs ?? DEFAULT_TURN_CAP_MS;
  const eligible = new Set(facts.eligible);
  const picks = t.cards ?? [];
  const shown = [...picks, ...(t.belowFloor ?? []), ...(t.nearMiss ?? [])];
  const server = facts.server?.[t.turnId] ?? null;
  const r = { turn: index + 1, query: t.query, status: t.status, failed: Boolean(t.failed), notRun: Boolean(t.notRun), picks: picks.length, checks: {} };

  // Locks: every shown title passes eligibility, and a single-media request shows only that media.
  r.leaks = shown.filter((c) => c.titleId && !eligible.has(c.titleId)).map((c) => c.name ?? c.titleId);
  // The media scope is the request's filter, or what the user's words asked for ("only movies"). A pick
  // against the request's OWN filter is a broken contract (gated); against the words alone it is ungated.
  const filterScope = mediaScope(t.mediaTypes);
  const scope = e.media ?? filterScope;
  const wrongMedia = (sc) => (sc === "Any" ? [] : picks.filter((c) => facts.media?.[c.titleId] && facts.media[c.titleId] !== sc).map((c) => c.name));
  r.mediaViolations = wrongMedia(scope);
  r.mediaFilterViolations = wrongMedia(filterScope);

  const mustNotMembers = new Set((e.mustNot ?? []).flatMap((k) => facts.truth?.[k]?.members ?? []));
  r.mustNotViolations = picks.filter((c) => mustNotMembers.has(c.titleId)).map((c) => c.name);

  if (e.concept) {
    const tr = facts.truth?.[e.concept];
    const members = new Set(tr?.members ?? []);
    const supply = tr?.supply?.[scope] ?? tr?.supply?.Any ?? null;
    const first8 = picks.slice(0, 8);
    const precise = first8.filter((c) => members.has(c.titleId) && !mustNotMembers.has(c.titleId));
    r.supply = supply;
    r.offTopic = first8.filter((c) => !members.has(c.titleId)).map((c) => c.name);
    if (supply && supply.eligible === 0) {
      // Honest only when the turn ran and SAID none were found; a 500, a timeout or an unrun turn is not.
      r.checks.honestEmpty = !t.failed && t.mode === "NoneFound" && picks.length === 0;
    } else if (first8.length > 0) {
      r.precision = precise.length / first8.length;
    } else if (t.failed) {
      r.precision = 0; // a failure must never improve precision by leaving the denominator
    } else {
      r.missedSupply = true;
    }
  } else if ((e.mustNot ?? []).length > 0 && (picks.length > 0 || t.failed)) {
    // Negation-only scenarios: precision is "nothing the user ruled out", over the first 8.
    const first8 = picks.slice(0, 8);
    r.precision = first8.length ? first8.filter((c) => !mustNotMembers.has(c.titleId)).length / first8.length : 0;
  }

  const expectedMode = e.mode ?? (r.checks.honestEmpty !== undefined ? "NoneFound" : null);
  if (expectedMode && expectedMode !== "Any") r.checks.mode = t.mode === expectedMode;
  if (e.act && (t.act || opts.actObservable)) r.checks.act = !t.failed && t.act === e.act;
  if (typeof e.conflict === "boolean") r.checks.conflict = !t.failed && Boolean(t.conflict) === e.conflict;

  if (e.references?.length) {
    const expected = e.references.map((ref) => scenarioTurns[ref.turn - 1]?.cards?.[ref.rank - 1] ?? null);
    if (t.notRun || t.failed) {
      r.checks.reference = false;
    } else if (expected.some((x) => x === null)) {
      r.referenceUnresolvable = true; // the earlier turn never showed that card; the premise is void
    } else {
      const v2 = referencedTitles(t, scenarioTurns.slice(0, index));
      if (v2) {
        r.checks.reference = expected.every((x) => v2.includes(x.titleId));
      } else {
        const named = (t.intent?.referenceTitles ?? []).map(norm);
        r.checks.reference = expected.every((x) => named.some((n) => n && (n.includes(norm(x.name)) || norm(x.name).includes(n))));
      }
    }
  }

  if (e.rejectTurns?.length) {
    const rejected = new Set(e.rejectTurns.flatMap((k) => (scenarioTurns[k - 1]?.cards ?? []).map((c) => c.titleId)));
    r.repeatsRejected = picks.filter((c) => rejected.has(c.titleId)).map((c) => c.name);
    const onTopic = r.checks.honestEmpty ?? (r.precision === undefined ? true : r.precision >= 0.75);
    r.checks.critique = r.repeatsRejected.length === 0 && onTopic && !t.failed;
  }

  if (e.noRepeatTurns?.length) {
    const earlier = new Set(e.noRepeatTurns.flatMap((k) => (scenarioTurns[k - 1]?.cards ?? []).map((c) => c.titleId)));
    r.repeats = picks.filter((c) => earlier.has(c.titleId)).map((c) => c.name);
    r.checks.noRepeat = r.repeats.length === 0 && !t.failed;
  }

  // Grounding: invented explanations shown as model output, and replies naming titles not on screen.
  const stubWhys = picks.filter((c) => c.why && STUB_WHY.test(c.why.trim()));
  const earlierCards = new Set(scenarioTurns.slice(0, index + 1).flatMap((x) => (x.cards ?? []).concat(x.belowFloor ?? []).map((c) => c.titleId)));
  const userText = norm(scenarioTurns.slice(0, index + 1).map((x) => x.query).join(" "));
  // A title counts as named only when it appears with its own capitalisation and is not made of common
  // words alone — "the other one" and "in and out" are prose, not the films The Other and In and Out.
  const namedOff = (facts.named?.[t.turnId] ?? []).filter((n) => !earlierCards.has(n.titleId) && !userText.includes(norm(n.name))
    && (t.reply ?? "").includes(n.name) && !n.name.toLowerCase().split(/\s+/).every((w) => COMMON_WORDS.has(w.replace(/[^\p{L}]/gu, ""))));
  r.groundingViolations = stubWhys.length + namedOff.length;
  r.groundingDetail = [...stubWhys.map((c) => `stub why on ${c.name}`), ...namedOff.map((n) => `reply names ${n.name}, not on screen`)];
  r.hedgingWhys = picks.filter((c) => c.why && HEDGE_WHY.test(c.why)).map((c) => `${c.name}: ${c.why}`);
  r.nonLatinWhys = picks.filter((c) => c.why && NON_LATIN.test(c.why)).map((c) => `${c.name}: ${c.why}`);
  r.echo = isEcho(t.query, t.reply);

  // Only a source that SAYS it is a fallback counts here (typed sources, WS-1/WS-5). "Generic" is the
  // by-design captionless filmography list, not a failed call; stub copy labelled Llm is caught above.
  const fallbackWhySources = picks.map((c) => [c.whySource ?? c.fitSource ?? server?.whySources?.[c.titleId], c]).filter(([src, c]) => isFallbackSource(src, c)).map(([src]) => src);
  r.fallback = Boolean(t.failed || (t.reply && STUB_REPLY.test(t.reply.trim())) || stubWhys.length > 0 || fallbackWhySources.length > 0 || (t.degraded ?? []).length > 0);
  r.fallbackDetail = [
    t.notRun ? "never ran (an earlier turn ended the scenario)" : t.failed ? `failed (${t.error ?? t.status}${server?.failureCode ? `/${server.failureCode}` : ""})` : null,
    t.reply && STUB_REPLY.test(t.reply.trim()) ? "stub reply" : null,
    stubWhys.length ? `${stubWhys.length} stub why-lines` : null,
    fallbackWhySources.length ? `${fallbackWhySources.length} cards with WhySource ${[...new Set(fallbackWhySources)].join("/")}` : null,
    (t.degraded ?? []).length ? `degraded: ${JSON.stringify(t.degraded)}` : null,
  ].filter(Boolean);
  r.latency = t.failed
    ? { totalMs: Math.max(t.totalMs ?? 0, turnCapMs), firstCardMs: Math.max(t.firstCardMs ?? 0, turnCapMs), serverMs: server?.latencyMs ?? null, charged: true }
    : { totalMs: t.totalMs, firstCardMs: t.firstCardMs, serverMs: server?.latencyMs ?? null };
  r.cardsShown = picks.slice(0, 8).map((c) => c.name);
  return r;
}

// The planned turns of a recorded scenario: what ran, then every turn the definition has that never ran
// (a refused or dead turn ends a scenario). Unrun turns are scored as failed turns with failed checks.
function plannedTurns(s, def) {
  const planned = [...s.turns];
  for (let i = s.turns.length; i < (def?.turns?.length ?? 0); i++) {
    const d = def.turns[i];
    planned.push({ query: d.query, status: null, failed: true, notRun: true, error: "not run", turnId: null, mode: null,
      cards: [], belowFloor: [], nearMiss: [], reply: null, totalMs: null, firstCardMs: null, mediaTypes: d.mediaTypes ?? def.mediaTypes ?? null });
  }
  return planned;
}

// Identity of a scenario as scored: its id and the queries it planned. compare refuses two runs whose
// fingerprints differ, so a run with scenarios missing or reworded can never be scored against a baseline.
const sha = (x) => createHash("sha256").update(JSON.stringify(x)).digest("hex").slice(0, 16);
export function scenarioFingerprint(id, queries) { return sha([id, queries]); }

export function score(recording, scenarios, prices, gates) {
  const defs = new Map(scenarios.map((s) => [s.id, s]));
  const facts = recording.facts ?? {};
  const turnCapMs = recording.turnCapMs ?? DEFAULT_TURN_CAP_MS;
  const actObservable = recording.scenarios.some((s) => s.turns.some((t) => t.act));
  const perScenario = [];
  const turns = [];
  for (const s of recording.scenarios) {
    const def = defs.get(s.id);
    const planned = plannedTurns(s, def);
    const evals = planned.map((t, i) => evaluateTurn(def?.turns?.[i], t, i, planned, facts, { actObservable, turnCapMs }));
    const missingTurns = planned.length - s.turns.length;
    const fingerprint = scenarioFingerprint(s.id, planned.map((t) => t.query));
    // A recording scored against definitions that were edited after it ran measures a different question.
    const stale = !def || s.turns.some((t, i) => def.turns?.[i]?.query !== t.query);
    perScenario.push({ id: s.id, set: s.set, categories: s.categories, conversationId: s.conversationId, fingerprint, stale, turns: evals, missingTurns });
    for (const ev of evals) turns.push({ ...ev, scenario: s.id, categories: s.categories });
  }

  const ok = (name) => turns.filter((t) => t.checks[name] !== undefined);
  const passRate = (name) => { const xs = ok(name); return { value: rate(xs.filter((t) => t.checks[name]).length, xs.length), n: xs.length }; };
  const precisionTurns = turns.filter((t) => typeof t.precision === "number");
  const conceptPrecision = precisionTurns.filter((t) => t.supply);
  const succeeded = turns.filter((t) => !t.failed);
  const cardTimed = turns.filter((t) => typeof t.latency.firstCardMs === "number");
  const cost = costOf(recording.ledger ?? [], prices);
  const attempted = turns.length;
  const traktCalls = (recording.ledger ?? []).filter((r) => r.provider === "Trakt").reduce((n, r) => n + r.calls, 0);

  const m = {
    constraintPrecisionAt8: { value: mean(conceptPrecision.map((t) => t.precision)), n: conceptPrecision.length },
    negationPrecisionAt8: { value: mean(precisionTurns.filter((t) => !t.supply).map((t) => t.precision)), n: precisionTurns.filter((t) => !t.supply).length },
    honestEmptyCompliance: passRate("honestEmpty"),
    referenceAccuracy: passRate("reference"),
    critiqueCompliance: passRate("critique"),
    actClassification: passRate("act"),
    conflictDetection: passRate("conflict"),
    modeAccuracy: passRate("mode"),
    noRepeatCompliance: passRate("noRepeat"),
    groundingViolations: { value: turns.reduce((n, t) => n + t.groundingViolations, 0), n: attempted },
    echoReplies: { value: turns.filter((t) => t.echo).length, n: attempted },
    fallbackRate: { value: rate(turns.filter((t) => t.fallback).length, attempted), n: attempted },
    exclusionLeaks: { value: turns.reduce((n, t) => n + t.leaks.length, 0), n: attempted },
    mediaFilterViolations: { value: turns.reduce((n, t) => n + t.mediaFilterViolations.length, 0), n: attempted },
    mediaMismatches: { value: turns.reduce((n, t) => n + t.mediaViolations.length, 0), n: attempted },
    mustNotViolations: { value: turns.reduce((n, t) => n + t.mustNotViolations.length, 0), n: attempted },
    hedgingWhyLines: { value: turns.reduce((n, t) => n + t.hedgingWhys.length, 0), n: attempted },
    nonLatinWhyLines: { value: turns.reduce((n, t) => n + t.nonLatinWhys.length, 0), n: attempted },
    missedSupply: { value: turns.filter((t) => t.missedSupply).length, n: attempted },
    failedTurns: { value: turns.filter((t) => t.failed).length, n: attempted },
    failedTurnRate: { value: rate(turns.filter((t) => t.failed).length, attempted), n: attempted },
    notRunTurns: { value: turns.filter((t) => t.notRun).length, n: attempted },
    // Every planned turn counts; a failed or unrun turn is charged the cap, so a timeout can never
    // improve p95 by leaving the sample.
    turnLatencyMs: { p50: pct(turns.map((t) => t.latency.totalMs), 50), p95: pct(turns.map((t) => t.latency.totalMs), 95), n: turns.length, chargedAtCap: turns.filter((t) => t.latency.charged).length, capMs: turnCapMs },
    serverLatencyMs: { p50: pct(succeeded.map((t) => t.latency.serverMs), 50), p95: pct(succeeded.map((t) => t.latency.serverMs), 95), n: succeeded.filter((t) => t.latency.serverMs !== null).length },
    firstCardMs: { p50: pct(cardTimed.map((t) => t.latency.firstCardMs), 50), p95: pct(cardTimed.map((t) => t.latency.firstCardMs), 95), n: cardTimed.length, chargedAtCap: cardTimed.filter((t) => t.latency.charged).length, capMs: turnCapMs },
    costPerTurnUsd: { value: attempted ? cost.total / attempted : null, n: attempted, totalUsd: cost.total, unpriced: cost.unpriced },
    traktCalls: { value: traktCalls, n: attempted },
    exposuresWritten: { value: recording.exposuresWritten ?? null, n: attempted },
  };

  // Gates. Latency gates apply only to runs at concurrency ≤ 2 that held every build slot for their whole
  // length (§4.12, §5.0 rule 1, §7 rule 5); any other run reports latency but never passes or fails on it.
  const latencyValid = recording.concurrency <= 2 && recording.buildSlotsHeld === true;
  const latencyWhy = recording.concurrency > 2 ? "concurrency > 2" : "build slots not held during the run";
  const results = [];
  for (const g of gates.gates) {
    const metric = m[g.metric];
    const v = g.stat ? metric?.[g.stat] : metric?.value;
    let status;
    if (g.latency && !latencyValid) status = `not-gated (${latencyWhy})`;
    else if (v === null || v === undefined || metric.n === 0) status = "no data";
    else status = (g.op === ">=" ? v >= g.target : v <= g.target) ? "pass" : "FAIL";
    results.push({ ...g, value: v ?? null, n: metric?.n ?? 0, status });
  }

  const byCategory = {};
  for (const t of turns) for (const c of t.categories) {
    const b = (byCategory[c] ??= { turns: 0, failedChecks: 0, checks: 0, fallback: 0 });
    b.turns++; b.fallback += t.fallback ? 1 : 0;
    for (const v of Object.values(t.checks)) { b.checks++; if (!v) b.failedChecks++; }
  }

  return {
    summary: {
      label: recording.label, engine: recording.engine, build: recording.build ?? null, buildInfo: recording.buildInfo ?? null, gitSha: recording.gitSha, snapshotId: recording.snapshotId,
      artifact: facts.artifact ?? null, concurrency: recording.concurrency, scenarios: perScenario.length, turns: attempted,
      fingerprint: sha(perScenario.map((p) => [p.id, p.fingerprint]).sort((a, b) => (a[0] < b[0] ? -1 : 1))),
      staleScenarios: perScenario.filter((p) => p.stale).map((p) => p.id),
      modelKey: recording.modelKey ?? "unrecorded", buildSlotsHeld: recording.buildSlotsHeld === true,
      wallSeconds: recording.wallSeconds, costUsd: cost.total, latencyGated: latencyValid,
      gatesPassed: results.filter((r) => r.status === "pass").length, gatesFailed: results.filter((r) => r.status === "FAIL").length,
    },
    metrics: m, gates: results, byCategory, cost: cost.rows, scenarios: perScenario,
  };
}

const fmt = (v, digits = 3) => (v === null || v === undefined ? "—" : typeof v === "number" ? (Number.isInteger(v) ? String(v) : v.toFixed(digits)) : String(v));

export function renderReport(recording, metrics) {
  const s = metrics.summary;
  const L = [];
  L.push(`# Ask Reel golden run — ${s.label}`, "");
  L.push(`- Engine: ${s.engine ?? "unspecified"} · API build ${s.build ?? "unrecorded"} · runner ${s.gitSha ?? "?"} · snapshot \`${s.snapshotId ?? "?"}\` · artifact \`${s.artifact?.id ?? "?"}\` (v${s.artifact?.version ?? "?"})`);
  L.push(`- ${s.scenarios} scenarios, ${s.turns} turns, concurrency ${s.concurrency}, ${s.wallSeconds ?? "?"} s wall, $${fmt(s.costUsd, 4)} from the ledger`);
  L.push(`- Latency gates ${s.latencyGated ? "APPLY (concurrency ≤ 2, every build slot held)" : `do NOT apply (${s.concurrency > 2 ? "concurrency > 2" : "build slots not held during the run"}) — latency below is reported, not gated`}`);
  L.push(`- Model key: ${s.modelKey} · scenario fingerprint \`${s.fingerprint}\``);
  if (s.staleScenarios.length) L.push(`- **${s.staleScenarios.length} scenario(s) were edited after this recording ran** (${s.staleScenarios.join(", ")}): their rows score the old queries against new expectations; compare refuses this recording`);
  L.push(`- Started ${recording.startedAt}, finished ${recording.finishedAt}`, "");
  L.push("## Gates", "", "| Metric | Value | n | Gate | Status |", "|---|---|---|---|---|");
  for (const g of metrics.gates) L.push(`| ${g.label} | ${fmt(g.value)} | ${g.n} | ${g.op} ${g.target} | ${g.status} |`);
  L.push("", "## All metrics", "", "| Metric | Value | n |", "|---|---|---|");
  for (const [k, v] of Object.entries(metrics.metrics)) {
    const val = v.value !== undefined ? fmt(v.value) : `p50 ${fmt(v.p50)} / p95 ${fmt(v.p95)}`;
    L.push(`| ${k} | ${val} | ${v.n} |`);
  }
  L.push("", "## By category", "", "| Category | Turns | Checks failed / run | Fallback turns |", "|---|---|---|---|");
  for (const [k, v] of Object.entries(metrics.byCategory).sort()) L.push(`| ${k} | ${v.turns} | ${v.failedChecks} / ${v.checks} | ${v.fallback} |`);
  L.push("", "## Cost (ledger delta over the run)", "", "| Provider | Model | Purpose | Calls | Prompt tok | Completion tok | USD |", "|---|---|---|---|---|---|---|");
  for (const r of metrics.cost) L.push(`| ${r.provider} | ${r.model || "—"} | ${r.purpose || "—"} | ${r.calls} | ${r.promptTokens} | ${r.completionTokens} | ${fmt(r.usd, 4)} |`);
  L.push("", "## Per-scenario diffs (turns with a failed check, a leak, a fallback or a grounding violation)", "");
  for (const sc of metrics.scenarios) {
    const bad = sc.turns.filter((t) => Object.values(t.checks).some((v) => !v) || t.leaks.length || t.mediaViolations.length || t.fallback || t.groundingViolations || t.echo || t.mustNotViolations.length || t.nonLatinWhys.length);
    if (!bad.length && !sc.missingTurns) continue;
    L.push(`### ${sc.id} (${sc.categories.join(", ")})`);
    if (sc.missingTurns) L.push(`- ${sc.missingTurns} turn(s) never ran (an earlier turn was refused); scored as failed turns`);
    for (const t of bad) {
      const failed = Object.entries(t.checks).filter(([, v]) => !v).map(([k]) => k);
      L.push(`- T${t.turn} “${t.query}” — ${t.picks} picks${typeof t.precision === "number" ? `, precision@8 ${fmt(t.precision, 2)}` : ""}${t.supply ? `, supply ${t.supply.eligible} eligible of ${t.supply.matching}` : ""}`);
      if (failed.length) L.push(`  - failed: ${failed.join(", ")}`);
      if (t.leaks.length) L.push(`  - exclusion leaks: ${t.leaks.join("; ")}`);
      if (t.mediaViolations.length) L.push(`  - wrong media: ${t.mediaViolations.join("; ")}`);
      if (t.mustNotViolations.length) L.push(`  - ruled-out titles shown: ${t.mustNotViolations.join("; ")}`);
      if (t.offTopic?.length) L.push(`  - off-topic in first 8: ${t.offTopic.join("; ")}`);
      if (t.repeatsRejected?.length) L.push(`  - re-showed rejected: ${t.repeatsRejected.join("; ")}`);
      if (t.repeats?.length) L.push(`  - repeated earlier cards: ${t.repeats.join("; ")}`);
      if (t.fallback) L.push(`  - fallback: ${t.fallbackDetail.join("; ")}`);
      if (t.groundingViolations) L.push(`  - grounding: ${t.groundingDetail.join("; ")}`);
      if (t.echo) L.push("  - reply echoes the user's message");
      if (t.nonLatinWhys.length) L.push(`  - non-Latin script in English copy: ${t.nonLatinWhys.join("; ")}`);
    }
    L.push("");
  }
  return L.join("\n") + "\n";
}

// Per-turn counts compared PAIRED: only over turns that ran in both runs. A crash then cannot hide a
// leak (the crashed turn is out of both sides) and is charged separately as a newly failed turn.
const PAIRED = {
  exclusionLeaks: (t) => t.leaks.length,
  groundingViolations: (t) => t.groundingViolations,
  echoReplies: (t) => (t.echo ? 1 : 0),
  mediaFilterViolations: (t) => (t.mediaFilterViolations ?? []).length,
  mustNotViolations: (t) => t.mustNotViolations.length,
  hedgingWhyLines: (t) => t.hedgingWhys.length,
  nonLatinWhyLines: (t) => (t.nonLatinWhys ?? []).length,
};

// Why two runs cannot be compared at all, or null. Different scenarios, snapshot or model artifact
// change the expectations themselves; re-run the baseline on the new set instead.
export function incomparable(current, baseline) {
  const c = current.summary, b = baseline.summary;
  if (!b.fingerprint || !c.fingerprint) return "a metrics file predates scenario fingerprints — re-score it with `askreel.mjs replay`";
  for (const [name, run] of [["current", current], ["baseline", baseline]]) {
    const stale = run.scenarios.filter((s) => s.stale).map((s) => s.id);
    if (stale.length) return `the ${name} recording no longer matches its scenario definitions (${stale.length}: ${stale.slice(0, 8).join(", ")}) — it measured different queries`;
  }
  if (c.fingerprint !== b.fingerprint) {
    const bs = new Map(baseline.scenarios.map((s) => [s.id, s.fingerprint]));
    const cs = new Map(current.scenarios.map((s) => [s.id, s.fingerprint]));
    const missing = [...bs.keys()].filter((id) => !cs.has(id));
    const extra = [...cs.keys()].filter((id) => !bs.has(id));
    const changed = [...cs.keys()].filter((id) => bs.has(id) && bs.get(id) !== cs.get(id));
    return `the scenario sets differ (missing ${missing.length}: ${missing.slice(0, 8).join(", ")}; extra ${extra.length}: ${extra.slice(0, 8).join(", ")}; reworded ${changed.length}: ${changed.slice(0, 8).join(", ")})`;
  }
  if (c.turns !== b.turns) return `planned turn counts differ (${c.turns} vs ${b.turns})`;
  if ((c.snapshotId ?? null) !== (b.snapshotId ?? null)) return `snapshots differ (${c.snapshotId} vs ${b.snapshotId}) — eligible sets moved; re-baseline on the new snapshot`;
  if ((c.artifact?.id ?? null) !== (b.artifact?.id ?? null)) return `model artifacts differ (${c.artifact?.id} vs ${b.artifact?.id}) — predicted-value expectations moved; re-baseline`;
  return null;
}

// Regression check against a recorded baseline (gate policy, docs/ask-reel-eval.md §Gate policy).
// Returns { refused, rows }: refused names why the runs are incomparable (rows is then empty).
export function compare(current, baseline, gates) {
  const refused = incomparable(current, baseline);
  if (refused) return { refused, rows: [] };
  const key = (s, t) => `${s.id}#${t.turn}`;
  const baseTurns = new Map(baseline.scenarios.flatMap((s) => s.turns.map((t) => [key(s, t), t])));
  const curTurns = new Map(current.scenarios.flatMap((s) => s.turns.map((t) => [key(s, t), t])));
  const both = [...curTurns.keys()].filter((k) => baseTurns.has(k) && !baseTurns.get(k).failed && !curTurns.get(k).failed);
  const rows = [];
  for (const t of gates.regression) {
    let c, b, allowed;
    if (t.metric === "newlyFailedTurns") {
      c = [...curTurns.keys()].filter((k) => curTurns.get(k).failed && baseTurns.has(k) && !baseTurns.get(k).failed).length;
      b = 0;
      allowed = Math.floor(t.tolerance * curTurns.size);
    } else if (t.paired) {
      const f = PAIRED[t.metric];
      c = both.reduce((n, k) => n + f(curTurns.get(k)), 0);
      b = both.reduce((n, k) => n + f(baseTurns.get(k)), 0);
      allowed = b + t.tolerance;
    } else {
      const cur = current.metrics[t.metric];
      const base = baseline.metrics[t.metric];
      c = t.stat ? cur?.[t.stat] : cur?.value;
      b = t.stat ? base?.[t.stat] : base?.value;
      const has = (m, v) => v !== null && v !== undefined && (m?.n ?? 0) > 0;
      if (!has(base, b)) { rows.push({ ...t, current: c ?? null, baseline: null, status: "no baseline data" }); continue; }
      // A metric the baseline measured and this run cannot is a regression, never a pass: losing the
      // data is exactly what a failing run looks like.
      if (!has(cur, c)) { rows.push({ ...t, current: null, baseline: b, status: "REGRESSION (no data)" }); continue; }
      if (t.latency && !(current.summary.latencyGated && baseline.summary.latencyGated)) { rows.push({ ...t, current: c, baseline: b, status: "not-gated" }); continue; }
      allowed = t.relative ? b * (1 + (t.higherIsBetter ? -t.tolerance : t.tolerance)) : b + (t.higherIsBetter ? -t.tolerance : t.tolerance);
    }
    const pass = t.higherIsBetter ? c >= allowed : c <= allowed;
    rows.push({ ...t, current: c, baseline: b, allowed, status: pass ? "pass" : "REGRESSION" });
  }
  return { refused: null, rows, pairedTurns: both.length };
}
