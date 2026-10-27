// @vitest-environment jsdom
import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useSettledGate } from "./useSettledGate";

/**
 * The gate behind /settings and /dna's one-shot reveal (HREV1-D03). Fixtures vary one input at a
 * time: settled at mount, settled later, never settled (the cap), and un-settling after opening.
 */
beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe("useSettledGate", () => {
  it("is open on the first render when the reads have already settled", () => {
    const { result } = renderHook(() => useSettledGate(true, 2000));
    expect(result.current).toBe(true);
  });

  it("stays shut while the reads are in flight, and opens in the render where they settle", () => {
    const { result, rerender } = renderHook(({ settled }) => useSettledGate(settled, 2000), {
      initialProps: { settled: false },
    });
    expect(result.current).toBe(false);
    act(() => vi.advanceTimersByTime(1999));
    expect(result.current).toBe(false);
    rerender({ settled: true });
    expect(result.current).toBe(true);
  });

  it("opens at the cap when a read never answers", () => {
    const { result } = renderHook(() => useSettledGate(false, 2000));
    act(() => vi.advanceTimersByTime(1999));
    expect(result.current).toBe(false);
    act(() => vi.advanceTimersByTime(1));
    expect(result.current).toBe(true);
  });

  it("never shuts again once open, even when a later read is in flight", () => {
    const { result, rerender } = renderHook(({ settled }) => useSettledGate(settled, 2000), {
      initialProps: { settled: true },
    });
    rerender({ settled: false });
    act(() => vi.advanceTimersByTime(5000));
    expect(result.current).toBe(true);
  });

  it("leaves no timer running after it has opened", () => {
    const { rerender, unmount } = renderHook(({ settled }) => useSettledGate(settled, 2000), {
      initialProps: { settled: false },
    });
    rerender({ settled: true });
    expect(vi.getTimerCount()).toBe(0);
    unmount();
  });
});
