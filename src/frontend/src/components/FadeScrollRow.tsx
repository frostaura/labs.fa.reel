import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";

const FADE = "3.5rem";

/**
 * Horizontal scroll row that fades its edges to signal "there's more this way". The right edge
 * fades whenever cards are cut off to the right; the left edge fades once you've scrolled in.
 *
 * Driven by a CSS `mask-image` keyed off scroll position. A mask is painted on the element's
 * visible box and does NOT scroll with its content (like `background-attachment: scroll`), so the
 * gradient stays pinned to whichever edge currently hides cards — no overlay element, so nothing
 * clips card hover/focus rings. Drop-in for the shared `flex … overflow-x-auto` row markup: pass
 * the same className through.
 */
export default function FadeScrollRow({ className, children }: { className?: string; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const [edges, setEdges] = useState({ left: false, right: false });

  const measure = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    // 4px slack so sub-pixel rounding at the extremes doesn't leave a hairline fade.
    const left = el.scrollLeft > 4;
    const right = el.scrollLeft + el.clientWidth < el.scrollWidth - 4;
    setEdges((prev) => (prev.left === left && prev.right === right ? prev : { left, right }));
  }, []);

  useEffect(() => {
    measure();
    const el = ref.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    // Re-measure when the row or its content resizes (filter changes, image loads, viewport).
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [measure]);

  const mask =
    edges.left && edges.right
      ? `linear-gradient(to right, transparent, #000 ${FADE}, #000 calc(100% - ${FADE}), transparent)`
      : edges.right
        ? `linear-gradient(to right, #000 calc(100% - ${FADE}), transparent)`
        : edges.left
          ? `linear-gradient(to right, transparent, #000 ${FADE})`
          : undefined;

  return (
    <div
      ref={ref}
      onScroll={measure}
      className={className}
      style={mask ? { maskImage: mask, WebkitMaskImage: mask } : undefined}
    >
      {children}
    </div>
  );
}
