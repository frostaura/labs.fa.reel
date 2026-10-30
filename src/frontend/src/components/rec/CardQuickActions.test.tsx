// @vitest-environment jsdom
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import CardQuickActions from "./CardQuickActions";
import { renderWithStore } from "../../test/renderWithStore";

/**
 * HREV7-DESK-01, the surface where the card STAYS while its request is out (Ask Reel's grid hides
 * only confirmed removals; here nothing filters the card at all): the control that sent the write
 * says it is busy, all three refuse a second action, and a failure frees them again. Varied: each
 * one-tap action, and each outcome.
 */

const ORIGIN = "http://localhost";
const NativeRequest = globalThis.Request;
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

let requests: string[];
let answers: ((status: number) => void)[];

beforeEach(() => {
  requests = [];
  answers = [];
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn(
      (request: Request) =>
        new Promise<Response>((resolve) => {
          requests.push(`${request.method} ${new URL(request.url).pathname}`);
          answers.push((status) => resolve(new Response("{}", { status, headers: { "content-type": "application/json" } })));
        })
    )
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const mount = () =>
  renderWithStore(<CardQuickActions titleId="t-1" mediaType="Movie" tmdbId={603} name="The Matrix" />);

describe.each([
  { control: "quick-save", path: "POST /api/titles/movie/603/reactions/save_for_later" },
  { control: "quick-not-interested", path: "POST /api/titles/movie/603/reactions/not_interested" },
])("$control while its request is out", ({ control, path }) => {
  it.each([200, 500])("says it is busy, refuses a second action, and frees up once answered (%i)", async (status) => {
    mount();
    const button = screen.getByTestId(control);
    expect(button.getAttribute("aria-busy")).toBeNull();

    fireEvent.click(button);
    await waitFor(() => expect(requests).toEqual([path]));
    expect(button.getAttribute("aria-busy")).toBe("true");
    expect(screen.getByTestId("card-quick-actions").getAttribute("aria-busy")).toBe("true");
    for (const id of ["quick-save", "quick-not-interested", "quick-rate"]) {
      expect(screen.getByTestId(id).getAttribute("aria-disabled")).toBe("true");
    }
    // The pill pulses where motion is welcome — the tap visibly took.
    expect(button.querySelector("span")?.className).toContain("motion-safe:animate-pulse");

    // A second tap on it, a tap on the other action, and the rate trigger: nothing more is sent,
    // and the rate picker does not open.
    fireEvent.click(button);
    fireEvent.click(screen.getByTestId(control === "quick-save" ? "quick-not-interested" : "quick-save"));
    fireEvent.click(screen.getByTestId("quick-rate"));
    expect(screen.queryByTestId("quick-rate-picker")).toBeNull();
    await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
    expect(requests).toEqual([path]);

    await act(async () => answers[0](status));
    await waitFor(() => expect(button.getAttribute("aria-busy")).toBeNull());
    // A keyboard activation (this click's `detail` is 0) that failed hands focus back to the control
    // that sent it, for nobody had claimed focus since; a success leaves it to the undo toast.
    if (status === 500) await waitFor(() => expect(document.activeElement).toBe(button));
    expect(screen.getByTestId("quick-save").getAttribute("aria-disabled")).toBeNull();
    expect(button.querySelector("span")?.className).not.toContain("animate-pulse");
  });
});

/**
 * HREV8-PH-02: the reason picker survives the release of the TOUCH hold that opened it. On touch the
 * release fires a compatibility mousedown on the held button and moves focus back to it, after the
 * picker has opened — which a non-modal Radix popover reads as focus leaving it. Driven here as that
 * exact sequence; the controls show a right-click's picker and a picker whose hold has finished
 * releasing still close on the same events, so the fix is scoped to the release and nothing else.
 */
describe("the reason picker and the press that opened it (HREV8-PH-02)", () => {
  const settle = (ms = 30) => act(async () => new Promise((resolve) => setTimeout(resolve, ms)));
  const picker = () => screen.queryByTestId("quick-reason-picker");

  /** The events a touch screen fires when the finger lifts: pointerup, touchend, then compatibility mouse events. */
  async function release(button: HTMLElement) {
    fireEvent.pointerUp(button);
    fireEvent.mouseDown(button);
    act(() => button.focus());
    fireEvent.mouseUp(button);
    fireEvent.click(button, { detail: 1 });
    await settle();
  }

  async function hold(button: HTMLElement) {
    fireEvent.pointerDown(button);
    await settle(520);
    expect(picker()).not.toBeNull();
  }

  it.each(["tone", "genre"])("a hold leaves the picker open through its release, and posts the reason tapped (%s)", async (reason) => {
    mount();
    const button = screen.getByTestId("quick-not-interested");
    await hold(button);
    await release(button);

    expect(picker()).not.toBeNull();
    expect(requests).toEqual([]);

    fireEvent.click(screen.getByTestId(`quick-reason-${reason}`), { detail: 1 });
    await waitFor(() => expect(requests).toEqual(["POST /api/titles/movie/603/reactions/not_interested"]));
    const sent = vi.mocked(fetch).mock.calls[0][0] as Request;
    expect(await sent.json()).toEqual({ reason });
  });

  it("once the hold's click has been swallowed, focus leaving for the button closes the picker as before", async () => {
    mount();
    const button = screen.getByTestId("quick-not-interested");
    await hold(button);
    await release(button);
    expect(picker()).not.toBeNull();

    // A second, separate press: focus moves to the button again, now an ordinary outside move.
    act(() => screen.getByTestId(`quick-reason-tone`).focus());
    act(() => button.focus());
    await settle();
    expect(picker()).toBeNull();
    expect(requests).toEqual([]);
  });

  it("a right-click's picker still closes when focus goes back to the button (no hold was released)", async () => {
    mount();
    const button = screen.getByTestId("quick-not-interested");
    fireEvent.contextMenu(button);
    await settle();
    expect(picker()).not.toBeNull();
    act(() => button.focus());
    await settle();
    expect(picker()).toBeNull();
    expect(requests).toEqual([]);
  });

  it.each([
    { how: "Enter (keyboard click)", detail: 0 },
    { how: "a fresh pointer press", detail: 1 },
  ])("after a right-click and Escape, $how on the button dismisses with the default reason", async ({ detail }) => {
    mount();
    const button = screen.getByTestId("quick-not-interested");
    fireEvent.contextMenu(button);
    await settle();
    expect(picker()).not.toBeNull();
    fireEvent.keyDown(screen.getByTestId("quick-reason-picker"), { key: "Escape" });
    await settle();
    expect(picker()).toBeNull();

    if (detail === 1) {
      fireEvent.pointerDown(button);
      fireEvent.pointerUp(button);
    }
    fireEvent.click(button, { detail });
    await waitFor(() => expect(requests).toEqual(["POST /api/titles/movie/603/reactions/not_interested"]));
    const sent = vi.mocked(fetch).mock.calls[0][0] as Request;
    expect(await sent.json()).toEqual({ reason: "not-for-me" });
  });
});
