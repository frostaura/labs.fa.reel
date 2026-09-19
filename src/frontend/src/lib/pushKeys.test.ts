import { describe, expect, it } from "vitest";
import { base64UrlToUint8Array } from "./pushKeys";

describe("base64UrlToUint8Array", () => {
  it("decodes plain base64 (no url-specific chars, already padded length)", () => {
    // "Man" -> TWFu
    expect(Array.from(base64UrlToUint8Array("TWFu"))).toEqual([77, 97, 110]);
  });

  it("re-pads unpadded input", () => {
    // "Ma" -> TWE= in standard base64; base64url drops the padding.
    expect(Array.from(base64UrlToUint8Array("TWE"))).toEqual([77, 97]);
    // "M" -> TQ==
    expect(Array.from(base64UrlToUint8Array("TQ"))).toEqual([77]);
  });

  it("translates the url-safe alphabet (- and _)", () => {
    // 0xfb 0xff = "-_8" in base64url ("+/8" in standard base64).
    expect(Array.from(base64UrlToUint8Array("-_8"))).toEqual([251, 255]);
  });

  it("round-trips a realistic 65-byte uncompressed P-256 point", () => {
    const bytes = new Uint8Array(65).map((_, i) => (i * 7 + 4) % 256);
    let binary = "";
    bytes.forEach((b) => {
      binary += String.fromCharCode(b);
    });
    const base64Url = btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    expect(Array.from(base64UrlToUint8Array(base64Url))).toEqual(Array.from(bytes));
  });

  it("decodes the empty string to an empty array", () => {
    expect(base64UrlToUint8Array("").length).toBe(0);
  });
});
