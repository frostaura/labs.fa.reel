import type { FetchBaseQueryError } from "@reduxjs/toolkit/query";
import type { SerializedError } from "@reduxjs/toolkit";

type ReadError = FetchBaseQueryError | SerializedError | undefined;

/**
 * True only when the server said the thread is not there: a 404 (deleted from another device, past
 * retention, or an id that never parsed — the route is `{id:guid}`) or a 410. The same rule the
 * stream applies when it maps a refused turn to `conversationGone`.
 *
 * HREV8-PH-01. The resume read used to forget the stored id on ANY error, so one 503 on a flaky
 * phone connection dropped this tab's thread, and the next reload fell through to
 * `/conversations/latest` and opened another device's conversation — SB-02 brought back by a blip.
 * A 5xx, a timeout or a dropped connection says nothing about whether the thread exists.
 */
export function isConversationGone(error: ReadError): boolean {
  if (error == null || !("status" in error)) return false;
  return error.status === 404 || error.status === 410;
}

/**
 * Whether the page shows "Couldn't load your conversation" in place of the cold start.
 *
 * A failed read is a third state, not "nothing to resume": the starters on their own after a 503
 * told the reader they had no conversation. Shown only while the page is asking for a thread, the
 * server has not said it is gone, and nothing of this visit is on screen — once the reader has asked
 * something, or a turn is running, the page is about THAT, and an alert about a read it no longer
 * needs would be noise.
 */
export function resumeFailureShown(read: {
  error: ReadError;
  skipped: boolean;
  turnCount: number;
  streaming: boolean;
}): boolean {
  if (read.skipped || read.error == null || read.streaming || read.turnCount > 0) return false;
  return !isConversationGone(read.error);
}
