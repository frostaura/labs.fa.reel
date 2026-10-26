import { useState } from "react";
import { Link } from "react-router";
import { ChevronDown, Undo2 } from "lucide-react";
import {
  useGetDislikesQuery,
  useUndoNotInterestedMutation,
  useUndropMutation,
  type DislikeEntry,
} from "../../store/api";
import LoadFailed from "../ui/load-failed";
import SaveStatus from "../ui/save-status";
import { saveFailureMessage, type SaveMessage } from "../ui/save-outcome";

const PAGE_SIZE = 20;

/** The picker's wire values, as stored: the panel says back what the user actually chose. */
const REASON_LABEL: Record<string, string> = {
  Genre: "not my genre",
  SeenEnough: "seen enough like this",
  Tone: "wrong tone",
  NotForMe: "just not for me",
};

function reasonLine(entry: DislikeEntry): string {
  if (entry.kind === "Dropped") return "stopped watching";
  // Older rows, and every dismissal made from the card overlay before it offered a picker, carry
  // no reason at all. Say so plainly rather than inventing one.
  return entry.reason ? REASON_LABEL[entry.reason] ?? "no reason given" : "no reason given";
}

function formatDate(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime())
    ? ""
    : date.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

/**
 * "You've told me no to N things. Here's the pattern I learned, and here's how to undo it."
 *
 * NotInterested and Dropped drive a recency-decayed, reason-weighted demotion on every scoring
 * surface — the strongest negative signal the product collects — and until now there was no list:
 * the user could not see what they had said no to, could not see what it had taught the model, and
 * could only revoke a title by finding it again.
 *
 * Two honest numbers, not one. `total` is every live dislike; the demotion is built only from the
 * ones inside the profile's window, so the panel reports that count separately instead of implying
 * all N are still steering anything.
 */
