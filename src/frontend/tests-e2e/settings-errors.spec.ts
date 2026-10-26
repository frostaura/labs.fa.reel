import { test, expect, type Page, type Route } from "@playwright/test";
import { SESSION_FOUNDER, mockJson, stubEventSource } from "./helpers";
import { mockFilterChips } from "./filterChips.fixture";

/**
 * Settings saves report what actually happened (H-3, H-4, H+2, H-10, H-12).
 *
 * Before: every failed conditions save said "Locked — verify the settings PIN first." in green, on
 * an account with no PIN; region and email flashed "Saved" over a 500; the stored email was never
 * shown; an unsaved draft was dropped silently on navigation; and the plan card offered an Upgrade
 * that could only answer "coming soon".
 *
 * What these pin: the status decides the copy (423 → PIN, 400 → the server's reason, anything else
 * → "nothing changed" with a Retry), only success is green, a failure and the unsaved marker stay
 * up, the leave guard asks, the email box shows what is stored, and billing is invisible while it
 * is switched off.
 */

const FILTERS = {
  excludeGenres: ["horror"],
  includeGenres: [],
  excludeKeywords: [],
  maturityCeiling: null,
  minPredictedRating: null,
  languages: ["en"],
  minCrowdRating: null,
  minYear: null,
  maxYear: null,
};

const SYNC = {
  pipelineStage: "FeedReady", connectionStatus: "Active", lastDeltaSyncAt: null,
  lastFullReconcileAt: null, activeJob: null, recentJobs: [], outboxPending: 0, outboxDeadLetters: 0,
};

interface Boot {
  billing?: { tier: string; configured: boolean };
  settings?: Record<string, unknown>;
}

async function boot(page: Page, { billing = { tier: "Founder", configured: false }, settings = {} }: Boot = {}) {
  await stubEventSource(page);
  await mockJson(page, "**/api/auth/me", SESSION_FOUNDER);
  await mockJson(page, "**/api/sync/status", SYNC);
  await mockJson(page, "**/api/billing/status", billing);
  await mockJson(page, "**/api/preferences/tags", []);
  await mockJson(page, "**/api/settings/filters/cost", { total: 5000, eligible: 400, byRule: [], suggestions: [] });
  await mockFilterChips(page);
  await page.route(
    (u) => u.pathname === "/api/settings",
    (route) =>
      route.request().method() === "GET"
        ? route.fulfill({
            status: 200,
            contentType: "application/json",
            body: JSON.stringify({ region: "US", onboarded: true, email: null, eveningBudgetMinutes: 150, ...settings }),
          })
        : route.fallback()
  );
}

/** Routes PUT /api/settings/filters through `handler`; GET answers with the saved FILTERS. */
async function routeFilters(page: Page, handler: (route: Route) => Promise<void> | void) {
  await page.route(
    (u) => u.pathname === "/api/settings/filters",
    (route) =>
      route.request().method() === "PUT"
        ? handler(route)
        : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(FILTERS) })
  );
}

/** Opens the region combobox, searches, and picks the option with that code. */
async function pickRegion(page: Page, query: string, code: string) {
  const box = page.getByTestId("region-select");
  await box.click();
  await box.fill(query);
  await page.locator(`[data-testid="region-option"][data-code="${code}"]`).click();
}

