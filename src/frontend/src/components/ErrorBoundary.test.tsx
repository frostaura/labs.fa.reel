// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import ErrorBoundary from "./ErrorBoundary";

function Boom({ when }: { when: boolean }): null {
  if (when) throw new Error("render failed");
  return null;
}

beforeEach(() => {
  // React logs every caught render error to console.error; the assertions below are the record.
  vi.spyOn(console, "error").mockImplementation(() => {});
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("ErrorBoundary", () => {
  it("a page that throws is replaced by the route boundary while the shell around it stays", () => {
    render(
      <>
        <header>Reel header</header>
        <ErrorBoundary scope="route" resetKey="/saved">
          <Boom when />
        </ErrorBoundary>
      </>
    );
    expect(screen.getByTestId("route-error-boundary").getAttribute("role")).toBe("alert");
    expect(screen.getByText("Something went wrong on this page")).toBeTruthy();
    expect(screen.getByText("Reel header")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Try again" })).toBeTruthy();
  });

  it("navigating elsewhere (a new reset key) clears the caught error", () => {
    const { rerender } = render(
      <ErrorBoundary scope="route" resetKey="/saved">
        <Boom when />
        <p>page</p>
      </ErrorBoundary>
    );
    expect(screen.queryByText("page")).toBeNull();
    rerender(
      <ErrorBoundary scope="route" resetKey="/home">
        <Boom when={false} />
        <p>page</p>
      </ErrorBoundary>
    );
    expect(screen.getByText("page")).toBeTruthy();
    expect(screen.queryByTestId("route-error-boundary")).toBeNull();
  });

  it("the root boundary offers a reload and no retry", () => {
    render(
      <ErrorBoundary scope="root">
        <Boom when />
      </ErrorBoundary>
    );
    expect(screen.getByTestId("root-error-boundary")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reload Reel" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Try again" })).toBeNull();
  });
});
