/** Shared display formatting (kept pure for unit tests). */

export function formatRuntime(minutes: number | null | undefined): string | null {
  if (!minutes || minutes <= 0) return null;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m}m`;
  return m === 0 ? `${h}h` : `${h}h ${m}m`;
}

/**
 * Vote counts, compactly. A crowd score is only as trustworthy as the crowd behind it — 9.1 from
 * 12 votes and 9.1 from 40,000 are different claims — but the count is a footnote, not a headline,
 * so it has to fit in a few characters beside the score.
 */
export function formatVotes(votes: number | null | undefined): string | null {
  if (votes == null || !Number.isFinite(votes) || votes <= 0) return null;
  if (votes < 1000) return `${Math.round(votes)}`;
  if (votes < 1_000_000) {
    const k = votes / 1000;
    return `${k < 10 ? k.toFixed(1).replace(/\.0$/, "") : Math.round(k)}k`;
  }
  const m = votes / 1_000_000;
  return `${m < 10 ? m.toFixed(1).replace(/\.0$/, "") : Math.round(m)}m`;
}

export function formatEpisodeRef(season: number | null, episode: number | null): string | null {
  if (season == null || episode == null) return null;
  return `S${season} E${episode}`;
}

/** Compact "2m ago" / "3h ago" / "just now" formatting for the last-sync stamps. */
export function relTime(iso: string | null | undefined): string {
  if (!iso) return "never";
  const deltaMs = Date.now() - new Date(iso).getTime();
  const minutes = Math.floor(deltaMs / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** "4:17 PM" — deliberately hand-rolled (not toLocaleTimeString) so tests and users get the same
 *  shape regardless of environment locale, and seconds can never leak in. */
function clockTime(d: Date): string {
  const h = d.getHours();
  const hour12 = h % 12 === 0 ? 12 : h % 12;
  return `${hour12}:${String(d.getMinutes()).padStart(2, "0")} ${h >= 12 ? "PM" : "AM"}`;
}

/**
 * The feed's "ranked for you" stamp: "today 4:17 PM", "yesterday 9:03 AM", else a short date
 * ("28 Jul", with the year only when it differs). Replaces toLocaleString(), whose full
 * date-and-seconds dump read like a debug line under the page title.
 */
export function formatRankedAt(iso: string, now: Date = new Date()): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  const startOfDay = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const dayDiff = Math.round((startOfDay(now) - startOfDay(d)) / 86_400_000);
  if (dayDiff === 0) return `today ${clockTime(d)}`;
  if (dayDiff === 1) return `yesterday ${clockTime(d)}`;
  const year = d.getFullYear() === now.getFullYear() ? "" : ` ${d.getFullYear()}`;
  return `${d.getDate()} ${MONTHS[d.getMonth()]}${year}`;
}
