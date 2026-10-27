import { feedApi } from "../store/api/feed";
import { savedApi } from "../store/api/saved";

/**
 * The measured band behind every "for you" number (FD-4 default: every displayed number carries its
 * measured band).
 *
 * The band is the account's own expected absolute error — what a displayed prediction has typically
 * been off by against the ratings the account went on to give. It comes from the same calibrator
 * the Saved queue already shows ("8.4 ± 1.1"), so a title cannot carry one band on Home and another
 * on Saved.
 *
 * READ FROM THE CACHE, NEVER FETCHED HERE. `useQueryState` subscribes to whatever the
 * recently-watched query already holds and never issues a request: Home and Saved both mount the
 * post-watch strip, which owns that request, so the band is there on the surfaces that show feed
 * cards. On a surface that never asked (a deep-linked title page) the band is simply absent — an
 * unmeasured number is shown bare, which is the honest fallback; a rail card must never be the
 * thing that adds an API call to a page. `/api/feed` carries the same band itself (WS-5), so Home
 * has it from its first answer; the recently-watched read is the fallback.
 */
export function useForYouBand(fromFeed?: number | null): number | null {
  const { data: feed } = feedApi.endpoints.getFeed.useQueryState();
  const { data: recent } = savedApi.endpoints.getRecentlyWatched.useQueryState();
  return firstUsableBand(fromFeed, feed?.expectedAbsoluteError, recent?.expectedAbsoluteError);
}

/** The first band worth printing, in the order given. */
export function firstUsableBand(...bands: (number | null | undefined)[]): number | null {
  for (const band of bands) {
    const usable = usableBand(band);
    if (usable !== null) return usable;
  }
  return null;
}

/** A band worth printing: finite and positive. "± 0.0" would be the least honest number on screen. */
export function usableBand(band: number | null | undefined): number | null {
  return band != null && Number.isFinite(band) && band > 0 ? band : null;
}

/** "±1.1", or null when there is no measured band to show. */
export function bandText(band: number | null | undefined): string | null {
  const usable = usableBand(band);
  return usable === null ? null : `±${usable.toFixed(1)}`;
}
