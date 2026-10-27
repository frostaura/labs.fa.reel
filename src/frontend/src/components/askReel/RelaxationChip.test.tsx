// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AskKnockout } from "../../lib/askReelStream";
import { Dialog, DialogContent, DialogDescription, DialogTitle, DialogTrigger } from "../ui/dialog";
import type { RuleFocus } from "../../lib/pickConditions";

/**
 * R1-33. The chip used to find the rule's control itself — `setTimeout` + `querySelector` against
 * the editor's test ids for up to three seconds — although the editor has owned that job since
 * WS-10B's `focusRule` (96437bf). Two implementations of "land on this rule" drift: the editor
 * knows when its own filters and chips have rendered, the poll could only guess. The chip now hands
 * the rule over and the editor lands on it.
 *
 * The editor is replaced by a probe that records what it was given: the editor's own landing
 * behaviour is WS-10B's and tested there; what the chip owes is the rule, once, as a stable object
 * (the editor applies a `focusRule` once per OBJECT, so a fresh literal per render would yank focus
 * back to the rule on every keystroke inside the dialog).
 */
const received: (RuleFocus | null | undefined)[] = [];
vi.mock("../settings/ConditionsEditor", () => ({
  default: ({ focusRule }: { focusRule?: RuleFocus | null }) => {
    received.push(focusRule);
    return <section data-testid="conditions-editor" />;
  },
}));

const { default: RelaxationChip } = await import("./RelaxationChip");

function knockout(extra: Partial<AskKnockout> = {}): AskKnockout {
  return { kind: "ExcludeGenre", value: "comedy", contentFilterId: null, label: "excluded genre: comedy", wouldUnlock: 4, ...extra };
}

beforeEach(() => {
  received.length = 0;
});
afterEach(cleanup);

describe("RelaxationChip — opens the Conditions editor ON its rule (R1-33)", () => {
  it.each([
    ["ExcludeGenre", "comedy"],
    ["Language", "en"],
    ["MinPredictedRating", "6.5"],
    ["ExcludeKeyword", "anime"],
  ])("hands the editor the %s rule it names", (kind, value) => {
    render(<RelaxationChip knockout={knockout({ kind, value })} />);
    fireEvent.click(screen.getByTestId("askreel-relax"));
    expect(screen.getByTestId("conditions-editor")).toBeTruthy();
    expect(received.at(-1)).toEqual({ kind, value });
  });

  it("gives the editor the same rule object across re-renders, so focus is applied once", () => {
    const k = knockout();
    const { rerender } = render(<RelaxationChip knockout={k} />);
    fireEvent.click(screen.getByTestId("askreel-relax"));
    const first = received.at(-1);
    // A parent re-render with an equal knockout (a fresh object off the next frame) is not a new rule.
    rerender(<RelaxationChip knockout={{ ...k }} />);
    rerender(<RelaxationChip knockout={{ ...k }} />);
    expect(received.length).toBeGreaterThan(1);
    for (const r of received) expect(r).toBe(first);
  });

  it("schedules no timer beyond what the dialog primitive itself schedules", () => {
    // The old chip polled with setTimeout for up to 3 s. Radix's own focus handling schedules timers
    // too, so the yardstick is a bare dialog opened the same way — the chip may add none to it.
    vi.useFakeTimers();
    try {
      const opened = (ui: React.ReactElement, trigger: string) => {
        const { unmount } = render(ui);
        const before = vi.getTimerCount();
        fireEvent.click(screen.getByTestId(trigger));
        act(() => {});
        const added = vi.getTimerCount() - before;
        unmount();
        vi.clearAllTimers();
        return added;
      };
      const bare = opened(
        <Dialog>
          <DialogTrigger data-testid="bare-trigger">open</DialogTrigger>
          <DialogContent>
            <DialogTitle>t</DialogTitle>
            <DialogDescription>d</DialogDescription>
            <section data-testid="conditions-editor" />
          </DialogContent>
        </Dialog>,
        "bare-trigger",
      );
      expect(opened(<RelaxationChip knockout={knockout()} />, "askreel-relax")).toBe(bare);
    } finally {
      vi.useRealTimers();
    }
  });
});
