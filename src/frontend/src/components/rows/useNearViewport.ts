import { useEffect, useState, type RefObject } from "react";

/**
 * How far below (and above) the viewport a rail may be and still start loading. Chosen against the
 * cold-load acceptance at 1440×900 and 390×844: one screen of rails is never within this margin of
 * the fold on a cold Home, so below-the-fold genre rails fire nothing — no billed curation call and
 * no exposure row (I-2, O-22) — while a rail still lands ahead of a normal scroll.
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
