// @vitest-environment jsdom
import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Button } from "../components/ui/button";
import { axeViolations } from "./axe";

afterEach(cleanup);

describe("axeViolations", () => {
  it("names what breaks: an icon button with no accessible name", async () => {
    const { container } = render(<Button size="icon"><svg aria-hidden /></Button>);
    expect((await axeViolations(container)).map((v) => v.rule)).toEqual(["button-name"]);
  });

  it("passes a named control, and skips the rules jsdom cannot judge", async () => {
    // Contrast needs paint; jsdom has none, so this would be a false alarm, never a finding.
    const { container } = render(<Button aria-label="Dismiss" size="icon"><svg aria-hidden /></Button>);
    expect(await axeViolations(container)).toEqual([]);
  });

  it("can switch off a rule that does not apply to a fragment", async () => {
    const { container } = render(<ul><div>not a list item</div></ul>);
    expect((await axeViolations(container)).map((v) => v.rule)).toEqual(["list"]);
    expect(await axeViolations(container, ["list"])).toEqual([]);
  });
});
