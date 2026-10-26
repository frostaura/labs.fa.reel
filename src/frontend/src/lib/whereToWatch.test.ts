import { describe, expect, it } from "vitest";
import type { ProviderEntry } from "../store/titleTypes";
import { LINK_LABEL, providerBrand, providerLadder, providerLink, cardProviders } from "./whereToWatch";
import { SEIZED_US_PROVIDERS } from "./whereToWatch.fixtures";

const TMDB = "https://www.themoviedb.org/movie/1/watch?locale=US";

function entry(provider: string, kinds: string[], linkKind: ProviderEntry["linkKind"] = "tmdb", url = TMDB): ProviderEntry {
  return { provider, logoPath: `/${provider}.jpg`, kinds, linkKind, url, displayPriority: 0 };
}

describe("providerLadder on the recorded Seized payload (R1-19)", () => {
  const ladder = providerLadder(SEIZED_US_PROVIDERS.providers, SEIZED_US_PROVIDERS.tmdbWatchPage);
  const names = ladder.chips.map((c) => c.provider);

  it("shows every subscription and free service the API returned, the ones the old card hid", () => {
    for (const name of ["Philo", "Starz", "The Roku Channel", "Tubi TV", "Fawesome", "Plex"]) {
      expect(names, name).toContain(name);
    }
  });

  it("orders streaming first, then rent, never a purchase above a stream", () => {
    const tiers = ladder.chips.map((c) => c.tier);
    const firstNonStream = tiers.findIndex((t) => t !== "stream");
    expect(firstNonStream).toBeGreaterThan(0);
    expect(tiers.slice(firstNonStream).every((t) => t !== "stream")).toBe(true);
    expect(names[0]).toBe("Philo");
    // Rent/buy with a link of their own follow the streams.
    expect(names.slice(firstNonStream)).toEqual(["Google Play Movies", "YouTube"]);
  });

  it("folds the four Starz storefronts into one chip with the brand's own logo", () => {
    expect(names.filter((n) => n.startsWith("Starz"))).toEqual(["Starz"]);
    const starz = ladder.chips.find((c) => c.provider === "Starz")!;
    expect(starz.logoPath).toBe(SEIZED_US_PROVIDERS.providers.find((p) => p.provider === "Starz")!.logoPath);
    expect(names).toContain("MovieSphere+");
  });

  it("labels the maintained search patterns as a search, never as direct", () => {
    for (const name of ["Google Play Movies", "YouTube"]) {
      const chip = ladder.chips.find((c) => c.provider === name)!;
      expect(chip.link).toBe("search");
      expect(LINK_LABEL[chip.link]).toBe("search");
    }
    expect(ladder.chips.some((c) => LINK_LABEL[c.link] === "direct")).toBe(false);
  });

  it("collapses the link-less purchase stores into one TMDB link", () => {
    expect(names).not.toContain("Amazon Video");
    expect(names).not.toContain("Apple TV Store");
    expect(names).not.toContain("Fandango At Home");
    expect(ladder.tmdbOnlyCount).toBe(3);
    expect(ladder.tmdbUrl).toBe(SEIZED_US_PROVIDERS.tmdbWatchPage);
  });

  it("labels every kind a chip carries, best first", () => {
    expect(ladder.chips.find((c) => c.provider === "YouTube")!.kinds).toEqual(["Rent", "Buy"]);
    expect(ladder.chips.find((c) => c.provider === "Tubi TV")!.kinds).toEqual(["Free (ads)"]);
  });
});

