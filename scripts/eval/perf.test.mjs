// Pure tests for the exposure-free perf harness: what it stubs, and how it summarises. No browser.
import { test } from "node:test";
import assert from "node:assert/strict";
import { stubFor, percentile, summarise, renderPerf } from "./perf.mjs";

const feed = {
  hero: [{ titleId: "h1" }, { titleId: "h2" }],
  rows: [{ kind: "because-you-loved", anchorTitleId: "a", items: [{ titleId: "r1" }, { titleId: "r2" }, { titleId: "r3" }] }, { kind: "deep-cuts", items: [{ titleId: "d1" }] }],
};

test("every exposure-writing or paid endpoint is answered at the route layer; everything else reaches the API", () => {
  const more = stubFor("http://x/api/feed/more", "POST", { row: "because-you-loved", anchorTitleId: "a", excludeTitleIds: ["r1"], limit: 1 }, feed);
  assert.deepEqual(JSON.parse(more.body).items, [{ titleId: "r2" }], "the row's own cards, minus what it has shown, up to the limit");
  assert.deepEqual(JSON.parse(stubFor("http://x/api/feed/more", "POST", { row: "tonights-picks", excludeTitleIds: ["h1"] }, feed).body).items, [{ titleId: "h2" }]);
  assert.deepEqual(JSON.parse(stubFor("http://x/api/feed/more", "POST", { row: "genre", genre: "drama" }, feed).body).items, []);
  assert.deepEqual(JSON.parse(stubFor("http://x/api/digest/current", "GET", null, feed).body).items, []);
  assert.equal(JSON.parse(stubFor("http://x/api/titles/movie/603/lens", "GET", null, feed).body).source, "none");
  const ask = stubFor("http://x/api/search/ask", "POST", { query: "q" }, feed);
  assert.equal(ask.contentType, "text/event-stream");
  assert.match(ask.body, /^event: done\ndata: \{.*\}\n\n$/);
  for (const url of ["http://x/api/feed", "http://x/api/feed/genres", "http://x/api/saved", "http://x/api/titles/movie/603", "http://x/api/titles/movie/603/lens/spoiled"]) {
    assert.equal(stubFor(url, "GET", null, feed), null, `${url} reaches the API`);
  }
});

test("percentiles are nearest-rank and ignore missing measurements", () => {
  assert.equal(percentile([5, 1, 3, null, 4, 2], 50), 3);
  assert.equal(percentile([5, 1, 3, 4, 2], 95), 5);
  assert.equal(percentile([], 50), null);
  const s = summarise([{ firstHeroCardMs: 100, lcpMs: 200, cls: 0.01, loadMs: 300, longTaskMs: 0 }, { firstHeroCardMs: null, lcpMs: 400, cls: 0.2, loadMs: 500, longTaskMs: 60 }]);
  assert.deepEqual(s.firstHeroCardMs, { p50: 100, p95: 100 });
  assert.deepEqual(s.cls, { p50: 0.01, p95: 0.2 });
});

test("a run whose exposure count moved says it is void", () => {
  const s = summarise([{ firstHeroCardMs: 1, lcpMs: 1, cls: 0, loadMs: 1, longTaskMs: 0 }]);
  assert.match(renderPerf(s, { path: "/", runs: 1, app: "a", api: "b", db: "d", exposuresBefore: 3, exposuresAfter: 3, stubbed: 4 }), /\(unchanged\)/);
  assert.match(renderPerf(s, { path: "/", runs: 1, app: "a", api: "b", db: "d", exposuresBefore: 3, exposuresAfter: 5, stubbed: 4 }), /CHANGED — the run is void/);
});
