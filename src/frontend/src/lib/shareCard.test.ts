import { describe, expect, it } from "vitest";
import type { TasteDnaPayload } from "../store/api";
import { SHARE_CREATOR_COUNT, SHARE_GENRE_COUNT, shareCardModel } from "./shareCard";
import { contrarianTile, genreBarGeometry, meanMarkerPct } from "./tasteDna";

const payload = (overrides: Partial<TasteDnaPayload> = {}): TasteDnaPayload => ({
  userMean: 6.8,
  contrarianScore: 0.4,
  ratingsCount: 1234,
  hoursWatched: 2101.6,
  titlesWatched: 987,
  showCompletionRate: 0.732,
  topGenres: [
    { genre: "science-fiction", affinity: 7.2, count: 120 },
    { genre: "thriller", affinity: 6.9, count: 80 },
    { genre: "drama", affinity: 6.7, count: 200 },
    { genre: "comedy", affinity: 6.5, count: 90 },
    { genre: "horror", affinity: 6.1, count: 40 },
    { genre: "romance", affinity: 5.9, count: 30 },
    { genre: "war", affinity: 5.4, count: 12 },
  ],
  eras: [],
  creators: [
    { name: "Denis Villeneuve", department: "Directing", profilePath: null, affinity: 8.5, count: 6 },
    { name: "Vince Gilligan", department: "Writing", profilePath: null, affinity: 8.25, count: 4 },
    { name: "Greta Gerwig", department: "Directing", profilePath: null, affinity: 7.9, count: 3 },
    { name: "Someone Else", department: null, profilePath: null, affinity: 7.0, count: 2 },
  ],
  topActors: [],
  histogram: [],
  drift: [],
  clusters: [],
  ...overrides,
});

describe("shareCardModel", () => {
  it("maps the four headline stats, formatted for print", () => {
    const model = shareCardModel(payload());
    expect(model.stats).toEqual([
      { label: "hours watched", value: "2,102" },
      { label: "titles", value: "987" },
      { label: "show completion", value: "73%" },
      { label: "contrarian score", value: "+0.4" },
    ]);
  });

  it("keeps the negative sign on a harsher-than-the-crowd contrarian score", () => {
    const model = shareCardModel(payload({ contrarianScore: -1.25 }));
    expect(model.stats[3].value).toBe("-1.3");
  });

  // HREV5-DESK-07 follow-on: the card rounded with raw toFixed while the page tile rounds half away
  // from zero, so the exported card disagreed with the page it was shared from.
  it.each([
    [-0.35, "-0.4"],
    [0.35, "+0.4"],
    [0.15, "+0.2"],
    [-0.04, "0.0"],
    [-0.2, "-0.2"],
  ])("prints contrarian %s as %s, the page tile's value", (score, value) => {
    const model = shareCardModel(payload({ contrarianScore: score }));
    expect(model.stats[3].value).toBe(value);
    expect(model.stats[3].value).toBe(contrarianTile(score).value);
  });

  it("caps the fingerprint at the template's row budget and humanises genre slugs", () => {
    const model = shareCardModel(payload());
    expect(model.genres).toHaveLength(SHARE_GENRE_COUNT);
    expect(model.genres[0].label).toBe("science fiction");
  });

  it("uses the exact on-page geometry for bars and the mean tick — one rule, two renderers", () => {
    const data = payload();
    const model = shareCardModel(data);
    const expected = genreBarGeometry(7.2, data.userMean);
    expect(model.genres[0].fillPct).toBe(expected.fillPct);
    expect(model.genres[0].aboveMean).toBe(expected.aboveMean);
    expect(model.meanPct).toBe(meanMarkerPct(data.userMean));
    expect(model.meanLabel).toBe("6.8");
  });

  it("marks below-mean genres so the template can dim them", () => {
    const model = shareCardModel(payload());
    expect(model.genres[0].aboveMean).toBe(true); // 7.2 vs 6.8
    expect(model.genres[4].aboveMean).toBe(false); // 6.1 vs 6.8
  });

  it("takes the top creators only, with printed affinity", () => {
    const model = shareCardModel(payload());
    expect(model.creators).toHaveLength(SHARE_CREATOR_COUNT);
    expect(model.creators[0]).toEqual({ name: "Denis Villeneuve", affinity: "8.5" });
    expect(model.creators[1].affinity).toBe("8.3");
  });

  it("stamps the footer date from the provided clock", () => {
    const model = shareCardModel(payload(), new Date("2026-08-07T12:00:00Z"));
    expect(model.dateLabel).toBe("7 Aug 2026");
  });

  it("survives an account with no creators or few genres", () => {
    const model = shareCardModel(payload({ creators: [], topGenres: [{ genre: "drama", affinity: 6, count: 1 }] }));
    expect(model.creators).toEqual([]);
    expect(model.genres).toHaveLength(1);
  });
});
