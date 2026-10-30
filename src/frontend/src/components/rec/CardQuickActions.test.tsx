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
