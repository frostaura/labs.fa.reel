// @vitest-environment jsdom
import { configureStore } from "@reduxjs/toolkit";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { Provider } from "react-redux";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { RatingVerdict } from "../lib/verdictCopy";
import verdictReducer, { verdictShown } from "../store/verdictSlice";
import VerdictToast, { DISMISS_AFTER_MS } from "./VerdictToast";

/**
 * The after-rating toast holds its clock while someone is on it (HREV5 follow-up to HREV4-PH-02,
 * WCAG 2.2.1 Timing Adjustable). It carries two focusable controls — Dismiss and "See the track
 * record" — and stacks with the card undo on every rating, so a keyboard or screen-reader user who
 * has just reached either control must not have it pulled out from under them by a timer they
 * cannot see. Same rule, same behaviour, as UndoToast: on it holds, leaving gives a fresh full window.
 */

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const MISS: RatingVerdict = { predictedBefore: 8.4, surface: "home", shownAt: "2026-10-28T10:00:00Z", delta: 3.4 };
const HIT: RatingVerdict = { predictedBefore: 7.2, surface: null, shownAt: null, delta: 0.2 };

function mount() {
  const store = configureStore({ reducer: { verdict: verdictReducer } });
  render(
    <Provider store={store}>
      <MemoryRouter>
        <button type="button">elsewhere</button>
        <VerdictToast />
      </MemoryRouter>
    </Provider>
  );
  // Date.now() is faked too, so two ratings in one test would share a shownKey without a tick.
  const rate = (verdict: RatingVerdict, actual: number) =>
    act(() => {
      vi.advanceTimersByTime(1);
      store.dispatch(verdictShown({ verdict, actual }));
    });
  return { store, rate };
}

const advance = (ms: number) => act(() => void vi.advanceTimersByTime(ms));
const toast = () => screen.queryByTestId("verdict-toast");
const dismissButton = () => screen.getByRole("button", { name: "Dismiss" });
const trackRecord = () => screen.getByRole("link", { name: "See the track record" });
const elsewhere = () => screen.getByRole("button", { name: "elsewhere" });

describe("VerdictToast's window", () => {
  it("goes on its own when nobody is on it", () => {
    const { rate } = mount();
    rate(MISS, 5);
    advance(DISMISS_AFTER_MS - 1);
    expect(toast()).not.toBeNull();
    advance(1);
    expect(toast()).toBeNull();
  });

  it("holds while Dismiss has keyboard focus", () => {
    const { rate } = mount();
    rate(MISS, 5);
    advance(DISMISS_AFTER_MS - 500);
    act(() => dismissButton().focus());
    advance(DISMISS_AFTER_MS * 5);
    expect(toast()).not.toBeNull();
  });

  it("holds while the track-record link has keyboard focus, on a hit as on a miss", () => {
    const { rate } = mount();
    rate(HIT, 7);
    act(() => trackRecord().focus());
    advance(DISMISS_AFTER_MS * 5);
    expect(toast()).not.toBeNull();
  });

  it("moving between its controls stays held", () => {
    const { rate } = mount();
    rate(MISS, 5);
    act(() => dismissButton().focus());
    act(() => trackRecord().focus());
    advance(DISMISS_AFTER_MS * 2);
    expect(toast()).not.toBeNull();
  });

  it("gives a fresh full window once focus leaves", () => {
    const { rate } = mount();
    rate(MISS, 5);
    advance(DISMISS_AFTER_MS - 100);
    act(() => trackRecord().focus());
    advance(DISMISS_AFTER_MS * 2);
    act(() => elsewhere().focus());
    // Not the 100 ms that were left when it paused.
    advance(DISMISS_AFTER_MS - 1);
    expect(toast()).not.toBeNull();
    advance(1);
    expect(toast()).toBeNull();
  });

  it("holds while a pointer rests on it and restarts the full window when it leaves", () => {
    const { rate } = mount();
    rate(MISS, 5);
    advance(DISMISS_AFTER_MS - 100);
    fireEvent.pointerEnter(toast()!);
    advance(DISMISS_AFTER_MS * 2);
    expect(toast()).not.toBeNull();
    fireEvent.pointerLeave(toast()!);
    advance(DISMISS_AFTER_MS - 1);
    expect(toast()).not.toBeNull();
    advance(1);
    expect(toast()).toBeNull();
  });

  it("a second rating while held stays held, and shows the newer verdict", () => {
    const { rate } = mount();
    rate(MISS, 5);
    act(() => dismissButton().focus());
    rate(HIT, 7);
    advance(DISMISS_AFTER_MS * 2);
    expect(toast()).not.toBeNull();
    expect(toast()!.textContent).toContain("7");
    act(() => elsewhere().focus());
    advance(DISMISS_AFTER_MS);
    expect(toast()).toBeNull();
  });

  it("a second rating while nobody is on it restarts the clock", () => {
    const { rate } = mount();
    rate(MISS, 5);
    advance(DISMISS_AFTER_MS - 100);
    rate(HIT, 7);
    advance(DISMISS_AFTER_MS - 1);
    expect(toast()).not.toBeNull();
    advance(1);
    expect(toast()).toBeNull();
  });

  // The trap a held flag sets: the control that had focus disappears with the toast, so no blur
  // need ever arrive. The next rating's toast must not inherit a hold from a toast that is gone.
  it.each([
    ["Dismiss", () => dismissButton()],
    ["the track-record link", () => trackRecord()],
  ])("after closing it from %s while focused, the next toast still goes on its own", (_, control) => {
    const { rate } = mount();
    rate(MISS, 5);
    act(() => control().focus());
    fireEvent.click(control());
    expect(toast()).toBeNull();
    rate(HIT, 7);
    advance(DISMISS_AFTER_MS);
    expect(toast()).toBeNull();
  });

  it("after a pointer was resting on it when it closed, the next toast still goes on its own", () => {
    const { rate } = mount();
    rate(MISS, 5);
    fireEvent.pointerEnter(toast()!);
    fireEvent.click(dismissButton());
    rate(HIT, 7);
    advance(DISMISS_AFTER_MS);
    expect(toast()).toBeNull();
  });
});
