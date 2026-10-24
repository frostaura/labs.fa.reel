import { describe, expect, it } from "vitest";
import { clearReelKeys } from "./signOut";
import { rootReducer } from "../store";
import { sessionReset } from "../store/sessionReset";
import { servedFromCache } from "../store/connectivitySlice";
import { activeJobObserved, syncCompleted } from "../store/syncSlice";
import { api } from "../store/api";

function storage(entries: Record<string, string>) {
  const map = new Map(Object.entries(entries));
  return {
    get length() {
      return map.size;
    },
    key: (i: number) => [...map.keys()][i] ?? null,
    removeItem: (k: string) => void map.delete(k),
    keys: () => [...map.keys()],
  };
}

describe("clearReelKeys", () => {
  it("removes every reel.* key and nothing else", () => {
    const s = storage({
      "reel.hasSession": "1",
      "reel.ask.conversationId": "c-1",
      "reel.pin.unlock": "x",
      "other.app": "keep",
      reelish: "keep",
    });
    expect(clearReelKeys(s).sort()).toEqual(["reel.ask.conversationId", "reel.hasSession", "reel.pin.unlock"]);
    expect(s.keys().sort()).toEqual(["other.app", "reelish"]);
  });
});

describe("sessionReset", () => {
  it("returns every client slice to its initial state and leaves the API cache to resetApiState", () => {
    let state = rootReducer(undefined, { type: "@@init" });
    const initial = state;
    state = rootReducer(state, servedFromCache({ savedAt: "2026-10-24T10:00:00.000Z" }));
    state = rootReducer(state, activeJobObserved({ userVisible: true, jobChanged: true }));
    state = rootReducer(
      state,
      syncCompleted({ ratingsAdded: 1, ratingsChanged: 0, watchedAdded: 0, retrainQueued: false, retrainAt: null, picksChanged: null })
    );
    const apiSlice = state[api.reducerPath];

    const reset = rootReducer(state, sessionReset());
    expect(reset.connectivity).toEqual(initial.connectivity);
    expect(reset.syncUi).toEqual(initial.syncUi);
    expect(reset.verdict).toEqual(initial.verdict);
    expect(reset.removedTitles).toEqual(initial.removedTitles);
    expect(reset.scoreOverrides).toEqual(initial.scoreOverrides);
    expect(reset.onboarding).toEqual(initial.onboarding);
    expect(reset[api.reducerPath]).toBe(apiSlice);
  });
});
