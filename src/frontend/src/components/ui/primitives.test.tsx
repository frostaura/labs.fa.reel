// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Button } from "./button";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "./card";
import { Chip } from "./chip";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "./dialog";
import LoadFailed from "./load-failed";
import { Input } from "./input";
import { PageHeader } from "./page-header";
import { SegmentedControl } from "./segmented-control";
import { Select } from "./select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "./tabs";

afterEach(cleanup);

describe("Button", () => {
  it("never submits a form by accident: type defaults to button", () => {
    render(<Button>Save</Button>);
    expect(screen.getByRole("button", { name: "Save" }).getAttribute("type")).toBe("button");
  });

  it("keeps an explicit submit", () => {
    render(<Button type="submit">Send</Button>);
    expect(screen.getByRole("button", { name: "Send" }).getAttribute("type")).toBe("submit");
  });

  it("every variant shows a keyboard focus ring", () => {
    for (const variant of ["default", "primary", "destructive", "outline", "ghost", "link", "chip"] as const) {
      render(<Button variant={variant}>{variant}</Button>);
      expect(screen.getByRole("button", { name: variant }).className).toContain("focus-visible:ring-2");
    }
  });
});

describe("Chip", () => {
  it("announces its on/off state", () => {
    function Toggle() {
      const [on, setOn] = useState(false);
      return (
        <Chip selected={on} onClick={() => setOn((v) => !v)}>
          Horror
        </Chip>
      );
    }
    render(<Toggle />);
    const chip = screen.getByRole("button", { name: "Horror" });
    expect(chip.getAttribute("aria-pressed")).toBe("false");
    fireEvent.click(chip);
    expect(chip.getAttribute("aria-pressed")).toBe("true");
    expect(chip.getAttribute("data-state")).toBe("on");
  });
});

describe("SegmentedControl", () => {
  const OPTIONS = [
    { value: "7d", label: "7 days" },
    { value: "30d", label: "30 days" },
    { value: "all", label: "All time", disabled: true },
  ] as const;

  function Window({ onChange = () => {} }: { onChange?: (v: string) => void }) {
    const [value, setValue] = useState<"7d" | "30d" | "all">("7d");
    return (
      <SegmentedControl
        label="Window"
        options={OPTIONS}
        value={value}
        onValueChange={(v) => {
          setValue(v);
          onChange(v);
        }}
      />
    );
  }

  it("is a labelled radio group with one tab stop on the chosen segment", () => {
    render(<Window />);
    expect(screen.getByRole("radiogroup", { name: "Window" })).toBeTruthy();
    const [seven, thirty] = screen.getAllByRole("radio");
    expect(seven.getAttribute("aria-checked")).toBe("true");
    expect(seven.tabIndex).toBe(0);
    expect(thirty.tabIndex).toBe(-1);
  });

  it("arrow keys move the choice and the focus, skipping disabled segments and wrapping", () => {
    const changes: string[] = [];
    render(<Window onChange={(v) => changes.push(v)} />);
    const [seven, thirty] = screen.getAllByRole("radio");
    fireEvent.keyDown(seven, { key: "ArrowRight" });
    expect(thirty.getAttribute("aria-checked")).toBe("true");
    expect(document.activeElement).toBe(thirty);
    fireEvent.keyDown(thirty, { key: "ArrowRight" });
    expect(seven.getAttribute("aria-checked")).toBe("true");
    expect(changes).toEqual(["30d", "7d"]);
  });
});

describe("Input", () => {
  it("is labelled, and ties its hint and error to the field", () => {
    render(<Input label="Email" hint="We only email about your account." error="That address has no @." />);
    const input = screen.getByLabelText("Email");
    expect(input.getAttribute("aria-invalid")).toBe("true");
    const described = (input.getAttribute("aria-describedby") ?? "").split(" ").map((id) => document.getElementById(id)?.textContent);
    expect(described).toEqual(["We only email about your account.", "That address has no @."]);
  });

  it("can hide its label visually and still be labelled", () => {
    render(<Input label="Search titles" hideLabel />);
    expect(screen.getByLabelText("Search titles")).toBeTruthy();
    expect(screen.getByText("Search titles").className).toContain("sr-only");
  });
});

describe("Select", () => {
  it("is a labelled combobox showing the chosen option, never a native select", () => {
    render(
      <Select
        label="Region"
        value="ZA"
        onValueChange={vi.fn()}
        options={[
          { value: "US", label: "United States" },
          { value: "ZA", label: "South Africa" },
        ]}
      />
    );
    const trigger = screen.getByLabelText("Region");
    expect(trigger.getAttribute("role")).toBe("combobox");
    expect(trigger.textContent).toContain("South Africa");
    expect(document.querySelector("select:not([aria-hidden='true'])")).toBeNull();
  });
});

