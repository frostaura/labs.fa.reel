import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type Options = {
  immediate?: boolean;
  onNeedRefresh?: () => void;
  onNeedReload?: () => void;
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
let controllerChange: (() => void)[] = [];
const reload = vi.fn();

beforeEach(() => {
  registerSW.mockReset();
  reload.mockReset();
  visibilityListeners = [];
  visibilityState = "visible";
  controllerChange = [];
  vi.stubEnv("DEV", false);
  vi.stubGlobal("navigator", {
    serviceWorker: {
      addEventListener: (type: string, listener: () => void) => {
        if (type === "controllerchange") controllerChange.push(listener);
      },
    },
  });
  vi.stubGlobal("window", { location: { reload } });
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

  // Which signal says the new worker took over depends on the tab: workbox-window's (onNeedReload)
  // when a worker controlled the page at registration, only the browser's controllerchange when
  // this visit installed the first worker. Every mix reloads exactly once.
  for (const [name, signals] of [
    ["workbox-window's signal only", ["onNeedReload"]],
    ["controllerchange only (the visit that installed the worker)", ["controllerchange"]],
    ["both, workbox first", ["onNeedReload", "controllerchange"]],
    ["both, controllerchange first", ["controllerchange", "onNeedReload"]],
  ] as const) {
    it(`Reload reloads the page exactly once on ${name}`, async () => {
      registerSW.mockReturnValue(vi.fn(async () => undefined));
      const pwa = await load();
      pwa.startPwaUpdates();
      const options = registerSW.mock.calls[0][0];
      options.onNeedRefresh?.();
      pwa.acceptPwaUpdate();
      expect(reload).not.toHaveBeenCalled();

      for (const signal of signals) {
        if (signal === "onNeedReload") options.onNeedReload?.();
        else controllerChange.forEach((listener) => listener());
      }
      expect(reload).toHaveBeenCalledOnce();
    });
  }

  it("before Reload is pressed, a bare controllerchange is not watched", async () => {
    registerSW.mockReturnValue(vi.fn(async () => undefined));
    const pwa = await load();
    pwa.startPwaUpdates();
    registerSW.mock.calls[0][0].onNeedRefresh?.();
    controllerChange.forEach((listener) => listener());
    expect(controllerChange).toHaveLength(0);
    expect(reload).not.toHaveBeenCalled();
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
