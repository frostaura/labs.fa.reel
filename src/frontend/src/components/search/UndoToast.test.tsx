// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import UndoToast, { UNDO_WINDOW_MS } from "./UndoToast";

/**
 * HREV4-PH-02 (WCAG 2.2.1, Timing Adjustable): the undo window must not run out under a person who
 * is on the toast. A keyboard or screen-reader user who has reached the Undo button, or a pointer
 * resting on the toast, holds the clock; leaving gives a fresh full window, never the scraps of the
 * old one.
 */

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function show(props: Partial<Parameters<typeof UndoToast>[0]> = {}) {
  const onExpire = vi.fn();
  const onUndo = vi.fn();
  const view = render(
    <>
      <button type="button">elsewhere</button>
      <UndoToast id="a" testId="t" message="Did it." onUndo={onUndo} onExpire={onExpire} {...props} />
    </>
  );
  return { onExpire, onUndo, view, toast: () => screen.getByTestId("t") };
}

const advance = (ms: number) => act(() => void vi.advanceTimersByTime(ms));

describe("UndoToast's window", () => {
  it("commits once when nobody is on it", () => {
    const { onExpire } = show();
    advance(UNDO_WINDOW_MS - 1);
    expect(onExpire).not.toHaveBeenCalled();
    advance(1);
    expect(onExpire).toHaveBeenCalledTimes(1);
    advance(UNDO_WINDOW_MS * 3);
    expect(onExpire).toHaveBeenCalledTimes(1);
  });

  it("holds while the Undo button has keyboard focus, and Undo still works long after the window", () => {
    const { onExpire, onUndo } = show();
    advance(UNDO_WINDOW_MS - 500);
    act(() => screen.getByTestId("t-undo").focus());
    advance(UNDO_WINDOW_MS * 5);
    expect(onExpire).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId("t-undo"));
    expect(onUndo).toHaveBeenCalledTimes(1);
  });

  it("holds while focus is on Dismiss too, and moving between its buttons does not restart it", () => {
    const { onExpire } = show();
    act(() => screen.getByTestId("t-undo").focus());
    act(() => screen.getByTestId("t-dismiss").focus());
    advance(UNDO_WINDOW_MS * 2);
    expect(onExpire).not.toHaveBeenCalled();
  });

  it("gives a fresh full window once focus leaves the toast", () => {
    const { onExpire } = show();
    advance(UNDO_WINDOW_MS - 100);
    act(() => screen.getByTestId("t-undo").focus());
    advance(UNDO_WINDOW_MS * 2);
    act(() => screen.getByRole("button", { name: "elsewhere" }).focus());
    // Not the 100 ms that were left when it paused.
    advance(UNDO_WINDOW_MS - 1);
    expect(onExpire).not.toHaveBeenCalled();
    advance(1);
    expect(onExpire).toHaveBeenCalledTimes(1);
  });

  it("holds while a pointer rests on it and restarts the full window when it leaves", () => {
    const { onExpire, toast } = show();
    advance(UNDO_WINDOW_MS - 100);
    fireEvent.pointerEnter(toast());
    advance(UNDO_WINDOW_MS * 2);
    expect(onExpire).not.toHaveBeenCalled();
    fireEvent.pointerLeave(toast());
    advance(UNDO_WINDOW_MS - 1);
    expect(onExpire).not.toHaveBeenCalled();
    advance(1);
    expect(onExpire).toHaveBeenCalledTimes(1);
  });

  it("stays held while either the pointer or focus is still on it", () => {
    const { onExpire, toast } = show();
    fireEvent.pointerEnter(toast());
    act(() => screen.getByTestId("t-undo").focus());
    fireEvent.pointerLeave(toast());
    advance(UNDO_WINDOW_MS * 2);
    expect(onExpire).not.toHaveBeenCalled();
    act(() => screen.getByRole("button", { name: "elsewhere" }).focus());
    advance(UNDO_WINDOW_MS);
    expect(onExpire).toHaveBeenCalledTimes(1);
  });

  it("a new action while held stays held, and commits the newest closure when it finally runs out", () => {
    const first = vi.fn();
    const second = vi.fn();
    const { view, toast } = show({ onExpire: first });
    fireEvent.pointerEnter(toast());
    view.rerender(
      <>
        <button type="button">elsewhere</button>
        <UndoToast id="b" testId="t" message="Did another." onUndo={vi.fn()} onExpire={second} />
      </>
    );
    advance(UNDO_WINDOW_MS * 2);
    expect(first).not.toHaveBeenCalled();
    expect(second).not.toHaveBeenCalled();
    fireEvent.pointerLeave(toast());
    advance(UNDO_WINDOW_MS);
    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledTimes(1);
  });

  it("an unrelated re-render does not restart the window", () => {
    const onExpire = vi.fn();
    const { rerender } = render(<UndoToast id="a" testId="t" message="Did it." onUndo={vi.fn()} onExpire={onExpire} />);
    advance(UNDO_WINDOW_MS - 100);
    rerender(<UndoToast id="a" testId="t" message="Did it." onUndo={vi.fn()} onExpire={() => onExpire()} />);
    advance(100);
    expect(onExpire).toHaveBeenCalledTimes(1);
  });
});
