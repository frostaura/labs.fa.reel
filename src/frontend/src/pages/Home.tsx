import { useEffect, useState } from "react";
import { useBrowseFilter } from "../lib/useBrowseFilter";
import { useDispatch, useSelector } from "react-redux";
import { Lock, Sparkles } from "lucide-react";
import {
  useGetContinueWatchingQuery,
  useGetDigestQuery,
  useGetFeedGenresQuery,
  useGetFeedQuery,
  useGetSessionQuery,
} from "../store/api";
import { removalsCleared, selectRemovedKeys } from "../store/removedTitlesSlice";
import { scoreOverridesCleared } from "../store/scoreOverridesSlice";
import InfiniteHero from "../components/rec/InfiniteHero";
import ModelLearnedBand from "../components/rec/ModelLearnedBand";
import CrossReferenceBand from "../components/rec/CrossReferenceBand";
import InfiniteRecRow from "../components/rows/InfiniteRecRow";
import StaticRail from "../components/rows/StaticRail";
import GenreRow from "../components/rows/GenreRow";
import RailPageProvider from "../components/rows/RailPageProvider";
import ContinueWatchingRow from "../components/rows/ContinueWatchingRow";
import BrowseFilters from "../components/rec/BrowseFilters";
import RefreshPicksButton from "../components/rec/RefreshPicksButton";
import PickConditionsButton, { PickConditionsSummary } from "../components/rec/PickConditionsButton";
import { matchesBrowseFilter, matchesMedia } from "../lib/browseFilter";
import { resolveSeedKey } from "../lib/feedSeed";
import { formatRankedAt } from "../lib/format";
import { digestRailCopy } from "../lib/digestCopy";
import PostWatchStrip from "../components/saved/PostWatchStrip";
import TonightMoodChips from "../components/rec/TonightMoodChips";
import HomeStatusLine from "../components/rec/HomeStatusLine";
import { heroReachingNote } from "../lib/heroConfidence";
import { MD_BREAKPOINT_PX, useMinWidth } from "../lib/useMinWidth";
import type { TonightMood } from "../lib/tonightMood";
import { pipelineStageCopy } from "../lib/syncCopy";
import { heroContainerClass, heroShimmerClass, type HeroRows } from "../lib/heroLayout";