test.describe("conditions save outcomes", () => {
  const cases: { label: string; status: number; body: unknown; text: RegExp; retry: boolean }[] = [
    { label: "500", status: 500, body: {}, text: /^Couldn't save — nothing changed\./, retry: true },
    { label: "400", status: 400, body: { error: "minYear must be between 1900 and 2027" }, text: /^The earliest year must be between 1900 and 2027\./, retry: false },
    { label: "423", status: 423, body: {}, text: /^Locked — enter your settings PIN, then save again\./, retry: false },
  ];

  for (const c of cases) {
    test(`a ${c.label} shows its own copy in danger tone, and the draft stays unsaved`, async ({ page }) => {
      await page.clock.install();
      await boot(page);
      await routeFilters(page, (route) =>
        route.fulfill({ status: c.status, contentType: "application/json", body: JSON.stringify(c.body) })
      );
      await page.goto("/settings");

      await page.getByTestId("genre-war-include").click();
      await expect(page.getByTestId("conditions-unsaved")).toBeVisible();
      await page.getByTestId("save-prefs").click();

      const status = page.getByTestId("conditions-save-status");
      await expect(status).toHaveText(c.text);
      await expect(status).toHaveAttribute("data-tone", "danger");
      await expect(status).toHaveAttribute("role", "alert");
      await expect(page.getByTestId("conditions-save-status-retry")).toHaveCount(c.retry ? 1 : 0);
      // The old copy is gone for good, whatever the status.
      await expect(page.getByText("Locked — verify the settings PIN first.")).toHaveCount(0);

      // A failure is not a toast: 10 s later the message and the unsaved marker are both still up.
      await page.clock.fastForward(10_000);
      await expect(status).toBeVisible();
      await expect(page.getByTestId("conditions-unsaved")).toBeVisible();
      await expect(page.getByTestId("genre-war")).toHaveAttribute("data-state", "include");
    });
  }

  test("only success is green; it fades, and the draft is clean", async ({ page }) => {
    await boot(page);
    let body: unknown = null;
    await routeFilters(page, async (route) => {
      body = route.request().postDataJSON();
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    });
    await page.goto("/settings");

    await page.getByTestId("genre-war-include").click();
    await page.getByTestId("save-prefs").click();

    const status = page.getByTestId("conditions-save-status");
    await expect(status).toHaveText("Saved — your picks are being re-cut.");
    await expect(status).toHaveAttribute("data-tone", "success");
    await expect(page.getByTestId("conditions-unsaved")).toHaveCount(0);
    expect((body as { includeGenres: string[] } | null)?.includeGenres).toContain("war");
  });

  test("Retry after a 500 re-sends the same draft and can succeed", async ({ page }) => {
    await boot(page);
    let calls = 0;
    await routeFilters(page, (route) => {
      calls += 1;
      return calls === 1
        ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
        : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(route.request().postDataJSON()) });
    });
    await page.goto("/settings");

    await page.getByTestId("genre-war-include").click();
    await page.getByTestId("save-prefs").click();
    await expect(page.getByTestId("conditions-save-status")).toHaveAttribute("data-tone", "danger");

    await page.getByTestId("conditions-save-status-retry").click();
    await expect(page.getByTestId("conditions-save-status")).toHaveAttribute("data-tone", "success");
    expect(calls).toBe(2);
  });

  test("a genre toggled all the way round is not an unsaved change", async ({ page }) => {
    await boot(page);
    await routeFilters(page, (route) => route.fulfill({ status: 500, body: "{}" }));
    await page.goto("/settings");

    const war = page.getByTestId("genre-war");
    await page.getByTestId("genre-war-include").click();
    await expect(page.getByTestId("conditions-unsaved")).toBeVisible();
    await page.getByTestId("genre-war-exclude").click();
    await page.getByTestId("genre-war-exclude").click();
    await expect(war).toHaveAttribute("data-state", "neutral");
    await expect(page.getByTestId("conditions-unsaved")).toHaveCount(0);
  });
});

test.describe("conditions load failure", () => {
  test("a failed read of the saved conditions is an error with Retry, not a skeleton for ever", async ({ page }) => {
    await boot(page);
    let fail = true;
    await page.route(
      (u) => u.pathname === "/api/settings/filters",
      (route) =>
        fail
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(FILTERS) })
    );
    await page.goto("/settings");

    await expect(page.getByTestId("conditions-load-error")).toBeVisible();
    await expect(page.getByTestId("prefs-loading")).toHaveCount(0);

    fail = false;
    await page.getByTestId("conditions-load-error-retry").click();
    await expect(page.getByTestId("prefs-section")).toBeVisible();
    await expect(page.getByTestId("genre-horror")).toHaveAttribute("data-state", "exclude");
  });
});

