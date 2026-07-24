import { useEffect, useState } from "react";
import { useDispatch } from "react-redux";
import { Lock } from "lucide-react";
import {
  useGetContinueWatchingQuery,
  useGetFeedGenresQuery,
  useGetFeedQuery,
  useGetSessionQuery,
} from "../store/api";
import { removalsCleared } from "../store/removedTitlesSlice";
import InfiniteHero from "../components/rec/InfiniteHero";
import InfiniteRecRow from "../components/rows/InfiniteRecRow";
import GenreRow from "../components/rows/GenreRow";
import ContinueWatchingRow from "../components/rows/ContinueWatchingRow";
import BrowseFilters from "../components/rec/BrowseFilters";
import RefreshPicksButton from "../components/rec/RefreshPicksButton";
import { DEFAULT_BROWSE_FILTER, matchesMedia } from "../lib/browseFilter";

/** The hybrid home: a tonight's-picks hero answering one question, then an endless reel below. */
export default function Home() {
  const { data: session } = useGetSessionQuery();
  const { data: feed, isLoading } = useGetFeedQuery();
  const { data: continueWatching } = useGetContinueWatchingQuery();
  const [filter, setFilter] = useState(DEFAULT_BROWSE_FILTER);
  const dispatch = useDispatch();

  const building = session != null && session.pipelineStage !== "FeedReady" && (feed?.hero.length ?? 0) === 0;
  const resetKey = feed?.generatedAt ?? "";

  // A rebuild re-seeds every row from the server (which already excludes removed titles), so the
  // session's removal set is now redundant — clear it to avoid masking re-eligible titles.
  useEffect(() => {
    if (resetKey) dispatch(removalsCleared());
  }, [resetKey, dispatch]);

  const { data: genres } = useGetFeedGenresQuery(undefined, { skip: building });
  const continueFiltered = (continueWatching ?? []).filter((e) => matchesMedia(e.mediaType, filter));

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-2xl font-light text-fa-frost-bright">Tonight&apos;s picks</h1>
          {feed?.generatedAt && (
            <p className="fa-caption text-fa-frost-dim mt-1">
              ranked for you · {new Date(feed.generatedAt).toLocaleString()}
            </p>
          )}
        </div>
        {!building && (
          <div className="flex items-center gap-2">
            {(feed?.hero.length ?? 0) > 0 && <BrowseFilters value={filter} onChange={setFilter} />}
            <RefreshPicksButton />
          </div>
        )}
      </div>

      {isLoading || building ? (
        <div className="space-y-8">
          <div className="aspect-[16/8.5] rounded-2xl reel-shimmer" />
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-4">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="reel-poster reel-shimmer" />
            ))}
          </div>
          {building && (
            <p className="fa-body text-fa-frost-dim text-center">
              Your picks are being assembled ({session?.pipelineStage}) — they&apos;ll appear here the moment they&apos;re ready.
            </p>
          )}
        </div>
      ) : (
        <>
          <InfiniteHero key={`hero:${filter.media}:${resetKey}`} seed={feed?.hero ?? []} filter={filter} />

          <ContinueWatchingRow entries={continueFiltered} />

          {(feed?.rows ?? []).map((row) => (
            <InfiniteRecRow
              key={`${row.kind}:${row.anchorTitleId ?? row.title}:${filter.media}:${resetKey}`}
              title={
                row.kind === "because-you-loved" ? (
                  <>
                    Because you loved <span className="text-fa-frost-bright">{row.anchorName}</span>
                  </>
                ) : (
                  <span className="text-fa-frost-bright">{row.title}</span>
                )
              }
              context={{ row: row.kind, anchorTitleId: row.anchorTitleId, genre: null }}
              seed={row.items}
              filter={filter}
            />
          ))}

          {(genres ?? []).map((g) => (
            <GenreRow key={`${g.slug}:${filter.media}:${resetKey}`} slug={g.slug} label={g.label} filter={filter} />
          ))}

          {(feed?.lockedRowCount ?? 0) > 0 && (
            <section className="fa-card p-5 flex items-center gap-4">
              <Lock className="h-5 w-5 text-fa-frost-dim shrink-0" />
              <div>
                <p className="fa-body text-fa-frost-bright">
                  {feed!.lockedRowCount} more personalised row{feed!.lockedRowCount === 1 ? "" : "s"} on the full feed
                </p>
                <p className="fa-caption text-fa-frost-dim">
                  The free shortlist gives you three picks a day — the paid feed unlocks every row, natural-language
                  search and the managed Trakt queue.
                </p>
              </div>
            </section>
          )}
        </>
      )}
    </div>
  );
}
