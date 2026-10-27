/**
 * The account time zone, for the Settings card (R1-17).
 *
 * The server stores an IANA id (`Africa/Johannesburg`); an account that never chose one reads as
 * UTC. Nothing counts the account's day in it yet (see TimeZoneCard), so the card claims no effect. `GET /api/settings` serves both the stored value
 * (`timeZone`, null when never set) and the one in force (`effectiveTimeZone`), so this card can tell
 * "you chose UTC" from "nobody chose", and offer the browser's own zone to the second.
 */

/** The zone an account with no setting reads as (the server's `AccountTimeZone.Default`). */
export const DEFAULT_TIME_ZONE = "UTC";

/** The stored and in-force zones, as `GET /api/settings` serves them (absent from an older server). */
export interface TimeZoneSettings {
  timeZone?: string | null;
  effectiveTimeZone?: string | null;
}

/** What the card shows: the zone in force, and whether anyone chose it. */
export function timeZoneState(settings: TimeZoneSettings | null | undefined): { chosen: string | null; inForce: string } {
  const chosen = settings?.timeZone ?? null;
  return { chosen, inForce: settings?.effectiveTimeZone ?? chosen ?? DEFAULT_TIME_ZONE };
}

/** This device's IANA zone, or null where the runtime will not say. */
export function deviceTimeZone(): string | null {
  try {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    return typeof zone === "string" && zone.length > 0 ? zone : null;
  } catch {
    return null;
  }
}

/**
 * The zone worth offering in one tap: this device's, when it differs from the one in force. Null
 * when the device does not report one, or already matches.
 */
export function suggestedTimeZone(inForce: string, device: string | null): string | null {
  if (!device || device === inForce) return null;
  return device;
}

/**
 * Every zone the runtime knows, sorted, with UTC first. `Intl.supportedValuesOf` leaves UTC out on
 * some engines and may not list the device's own alias, so both are added; the server is the judge
 * of what it accepts (400 `time_zone_invalid`), and the card says so when it refuses one.
 */
export function timeZoneOptions(extra: (string | null | undefined)[] = []): string[] {
  let listed: string[] = [];
  try {
    listed = typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : [];
  } catch {
    listed = [];
  }
  const all = new Set([...listed, ...extra.filter((z): z is string => typeof z === "string" && z.length > 0)]);
  all.delete(DEFAULT_TIME_ZONE);
  return [DEFAULT_TIME_ZONE, ...[...all].sort((a, b) => a.localeCompare(b))];
}

/** "America/Argentina/Buenos_Aires" → "America / Argentina / Buenos Aires". */
export function timeZoneName(id: string): string {
  return id.replace(/_/g, " ").split("/").join(" / ");
}

/**
 * The zone's offset right now, as the locale writes it ("GMT+2"), or null where the runtime cannot
 * format it. "Right now" because daylight saving moves it — which is why the server stores the id.
 */
export function timeZoneOffset(id: string, at: Date = new Date()): string | null {
  try {
    const part = new Intl.DateTimeFormat("en-US", { timeZone: id, timeZoneName: "shortOffset" })
      .formatToParts(at)
      .find((p) => p.type === "timeZoneName");
    // Engines disagree on zero: "GMT" in one, "GMT+0" in another. Say it one way.
    return part?.value === "GMT+0" ? "GMT" : (part?.value ?? null);
  } catch {
    return null;
  }
}

/**
 * "Africa / Johannesburg (GMT+2)", or the bare name where there is no offset to show — and for UTC,
 * which is its own offset ("UTC (GMT)" says one thing twice).
 */
export function timeZoneLabel(id: string, at?: Date): string {
  if (id === DEFAULT_TIME_ZONE) return id;
  const offset = timeZoneOffset(id, at);
  return offset ? `${timeZoneName(id)} (${offset})` : timeZoneName(id);
}

/**
 * Matches any segment's word start ("joh", "buenos", "new york") or the whole id, best first: an
 * exact id, then a city that starts with the query, then any word inside the id.
 */
export function filterTimeZones(options: string[], query: string): string[] {
  const q = query.trim().toLowerCase().replace(/_/g, " ");
  if (q.length === 0) return options;
  const rank = (id: string): number => {
    const spaced = id.toLowerCase().replace(/_/g, " ");
    const city = spaced.split("/").pop() ?? spaced;
    if (spaced === q) return 0;
    if (city.startsWith(q)) return 1;
    if (spaced.split(/[\s/-]+/).some((word) => word.startsWith(q)) || spaced.includes(q)) return 2;
    return -1;
  };
  return options
    .map((id) => ({ id, r: rank(id) }))
    .filter((x) => x.r >= 0)
    .sort((a, b) => a.r - b.r)
    .map((x) => x.id);
}
