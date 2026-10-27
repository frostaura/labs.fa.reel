import { Link, useLocation } from "react-router";
import { Layers } from "lucide-react";
import { useGetCrossReferenceQuery } from "../../store/api";
import { xrefBandCopy, xrefCoverageNote } from "../../lib/xrefCopy";

/**
 * "Cross-referenced against your history" — the deep pass, made legible.
 *
 * The pass judges the whole eligible pool against the titles the account has actually rated, sixty
 * at a time, and can legitimately run for ten minutes. Without a band it is a pill that counts and
 * then vanishes, leaving no trace of what it concluded; with one it is a sentence made entirely of
 * counts the server did.
 *
 * <p><b>It never claims a demotion.</b> A keep nudges a title up by at most a fifth of its rank
 * key; a rule-out does nothing at all, because on the founder's own holdout the `out` verdict
 * caught 16.2% of the titles he went on to love against 22.8% of the ones he went on to dislike.
 * The server's gating decision picks the clause, so the band cannot drift ahead of what the ranking
 * layer actually does — see `lib/xrefCopy.ts`.</p>
 *
 * Renders nothing at all when the server has nothing honest to say (204 → null), which is the
 * correct output for an account whose pass has never run.
 *
 * <p>On Track record — its home since FD-15 — it carries no link to Track record and no "see the
 * track record" clause (R1-41). `linkToTrackRecord` decides; left unset it is read off the route,
 * so the band cannot self-link wherever it is mounted.</p>
 */
export default function CrossReferenceBand({ linkToTrackRecord }: { linkToTrackRecord?: boolean } = {}) {
  const { pathname } = useLocation();
  const onTrackRecord = !(linkToTrackRecord ?? !pathname.startsWith("/track-record"));
  const { data: status } = useGetCrossReferenceQuery();
  const sentence = xrefBandCopy(status, { onTrackRecord });

  if (!sentence) return null;

  const coverage = xrefCoverageNote(status);

  return (
    <section
      data-testid="cross-reference-band"
      className="fa-card flex items-start gap-3 px-4 py-3 border-fa-frost/30"
    >
      <Layers className="h-4 w-4 text-fa-frost shrink-0 mt-0.5" aria-hidden />
      <div className="flex-1 min-w-0">
        <p className="fa-caption text-fa-frost-bright" data-testid="cross-reference-sentence">
          {sentence}
        </p>
        {coverage && (
          <p className="fa-caption text-3 mt-1" data-testid="cross-reference-coverage">
            {coverage}
          </p>
        )}
      </div>
      {!onTrackRecord && (
        <Link
          to="/track-record"
          className="fa-caption text-3 shrink-0 underline underline-offset-2 hover:text-fa-frost-bright"
          data-testid="cross-reference-track-record-link"
        >
          Track record
        </Link>
      )}
    </section>
  );
}
