import { useRef, useState, type ReactNode } from "react";
import { useSelector } from "react-redux";
import { feedApi, useGetTraktRecommendationsQuery } from "../../store/api";
import { selectHiddenKeys } from "../../store/removedTitlesSlice";
import { matchesBrowseFilter, type BrowseFilter } from "../../lib/browseFilter";
import { TRAKT_RECS_PROVENANCE, TRAKT_RECS_TITLE, traktRecsCaption, traktRecsNotice } from "../../lib/traktRecsCopy";
import { useReadRetry } from "../../lib/useReadRetry";
import { Button } from "../ui/button";
import RecRow from "./RecRow";
import ReadFailedLine from "./ReadFailedLine";
import { RAIL_INLINE_ACTION } from "./railLayout";
import { useNearViewport } from "./useNearViewport";

/**
 * Placeholder cards while the first answer is on its way: enough columns of two rows to cover a
 * 1440 px row (the cards are a fixed width, so a wider screen shows more of them), and the SAME card
 * metrics a loaded card has — the rail is exactly as tall loading as loaded.
 */
export const TRAKT_RECS_SKELETON_CARDS = 18;

interface Props {
  filter: BrowseFilter;
}

/**
 * Home's "Trakt recommends" rail: Trakt's own personalised recommendations — all of them that the
 * account has not seen — two rows deep, scrolling sideways together.
 *
 * It asks only once it nears the viewport (the genre rails' rule: a rail nobody approaches fetches
 * nothing and logs no exposure), and holds its two-row height with placeholders until then. Acting
 * on a card (Save, Not for me, a rating) takes it off the rail on the tap through the shared
 * removal set, exactly as on every other rail; the server's own filter makes it permanent on the
 * next read.
 *
 * It does not register its titles with the page's de-duplication: the brief is ALL of Trakt's
 * unseen recommendations, and the paging rails below it would otherwise lose up to two hundred of
 * their best candidates to a list that is not Reel's.
 *
 * Every state says what it is (`traktRecsCopy.ts`): a failed read is the page's "Couldn't load"
 * line with Retry; no link, a dead link and an unreachable Trakt each have their own sentence; and
 * an empty list after filtering says what was filtered, never just shows nothing.
 */
export default function TraktRecsRail({ filter }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const near = useNearViewport(ref);
  // Each Retry is its own request (`?retry=true` past the first), so the press always reaches Trakt.
  const [attempt, setAttempt] = useState(0);
  const read = useGetTraktRecommendationsQuery(attempt, { skip: !near });
  // A remount (a medium switch or a re-seed remounts every rail under the page provider) starts
  // un-near for a frame, and a skipped query reports no data; the cached answer is read directly so
  // the rail never drops back to placeholders over a list it already has.
  const cached = feedApi.endpoints.getTraktRecommendations.useQueryState(attempt);
  const readRetry = useReadRetry(read);
  const removed = useSelector(selectHiddenKeys);

  const data = read.data ?? cached.data;
  const retryTrakt = () => setAttempt((a) => a + 1);

  let body: ReactNode;
  if (!data && readRetry.phase !== "ok") {
    body = (
      <RecRow
        title={TRAKT_RECS_TITLE}
        items={[]}
        rows={2}
        notice={<ReadFailedLine what="Trakt's recommendations" phase={readRetry.phase} onRetry={readRetry.retry} testId="trakt-recs-failed" />}
      />
    );
  } else if (!data) {
    // Not near yet, or the first answer is in flight: hold the rail's full two-row height.
    body = (
      <RecRow
        title={TRAKT_RECS_TITLE}
        caption={TRAKT_RECS_PROVENANCE}
        items={[]}
        rows={2}
        loading
        skeletonCount={TRAKT_RECS_SKELETON_CARDS}
      />
    );
  } else {
    const hidden = new Set(removed);
    const shown = data.items.filter((c) => !hidden.has(c.titleId) && matchesBrowseFilter(c, filter));
    const hiddenByView = data.items.filter((c) => !hidden.has(c.titleId) && !matchesBrowseFilter(c, filter)).length;
    const notice = traktRecsNotice(data);
    const retrying = read.isFetching && attempt > 0;
    const retryButton = (
      <Button variant="link" onClick={retryTrakt} disabled={retrying} className={RAIL_INLINE_ACTION} data-testid="trakt-recs-retry">
        {retrying ? "asking Trakt…" : "Retry"}
      </Button>
    );

    if (shown.length > 0) {
      body = (
        <RecRow
          title={TRAKT_RECS_TITLE}
          rows={2}
          items={shown}
          caption={
            <span className="inline-flex flex-wrap items-center gap-x-1.5" data-state={data.state}>
              <span>{traktRecsCaption(data)}</span>
              {data.state === "Stale" && retryButton}
            </span>
          }
        />
      );
    } else {
      const text =
        notice?.text ??
        // Every card Trakt sent that survived Reel's rules is hidden by this view's Movies/TV or
        // release toggle, or was acted on this session: say which, so the rail is not a blank.
        (hiddenByView > 0
          ? `All ${hiddenByView} of Trakt's remaining picks are hidden by the view toggles above.`
          : "You've acted on every one of Trakt's recommendations shown here.");
      body = (
        <RecRow
          title={TRAKT_RECS_TITLE}
          rows={2}
          items={[]}
          notice={
            <p className="fa-caption flex flex-wrap items-center gap-x-1.5 text-3" role="status" data-testid="trakt-recs-notice" data-state={data.state}>
              <span>{text}</span>
              {notice?.retry && retryButton}
            </p>
          }
        />
      );
    }
  }

  return (
    <div ref={ref} data-testid="trakt-recs-rail">
      {body}
    </div>
  );
}
