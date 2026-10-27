// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import TimeZoneCard from "./TimeZoneCard";
import { api } from "../../store/api";
import { makeStore, renderWithStore } from "../../test/renderWithStore";
import { axeViolations } from "../../test/axe";

/**
 * The Settings time-zone card (R1-17): the server could store a zone, but nothing on screen could
 * set one, so every account was counted in UTC. Fixtures vary one field at a time: the stored zone
 * (none, chosen, the device's), the device's zone, and the PUT's answer (200, 400, 500).
 */
const ORIGIN = "http://localhost";
const NativeRequest = globalThis.Request;
class RelativeUrlRequest extends NativeRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    super(typeof input === "string" && input.startsWith("/") ? ORIGIN + input : input, init);
  }
}

const SETTINGS = { region: "ZA", onboarded: true, email: null, eveningBudgetMinutes: 150 };
const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

let puts: unknown[];
let answer: (body: { timeZone?: string }) => Response;

function stubDevice(zone: string | undefined) {
  const real = Intl.DateTimeFormat.prototype.resolvedOptions;
  vi.spyOn(Intl.DateTimeFormat.prototype, "resolvedOptions").mockImplementation(function (this: Intl.DateTimeFormat) {
    return { ...real.call(this), timeZone: zone as string };
  });
}

