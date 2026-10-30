// @vitest-environment jsdom
import { act, cleanup, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Onboarding from "./Onboarding";
import { eventReceived, markStalled, pollAdvance } from "../store/onboardingSlice";
import { makeStore, renderWithStore, type TestStore } from "../test/renderWithStore";

/**
 * REV7-A11Y-01 (WCAG 4.1.3): the onboarding show renders outside the app shell, so it gets none of
 * the shell's live regions. Its stage line changes over a multi-minute import ("Pulling in…",
 * "Teaching your model…", "Your taste DNA is ready.", "Almost there…") and a screen-reader user
 * must hear each change without moving focus. The region has to be in the DOM before the text
 * changes — a live region inserted together with its content is not announced — so these tests
 * hold on to the region from the first render and watch the text change inside that same node.
 */

/** jsdom has no EventSource; the page only opens one and listens. */
class SilentEventSource {
  addEventListener() {}
  close() {}
}

beforeEach(() => {
  vi.stubGlobal("EventSource", SilentEventSource);
  // The session and status queries fire on mount; nothing they answer matters here, and a failed
  // read leaves the stage where the test put it.
  vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response("{}", { status: 500, headers: { "content-type": "application/json" } }))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function mount(store: TestStore = makeStore()) {
  renderWithStore(
    <MemoryRouter>
      <Onboarding />
    </MemoryRouter>,
    store
  );
  return store;
}

const stageEvent = (seq: number, stage: string, extra: Record<string, unknown> = {}) =>
  eventReceived({ type: "stage-changed", data: { seq, stage, ...extra } });

function liveRegion() {
  const region = screen.getByTestId("stage-announcer");
  expect(region.getAttribute("role")).toBe("status");
  expect(region.getAttribute("aria-live")).toBe("polite");
  expect(region.getAttribute("aria-atomic")).toBe("true");
  return region;
}

describe("onboarding stage changes are announced (REV7-A11Y-01)", () => {
  it("is a polite, atomic status region from the first render", () => {
    mount();
    const region = liveRegion();
    expect(within(region).getByTestId("stage-copy").textContent).toContain("Linking your Trakt profile…");
    // The only status region on the page: two would read the same change twice.
    expect(screen.getAllByRole("status")).toEqual([region]);
  });

  it("changes the stage text inside the same region, through to the reveal", () => {
    const store = mount();
    const region = liveRegion();

    act(() => {
      store.dispatch(eventReceived({ type: "ingest-progress", data: { seq: 1, kind: "movies", found: 12 } }));
    });
    expect(screen.getByTestId("stage-announcer")).toBe(region);
    expect(region.textContent).toContain("Pulling in everything you've ever watched…");

    act(() => {
      store.dispatch(stageEvent(2, "Training"));
    });
    expect(screen.getByTestId("stage-announcer")).toBe(region);
    expect(region.textContent).toContain("Teaching your model…");

    act(() => {
      store.dispatch(eventReceived({ type: "feed-ready", data: { seq: 3 } }));
    });
    expect(screen.getByTestId("stage-announcer")).toBe(region);
    expect(region.textContent).toContain("Your taste DNA is ready.");
  });

  it.each([
    {
      name: "the too-few-ratings stop, from the stream",
      act: (store: TestStore) => store.dispatch(stageEvent(4, "Degraded", { reason: "insufficient-ratings" })),
      text: "Almost there — Reel needs a few more of your ratings.",
    },
    {
      name: "the too-few-ratings stop, from the poll",
      act: (store: TestStore) =>
        store.dispatch(
          pollAdvance({ pipelineStage: "NotEnoughRatings", degradedReason: "NotEnoughRatings", notEnoughRatings: { usableRatings: 4, required: 25 } })
        ),
      text: "Almost there — Reel needs a few more of your ratings.",
    },
    {
      name: "the floor-met wait for the first model",
      act: (store: TestStore) =>
        store.dispatch(
          pollAdvance({ pipelineStage: "AwaitingFirstModel", degradedReason: "AwaitingFirstModel", notEnoughRatings: null, retrain: { hasActiveModel: false } })
        ),
      text: "You've rated enough — your model is next.",
    },
    {
      name: "a stop",
      act: (store: TestStore) => store.dispatch(eventReceived({ type: "failed", data: { seq: 5, error: "boom" } })),
      text: "Building your profile stopped.",
    },
  ])("announces $name in the same region", ({ act: change, text }) => {
    const store = mount();
    const region = liveRegion();
    act(() => {
      change(store);
    });
    expect(screen.getByTestId("stage-announcer")).toBe(region);
    expect(region.textContent).toContain(text);
  });

  it("says 'still working' inside the region when the stream goes quiet", () => {
    const store = mount();
    const region = liveRegion();
    expect(within(region).queryByTestId("stall-notice")).toBeNull();
    act(() => {
      store.dispatch(markStalled());
    });
    expect(within(region).getByTestId("stall-notice").textContent).toContain("Still working — large libraries take a minute…");
  });
});

describe("the progress strip is a named, indeterminate progress bar (REV7-A11Y-01)", () => {
  it("is exposed while the show runs, with no value (indeterminate)", () => {
    mount();
    const bar = screen.getByRole("progressbar", { name: "Building your profile" });
    expect(bar.hasAttribute("aria-valuenow")).toBe(false);
  });

  it.each([
    { name: "the reveal", end: (store: TestStore) => store.dispatch(eventReceived({ type: "feed-ready", data: { seq: 1 } })) },
    { name: "a stop", end: (store: TestStore) => store.dispatch(eventReceived({ type: "failed", data: { seq: 1, error: "boom" } })) },
  ])("is gone once the show ends at $name", ({ end }) => {
    const store = mount();
    act(() => {
      end(store);
    });
    expect(screen.queryByRole("progressbar")).toBeNull();
  });
});
