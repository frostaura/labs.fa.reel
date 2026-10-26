import { describe, expect, it } from "vitest";
import { filmographyRoleLine } from "./personRoles";

describe("filmographyRoleLine", () => {
  it("names what the person did, in the API's order", () => {
    expect(filmographyRoleLine({ roles: ["Director", "Writer"] })).toBe("Director · Writer");
  });

  it("reads an acting credit as its character", () => {
    expect(filmographyRoleLine({ roles: ["Actor"], character: "Neil McCauley" })).toBe("as Neil McCauley");
    expect(filmographyRoleLine({ roles: ["Director", "Actor"], character: "Himself" })).toBe("Director · as Himself");
    expect(filmographyRoleLine({ roles: ["Actor"] })).toBe("Cast");
  });

  it("says nothing it was not told — no roles, no line", () => {
    expect(filmographyRoleLine({})).toBeNull();
    expect(filmographyRoleLine({ roles: null, character: "  " })).toBeNull();
  });

  it("passes an unknown role through verbatim rather than dropping it", () => {
    expect(filmographyRoleLine({ roles: ["Producer"] })).toBe("Producer");
  });
});
