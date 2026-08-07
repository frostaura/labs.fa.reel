import { describe, expect, it } from "vitest";
import { youTubeVideoId } from "./youtube";

/**
 * Trailer URLs arrive from TMDB in three shapes; the old inline regex understood only
 * `watch?v=`, so short-link and embed trailers silently lost their play button.
 */
describe("youTubeVideoId", () => {
  it("parses the watch?v= form", () => {
    expect(youTubeVideoId("https://www.youtube.com/watch?v=dQw4w9WgXcQ")).toBe("dQw4w9WgXcQ");
    // …also when v= is not the first query parameter.
    expect(youTubeVideoId("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")).toBe("dQw4w9WgXcQ");
  });

  it("parses the youtu.be short form", () => {
    expect(youTubeVideoId("https://youtu.be/dQw4w9WgXcQ")).toBe("dQw4w9WgXcQ");
  });

  it("parses the /embed/ form", () => {
    expect(youTubeVideoId("https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1")).toBe("dQw4w9WgXcQ");
  });

  it("yields undefined for garbage, youtube-less URLs, and missing input", () => {
    expect(youTubeVideoId("not a url")).toBeUndefined();
    expect(youTubeVideoId("https://vimeo.com/123456")).toBeUndefined();
    expect(youTubeVideoId("https://www.youtube.com/")).toBeUndefined();
    expect(youTubeVideoId("")).toBeUndefined();
    expect(youTubeVideoId(null)).toBeUndefined();
    expect(youTubeVideoId(undefined)).toBeUndefined();
  });
});
