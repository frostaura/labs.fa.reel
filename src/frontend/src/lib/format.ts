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