test.describe("leave guard", () => {
  test("navigating away with an unsaved draft asks; cancelling keeps the page and the draft", async ({ page }) => {
    await boot(page);
    await routeFilters(page, (route) => route.fulfill({ status: 500, body: "{}" }));
    await mockJson(page, "**/api/titles/rate-suggestions", []);
    await page.goto("/settings");

    await page.getByTestId("genre-war-include").click();

    const prompts: string[] = [];
    page.once("dialog", (dialog) => {
      prompts.push(dialog.message());
      void dialog.dismiss();
    });
    await page.getByTestId("sharpen-link").click();
    await expect.poll(() => prompts.length).toBe(1);
    expect(prompts[0]).toMatch(/unsaved changes/i);
    await expect(page).toHaveURL(/\/settings$/);
    await expect(page.getByTestId("genre-war")).toHaveAttribute("data-state", "include");

    page.once("dialog", (dialog) => void dialog.accept());
    await page.getByTestId("sharpen-link").click();
    await expect(page).toHaveURL(/\/rate$/);
  });

  test("two dirty drafts ask once, naming both, and OK leaves", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: true } });
    await routeFilters(page, (route) => route.fulfill({ status: 500, body: "{}" }));
    await mockJson(page, "**/api/titles/rate-suggestions", []);
    await page.goto("/settings");

    await page.getByTestId("genre-war-include").click();
    await page.getByTestId("email-input").fill("me@example.com");

    const prompts: string[] = [];
    page.on("dialog", (dialog) => {
      prompts.push(dialog.message());
      void dialog.accept();
    });
    await page.getByTestId("sharpen-link").click();
    await expect(page).toHaveURL(/\/rate$/);
    // One click, one question — the old per-card listeners asked twice.
    expect(prompts).toEqual(["You have unsaved changes to your conditions and your email address. Leave without saving them?"]);
  });

  test("a clean page navigates without asking", async ({ page }) => {
    await boot(page);
    await routeFilters(page, (route) => route.fulfill({ status: 500, body: "{}" }));
    await mockJson(page, "**/api/titles/rate-suggestions", []);
    let asked = false;
    page.on("dialog", (dialog) => {
      asked = true;
      void dialog.dismiss();
    });
    await page.goto("/settings");
    await expect(page.getByTestId("prefs-section")).toBeVisible();
    await page.getByTestId("sharpen-link").click();
    await expect(page).toHaveURL(/\/rate$/);
    expect(asked).toBe(false);
  });
});

test.describe("region and email", () => {
  test("a failed region save says so and offers Retry — never 'Saved'", async ({ page }) => {
    await boot(page);
    await page.route(
      (u) => u.pathname === "/api/settings",
      (route) =>
        route.request().method() === "PUT"
          ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
          : route.fallback()
    );
    await page.goto("/settings");

    await pickRegion(page, "south africa", "ZA");
    const status = page.getByTestId("region-save-status");
    await expect(status).toHaveText(/^Couldn't save — nothing changed\./);
    await expect(status).toHaveAttribute("data-tone", "danger");
    await expect(page.getByTestId("region-save-status-retry")).toBeVisible();
    // The control snaps back to what is stored, which is what "nothing changed" claims.
    await expect(page.getByTestId("region-select")).toHaveValue("United States (US)");
    await expect(status).not.toContainText("Saved");
  });

  test("a region save that lands is green", async ({ page }) => {
    await boot(page);
    await page.route(
      (u) => u.pathname === "/api/settings",
      (route) =>
        route.request().method() === "PUT"
          ? route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ region: "ZA", onboarded: true, email: null }) })
          : route.fallback()
    );
    await page.goto("/settings");
    await pickRegion(page, "south africa", "ZA");
    await expect(page.getByTestId("region-save-status")).toHaveAttribute("data-tone", "success");
  });

  test("the region picker is labelled and searchable by name or code, by mouse or keyboard (H-10)", async ({ page }) => {
    await boot(page);
    const puts: unknown[] = [];
    await page.route(
      (u) => u.pathname === "/api/settings",
      (route) => {
        if (route.request().method() !== "PUT") return route.fallback();
        puts.push(route.request().postDataJSON());
        return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ region: "NZ", onboarded: true, email: null }) });
      }
    );
    await routeFilters(page, (route) => route.fulfill({ status: 500, body: "{}" }));
    await page.goto("/settings");

    const box = page.getByRole("combobox", { name: "Your region" });
    await expect(box).toHaveValue("United States (US)");
    await box.click();
    // Far beyond the 13 hand-picked codes the old select offered.
    await box.fill("nz");
    await expect(page.getByTestId("region-option").first()).toHaveAttribute("data-code", "NZ");
    await box.press("Enter");
    await expect.poll(() => puts).toEqual([{ region: "NZ" }]);

    await box.click();
    await box.fill("qqqq");
    await expect(page.getByTestId("region-options")).toContainText("No region matches");
    await box.press("Escape");
    await expect(page.getByTestId("region-options")).toHaveCount(0);
  });

  test("the stored email is in the box on load, and an address the server keeps the old value for is refused", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: true }, settings: { email: "me@example.com" } });
    await page.route(
      (u) => u.pathname === "/api/settings",
      (route) =>
        route.request().method() === "PUT"
          ? // The endpoint answers 200 and keeps the OLD address for one it does not accept.
            route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ region: "US", onboarded: true, email: "me@example.com" }) })
          : route.fallback()
    );
    await page.goto("/settings");

    await expect(page.getByTestId("email-input")).toHaveValue("me@example.com");
    await page.getByTestId("email-input").fill("me@nowhere");
    await page.getByTestId("email-save").click();

    const status = page.getByTestId("email-save-status");
    await expect(status).toHaveText("That address wasn't accepted — nothing changed.");
    await expect(status).toHaveAttribute("data-tone", "danger");
    await expect(page.getByText("Saved ✓")).toHaveCount(0);
  });

  test("a saved email stays in the box even when the follow-up read fails — never the old address", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: true }, settings: { email: "old@example.com" } });
    let saved = false;
    await page.route(
      (u) => u.pathname === "/api/settings",
      async (route) => {
        if (route.request().method() === "PUT") {
          saved = true;
          // Hold the answer so the box is observed between the save and the refetch.
          await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ region: "US", onboarded: true, email: "new@example.com" }) });
          return;
        }
        // Every read after the save fails.
        return saved ? route.fulfill({ status: 500, contentType: "application/json", body: "{}" }) : route.fallback();
      }
    );
    await page.goto("/settings");
    await expect(page.getByTestId("email-input")).toHaveValue("old@example.com");

    const values: string[] = [];
    await page.exposeFunction("recordEmail", (v: string) => values.push(v));
    await page.evaluate(() => {
      const input = document.querySelector<HTMLInputElement>("[data-testid=email-input]")!;
      const record = (window as unknown as { recordEmail: (v: string) => void }).recordEmail;
      new MutationObserver(() => record(input.value)).observe(input, { attributes: true });
      const poll = () => {
        record(input.value);
        requestAnimationFrame(poll);
      };
      poll();
    });

    await page.getByTestId("email-input").fill("new@example.com");
    await page.getByTestId("email-save").click();
    await expect(page.getByTestId("email-save-status")).toHaveText("Saved.");
    await expect(page.getByTestId("email-input")).toHaveValue("new@example.com");
    await expect(page.getByTestId("email-save")).toBeDisabled();
    // Give the failing refetch time to land, then check nothing ever painted the old address back.
    await page.waitForTimeout(500);
    await expect(page.getByTestId("email-input")).toHaveValue("new@example.com");
    const afterFill = values.slice(values.indexOf("new@example.com"));
    expect(afterFill).not.toContain("old@example.com");
  });

  test("a failed email save is a danger message, not 'Saved ✓'", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: true } });
    await page.route(
      (u) => u.pathname === "/api/settings",
      (route) => (route.request().method() === "PUT" ? route.fulfill({ status: 500, body: "{}" }) : route.fallback())
    );
    await page.goto("/settings");

    await page.getByTestId("email-input").fill("me@example.com");
    await page.getByTestId("email-save").click();
    await expect(page.getByTestId("email-save-status")).toHaveAttribute("data-tone", "danger");
    await expect(page.getByTestId("email-save-status-retry")).toBeVisible();
  });
});

