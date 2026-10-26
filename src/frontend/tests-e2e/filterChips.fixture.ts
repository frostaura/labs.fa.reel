import type { Page } from "@playwright/test";
import { mockJson } from "./helpers";

/**
 * GET /api/search/filter-chips in the shape WS-3's FilterChipCounts serves it (offered first, each
 * group biggest first): the catalogue's genres with the films and shows an exclusion of each would
 * remove, TV genres included, anime as a composite, and two chips under the 50-title bar.
 */
export const FILTER_CHIPS = {
  minTitles: 50,
  chips: [
    { slug: "drama", movies: 2100, shows: 640, composite: false, offered: true },
    { slug: "comedy", movies: 1400, shows: 520, composite: false, offered: true },
    { slug: "thriller", movies: 1100, shows: 210, composite: false, offered: true },
    { slug: "action", movies: 1000, shows: 180, composite: false, offered: true },
    { slug: "crime", movies: 700, shows: 260, composite: false, offered: true },
    { slug: "romance", movies: 690, shows: 120, composite: false, offered: true },
    { slug: "adventure", movies: 640, shows: 150, composite: false, offered: true },
    { slug: "horror", movies: 520, shows: 60, composite: false, offered: true },
    { slug: "science-fiction", movies: 480, shows: 190, composite: false, offered: true },
    { slug: "mystery", movies: 400, shows: 170, composite: false, offered: true },
    { slug: "fantasy", movies: 390, shows: 140, composite: false, offered: true },
    { slug: "family", movies: 380, shows: 90, composite: false, offered: true },
    { slug: "documentary", movies: 300, shows: 110, composite: false, offered: true },
    { slug: "history", movies: 260, shows: 70, composite: false, offered: true },
    { slug: "animation", movies: 250, shows: 160, composite: false, offered: true },
    { slug: "soap", movies: 0, shows: 229, composite: false, offered: true },
    { slug: "war", movies: 200, shows: 30, composite: false, offered: true },
    { slug: "reality", movies: 0, shows: 183, composite: false, offered: true },
    { slug: "music", movies: 140, shows: 20, composite: false, offered: true },
    { slug: "western", movies: 90, shows: 12, composite: false, offered: true },
    { slug: "anime", movies: 41, shows: 30, composite: true, offered: true },
    { slug: "talk-show", movies: 0, shows: 46, composite: false, offered: false },
    { slug: "news", movies: 0, shows: 10, composite: false, offered: false },
    { slug: "sports", movies: 0, shows: 0, composite: false, offered: false },
  ],
};

export async function mockFilterChips(page: Page, body: unknown = FILTER_CHIPS): Promise<void> {
  await mockJson(page, "**/api/search/filter-chips", body);
}
