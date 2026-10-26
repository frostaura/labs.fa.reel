import { useEffect, useRef, useState } from "react";

/** Whether the user asked their OS for less motion. */
function prefersReducedMotion(): boolean {
  return typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true;
}

/**
 * Eased count-up toward a moving target — the odometer feel for live ingest tickers. With reduced
 * motion asked for, the number lands on its target in one frame, as index.css does for CSS motion.
 */
export function useCountUp(target: number, durationMs = 700): number {
  const [value, setValue] = useState(0);
  const fromRef = useRef(0);

  useEffect(() => {
    const from = fromRef.current;
    if (from === target) {
      return;
    }

    const start = performance.now();
    const ms = prefersReducedMotion() ? 0 : durationMs;
    let raf = 0;
    const tick = (now: number) => {
      const t = ms <= 0 ? 1 : Math.min(1, (now - start) / ms);
      const eased = 1 - Math.pow(1 - t, 3);
      const next = Math.round(from + ((target - from) * eased));
      setValue(next);
      if (t < 1) {
        raf = requestAnimationFrame(tick);
      } else {
        fromRef.current = target;
      }
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [target, durationMs]);

  return value;
}
