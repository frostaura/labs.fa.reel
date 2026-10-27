// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it } from "vitest";
import CastMemberCard from "./CastMemberCard";

afterEach(cleanup);

const renderChip = (member: Parameters<typeof CastMemberCard>[0]["member"]) =>
  render(
    <MemoryRouter>
      <CastMemberCard member={member} />
    </MemoryRouter>
  );

// PH-08: the photo's alt repeated the visible name, so a screen reader heard every name twice
// (axe image-redundant-alt ×5 on a title page).
describe("CastMemberCard — the name is said once", () => {
  it("gives the photo an empty alt: the chip's text already names the person", () => {
    const { container } = renderChip({ personId: "p1", name: "Fede Álvarez", profilePath: "/abc.jpg", userRating: null });
    const img = container.querySelector("img")!;
    expect(img.getAttribute("alt")).toBe("");
    expect(screen.getByRole("link").textContent).toBe("Fede Álvarez");
  });

  it("hides the initials fallback from the name too, and keeps the character", () => {
    renderChip({ personId: "p2", name: "Claire Foy", profilePath: null, userRating: null, character: "Lisbeth Salander" });
    const link = screen.getByRole("link");
    // Visible "CF" initials are decoration beside the full name.
    expect(link.textContent).toContain("CF");
    expect(link.querySelector('[aria-hidden="true"]')?.textContent).toBe("CF");
    expect(screen.getByText("Lisbeth Salander")).toBeTruthy();
  });
});
