// @vitest-environment jsdom
import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RAIL_BACKOFF_MS } from "../components/rows/railPaging";
import { useReadRetry, type ReadState } from "./useReadRetry";

/**
 * HREV8-DESK-02: a failed Home read retries on the rails' schedule and then says so, instead of the
 * section vanishing after one 503. Driven through the states an RTK Query result passes through.
 */

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

const fetching: Omit<ReadState, "refetch"> = { isError: false, isFetching: true, isSuccess: false };
const failed: Omit<ReadState, "refetch"> = { isError: true, isFetching: false, isSuccess: false };
const loaded: Omit<ReadState, "refetch"> = { isError: false, isFetching: false, isSuccess: true };

function setup() {
  const refetch = vi.fn();
  const hook = renderHook((state: Omit<ReadState, "refetch">) => useReadRetry({ ...state, refetch }), {
    initialProps: fetching,
  });
  return { refetch, ...hook };
}

describe("useReadRetry", () => {
  it("a read that never fails costs nothing and asks for nothing", () => {
    const { result, rerender, refetch } = setup();
    expect(result.current.phase).toBe("ok");
    rerender(loaded);
    act(() => vi.advanceTimersByTime(10_000));
    expect(result.current.phase).toBe("ok");
    expect(refetch).not.toHaveBeenCalled();
  });

  it("one failure retries after 1 s and a success clears it (the first call failed, the second did not)", () => {
    const { result, rerender, refetch } = setup();
    rerender(failed);
    expect(result.current.phase).toBe("retrying");
    act(() => vi.advanceTimersByTime(RAIL_BACKOFF_MS[0] - 1));
    expect(refetch).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(1));
    expect(refetch).toHaveBeenCalledTimes(1);
    rerender(fetching);
    rerender(loaded);
    expect(result.current.phase).toBe("ok");
    act(() => vi.advanceTimersByTime(10_000));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it("retries at 1, 2 and 4 s, then stops and waits; Retry is one more request", () => {
    const { result, rerender, refetch } = setup();
    for (const [i, delay] of RAIL_BACKOFF_MS.entries()) {
      rerender(failed);
      expect(result.current.phase).toBe("retrying");
      act(() => vi.advanceTimersByTime(delay));
      expect(refetch).toHaveBeenCalledTimes(i + 1);
      rerender(fetching);
    }
    rerender(failed);
    expect(result.current.phase).toBe("failed");
    act(() => vi.advanceTimersByTime(60_000));
    expect(refetch).toHaveBeenCalledTimes(RAIL_BACKOFF_MS.length);

    act(() => result.current.retry());
    expect(refetch).toHaveBeenCalledTimes(RAIL_BACKOFF_MS.length + 1);
    expect(result.current.phase).toBe("retrying");
    rerender(fetching);
    rerender(failed);
    // Straight back to failed: a manual retry is one request, not a fresh round of three.
    expect(result.current.phase).toBe("failed");
    act(() => vi.advanceTimersByTime(60_000));
    expect(refetch).toHaveBeenCalledTimes(RAIL_BACKOFF_MS.length + 1);

    act(() => result.current.retry());
    rerender(fetching);
    rerender(loaded);
    expect(result.current.phase).toBe("ok");
  });

  it("Retry does nothing while the automatic retries are still running", () => {
    const { result, rerender, refetch } = setup();
    rerender(failed);
    act(() => result.current.retry());
    expect(refetch).not.toHaveBeenCalled();
  });

  it("a later failure after a success starts a fresh round", () => {
    const { result, rerender, refetch } = setup();
    rerender(loaded);
    rerender(fetching);
    rerender(failed);
    expect(result.current.phase).toBe("retrying");
    act(() => vi.advanceTimersByTime(RAIL_BACKOFF_MS[0]));
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it("unmounting during a backoff cancels the retry", () => {
    const { rerender, refetch, unmount } = setup();
    rerender(failed);
    unmount();
    act(() => vi.advanceTimersByTime(10_000));
    expect(refetch).not.toHaveBeenCalled();
  });
});