describe("providerLadder, varied fixtures", () => {
  it("a subscription with no maintained link still gets a chip, pointing at TMDB and saying so", () => {
    const ladder = providerLadder([entry("Netflix", ["Flatrate"])], TMDB);
    expect(ladder.chips).toHaveLength(1);
    expect(ladder.chips[0]!.link).toBe("tmdb");
    expect(LINK_LABEL[ladder.chips[0]!.link]).toBe("via TMDB");
    expect(ladder.tmdbOnlyCount).toBe(0);
  });

  it("a real deep link is the only thing labelled direct", () => {
    const ladder = providerLadder(
      [entry("Max", ["Flatrate"], "deep", "https://play.max.com/movie/abc"), entry("Hulu", ["Flatrate"], "direct", "https://www.hulu.com/search?q=x")],
      TMDB
    );
    expect(ladder.chips.map((c) => [c.provider, LINK_LABEL[c.link]])).toEqual([
      ["Max", "direct"],
      ["Hulu", "search"],
    ]);
  });

  it("a provider that both streams and sells sits with the streams", () => {
    const ladder = providerLadder([entry("Store", ["Buy"], "direct"), entry("Both", ["Buy", "Rent", "Flatrate"])], TMDB);
    expect(ladder.chips.map((c) => c.provider)).toEqual(["Both", "Store"]);
    expect(ladder.chips[0]!.kinds).toEqual(["Stream", "Rent", "Buy"]);
  });

  it("buy-only sits after rent-only, keeping display priority inside a tier", () => {
    const ladder = providerLadder(
      [entry("BuyFirst", ["Buy"], "direct"), entry("RentB", ["Rent"], "direct"), entry("RentA", ["Rent"], "direct")],
      TMDB
    );
    expect(ladder.chips.map((c) => c.provider)).toEqual(["RentB", "RentA", "BuyFirst"]);
  });

  it("a folded brand keeps its strongest link", () => {
    const ladder = providerLadder(
      [entry("Starz Amazon Channel", ["Flatrate"]), entry("Starz", ["Flatrate"], "deep", "https://starz.com/t/1")],
      TMDB
    );
    expect(ladder.chips).toHaveLength(1);
    expect(ladder.chips[0]).toMatchObject({ provider: "Starz", link: "deep", url: "https://starz.com/t/1" });
  });

  it("an unknown kind is never presented as streaming", () => {
    const ladder = providerLadder([entry("Odd", ["Cinema"], "direct")], TMDB);
    expect(ladder.chips[0]!.tier).toBe("buy");
    expect(ladder.chips[0]!.kinds).toEqual(["Cinema"]);
  });

  it("with nothing link-less the TMDB fallback is the watch page", () => {
    expect(providerLadder([], TMDB)).toEqual({ chips: [], tmdbOnlyCount: 0, tmdbUrl: TMDB });
  });
});

describe("providerLink and providerBrand", () => {
  it("maps the API's link kinds to what the link does", () => {
    expect(providerLink("direct")).toBe("search");
    expect(providerLink("search")).toBe("search");
    expect(providerLink("deep")).toBe("deep");
    expect(providerLink("tmdb")).toBe("tmdb");
    expect(providerLink("something-new")).toBe("search");
  });

  it("folds only storefront channels", () => {
    expect(providerBrand("Starz Roku Premium Channel")).toBe("Starz");
    expect(providerBrand("Starz Apple TV Channel")).toBe("Starz");
    expect(providerBrand("The Roku Channel")).toBe("The Roku Channel");
    expect(providerBrand("Plex Channel")).toBe("Plex Channel");
  });
});

describe("cardProviders", () => {
  it("keeps one logo per brand, in the server's order, up to three", () => {
    const providers = [
      { name: "Starz Amazon Channel", logoPath: "/a.png" },
      { name: "Starz", logoPath: "/b.png" },
      { name: "Netflix", logoPath: "/c.png" },
      { name: "Starz Roku Premium Channel", logoPath: "/d.png" },
      { name: "Tubi", logoPath: "/e.png" },
      { name: "Hulu", logoPath: "/f.png" },
    ];
    expect(cardProviders(providers).map((p) => p.name)).toEqual(["Starz Amazon Channel", "Netflix", "Tubi"]);
  });

  it("is empty for no providers", () => {
    expect(cardProviders(undefined)).toEqual([]);
    expect(cardProviders(null)).toEqual([]);
  });
});
