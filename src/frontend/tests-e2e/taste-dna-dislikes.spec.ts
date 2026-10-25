import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * The dislike panel on Taste DNA.
 *
 * NotInterested and Dropped drive a recency-decayed, reason-weighted demotion on every scoring
 * surface — the strongest negative signal the product collects — and there was no list of them
 * anywhere: nothing to read, nothing to audit, nothing to undo from one place.
 *
 * What these pin: the count, a summary of what is actually being demoted RIGHT NOW (which is not
 * the same number as the total, and must not pretend to be), the paginated history with the reason
 * each dismissal carried, and a revoke that goes to the existing DELETE endpoint.
 */

const DNA = {
  userMean: 6.7,
  contrarianScore: 0.2,
  ratingsCount: 2044,
  hoursWatched: 4000,
  titlesWatched: 3353,
  showCompletionRate: 0.7,
  topGenres: [],
  eras: [],
  creators: [],
  topActors: [],
  histogram: [{ rating: 7, count: 10 }],
  drift: [],
};

const entry = (n: number, over: Record<string, unknown> = {}) => ({
  titleId: `00000000-0000-0000-0000-0000000000${String(n).padStart(2, "0")}`,
  mediaType: "Movie",
  tmdbId: 3000 + n,
  name: `Declined ${n}`,
  year: 2019,
  posterPath: null,
  kind: "NotInterested",
  reason: "Genre",
  createdAt: "2026-09-01T12:00:00Z",
  ...over,
});

const DISLIKES = {
  total: 175,
  offset: 0,
  limit: 20,
  hasMore: true,
  items: [
    entry(1),
    entry(2, { kind: "Dropped", reason: null, name: "Dropped Show", mediaType: "Show" }),
    entry(3, { reason: null, name: "No Reason Given" }),
  ],
  profile: {
    windowDays: 180,
    activeCount: 42,
    genres: [{ genre: "horror", weight: 1.8 }],
    tones: [{ tone: "bleak", weight: 0.9 }],
  },
};

async function bootDna(page: Page, dislikes: unknown = DISLIKES) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    lastListSyncAt: null, outboxLastError: null,
  });
  await mockJson(page, "**/api/taste", DNA);
  await mockJson(page, "**/api/taste/dislikes", dislikes);
  await page.goto("/dna");
  await expect(page.getByText("Your taste DNA")).toBeVisible();
}

test.describe("taste DNA dislikes", () => {
  test("counts the dislikes, names what is being demoted, and never conflates the two numbers", async ({ page }) => {
    await bootDna(page);

    await expect(page.getByTestId("dislike-panel")).toBeVisible();
    await expect(page.getByText("You’ve told me no to 175 things")).toBeVisible();

    // The demotion is windowed: 42 of the 175 are actually steering anything, and the copy says so.
    const summary = page.getByTestId("dislike-summary");
    await expect(summary).toContainText("horror, bleak");
    await expect(summary).toContainText("42");
    await expect(summary).toContainText("180 days");
  });

  test("each row says the reason it was given, and admits when none was", async ({ page }) => {
    await bootDna(page);

    const rows = page.getByTestId("dislike-row");
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(0)).toContainText("not my genre");
    await expect(rows.nth(1)).toContainText("stopped watching");
    await expect(rows.nth(2)).toContainText("no reason given");
  });

  test("undo goes to the existing revoke endpoint, with the right verb per reaction kind", async ({ page }) => {
    await bootDna(page);

    const calls: { method: string; url: string }[] = [];
    await page.route(/\/api\/titles\/.*\/reactions\/(not_interested|dropped)$/, (route) => {
      calls.push({ method: route.request().method(), url: route.request().url() });
      return route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
    });

    await page.getByTestId("dislike-row").nth(0).getByTestId("dislike-revoke").click();
    await page.getByTestId("dislike-row").nth(1).getByTestId("dislike-revoke").click();

    await expect.poll(() => calls.length).toBe(2);
    expect(calls[0].method).toBe("DELETE");
    expect(calls[0].url).toContain("/titles/movie/3001/reactions/not_interested");
    expect(calls[1].method).toBe("DELETE");
    expect(calls[1].url).toContain("/titles/show/3002/reactions/dropped");
  });

  test("paging asks for the next page and the panel disappears entirely when there is nothing to show", async ({ page }) => {
    await bootDna(page);

    const requested: string[] = [];
    await page.route(/\/api\/taste\/dislikes/, (route) => {
      requested.push(route.request().url());
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ ...DISLIKES, offset: 20, hasMore: false, items: [entry(9, { name: "Older One" })] }),
      });
    });

    await page.getByTestId("dislike-next").click();
    await expect(page.getByText("Older One")).toBeVisible();
    expect(requested.some((u) => u.includes("offset=20"))).toBe(true);
    await expect(page.getByTestId("dislike-next")).toBeDisabled();
  });

  test("a failed Older page says so — page 1's rows never stand in for it — and Newer recovers", async ({ page }) => {
    await bootDna(page);
    await expect(page.getByText("Declined 1")).toBeVisible();

    await page.route(/\/api\/taste\/dislikes/, (route) =>
      new URL(route.request().url()).searchParams.get("offset") === "20"
        ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
        : route.fallback()
    );
    await page.getByTestId("dislike-next").click();

    await expect(page.getByTestId("dislikes-page-error")).toBeVisible();
    await expect(page.getByText("Declined 1")).toHaveCount(0);
    await expect(page.getByTestId("dislike-range")).toHaveText("from 21 of 175");
    await expect(page.getByTestId("dislike-next")).toBeDisabled();

    await page.getByTestId("dislike-prev").click();
    await expect(page.getByText("Declined 1")).toBeVisible();
    await expect(page.getByTestId("dislikes-page-error")).toHaveCount(0);
    await expect(page.getByTestId("dislike-range")).toHaveText("1–3 of 175");
  });

  test("a failed undo says so and the row stays", async ({ page }) => {
    await bootDna(page);
    await page.route(/\/api\/titles\/.*\/reactions\/not_interested$/, (route) =>
      route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
    );
    await page.getByTestId("dislike-row").nth(0).getByTestId("dislike-revoke").click();

    const status = page.getByTestId("dislike-revoke-status");
    await expect(status).toContainText("Couldn't undo Declined 1 — it's still on the list.");
    await expect(status).toHaveAttribute("data-tone", "danger");
    await expect(page.getByTestId("dislike-row")).toHaveCount(3);
  });

  test("an account that has said no to nothing gets no panel at all", async ({ page }) => {
    await bootDna(page, {
      total: 0,
      offset: 0,
      limit: 20,
      hasMore: false,
      items: [],
      profile: { windowDays: 180, activeCount: 0, genres: [], tones: [] },
    });

    await expect(page.getByTestId("dislike-panel")).toHaveCount(0);
  });
});

