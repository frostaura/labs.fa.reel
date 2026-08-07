/**
 * Extract a YouTube video id from the URL shapes TMDB (and hand-entered data) actually produce:
 * `watch?v={id}`, `youtu.be/{id}` and `/embed/{id}`. Anything else — including garbage that merely
 * mentions youtube — yields undefined, and the caller simply renders no trailer button.
 */
export function youTubeVideoId(url: string | null | undefined): string | undefined {
  if (!url) return undefined;
  const patterns = [/[?&]v=([\w-]{6,})/, /youtu\.be\/([\w-]{6,})/, /\/embed\/([\w-]{6,})/];
  for (const pattern of patterns) {
    const match = url.match(pattern);
    if (match) return match[1];
  }
  return undefined;
}
