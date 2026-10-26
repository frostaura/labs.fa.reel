// @vitest-environment jsdom
import { cleanup, screen, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Layout from "./Layout";
import { api } from "../store/api";
import type { Session } from "../store/api/auth";
import { idleStatus, makeStore, renderWithStore, seedSyncStatus } from "../test/renderWithStore";

/** The shell as rendered (WS-9 task 11): header, fluid main, and a page error that stays inside it. */
const SESSION = {
  accountId: "00000000-0000-0000-0000-000000000001",
  displayName: "Test Founder",
  traktSlug: "test-founder",
  avatarUrl: null,
  region: "US",
  tier: "Founder",
  pipelineStage: "FeedReady",
  onboarded: true,
  pinConfigured: false,
} as unknown as Session;

class SilentEventSource {
  onerror: (() => void) | null = null;
  addEventListener() {}
  removeEventListener() {}
  close() {}
}

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn(() => new Promise(() => {})));
  vi.stubGlobal("EventSource", SilentEventSource);
  vi.spyOn(console, "error").mockImplementation(() => {});
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function Broken(): null {
  throw new Error("page render failed");
}

async function renderShell(path: string) {
  const store = makeStore();
  await seedSyncStatus(store, idleStatus());
  await store.dispatch(api.util.upsertQueryData("getSession", undefined, SESSION));
  const router = createMemoryRouter(
    [
      {
        element: <Layout />,
        children: [
          { path: "/saved", element: <h1>Saved</h1> },
          { path: "/broken", element: <Broken /> },
        ],
      },
    ],
    { initialEntries: [path] }
  );
  return renderWithStore(<RouterProvider router={router} />, store);
}

describe("Layout", () => {
  it("renders the page inside a fluid shell, under a header with the primary navigation", async () => {
    await renderShell("/saved");
    const header = screen.getByRole("banner");
    expect(within(header).getByRole("link", { name: "Reel home" })).toBeTruthy();
    for (const name of ["Home", "Saved", "Taste DNA"]) expect(within(header).getByRole("link", { name })).toBeTruthy();
    const main = screen.getByRole("main");
    expect(within(main).getByRole("heading", { name: "Saved" })).toBeTruthy();
    expect(main.className).toContain("max-w-shell");
    expect(main.className).toContain("px-gutter");
  });

  it("a page that throws is replaced by the route boundary; the header and the pill stay mounted", async () => {
    await renderShell("/broken");
    expect(screen.getByTestId("route-error-boundary")).toBeTruthy();
    expect(within(screen.getByRole("banner")).getByRole("link", { name: "Reel home" })).toBeTruthy();
    expect(screen.getByTestId("sync-announce")).toBeTruthy();
  });
});