test.describe("the other settings saves report failures too", () => {
  for (const c of [
    { label: "500", status: 500, body: {}, text: "Couldn't save — nothing changed." },
    { label: "400", status: 400, body: { error: "pin must be 4-8 digits" }, text: "The PIN must be 4-8 digits." },
  ]) {
    test(`a PIN set that fails with ${c.label} says what happened — never "Wrong current PIN"`, async ({ page }) => {
      await boot(page);
      await routeFilters(page, (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(FILTERS) }));
      await page.route(
        (u) => u.pathname === "/api/settings/pin",
        (route) => route.fulfill({ status: c.status, contentType: "application/json", body: JSON.stringify(c.body) })
      );
      await page.goto("/settings");
      await page.getByTestId("new-pin-input").fill("1234");
      await page.getByTestId("pin-submit").click();
      await expect(page.getByTestId("pin-error")).toHaveText(c.text);
      await expect(page.getByText(/Wrong current PIN|Could not set the PIN|PIN updated/)).toHaveCount(0);
    });
  }

  test("a tag that fails to save stays in the box and says so; Retry adds it", async ({ page }) => {
    await boot(page);
    let fail = true;
    let tags: { id: string; text: string }[] = [];
    await page.route(
      (u) => u.pathname === "/api/preferences/tags",
      (route) => {
        if (route.request().method() === "POST") {
          if (fail) return route.fulfill({ status: 500, contentType: "application/json", body: "{}" });
          tags = [{ id: "t1", text: "slow-burn heists" }];
          return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(tags[0]) });
        }
        return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(tags) });
      }
    );
    await page.goto("/settings");

    await page.getByTestId("add-tag-input").fill("slow-burn heists");
    await page.getByTestId("add-tag-input").press("Enter");
    const status = page.getByTestId("tag-save-status");
    await expect(status).toHaveText(/^Couldn't add "slow-burn heists" — nothing changed\./);
    await expect(status).toHaveAttribute("data-tone", "danger");
    // The words the user typed are still there.
    await expect(page.getByTestId("add-tag-input")).toHaveValue("slow-burn heists");

    fail = false;
    await page.getByTestId("tag-save-status-retry").click();
    await expect(page.getByTestId("preference-tags")).toContainText("slow-burn heists");
    await expect(page.getByTestId("add-tag-input")).toHaveValue("");
    await expect(page.getByTestId("tag-save-status")).toHaveCount(0);
  });

  test("a tag that fails to remove says it is still on", async ({ page }) => {
    await boot(page);
    await page.route(
      (u) => u.pathname === "/api/preferences/tags",
      (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([{ id: "t1", text: "slow-burn heists" }]) })
    );
    await page.route(
      (u) => u.pathname === "/api/preferences/tags/t1",
      (route) => route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
    );
    await page.goto("/settings");
    await page.getByTestId("remove-tag").click();
    await expect(page.getByTestId("tag-save-status")).toHaveText(/^Couldn't remove "slow-burn heists" — it's still on\./);
    await expect(page.getByTestId("preference-tags")).toContainText("slow-burn heists");
  });

  test("a failed notifications enable says so instead of silently showing 'Turn on' again", async ({ page }) => {
    // A browser with push: a registration whose subscribe works, so the server's refusal is the failure.
    await page.addInitScript(() => {
      const subscription = {
        endpoint: "https://push.example/abc",
        toJSON: () => ({ keys: { p256dh: "p", auth: "a" } }),
        unsubscribe: async () => true,
      };
      const registration = {
        pushManager: { getSubscription: async () => null, subscribe: async () => subscription },
      };
      Object.defineProperty(window, "PushManager", { value: function PushManager() {}, configurable: true });
      Object.defineProperty(window, "Notification", {
        value: { permission: "default", requestPermission: async () => "granted" },
        configurable: true,
      });
      Object.defineProperty(navigator, "serviceWorker", {
        value: { getRegistration: async () => registration, getRegistrations: async () => [], ready: Promise.resolve(registration) },
        configurable: true,
      });
    });
    await boot(page);
    await mockJson(page, "**/api/push/vapid-public-key", { key: "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U" });
    await page.route(
      (u) => u.pathname === "/api/push/subscribe",
      (route) => route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
    );
    await page.goto("/settings");

    await expect(page.getByTestId("push-toggle")).toContainText("Turn on");
    await page.getByTestId("push-toggle").click();
    await expect(page.getByTestId("push-failure")).toHaveText("Couldn't turn notifications on — they're still off. Try again in a moment.");
    await expect(page.getByTestId("push-toggle")).toContainText("Turn on");
  });
});

