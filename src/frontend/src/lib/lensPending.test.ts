import { describe, expect, it } from "vitest";
import { LENS_POLL_LIMIT, LENS_POLL_MS, lensPendingNote, lensPollInterval } from "./lensPending";

const none = { source: "none" as const, pending: true };
const stale = { source: "stale" as const, pending: true };
const fresh = { source: "fresh" as const, pending: true };

describe("lensPollInterval (WS-1 CR-2: the lens is off the read path)", () => {
  it("re-asks while a pass is queued and the note is not current", () => {
    expect(lensPollInterval(none, 0)).toBe(LENS_POLL_MS);
    expect(lensPollInterval(stale, LENS_POLL_LIMIT - 1)).toBe(LENS_POLL_MS);
  });

  it("stops after a bounded number of re-asks, so a pass that never lands costs nothing more", () => {
    expect(lensPollInterval(none, LENS_POLL_LIMIT)).toBe(0);
    expect(LENS_POLL_LIMIT * LENS_POLL_MS).toBeLessThanOrEqual(60_000);
  });

  it("never polls a fresh note, a note with nothing queued, an older server without the field, or no answer", () => {
    expect(lensPollInterval(fresh, 0)).toBe(0);
    expect(lensPollInterval({ source: "none", pending: false }, 0)).toBe(0);
    expect(lensPollInterval({ source: "stale" }, 0)).toBe(0);
    expect(lensPollInterval(undefined, 0)).toBe(0);
  });
});

describe("lensPendingNote", () => {
  it("says a note is being written only while it is still being waited for", () => {
    expect(lensPendingNote(none, 0)).toMatch(/being written/i);
    expect(lensPendingNote(none, LENS_POLL_LIMIT)).toBeNull();
  });

  it("says a stale note is being rewritten, without claiming when", () => {
    const note = lensPendingNote(stale, 1);
    expect(note).toMatch(/rewrite/i);
    expect(note).not.toMatch(/\d+\s*(s|sec|second|minute)/i);
  });

  it("says nothing when nothing is queued", () => {
    expect(lensPendingNote({ source: "stale", pending: false }, 0)).toBeNull();
    expect(lensPendingNote(fresh, 0)).toBeNull();
  });
});