/**
 * The page's three states. A failed read used to shimmer for ever, a failed narrative read claimed
 * Reel had no language model, and a failed dislike read hid the panel as if nothing had been
 * declined — "an empty collection is not an answer".
 */
test.describe("taste DNA data states", () => {
  async function bootWith(page: Page, routes: { taste?: number; narrative?: number; dislikes?: number; dna?: unknown }) {
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    const respond = (status: number | undefined, body: unknown) => (route: import("@playwright/test").Route) =>
      status && status !== 200
        ? route.fulfill({ status, contentType: "application/json", body: "{}" })
        : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    await page.route((u) => u.pathname === "/api/taste", respond(routes.taste, routes.dna ?? DNA));
    await page.route((u) => u.pathname === "/api/taste/dislikes", respond(routes.dislikes, DISLIKES));
    await page.route(
      (u) => u.pathname === "/api/taste/narrative",
      respond(routes.narrative, { text: "You like slow-burn crime.", isEdited: false, version: 1, ratingsCount: 2044, generatedAt: null, isStale: false, regenerating: false, writerAvailable: true })
    );
    await page.goto("/dna");
  }

  test("a failed DNA read is an error with a Retry, and the retry recovers", async ({ page }) => {
    let fail = true;
    await stubEventSource(page);
    await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
    await mockJson(page, "**/api/sync/status", {
      pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
      lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
    });
    await mockJson(page, "**/api/taste/dislikes", DISLIKES);
    await page.route(
      (u) => u.pathname === "/api/taste",
      (route) =>
        fail
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(DNA) })
    );
    await page.goto("/dna");

    await expect(page.getByTestId("dna-error")).toBeVisible();
    await expect(page.getByTestId("dna-error")).toContainText("Couldn't load your taste DNA");
    await expect(page.getByTestId("dna-loading")).toHaveCount(0);

    fail = false;
    await page.getByTestId("dna-error-retry").click();
    await expect(page.getByText("Your taste DNA")).toBeVisible();
    await expect(page.getByTestId("dna-error")).toHaveCount(0);
  });

  test("an account with no ratings gets an empty state that points at /rate", async ({ page }) => {
    await bootWith(page, { dna: { ...DNA, ratingsCount: 0, histogram: [] } });
    await expect(page.getByTestId("dna-empty")).toBeVisible();
    await expect(page.getByTestId("dna-empty-rate")).toHaveAttribute("href", "/rate");
    await expect(page.getByTestId("dna-error")).toHaveCount(0);
  });

  test("a failed narrative read says it failed — not that Reel has no language model", async ({ page }) => {
    await bootWith(page, { narrative: 500 });
    await expect(page.getByText("Your taste DNA")).toBeVisible();
    await expect(page.getByTestId("narrative-error")).toBeVisible();
    await expect(page.getByText(/without a language model/i)).toHaveCount(0);
  });

  test("a failed dislikes read says it failed instead of hiding the panel", async ({ page }) => {
    await bootWith(page, { dislikes: 502 });
    await expect(page.getByText("Your taste DNA")).toBeVisible();
    await expect(page.getByTestId("dislikes-error")).toBeVisible();
  });
});
