import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type Options = {
  immediate?: boolean;
  onNeedRefresh?: () => void;
  onRegisteredSW?: (url: string, registration: { update(): Promise<void> } | undefined) => void;
};

const { registerSW } = vi.hoisted(() => ({
  registerSW: vi.fn<(options: Options) => (reloadPage?: boolean) => Promise<void>>(),
}));
vi.mock("virtual:pwa-register", () => ({ registerSW }));

async function load() {
  vi.resetModules();
  return import("./pwaUpdate");
}

let visibilityListeners: (() => void)[] = [];
let visibilityState = "visible";

beforeEach(() => {
  registerSW.mockReset();
  visibilityListeners = [];
  visibilityState = "visible";
  vi.stubEnv("DEV", false);
  vi.stubGlobal("navigator", { serviceWorker: {} });
  vi.stubGlobal("document", {
    get visibilityState() {
      return visibilityState;
    },
    addEventListener: (type: string, listener: () => void) => {
      if (type === "visibilitychange") visibilityListeners.push(listener);
    },
  });
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe("pwaUpdate", () => {
  it("registers in prompt mode and shows nothing until a new worker is waiting", async () => {
    const pwa = await load();
    pwa.startPwaUpdates();
    expect(registerSW).toHaveBeenCalledOnce();
    expect(registerSW.mock.calls[0][0]).toMatchObject({ immediate: true });
    expect(pwa.isPwaUpdateReady()).toBe(false);
  });

  it("a waiting worker raises the prompt; Reload activates it and reloads the page", async () => {
    const apply = vi.fn(async () => undefined);
    registerSW.mockReturnValue(apply);
    const pwa = await load();
    const listener = vi.fn();
    pwa.subscribePwaUpdate(listener);
    pwa.startPwaUpdates();

    registerSW.mock.calls[0][0].onNeedRefresh?.();
    expect(pwa.isPwaUpdateReady()).toBe(true);
    expect(listener).toHaveBeenCalledOnce();

    pwa.acceptPwaUpdate();
    expect(apply).toHaveBeenCalledExactlyOnceWith(true);
  });

  it("Later hides the prompt without activating the worker", async () => {
    const apply = vi.fn(async () => undefined);
    registerSW.mockReturnValue(apply);
    const pwa = await load();
    pwa.startPwaUpdates();
    registerSW.mock.calls[0][0].onNeedRefresh?.();

    pwa.dismissPwaUpdate();
    expect(pwa.isPwaUpdateReady()).toBe(false);
    expect(apply).not.toHaveBeenCalled();
  });

  it("looks for a new build each time the app comes back into view", async () => {
    const update = vi.fn(async () => undefined);
    const pwa = await load();
    pwa.startPwaUpdates();
    registerSW.mock.calls[0][0].onRegisteredSW?.("/sw.js", { update });

    visibilityState = "hidden";
    visibilityListeners.forEach((l) => l());
    expect(update).not.toHaveBeenCalled();
    visibilityState = "visible";
    visibilityListeners.forEach((l) => l());
    expect(update).toHaveBeenCalledOnce();
  });

  it("does nothing in dev or without service-worker support", async () => {
    vi.stubEnv("DEV", true);
    (await load()).startPwaUpdates();
    vi.stubEnv("DEV", false);
    vi.stubGlobal("navigator", {});
    (await load()).startPwaUpdates();
    expect(registerSW).not.toHaveBeenCalled();
  });
});
