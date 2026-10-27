// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import UndoToast from "../search/UndoToast";
import { ToastPortal } from "./toast-stack";

afterEach(cleanup);

const column = () => document.querySelector<HTMLElement>('[data-testid="toast-stack"]');

describe("the shared toast column", () => {
  it("stacks two toasts in one column instead of laying one over the other", () => {
    render(
      <>
        <UndoToast id="a" testId="delete-toast" message="Deleted it." onUndo={vi.fn()} onExpire={vi.fn()} />
        <UndoToast id="b" testId="save-toast" message="Saved it." onUndo={vi.fn()} onExpire={vi.fn()} />
      </>
    );
    const stack = column();
    expect(stack?.parentElement).toBe(document.body);
    expect(stack?.className).toContain("flex-col-reverse");
    expect([...(stack?.children ?? [])].map((c) => c.getAttribute("data-testid"))).toEqual(["delete-toast", "save-toast"]);
    // Neither toast positions itself: the column does, once, so they cannot coincide.
    for (const id of ["delete-toast", "save-toast"]) expect(screen.getByTestId(id).className).not.toMatch(/\bfixed\b/);
  });

  it("is attached only while a toast is up", () => {
    const { rerender } = render(<ToastPortal><p>one</p></ToastPortal>);
    expect(column()).not.toBeNull();
    rerender(<></>);
    expect(column()).toBeNull();
  });

  it("lifts a dialog's aria-hidden sweep off itself when a new toast arrives", () => {
    const { rerender } = render(<ToastPortal><p>first</p></ToastPortal>);
    // What a Radix dialog does to every other child of <body> when it opens.
    column()!.setAttribute("aria-hidden", "true");
    rerender(
      <>
        <ToastPortal><p>first</p></ToastPortal>
        <ToastPortal><p>second</p></ToastPortal>
      </>
    );
    expect(column()!.hasAttribute("aria-hidden")).toBe(false);
  });

  it("keeps a press on a toast from reaching a dialog's outside-press listener", () => {
    const outside = vi.fn();
    document.addEventListener("pointerdown", outside);
    render(<ToastPortal><button type="button">Undo</button></ToastPortal>);
    screen.getByRole("button", { name: "Undo" }).dispatchEvent(new Event("pointerdown", { bubbles: true }));
    document.removeEventListener("pointerdown", outside);
    expect(outside).not.toHaveBeenCalled();
  });
});
