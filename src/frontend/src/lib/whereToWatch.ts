import type { ProviderEntry } from "../store/titleTypes";

/**
 * The where-to-watch ladder, kept pure so the ordering and the link honesty are unit-tested rather
 * than eyeballed.
 *
 * Three rules, each one a defect it closes:
 *
 * 1. **Streaming first, and every streaming service gets a chip.** The old card gave a chip only to
 *    providers with a maintained link and folded everything else into one "More options via TMDB"
 *    link, so The Seized showed Google Play and YouTube (rent · buy) while Philo and Starz
 *    (subscription) and Tubi and Roku (free with ads) — the answer to "can I watch it tonight?" —
 *    were invisible behind it (R1-19). The Saved pin then named Philo above a card that did not show
 *    it. A subscription or free service is now always a chip, ordered first; rent and buy follow.
 * 2. **A search URL is never labelled "direct".** Every maintained pattern the API has today is a
 *    provider-side SEARCH (`netflix.com/search?q=…`), which it reports as `linkKind: "direct"`. The
 *    chip says what the link does: "search" for a provider search, "direct" only for a real
 *    deep link to the title, "via TMDB" for the TMDB watch page.
 * 3. **One brand, one chip.** "Starz", "Starz Amazon Channel", "Starz Apple TV Channel" and "Starz
 *    Roku Premium Channel" are one subscription sold through four storefronts; listed separately
 *    they are four chips saying the same thing.
 */

/** What tapping the chip actually does. */
export type ProviderLink = "deep" | "search" | "tmdb";

/** Where a service sits on the ladder: streaming (subscription, free, ads), then rent, then buy. */
export type ProviderTier = "stream" | "rent" | "buy";

export interface LadderChip {
  provider: string;
  logoPath: string | null;
  /** Human labels, best first: "Stream", "Free", "Free (ads)", "Rent", "Buy". */
  kinds: string[];
  tier: ProviderTier;
  link: ProviderLink;
  url: string;
}

export interface Ladder {
  chips: LadderChip[];
  /** Rent/buy-only services with no link of their own: one trailing TMDB link stands in for them. */
  tmdbOnlyCount: number;
  tmdbUrl: string | null;
}

/** TMDB availability kinds, best first, with the chip's label. Unknown kinds sort last, verbatim. */
const KIND_ORDER: { kind: string; label: string; tier: ProviderTier }[] = [
  { kind: "Flatrate", label: "Stream", tier: "stream" },
  { kind: "Free", label: "Free", tier: "stream" },
  { kind: "Ads", label: "Free (ads)", tier: "stream" },
  { kind: "Rent", label: "Rent", tier: "rent" },
  { kind: "Buy", label: "Buy", tier: "buy" },
];

const TIER_RANK: Record<ProviderTier, number> = { stream: 0, rent: 1, buy: 2 };

function kindRank(kind: string): number {
  const i = KIND_ORDER.findIndex((k) => k.kind === kind);
  return i === -1 ? KIND_ORDER.length : i;
}

/**
 * The chip's link kind from the API's. `"direct"` is how the API reports a maintained
 * provider-side search pattern, so it is a search; only an explicit `"deep"` is a link to the title
 * itself. Anything unrecognised is treated as a search too — the weaker promise is the safe one.
 */
export function providerLink(linkKind: string): ProviderLink {
  if (linkKind === "deep") return "deep";
  if (linkKind === "tmdb") return "tmdb";
  return "search";
}

/** The pill text for each link kind — the chip states its promise, it does not merely hover it. */
export const LINK_LABEL: Record<ProviderLink, string> = {
  deep: "direct",
  search: "search",
  tmdb: "via TMDB",
};

/** What a screen reader hears for each link kind. */
export const LINK_DESCRIPTION: Record<ProviderLink, string> = {
  deep: "direct link, opens the title on the provider",
  search: "opens a search on the provider",
  tmdb: "opens the TMDB watch page",
};

/** Storefront suffixes that resell another brand's subscription ("Starz Amazon Channel" → "Starz"). */
const CHANNEL_SUFFIX = / (Amazon|Apple TV|Roku Premium) Channel$/;

/** The subscription brand a provider name sells. Only storefront channels are folded. */
export function providerBrand(name: string): string {
  return name.replace(CHANNEL_SUFFIX, "");
}

const LINK_RANK: Record<ProviderLink, number> = { deep: 0, search: 1, tmdb: 2 };

/** Builds the ladder from the API's provider list (already in TMDB display-priority order). */
export function providerLadder(providers: ProviderEntry[], tmdbWatchPage: string | null): Ladder {
  // Fold storefront channels into their brand, keeping the earliest (highest-priority) entry's
  // name and logo, the union of the kinds and the strongest link any of them has.
  const byBrand = new Map<string, { entry: ProviderEntry; order: number; kinds: Set<string>; link: ProviderLink; url: string }>();
  providers.forEach((p, order) => {
    const brand = providerBrand(p.provider);
    const link = providerLink(p.linkKind);
    const existing = byBrand.get(brand);
    if (!existing) {
      byBrand.set(brand, { entry: { ...p, provider: brand }, order, kinds: new Set(p.kinds), link, url: p.url });
      return;
    }
    p.kinds.forEach((k) => existing.kinds.add(k));
    // The brand's own listing carries the brand's own logo; a storefront channel's may not.
    if (p.provider === brand) existing.entry = { ...existing.entry, logoPath: p.logoPath };
    if (LINK_RANK[link] < LINK_RANK[existing.link]) {
      existing.link = link;
      existing.url = p.url;
    }
  });

  const all = [...byBrand.values()].map((b) => {
    const kinds = [...b.kinds].sort((x, y) => kindRank(x) - kindRank(y));
    const best = KIND_ORDER.find((k) => k.kind === kinds[0]);
    return {
      order: b.order,
      chip: {
        provider: b.entry.provider,
        logoPath: b.entry.logoPath,
        kinds: kinds.map((k) => KIND_ORDER.find((o) => o.kind === k)?.label ?? k),
        // A provider whose only kinds are unknown is listed with the purchases, never as streaming.
        tier: best?.tier ?? "buy",
        link: b.link,
        url: b.url,
      } satisfies LadderChip,
    };
  });

  all.sort((a, b) => TIER_RANK[a.chip.tier] - TIER_RANK[b.chip.tier] || a.order - b.order);

  // Streaming services always get a chip; rent and buy only when they have a link of their own.
  // The rest bounce to the same TMDB page, so one trailing link stands in for all of them rather
  // than a wall of chips that promise different providers and deliver one URL.
  const chips = all.filter((x) => x.chip.tier === "stream" || x.chip.link !== "tmdb").map((x) => x.chip);
  const tmdbOnly = all.filter((x) => x.chip.tier !== "stream" && x.chip.link === "tmdb");
  return {
    chips,
    tmdbOnlyCount: tmdbOnly.length,
    tmdbUrl: tmdbOnly[0]?.chip.url ?? tmdbWatchPage,
  };
}