export default function DislikePanel() {
  const [offset, setOffset] = useState(0);
  // Collapsed by default (P1): the header and the "what I'm pushing down" sentence are the part a
  // reader needs on every visit; the full list is for the visit where they go looking for a title.
  const [open, setOpen] = useState(false);
  // `data` is the last page that loaded (any offset) and carries the totals and the profile, which do
  // not change between pages; `currentData` is THIS page only. Rows come from `currentData`, so a
  // failed "Older" never leaves page 1's rows on screen under page 2's range — the same bug the
  // track record had with its windows.
  const { data, currentData, isError, isFetching, refetch } = useGetDislikesQuery({ offset, limit: PAGE_SIZE });
  const [undoNotInterested] = useUndoNotInterestedMutation();
  const [undrop] = useUndropMutation();
  const [revokeFailure, setRevokeFailure] = useState<{ message: SaveMessage; retry: () => void } | null>(null);

  // A failed read says so; returning null here used to make "couldn't load" look like "you have
  // never said no to anything".
  if (isError && !data) {
    return (
      <LoadFailed
        what="what you've said no to"
        onRetry={() => void refetch()}
        retrying={isFetching}
        variant="inline"
        testId="dislikes-error"
      />
    );
  }
  if (!data || data.total === 0) return null;

  const { profile } = data;
  const demoted = [
    ...profile.genres.map((g) => g.genre),
    ...profile.tones.map((t) => t.tone),
  ];

  const revoke = async (entry: DislikeEntry) => {
    if (entry.tmdbId == null) return;
    setRevokeFailure(null);
    const args = { titleId: entry.titleId, mediaType: entry.mediaType.toLowerCase(), tmdbId: entry.tmdbId };
    try {
      await (entry.kind === "Dropped" ? undrop(args) : undoNotInterested(args)).unwrap();
    } catch (error: unknown) {
      // The row stays (nothing was invalidated); say why it did, instead of a button that did nothing.
      setRevokeFailure({
        message: { ...saveFailureMessage(error), text: `Couldn't undo ${entry.name} — it's still on the list.` },
        retry: () => void revoke(entry),
      });
    }
  };

  return (
    <section className="fa-card p-5 space-y-4" data-testid="dislike-panel">
      <div>
        <h2 className="fa-section-title">
          You&rsquo;ve told me no to {data.total.toLocaleString("en-US")} {data.total === 1 ? "thing" : "things"}
        </h2>
        <p className="fa-caption text-fa-frost-dim mt-1" data-testid="dislike-summary">
          {demoted.length > 0 ? (
            <>
              Right now I&rsquo;m pushing down{" "}
              <span className="text-fa-frost">{demoted.join(", ")}</span> — from the{" "}
              {profile.activeCount.toLocaleString("en-US")} you passed on in the last {profile.windowDays} days.
            </>
          ) : (
            <>
              Nothing specific is being pushed down: a dismissal only teaches me a genre or a tone
              when you say which, and none of the last {profile.windowDays} days&rsquo; passes did.
            </>
          )}
        </p>
      </div>

      <button
        type="button"
        onClick={() => setOpen(!open)}
        aria-expanded={open}
        aria-controls="dislike-list-region"
        className="fa-button-ghost inline-flex items-center gap-2"
        data-testid="dislike-toggle"
      >
        <ChevronDown className={`h-4 w-4 transition-transform ${open ? "rotate-180" : ""}`} />
        {open ? "Hide the list" : `Show all ${data.total.toLocaleString("en-US")}`}
      </button>

      {open && (
        <div id="dislike-list-region" className="space-y-4">
          <SaveStatus message={revokeFailure?.message ?? null} onRetry={revokeFailure?.retry} testId="dislike-revoke-status" />

          {isError && !currentData ? (
            <LoadFailed
              what="this page of the list"
              onRetry={() => void refetch()}
              retrying={isFetching}
              variant="inline"
              testId="dislikes-page-error"
            />
          ) : !currentData ? (
            <div className="space-y-2" data-testid="dislikes-page-loading">
              {Array.from({ length: 3 }, (_, i) => (
                <div key={i} className="h-10 rounded-md reel-shimmer" />
              ))}
            </div>
          ) : (
            <ul className="space-y-2" data-testid="dislike-list">
              {currentData.items.map((entry) => (
                <li key={`${entry.titleId}:${entry.createdAt}`} className="flex items-center gap-3" data-testid="dislike-row">
                  <div className="min-w-0 flex-1">
                    {entry.tmdbId == null ? (
                      <span className="fa-body text-fa-frost truncate block">{entry.name}</span>
                    ) : (
                      <Link
                        to={`/title/${entry.mediaType.toLowerCase()}/${entry.tmdbId}`}
                        className="fa-body text-fa-frost hover:text-fa-frost-bright truncate block"
                      >
                        {entry.name}
                        {entry.year != null && <span className="text-fa-frost-dim"> ({entry.year})</span>}
                      </Link>
                    )}
                    <span className="fa-caption text-fa-frost-dim">
                      {reasonLine(entry)} · {formatDate(entry.createdAt)}
                    </span>
                  </div>
                  <button
                    type="button"
                    onClick={() => void revoke(entry)}
                    className="fa-button-ghost shrink-0"
                    aria-label={`Undo — ${entry.name}`}
                    data-testid="dislike-revoke"
                  >
                    <Undo2 className="h-3.5 w-3.5" /> Undo
                  </button>
                </li>
              ))}
            </ul>
          )}

          {(data.hasMore || offset > 0) && (
            <div className="flex items-center justify-between">
              <button
                type="button"
                onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
                disabled={offset === 0}
                className="fa-button-ghost disabled:opacity-40"
                data-testid="dislike-prev"
              >
                Newer
              </button>
              <span className="fa-caption text-fa-frost-dim tabular-nums" data-testid="dislike-range">
                {currentData
                  ? `${currentData.offset + 1}–${currentData.offset + currentData.items.length} of ${currentData.total}`
                  : `from ${offset + 1} of ${data.total}`}
              </span>
              <button
                type="button"
                onClick={() => setOffset(offset + PAGE_SIZE)}
                disabled={!currentData?.hasMore}
                className="fa-button-ghost disabled:opacity-40"
                data-testid="dislike-next"
              >
                Older
              </button>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