test.describe("billing visibility (KILL-10)", () => {
  test("with billing switched off there is no plan card, no Upgrade and no email card", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: false } });
    await routeFilters(page, (route) => route.fulfill({ status: 500, body: "{}" }));
    await page.goto("/settings");
    await expect(page.getByTestId("prefs-section")).toBeVisible();
    await expect(page.getByTestId("billing-card")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /upgrade/i })).toHaveCount(0);
    await expect(page.getByTestId("email-capture")).toHaveCount(0);
    await expect(page.getByText(/coming soon|paid plans land/i)).toHaveCount(0);
  });

  test("an email already on file stays visible and removable while billing is off", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: false }, settings: { email: "old@example.com" } });
    await page.goto("/settings");
    await expect(page.getByTestId("email-capture")).toContainText("Reel sends nothing to it");
    await expect(page.getByTestId("email-input")).toHaveValue("old@example.com");
    await page.getByTestId("email-input").fill("");
    await expect(page.getByTestId("email-save")).toHaveText("Remove");
  });

  test("with billing on, a Free account sees what the server actually gates", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: true } });
    await page.goto("/settings");
    const card = page.getByTestId("billing-card");
    await expect(card).toBeVisible();
    await expect(page.getByTestId("plan-line")).toContainText("Free includes taste DNA");
    await expect(page.getByTestId("plan-line")).toContainText("Paid adds every recommendation row");
    // Ask Reel and the Trakt queue are not gated on the server, so the card never sells them.
    await expect(page.getByTestId("plan-line")).not.toContainText(/Paid adds[^.]*Ask Reel/);
  });

  test("a refused checkout gives the server's reason, never 'coming soon'", async ({ page }) => {
    await boot(page, { billing: { tier: "Free", configured: true } });
    await mockJson(page, "**/api/billing/checkout", { error: "Billing isn't switched on yet." }, 501);
    await page.goto("/settings");
    await page.getByRole("button", { name: /upgrade/i }).click();
    await expect(page.getByTestId("checkout-failure")).toHaveText("Billing isn't switched on yet. Nothing was charged.");
  });
});
