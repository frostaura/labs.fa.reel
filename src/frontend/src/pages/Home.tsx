import { useEffect, useState } from "react";
import { useBrowseFilter } from "../lib/useBrowseFilter";
import { useDispatch } from "react-redux";
import { Lock, Sparkles } from "lucide-react";
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
import PickConditionsButton, { PickConditionsSummary } from "../components/rec/PickConditionsButton";
import { matchesMedia } from "../lib/browseFilter";
import { resolveSeedKey } from "../lib/feedSeed";

/** The hybrid home: a tonight's-picks hero answering one question, then an endless reel below. */
export default function Home() {
  const { data: session } = useGetSessionQuery();
  const { data: feed, isLoading } = useGetFeedQuery();
  const { data: continueWatching } = useGetContinueWatchingQuery();
  const [filter, setFilter] = useBrowseFilter("home");
  const dispatch = useDispatch();

  const building = session != null && session.pipelineStage !== "FeedReady" && (feed?.hero.length ?? 0) === 0;

  // Every row is keyed on `resetKey`, so changing it remounts them and discards the pages the user
  // has scrolled. The snapshot's own generatedAt is therefore NOT the key: acting on a card queues
  // a server rebuild, and keying on it directly meant a rating reset the list out from under the
  // user mid-triage. Instead the first snapshot is adopted, later ones are held, and the user
  // adopts them at a moment of their choosing.
  const latestKey = feed?.generatedAt ?? "";
  const [adoptedKey, setAdoptedKey] = useState("");
  const [adoptOnNext, setAdoptOnNext] = useState(false);

  // Adjusted during render rather than in an effect (React's documented alternative to a syncing
  // effect — it re-renders before commit instead of cascading). `resolveSeedKey` returns null in
  // the common case, which is what protects an in-progress scroll; it is self-clearing, so this
  // settles in one extra render pass.
  const nextSeed = resolveSeedKey({ latest: latestKey, adopted: adoptedKey, adoptOnNext });
  if (nextSeed !== null) {
    setAdoptedKey(nextSeed);
    if (adoptOnNext) setAdoptOnNext(false);
  }

  // Saving new conditions is the one change the "hold a fresher snapshot" rule above must NOT
  // protect the user from: they just told the app what they want to see, so the answer has to
  // arrive now rather than behind a "new picks ready" button. `updateFilters` invalidates the Feed
  // tag, so the snapshot refetches on its own; this counter forces the rows to remount around it
  // even when the server hands back the same `generatedAt` and the seed keys would not otherwise
  // change. Bumped only on a successful save, so nothing resets while the dialog is merely open.
  const [conditionsVersion, setConditionsVersion] = useState(0);

  const resetKey = `${adoptedKey}#${conditionsVersion}`;
  const hasFresherPicks = latestKey !== "" && adoptedKey !== "" && latestKey !== adoptedKey;

  // Adopting re-seeds every row from the server (which already excludes removed titles), so the
  // session's removal set is now redundant — clear it to avoid masking re-eligible titles.
  useEffect(() => {
    if (adoptedKey) dispatch(removalsCleared());
  }, [adoptedKey, dispatch]);

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
          {/* The conditions actually in force, under the title. Without this the feed silently
              obeys settings made on another screen — a standing "no documentary" is invisible
              exactly where its effect is. */}
          <PickConditionsSummary className="mt-2" />
        </div>
        {!building && (
          <div className="flex items-center gap-2">
            <PickConditionsButton
              onSaved={() => {
                setAdoptOnNext(true);
                setConditionsVersion((v) => v + 1);
              }}
            />
            {hasFresherPicks && (
              <button
                type="button"
                onClick={() => setAdoptedKey(latestKey)}
                data-testid="adopt-fresh-picks"
                title="Load the newly generated picks — this resets the rows below"
                className="fa-overline inline-flex items-center gap-1.5 rounded-lg border border-fa-frost/40 bg-fa-glass px-2.5 py-2 text-fa-frost transition hover:border-fa-frost/60 hover:bg-fa-glass-strong hover:text-fa-frost-bright"
              >
                <Sparkles className="h-3.5 w-3.5" />
                New picks ready
              </button>
            )}
            {(feed?.hero.length ?? 0) > 0 && <BrowseFilters value={filter} onChange={setFilter} />}
            <RefreshPicksButton onRequested={() => setAdoptOnNext(true)} />
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
