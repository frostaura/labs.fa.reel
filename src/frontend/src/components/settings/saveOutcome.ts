/**
 * What a failed settings save says, decided in one place.
 *
 * Every save on the Settings screen used to have one catch for every error. The conditions editor
 * answered a 500 and a 400 alike with "Locked — verify the settings PIN first." in green, on an
 * account with no PIN at all; region and email swallowed the error and flashed "Saved". The rule now
 * is that the status decides the copy, only success is green, and a failure stays on screen.
 */

/** The tone a save message renders in. Only a confirmed save is `success`. */
export type SaveTone = "success" | "danger";

export interface SaveMessage {
  tone: SaveTone;
  text: string;
  /** Failures stay until the user acts; only a success fades. */
  persistent: boolean;
  /** Whether a Retry beside the message makes sense — not for a lock or a rejected value. */
  retryable: boolean;
  /** The failure kind, for the caller that must react to it (a 423 drops the stale unlock token). */
  kind: "saved" | "locked" | "rejected" | "failed";
}

/** The copy for anything that is not a lock or a validation refusal. */
export const SAVE_FAILED_TEXT = "Couldn't save — nothing changed.";

/** The copy for a 423: the settings PIN is set and the unlock has lapsed (or was never made). */
export const SAVE_LOCKED_TEXT = "Locked — enter your settings PIN, then save again. Nothing changed.";

/** What a 400 says when the server sent no usable reason. */
export const SAVE_REJECTED_FALLBACK = "That wasn't accepted — nothing changed.";

/** A confirmed save. */
export function savedMessage(text: string): SaveMessage {
  return { tone: "success", text, persistent: false, retryable: false, kind: "saved" };
}

/**
 * Classifies an RTK Query error (or anything thrown by `.unwrap()`) into the message to show.
 * The shape is `FetchBaseQueryError`: `{ status: number | "FETCH_ERROR" | …, data?: unknown }`.
 */
export function saveFailureMessage(error: unknown): SaveMessage {
  const status = (error as { status?: unknown } | null)?.status;
  if (status === 423) {
    return { tone: "danger", text: SAVE_LOCKED_TEXT, persistent: true, retryable: false, kind: "locked" };
  }
  if (status === 400) {
    return { tone: "danger", text: serverReason(error) ?? SAVE_REJECTED_FALLBACK, persistent: true, retryable: false, kind: "rejected" };
  }
  return { tone: "danger", text: SAVE_FAILED_TEXT, persistent: true, retryable: true, kind: "failed" };
}

/** The server's own `{ error: "…" }` text, when it sent one worth showing. */
function serverReason(error: unknown): string | null {
  const data = (error as { data?: unknown } | null)?.data;
  const text = typeof data === "string" ? null : (data as { error?: unknown } | null)?.error;
  if (typeof text !== "string") return null;
  const trimmed = text.trim();
  if (trimmed.length === 0) return null;
  // Server reasons are lower-case fragments ("pin must be 4-8 digits"); a sentence reads better.
  return trimmed[0].toUpperCase() + trimmed.slice(1) + (/[.!?]$/.test(trimmed) ? "" : ".");
}

/**
 * The email save cannot be judged by its status alone: the endpoint answers 200 and quietly keeps
 * the OLD address when it does not like the new one. The response carries what is stored, so the
 * honest check is whether that is what was sent.
 */
export function emailSaveMessage(sent: string, stored: string | null | undefined): SaveMessage {
  const wanted = sent.trim();
  const kept = (stored ?? "").trim();
  if (wanted === kept) {
    return savedMessage(wanted.length === 0 ? "Removed." : "Saved.");
  }
  return {
    tone: "danger",
    text: "That address wasn't accepted — nothing changed.",
    persistent: true,
    retryable: false,
    kind: "rejected",
  };
}