describe("Tabs", () => {
  it("switches panels and marks the active tab", () => {
    render(
      <Tabs defaultValue="a">
        <TabsList aria-label="Sections">
          <TabsTrigger value="a">First</TabsTrigger>
          <TabsTrigger value="b">Second</TabsTrigger>
        </TabsList>
        <TabsContent value="a">Panel A</TabsContent>
        <TabsContent value="b">Panel B</TabsContent>
      </Tabs>
    );
    expect(screen.getByRole("tabpanel").textContent).toBe("Panel A");
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Second" }), { button: 0 });
    expect(screen.getByRole("tab", { name: "Second" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tabpanel").textContent).toBe("Panel B");
  });
});

describe("PageHeader and Card", () => {
  it("gives the page one h1 and a card an h2 unless told otherwise", () => {
    render(
      <>
        <PageHeader title="Saved" description="What you meant to watch." actions={<Button>Sort</Button>} />
        <Card>
          <CardHeader>
            <CardTitle>Queue</CardTitle>
            <CardDescription>Tonight first.</CardDescription>
          </CardHeader>
          <CardTitle as="h3">Sub</CardTitle>
          <CardContent>body</CardContent>
          <CardFooter>
            <Button>Open</Button>
          </CardFooter>
        </Card>
      </>
    );
    expect(screen.getByRole("heading", { level: 1, name: "Saved" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 2, name: "Queue" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 3, name: "Sub" })).toBeTruthy();
    expect(screen.getByText("Tonight first.").tagName).toBe("P");
    expect(screen.getByRole("button", { name: "Open" })).toBeTruthy();
  });
});

/**
 * R1-38: the 44 px touch floor lives in the primitives, not in a lane's helper string. jsdom cannot
 * evaluate `(pointer: coarse)`, so this pins the class on every primitive and every size; the pixels
 * are measured in a browser by tests-e2e/shell-targets.spec.ts.
 */
describe("the 44 px touch floor", () => {
  const FLOOR = ["coarse:min-h-11", "coarse:min-w-11"];
  const hasFloor = (el: Element, classes = FLOOR) => classes.every((c) => el.className.split(" ").includes(c));

  it("is on every Button variant and size except the inline link", () => {
    for (const variant of ["default", "primary", "destructive", "outline", "ghost", "chip"] as const) {
      for (const size of ["default", "sm", "lg", "chip", "icon", "icon-sm"] as const) {
        const { unmount } = render(<Button variant={variant} size={size}>{`${variant}-${size}`}</Button>);
        expect(hasFloor(screen.getByRole("button", { name: `${variant}-${size}` })), `${variant}/${size}`).toBe(true);
        unmount();
      }
    }
    render(<Button variant="link">inline</Button>);
    expect(screen.getByRole("button", { name: "inline" }).className).toContain("coarse:min-h-0");
  });

  it("survives a caller's compact size: min-height and min-width outrank h-7 w-7", () => {
    render(
      <Button size="icon-sm" className="h-7 w-7 min-h-0" aria-label="dense">
        x
      </Button>
    );
    const button = screen.getByRole("button", { name: "dense" });
    expect(hasFloor(button)).toBe(true);
    expect(button.className).toContain("h-7");
  });

  it("is on the Chip, the Input, the Select trigger and a Tab", () => {
    render(
      <>
        <Chip selected={false}>Drama</Chip>
        <Input label="Email" />
        <Select label="Region" value="ZA" onValueChange={vi.fn()} options={[{ value: "ZA", label: "South Africa" }]} />
        <Tabs defaultValue="a">
          <TabsList aria-label="Sections">
            <TabsTrigger value="a">First</TabsTrigger>
          </TabsList>
        </Tabs>
      </>
    );
    expect(hasFloor(screen.getByRole("button", { name: "Drama" }))).toBe(true);
    expect(hasFloor(screen.getByLabelText("Email"), ["coarse:min-h-11"])).toBe(true);
    expect(hasFloor(screen.getByLabelText("Region"), ["coarse:min-h-11"])).toBe(true);
    expect(hasFloor(screen.getByRole("tab", { name: "First" }), ["coarse:min-h-11"])).toBe(true);
  });

  it("is on the dialog's close button, which also shows a focus ring", () => {
    render(
      <Dialog open>
        <DialogContent>
          <DialogTitle>Remove PIN</DialogTitle>
          <DialogDescription>Conditions unlock for everyone on this account.</DialogDescription>
        </DialogContent>
      </Dialog>
    );
    const close = screen.getByRole("button", { name: "Close" });
    expect(hasFloor(close)).toBe(true);
    expect(close.className).toContain("focus-visible:ring-2");
  });

  it("is on a failed read's Retry, compact size and all", () => {
    render(<LoadFailed what="your taste DNA" onRetry={vi.fn()} />);
    expect(hasFloor(screen.getByTestId("load-failed-retry"))).toBe(true);
  });
});
