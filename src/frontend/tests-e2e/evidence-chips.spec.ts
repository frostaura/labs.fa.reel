import { test, expect, type Page } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";

/**
 * Anchored cards show what they share with their anchor (WS-5 `FeedCard.evidence`, R1-56).
 *
 * The server admits a card to "Because you loved X" (Home) and "More like this" (the title page)
 * only on a shared person, keyword or theme, and sends that evidence with the card. A row whose
 * claim is "these are like X" is checkable only if the reason is on the card — so every anchored
 * card that carries evidence shows it, and a card on any other row shows none.
 */

let seq = 0;
function card(name: string, evidence?: unknown) {
  seq += 1;
  return {
    titleId: `00000000-0000-0000-0000-${String(seq).padStart(12, "0")}`,
    mediaType: "Movie",
    tmdbId: 30_000 + seq,
    name,
    year: 2015,
    runtimeMinutes: 121,
    posterPath: null,
    backdropPath: null,
    genres: ["crime"],
    predictedRating: 7.9,
    crowdRating: 7.6,
    crowdVotes: 40000,
    whyThis: "Shares Sicario's director and its border-war setting.",
    isReleased: true,
    ...(evidence === undefined ? {} : { evidence }),
  };
}

const SICARIO_EVIDENCE = [
  { kind: "person", label: "Denis Villeneuve", role: "director" },
  { kind: "keyword", label: "drug cartel" },
  { kind: "theme", label: "moral compromise" },
];

async function boot(page: Page): Promise<void> {
  await stubEventSource(page);
  // Registered first so every specific mock below wins: nothing reaches the dev proxy.
  await page.route(
    (u) => u.pathname.startsWith("/api/"),
    (route) => route.fulfill({ status: 404, contentType: "application/json", body: "{}" })
  );
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", {
    pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
    lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
  });
}

test("a because-you-loved card shows its shared evidence; an unanchored row shows none", async ({ page }) => {
  await boot(page);
  const anchored = [
    card("Prisoners", SICARIO_EVIDENCE),
    card("Hell or High Water", [{ kind: "person", label: "Taylor Sheridan", role: "writer" }]),
    // An older snapshot row written before evidence was stored: no chips, and nothing breaks.
    card("Wind River", null),
  ];
  await mockJson(page, "**/api/feed", {
    generatedAt: new Date().toISOString(),
    hero: [card("Heat")],
    rows: [
      { kind: "because-you-loved", anchorTitleId: "00000000-0000-0000-0000-00000000a0a0", anchorName: "Sicario", title: null, items: anchored },
      { kind: "new-for-you", anchorTitleId: null, anchorName: null, title: "New for you", items: [card("Unanchored")] },
    ],
    lockedRowCount: 0,
  });
  await mockJson(page, "**/api/feed/more", { items: [], exhausted: true });
  await page.goto("/home");

  const prisoners = page.getByTestId("rec-card").filter({ hasText: "Prisoners" });
  await expect(prisoners.getByTestId("card-evidence-chip")).toHaveText([
    "Denis Villeneuve · dir.",
    "drug cartel",
    "moral compromise",
  ]);
  await expect(prisoners.getByTestId("card-evidence-chip").first()).toHaveAttribute("title", "Shared director: Denis Villeneuve");
  await expect(page.getByTestId("rec-card").filter({ hasText: "Hell or High Water" }).getByTestId("card-evidence-chip")).toHaveText([
    "Taylor Sheridan · writer",
  ]);
  await expect(page.getByTestId("rec-card").filter({ hasText: "Wind River" }).getByTestId("card-evidence")).toHaveCount(0);
  await expect(page.getByTestId("rec-card").filter({ hasText: "Unanchored" }).getByTestId("card-evidence")).toHaveCount(0);
});

test("the title page's More like this cards show the evidence /feed/more sends", async ({ page }) => {
  await boot(page);
  await mockJson(page, "**/api/titles/movie/1001", {
    titleId: "00000000-0000-0000-0000-0000000000t1", mediaType: "Movie", tmdbId: 1001, name: "Sicario", year: 2015,
    overview: "An idealistic agent is enlisted to fight the drug war.", whyThis: "", tagline: null, runtimeMinutes: 121,
    certification: "R", genres: ["crime"], network: null, status: "Released", traktRating: 7.6, traktVotes: 40000,
    posterPath: null, backdropPath: null, trailerUrl: null, cast: [], directors: [], writers: [], prediction: null,
    contributionDetails: [],
    userState: { isFullyWatched: false, plays: 0, userRating: null, savedForLater: false, notInterested: false, dropped: false },
  });
  await mockJson(page, "**/api/titles/movie/1001/providers", {
    region: "US", attribution: "JustWatch", tmdbWatchPage: "https://www.themoviedb.org/movie/1001/watch", providers: [],
  });
  const asked: { row: string; anchorTitleId?: string }[] = [];
  await page.route(
    (u) => u.pathname === "/api/feed/more",
    (route) => {
      asked.push(route.request().postDataJSON());
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ items: [card("Enemy", [{ kind: "person", label: "Jake Gyllenhaal", role: "cast" }])], exhausted: true }),
      });
    }
  );
  await page.goto("/title/movie/1001");

  const enemy = page.getByTestId("rec-card").filter({ hasText: "Enemy" });
  await expect(enemy.getByTestId("card-evidence-chip")).toHaveText(["Jake Gyllenhaal"]);
  await expect(enemy.getByTestId("card-evidence-chip")).toHaveAttribute("aria-label", "Shared cast member: Jake Gyllenhaal");
  expect(asked[0]).toMatchObject({ row: "similar", anchorTitleId: "00000000-0000-0000-0000-0000000000t1" });
});