beforeEach(() => {
  puts = [];
  answer = (body) => json(200, { ...SETTINGS, timeZone: body.timeZone, effectiveTimeZone: body.timeZone });
  vi.stubGlobal("Request", RelativeUrlRequest);
  vi.stubGlobal(
    "fetch",
    vi.fn(async (request: Request) => {
      if (request.method === "PUT" && request.url.endsWith("/api/settings")) {
        const body = (await request.json()) as { timeZone?: string };
        puts.push(body);
        return answer(body);
      }
      // Nothing else may leave the test: the read is seeded below.
      return new Promise<Response>(() => {});
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

async function renderCard(stored: { timeZone: string | null; effectiveTimeZone: string }) {
  const store = makeStore();
  await store.dispatch(api.util.upsertQueryData("getSettings", undefined, { ...SETTINGS, ...stored }));
  return renderWithStore(<TimeZoneCard />, store);
}

describe("TimeZoneCard", () => {
  it("an account that never chose says so, and offers this device's zone in one tap", async () => {
    stubDevice("Africa/Johannesburg");
    const { container } = await renderCard({ timeZone: null, effectiveTimeZone: "UTC" });

    expect(screen.getByTestId("time-zone-current").textContent).toBe("Not set — Reel is counting your day in UTC.");
    const use = screen.getByTestId("time-zone-use-device");
    expect(use.textContent).toBe("Use this device's zone — Africa / Johannesburg");

    fireEvent.click(use);
    await waitFor(() => expect(screen.getByTestId("time-zone-save-status").textContent).toBe("Saved."));
    expect(puts).toEqual([{ timeZone: "Africa/Johannesburg" }]);
    // The stored settings come back into the read, so the card now reports the choice.
    await waitFor(() =>
      expect(screen.getByTestId("time-zone-current").textContent).toMatch(/^Counting your day in Africa \/ Johannesburg \(GMT\+2\)\.$/),
    );
    expect(screen.queryByTestId("time-zone-use-device")).toBeNull();
    expect(await axeViolations(container)).toEqual([]);
  });

  it("a chosen zone is shown as chosen, and a device elsewhere is offered quietly", async () => {
    stubDevice("Europe/London");
    await renderCard({ timeZone: "Africa/Johannesburg", effectiveTimeZone: "Africa/Johannesburg" });

    expect(screen.getByTestId("time-zone-current").textContent).toMatch(/^Counting your day in Africa \/ Johannesburg/);
    expect((screen.getByTestId("time-zone-select") as HTMLInputElement).value).toBe("Africa / Johannesburg");
    expect(screen.getByTestId("time-zone-use-device").textContent).toBe("Use this device's zone — Europe / London");
  });

  it("offers nothing extra when the device already matches, or reports no zone", async () => {
    stubDevice("Africa/Johannesburg");
    await renderCard({ timeZone: "Africa/Johannesburg", effectiveTimeZone: "Africa/Johannesburg" });
    expect(screen.queryByTestId("time-zone-use-device")).toBeNull();
    cleanup();

    stubDevice(undefined);
    await renderCard({ timeZone: null, effectiveTimeZone: "UTC" });
    expect(screen.queryByTestId("time-zone-use-device")).toBeNull();
    expect(screen.getByTestId("time-zone-select")).toBeTruthy();
  });

  it("picks any zone from the searchable list", async () => {
    stubDevice("Africa/Johannesburg");
    await renderCard({ timeZone: null, effectiveTimeZone: "UTC" });

    const box = screen.getByTestId("time-zone-select");
    fireEvent.focus(box);
    fireEvent.change(box, { target: { value: "buenos" } });
    const option = screen.getAllByTestId("time-zone-select-option")[0];
    // The runtime's own spelling (CLDR lists America/Buenos_Aires; tzdata also has the Argentina form).
    const zone = option.getAttribute("data-code");
    expect(zone).toMatch(/Buenos_Aires$/);
    fireEvent.mouseDown(option);

    await waitFor(() => expect(puts).toEqual([{ timeZone: zone }]));
    await waitFor(() => expect(screen.getByTestId("time-zone-save-status").textContent).toBe("Saved."));
  });

  it("a refused zone (400 time_zone_invalid) shows the server's reason, in danger tone, and changes nothing", async () => {
    stubDevice("Africa/Johannesburg");
    answer = () =>
      json(400, { error: "timeZone must be an IANA time zone such as Africa/Johannesburg", code: "time_zone_invalid" });
    const { container } = await renderCard({ timeZone: null, effectiveTimeZone: "UTC" });

    fireEvent.click(screen.getByTestId("time-zone-use-device"));
    const status = await screen.findByTestId("time-zone-save-status");
    await waitFor(() =>
      expect(status.textContent).toBe("Time zone must be an IANA time zone such as Africa/Johannesburg."),
    );
    expect(status.getAttribute("data-tone")).toBe("danger");
    expect(status.getAttribute("role")).toBe("alert");
    // A rejected value is not retried as-is.
    expect(screen.queryByTestId("time-zone-save-status-retry")).toBeNull();
    expect(screen.getByTestId("time-zone-current").textContent).toMatch(/^Not set — /);
    expect(await axeViolations(container)).toEqual([]);
  });

  it("a failed save (500) says nothing changed and offers a Retry that sends the same zone", async () => {
    stubDevice("Africa/Johannesburg");
    answer = () => json(500, {});
    await renderCard({ timeZone: null, effectiveTimeZone: "UTC" });

    fireEvent.click(screen.getByTestId("time-zone-use-device"));
    await waitFor(() => expect(screen.getByTestId("time-zone-save-status").textContent).toMatch(/^Couldn't save — nothing changed\./));

    answer = (body) => json(200, { ...SETTINGS, timeZone: body.timeZone, effectiveTimeZone: body.timeZone });
    fireEvent.click(screen.getByTestId("time-zone-save-status-retry"));
    await waitFor(() => expect(screen.getByTestId("time-zone-save-status").textContent).toBe("Saved."));
    expect(puts).toEqual([{ timeZone: "Africa/Johannesburg" }, { timeZone: "Africa/Johannesburg" }]);
  });

  it("a 200 that did not store the zone (an older server) is not reported as saved", async () => {
    stubDevice("Africa/Johannesburg");
    answer = () => json(200, SETTINGS);
    await renderCard({ timeZone: null, effectiveTimeZone: "UTC" });

    fireEvent.click(screen.getByTestId("time-zone-use-device"));
    await waitFor(() =>
      expect(screen.getByTestId("time-zone-save-status").textContent).toBe("Africa/Johannesburg wasn't stored — nothing changed."),
    );
    expect(screen.getByTestId("time-zone-save-status").getAttribute("data-tone")).toBe("danger");
  });
});