/** The hybrid home: a tonight's-picks hero answering one question, then an endless reel below. */
export default function Home() {
  const { data: session } = useGetSessionQuery();
  const { data: feed, isLoading } = useGetFeedQuery();
  const { data: continueWatching } = useGetContinueWatchingQuery();
  const [filter, setFilter] = useBrowseFilter("home");
  const dispatch = useDispatch();
  const removed = useSelector(selectRemovedKeys);
  const isMdUp = useMinWidth(MD_BREAKPOINT_PX);

  const building = session != null && session.pipelineStage !== "FeedReady" && (feed?.hero.length ?? 0) === 0;

  // Two rows of tonight's picks for anyone who can page for more of them. Free is capped at three
  // hero cards server-side and its /more returns exhausted, so a second row there would be a half-
  // empty grid advertising the cap. Below `md` the grid collapses back to one row on its own.
  const heroRows: HeroRows = session?.tier === "Free" ? 1 : 2;

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

  // The tonight mood, held in component state and nowhere else. Deliberately NOT persisted and
  // deliberately NOT part of `resetKey`: it re-sorts the snapshot the browser already has, so it
  // must not remount the rows and lose the user's scroll, and it must not survive into tomorrow as
  // a standing preference nobody remembers setting.
  const [mood, setMood] = useState<TonightMood | null>(null);

  const resetKey = `${adoptedKey}#${conditionsVersion}`;
  const hasFresherPicks = latestKey !== "" && adoptedKey !== "" && latestKey !== adoptedKey;

  // Adopting re-seeds every row from the server (which already excludes removed titles, and whose
  // numbers are current by construction), so the session's removal set AND its recalibration
  // overrides are now redundant — clear both, or an override could mask a fresher server value.
  useEffect(() => {
    if (adoptedKey) {
      dispatch(removalsCleared());
      dispatch(scoreOverridesCleared());
    }
  }, [adoptedKey, dispatch]);

  const { data: genres } = useGetFeedGenresQuery(undefined, { skip: building });
  const { data: digest } = useGetDigestQuery(undefined, { skip: building });
  const continueFiltered = (continueWatching ?? []).filter((e) => matchesMedia(e.mediaType, filter));
  // The weekly digest rail hides itself entirely until Friday's build lands something.
  const digestFiltered = (digest?.items ?? []).filter((c) => matchesMedia(c.mediaType, filter));
  const digestCopy = digestRailCopy(digest?.degradedReason);

  // The hero's row-level admission, from the same cards the hero shows (the snapshot under the
  // browse filter, less what the user just acted on). Cards paged in later carry no confidence and
  // never change it — see heroReachingNote.
  const removedSet = new Set(removed);
  const reachingNote = heroReachingNote(
    (feed?.hero ?? []).filter((c) => matchesBrowseFilter(c, filter) && !removedSet.has(c.titleId))
  );

  // Everything the snapshot puts on the page, registered before any rail's first request so the
  // hero's first top-up already excludes the rows below it (review D3). Rails register their own
  // pages as they land; the digest registers through StaticRail when it arrives.
  const pageSeedIds = [
    ...(feed?.hero ?? []),
    ...(feed?.rows ?? []).flatMap((r) => r.items),
    ...digestFiltered,
  ].map((c) => c.titleId);

  return (
    // Tighter rhythm on a phone, where every 12px of chrome above the first pick is 12px of pick
    // below the fold. The budget is the cold-Home acceptance: the first pick's top edge within 220px
    // at 1440×900 and 300px at 390×844, with the account's real conditions on screen.
    <div className="space-y-5 md:space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-2 md:gap-3">
        <div className="min-w-0">
          {/* The stamp sits beside the heading where it fits (a phone included) and wraps under it
              where it does not: a line of chrome saved is a line of the first pick above the fold. */}
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h1 className="text-2xl font-light text-fa-frost-bright">Tonight&apos;s picks</h1>
            {feed?.generatedAt && (
              <p className="fa-caption text-fa-frost-dim">
                ranked for you · {formatRankedAt(feed.generatedAt)}
              </p>
            )}
          </div>
          {/* Everything actually in force, under the title: the saved conditions AND the browse
              toggles. Without this the feed silently obeys settings made on another screen — a
              standing "no documentary" is invisible exactly where its effect is — and the browse
              toggles were doubly invisible, since the summary used to omit them entirely while they
              were halving the feed. */}
          {/* Two chips and a count on a phone, so the row stays one line; the button beside the
              controls carries the full count and opens every chip. */}
          <PickConditionsSummary
            className="mt-2"
            browse={filter}
            onBrowseChange={setFilter}
            limit={isMdUp ? 6 : 2}
          />
        </div>
        {!building && (
          // One scrolling line on a phone, wrapping from `md`. Wrapped on a phone the controls took
          // two lines (the browse segments alone are 44px tall there) and put the first pick below
          // the fold; letting them scroll keeps conditions, refresh and the media toggle on screen
          // with the availability toggle peeking in from the edge. Nothing is ever wider than the
          // viewport, so the page itself never scrolls sideways.
          <div className="-mx-1 flex w-full items-center gap-2 overflow-x-auto px-1 [scrollbar-width:none] md:mx-0 md:w-auto md:flex-wrap md:overflow-visible md:px-0">
            <div className="shrink-0">
              <PickConditionsButton
                onSaved={() => {
                  setAdoptOnNext(true);
                  setConditionsVersion((v) => v + 1);
                }}
              />
            </div>
            {hasFresherPicks && (
              <button
                type="button"
                onClick={() => setAdoptedKey(latestKey)}
                data-testid="adopt-fresh-picks"
                title="Load the newly generated picks — this resets the rows below"
                className="fa-overline inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-lg border border-fa-frost/40 bg-fa-glass px-2.5 py-2 text-fa-frost transition hover:border-fa-frost/60 hover:bg-fa-glass-strong hover:text-fa-frost-bright"
              >
                <Sparkles className="h-3.5 w-3.5" />
                New picks ready
              </button>
            )}
            {/* Refresh sits second on a phone so it is never scrolled out of reach, and last from
                `md`, at the end of the control strip where it has always been. */}
            <div className="shrink-0 md:order-last">
              <RefreshPicksButton onRequested={() => setAdoptOnNext(true)} />
            </div>
            {(feed?.hero.length ?? 0) > 0 && (
              <div className="shrink-0">
                <BrowseFilters value={filter} onChange={setFilter} />
              </div>
            )}
          </div>
        )}
      </div>

      <PostWatchStrip />

      {isLoading || building ? (
        <div className="space-y-8">
          {/* Literally the hero's own container and card metrics, so the picks land in the space
              already held for them. The full-bleed block this replaced was a head taller than the
              card it stood in for, and a second row would have doubled that shove. */}
          <div className={heroContainerClass(heroRows)} aria-hidden>
            <div className={heroShimmerClass(heroRows)} />
            {heroRows === 2 && <div className={heroShimmerClass(heroRows)} />}
          </div>
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-4">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="reel-poster reel-shimmer" />
            ))}
          </div>
          {building && (
            <p className="fa-body text-fa-frost-dim text-center">
              Your picks are being assembled — {pipelineStageCopy(session?.pipelineStage)} — they&apos;ll appear here the moment they&apos;re ready.
            </p>
          )}
        </div>
      ) : (
        <>
          {/* One registry of titles on screen per page view (F+3): every rail sends what the
              others already show, so a title never repeats across rails. Keyed like the rows it
              wraps — a re-seed or a medium switch is a new page view. */}
          <RailPageProvider key={`page:${filter.media}:${resetKey}`} seedTitleIds={pageSeedIds}>
          {/* The mood chips (a question about tonight) and the status line (what is true about
              tonight's picks) sit tight above the picks they are about. From `lg` they share one
              line; on a phone the status line takes its own, under the chips. The product's own
              error rate lives in that line now, not in a banner of its own (FD-15). */}
          <div className="space-y-3">
            <div className="flex flex-wrap items-center gap-x-6 gap-y-1.5">
              <TonightMoodChips className="w-full lg:w-auto" value={mood} onChange={setMood} />
              <HomeStatusLine reachingNote={reachingNote} className="lg:ml-auto" />
            </div>
            <InfiniteHero key={`hero:${filter.media}:${resetKey}`} seed={feed?.hero ?? []} filter={filter} mood={mood} rows={heroRows} />
          </div>

          {/* The two bands are news about the MODEL and the deep pass, not about tonight's picks,
              so they sit BELOW the hero (FD-15): above it they put the first pick at 286px on a
              desktop and 416px on a phone, against budgets of 220 and 300 (R1-17). The retrain band
              leads because a retrain is the larger event. A re-key remounts them with the rails,
              which is harmless: both read a cached query, and a dismissal lives in storage.
              `empty:hidden` because with nothing honest to say both render nothing, and an empty
              wrapper would still take the page's rhythm gap. */}
          <div className="space-y-3 empty:hidden" data-testid="home-bands">
            <ModelLearnedBand />
            <CrossReferenceBand />
          </div>

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
              mood={mood}
            />
          ))}

          {/* "High for you" only when the build met the bar; a relaxed build is retitled and says
              why, in the build's own words (IR1S-03). */}
          {digestFiltered.length > 0 && (
            <StaticRail
              title={<span className="text-fa-frost-bright">{digestCopy.title}</span>}
              caption={digestCopy.caption}
              items={digestFiltered}
            />
          )}

          {(genres ?? []).map((g) => (
            <GenreRow key={`${g.slug}:${filter.media}:${resetKey}`} slug={g.slug} label={g.label} filter={filter} />
          ))}
          </RailPageProvider>

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
