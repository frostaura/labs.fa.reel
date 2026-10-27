// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import BrowseFilters from "./BrowseFilters";
import { DEFAULT_BROWSE_FILTER } from "../../lib/browseFilter";

afterEach(cleanup);

// PH-10: a keyboard walk met "All, Movies, TV, All, Available, Coming soon" in two unnamed groups,
// and both "All" segments carried the test id `browse-all`.
describe("BrowseFilters", () => {
  it("is two named radio groups, so each 'All' says which choice it is", () => {
    render(<BrowseFilters value={DEFAULT_BROWSE_FILTER} onChange={() => {}} />);
    const media = screen.getByRole("radiogroup", { name: "Media type" });
    const availability = screen.getByRole("radiogroup", { name: "Availability" });
    expect(Array.from(media.querySelectorAll('[role="radio"]')).map((r) => r.textContent)).toEqual(["All", "Movies", "TV"]);
    expect(Array.from(availability.querySelectorAll('[role="radio"]')).map((r) => r.textContent)).toEqual(["All", "Available", "Coming soon"]);
  });

  it("gives every segment its own test id", () => {
    const { container } = render(<BrowseFilters value={DEFAULT_BROWSE_FILTER} onChange={() => {}} />);
    const ids = Array.from(container.querySelectorAll("[data-testid^='browse-']"))
      .map((el) => el.getAttribute("data-testid"))
      .filter((id) => id !== "browse-filters");
    expect(ids).toHaveLength(6);
    expect(new Set(ids).size).toBe(6);
  });

  it("is one Tab stop per group, on the chosen segment, and says which is chosen", () => {
    render(<BrowseFilters value={{ ...DEFAULT_BROWSE_FILTER, media: "show" }} onChange={() => {}} />);
    const tv = screen.getByRole("radio", { name: "TV" });
    expect(tv.getAttribute("aria-checked")).toBe("true");
    expect(tv.tabIndex).toBe(0);
    expect(screen.getByRole("radio", { name: "Movies" }).tabIndex).toBe(-1);
  });

  it("changes only its own half of the filter, by click and by arrow key", () => {
    const onChange = vi.fn();
    render(<BrowseFilters value={DEFAULT_BROWSE_FILTER} onChange={onChange} />);
    fireEvent.click(screen.getByTestId("browse-movie"));
    expect(onChange).toHaveBeenLastCalledWith({ ...DEFAULT_BROWSE_FILTER, media: "movie" });
    const availability = screen.getByRole("radiogroup", { name: "Availability" });
    fireEvent.keyDown(availability.querySelector('[role="radio"][aria-checked="true"]')!, { key: "ArrowRight" });
    expect(onChange).toHaveBeenLastCalledWith({ ...DEFAULT_BROWSE_FILTER, availability: "available" });
  });

  it("leaves the availability group out when asked", () => {
    render(<BrowseFilters value={DEFAULT_BROWSE_FILTER} onChange={() => {}} showAvailability={false} />);
    expect(screen.getAllByRole("radiogroup")).toHaveLength(1);
  });
});
