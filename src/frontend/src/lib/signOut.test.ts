import { describe, expect, it } from "vitest";
import { clearClientState, clearReelKeys, consumeSignOutPending, markSignOutPending, type SignOutDeps } from "./signOut";
import { API_CACHE, SIGNED_OUT } from "./swContract";
import type { AppDispatch } from "../store";
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

describe("sign-out hand-off", () => {
  it("is consumed exactly once per sign-out", () => {
    expect(consumeSignOutPending()).toBe(false);
    markSignOutPending();
    expect(consumeSignOutPending()).toBe(true);
    expect(consumeSignOutPending()).toBe(false);
  });
});

describe("clearClientState", () => {
  function harness() {
    const log: string[] = [];
    let settle: () => void = () => undefined;
    const late = new Promise<void>((resolve) => (settle = resolve));
    const request = {
      abort: () => log.push("abort"),
      then: late.then.bind(late) as PromiseLike<unknown>["then"],
    };
    const keys = storage({ "reel.ask.conversationId": "conv-of-A", "other.app": "keep" });
    const deps: SignOutDeps = {
      dispatch: ((action: { type: string }) => {
        log.push(action.type);
        return action;
      }) as unknown as AppDispatch,
      inFlight: () => [request],
      worker: () => ({ postMessage: (message: unknown) => log.push(`worker:${(message as { type: string }).type}`) }),
      cacheStorage: () => ({
        delete: async (name: string) => {
          log.push(`cache-delete:${name}`);
          return true;
        },
      }),
      storageAreas: () => [
        {
          get length() {
            return keys.length;
          },
          key: keys.key,
          removeItem: (k: string) => {
            log.push(`remove:${k}`);
            keys.removeItem(k);
          },
        },
      ],
    };
    return { log, deps, settle, keys };
  }

  it("aborts and waits out the old session's requests before it clears anything (I-11)", async () => {
    const { log, deps, settle, keys } = harness();
    const done = clearClientState(deps.dispatch, deps);
    await Promise.resolve();
    // Still waiting on the in-flight request: nothing has been reset yet.
    expect(log).toEqual(["abort"]);

    settle();
    await done;
    expect(log).toEqual([
      "abort",
      "reelApi/resetApiState",
      "session/reset",
      "remove:reel.ask.conversationId",
      `worker:${SIGNED_OUT}`,
      `cache-delete:${API_CACHE}`,
    ]);
    expect(keys.keys()).toEqual(["other.app"]);
  });

  it("keeps going when there is no worker, no Cache API and storage throws", async () => {
    const { log, deps, settle } = harness();
    settle();
    await clearClientState(deps.dispatch, {
      ...deps,
      worker: () => null,
      cacheStorage: () => null,
      storageAreas: () => [
        {
          length: 1,
          key: () => {
            throw new Error("SecurityError");
          },
          removeItem: () => undefined,
        },
      ],
    });
    expect(log).toEqual(["abort", "reelApi/resetApiState", "session/reset"]);
  });
});
