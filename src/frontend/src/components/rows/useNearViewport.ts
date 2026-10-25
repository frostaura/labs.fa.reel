import { useEffect, useState, type RefObject } from "react";

/**
 * How far below (and above) the viewport a rail may be and still start loading: far enough that a
 * rail lands ahead of a normal scroll, near enough that a rail nobody is approaching fires nothing
 * — no billed curation call and no exposure row (I-2, O-22).
 *
 * It is a distance, not a count. On a cold Home with personalised rows (three, or even one, at
 * 1440×900 and 390×844) every genre rail starts beyond it and none fetches; on a feed with no
 * personalised rows the first genre rail is on screen, and it does fetch, as a rail on screen
 * should. `hero-rails-paging.spec.ts` pins both shapes, and that every rail that asks was inside
 * the margin when it asked.
 */
export const RAIL_PREFETCH_MARGIN_PX = 200;

/**
 * Latches true the first time the element comes within {@link RAIL_PREFETCH_MARGIN_PX} of the
 * viewport, and stays true. A FETCH trigger, not a viewport beacon: nothing is recorded about what
 * was seen, the rail simply does not ask for cards nobody is near (the locked exposure rule is
 * "served", and this only changes when a rail is served).
 *
 * Without IntersectionObserver (old engines, the node test environment) it answers true — the
 * rail behaves as it always did rather than never loading.
 */
export function useNearViewport(ref: RefObject<Element | null>): boolean {
  const supported = typeof IntersectionObserver !== "undefined";
  const [near, setNear] = useState(!supported);

  useEffect(() => {
    if (near || !supported) return;
    const el = ref.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setNear(true);
          observer.disconnect();
        }
      },
      { rootMargin: `${RAIL_PREFETCH_MARGIN_PX}px 0px ${RAIL_PREFETCH_MARGIN_PX}px 0px` }
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [near, supported, ref]);

  return near;
}
