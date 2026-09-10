import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Bookmark, BookmarkCheck, ChevronDown, Eye, EyeOff, Play, Undo2, X } from "lucide-react";
import {
  useGetTitleQuery,
  useMarkNotInterestedMutation,
  useMarkDroppedMutation,
  useUndropMutation,
  useRateTitleMutation,
  useUnrateTitleMutation,
  useSaveForLaterMutation,
  useUndoNotInterestedMutation,
  useUnsaveForLaterMutation,
} from "../store/api";
import { backdropUrl } from "../lib/tmdbImages";
import PosterImage from "../components/rec/PosterImage";
import PredictedRatingBadge from "../components/rec/PredictedRatingBadge";
import WhereToWatch from "../components/title/WhereToWatch";
import CastMemberCard from "../components/title/CastMemberCard";
import FadeScrollRow from "../components/FadeScrollRow";
import LensBlock from "../components/title/LensBlock";
import InfiniteRecRow from "../components/rows/InfiniteRecRow";
import { DEFAULT_BROWSE_FILTER } from "../lib/browseFilter";
import { DISLIKE_REASONS } from "../lib/dislikeReasons";
import type { ContributionDetail, CreditPerson } from "../store/titleTypes";
import { formatRuntime } from "../lib/format";
import { youTubeVideoId } from "../lib/youtube";
import { Popover, PopoverContent, PopoverTrigger } from "../components/ui/popover";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "../components/ui/dropdown-menu";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import { Dialog, DialogClose, DialogOverlay, DialogPortal, DialogTitle } from "../components/ui/dialog";

/** A labelled row of person chips (Director / Writers / Cast) — each a clickable, rateable avatar. */
function CreditRow({ label, people }: { label: string; people: CreditPerson[] }) {
  if (people.length === 0) return null;
  return (
    <div className="space-y-2">
      <p className="fa-overline text-fa-frost-dim">{label}</p>
      <FadeScrollRow className="flex gap-3 overflow-x-auto pb-2 [scrollbar-width:thin]">
        {people.map((p) => (
          <CastMemberCard key={p.personId} member={p} />
        ))}
      </FadeScrollRow>
    </div>
  );
}

/** Humanize a model feature name for the breakdown bars. Covers every FeatureSchema scalar; any
 *  future/unmapped name falls back to a camelCase→words humanizer so no raw token ever shows. */
function featureLabel(feature: string): string {
  if (feature.startsWith("genre:")) return feature.slice(6).replace(/-/g, " ");
  if (feature.startsWith("subgenreBucket:")) return "theme match";
  const map: Record<string, string> = {
    // cast & crew
    castAffinity: "cast you rate highly",
    lovedCastCount: "favourite cast members",
    directorAffinity: "director affinity",
    writerAffinity: "writer affinity",
    hasCredits: "cast & crew data",
    // genre / era
    userGenreAffinity: "your genre taste",
    genreDrift: "recent genre streak",
    decadeAffinity: "your favourite era",
    // crowd signals
    traktRating: "crowd rating",
    traktVotesLog: "vote volume",
    tmdbPopularityLog: "popularity",
    contrarianAdjustedRating: "your contrarian lens",
    // title scalars
    releaseAgeLog: "release-year fit",
    runtimeMinutes: "runtime",
    certificationOrdinal: "maturity rating",
    isShow: "series format",
    showEngagement: "your episode ratings",
    // LLM tone attributes
    attrDarkness: "tone (darkness)",
    attrPacing: "pacing",
    attrComplexity: "complexity",
    attrEmotionalIntensity: "emotional intensity",
    attrHumor: "humor",
    attrOptimism: "optimism",
    attrEnsembleVsSolo: "ensemble vs. lead",
    hasAttributes: "tone analysis",
    // plot-embedding similarities
    simLovedCentroid: "close in feel to titles you love",
    simRecentCentroid: "matches your recent taste",
    simTopLovedMax: "very close to a favourite",
    simTopLovedMean: "feels like your favourites",
    hasEmbedding: "story similarity",
    // synthetic serving-time entries (not model features)
    dislikedSimilarity: "similar to titles you passed on",
  };
  if (map[feature]) return map[feature];
  // camelCase → spaced words, lower-cased (e.g. "simTopLovedMax" → "sim top loved max").
  return feature.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
}

/**
 * The top-5 breakdown bars, plus the dislike-demotion row whenever the server sent one — that row
 * is the explanation for a visibly lowered score, so it must never be crowded out by the slice.
 */
function visibleContributions(all: { feature: string; value: number }[]) {
  const top = all.slice(0, 5);
  const disliked = all.find((c) => c.feature === "dislikedSimilarity");
  return disliked && !top.includes(disliked) ? [...top, disliked] : top;
}

/**
 * The tap-through payload for one contribution bar, when the server has something verified to
 * show. lovedCastCount rides the castAffinity detail — the templater treats the two as the same
 * "cast you rate highly" story, so the tap-through must too.
 */
function detailFor(feature: string, details: ContributionDetail[] | undefined): ContributionDetail | undefined {
  const byFeature = (f: string) => details?.find((d) => d.feature === f);
  return byFeature(feature) ?? (feature === "lovedCastCount" ? byFeature("castAffinity") : undefined);
}

/** Avatar fallback: up to two initials from a person's name ("Denis Villeneuve" → "DV"). */
function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((word) => word[0]!.toUpperCase())
    .join("");
}

export default function TitleDetail() {
  const { mediaType = "movie", tmdbId = "0" } = useParams();
  const navigate = useNavigate();
  const args = { mediaType, tmdbId: Number(tmdbId) };
  const { data: title, isLoading } = useGetTitleQuery(args);

  const [rate] = useRateTitleMutation();
  const [unrate] = useUnrateTitleMutation();
  const [notInterested] = useMarkNotInterestedMutation();
  const [undoNotInterested] = useUndoNotInterestedMutation();
  const [markDropped] = useMarkDroppedMutation();
  const [undrop] = useUndropMutation();
  const [save] = useSaveForLaterMutation();
  const [unsave] = useUnsaveForLaterMutation();
  const [trailerOpen, setTrailerOpen] = useState(false);
  const [ratePickerOpen, setRatePickerOpen] = useState(false);
  // Which contribution bar's tap-through is open ("why not?" — who/what drives the feature).
  const [expandedFeature, setExpandedFeature] = useState<string | null>(null);

  // The card the user tapped, captured once on mount. Radix restores focus to its `DialogTrigger`,
  // and this dialog has none — it is opened by routing, not by a trigger — so without this, closing
  // drops focus on <body> and a keyboard user is returned to the top of the page instead of to the
  // card they came from. Lazy initialiser rather than a ref assigned during render, which the
  // React Compiler rules (correctly) reject.
  const [opener] = useState<HTMLElement | null>(() => document.activeElement as HTMLElement | null);

  const close = () => navigate(-1);

  const trailerKey = youTubeVideoId(title?.trailerUrl);

  return (
    /* This is spec §2's "Expanded (tap)" surface, so it is a real dialog, not a styled div: the
       hand-rolled fixed overlay it replaces had only a click-outside handler — no ESC, no focus
       trap, no focus restored to the card you opened it from. Composed from the shipped Radix
       primitives (ui/dialog.tsx), which carry all three. Only Content is taken raw: the shared
       DialogContent is a max-w-lg box with its own small close button, and this surface needs an
       880px panel with a close affordance legible over backdrop art. */
    <Dialog open onOpenChange={(next) => !next && close()}>
      <DialogPortal>
        <DialogOverlay className="bg-fa-ink/75" />
        <DialogPrimitive.Content
          /* Content is the flex/clip frame; the inner div scrolls, so the close button stays put
             instead of scrolling away with the synopsis. Centred with `mx-auto`, deliberately not
             with `-translate-x-1/2`: `reel-rise` animates `transform` with fill-mode `both`, so a
             translate-based centre is permanently overwritten by the animation's end keyframe and
             the panel lands at the viewport's midpoint by its top-left corner. */
          className="fixed inset-x-0 top-6 z-50 mx-auto flex max-h-[calc(100vh-3rem)] w-[min(94vw,880px)] flex-col overflow-hidden rounded-2xl border border-fa-edge bg-fa-ink-2 shadow-2xl reel-rise"
          aria-describedby={undefined}
          onCloseAutoFocus={(event) => {
            // Take over the restore: Radix's own handler aims at a trigger that does not exist.
            // `isConnected` guards the direct-URL case, where the background page never mounted.
            event.preventDefault();
            if (opener?.isConnected) opener.focus();
          }}
          data-testid="title-detail"
        >
          <DialogClose
            aria-label="Close"
            className="absolute right-3 top-3 z-20 fa-button-ghost rounded-full p-2 bg-fa-ink/60"
          >
            <X className="h-5 w-5" />
          </DialogClose>

          <div className="overflow-y-auto">
            {isLoading || !title ? (
              <>
                {/* The dialog needs an accessible name from its first frame, before the title lands. */}
                <DialogTitle className="sr-only">Loading title…</DialogTitle>
                <div className="aspect-[16/8] reel-shimmer" />
              </>
            ) : (
              <>
                {/* Screen-reader name for the dialog. The visible <h1> below stays a plain heading so
                    its own type scale is not overridden by the DialogTitle styling. */}
                <DialogTitle className="sr-only">{title.name}</DialogTitle>
                {/* Header art / trailer */}
                <div className="relative aspect-[16/8] bg-fa-ink-3">
                  {trailerOpen && trailerKey ? (
                    <iframe
                      src={`https://www.youtube-nocookie.com/embed/${trailerKey}?autoplay=1`}
                      title={`${title.name} trailer`}
                      allow="autoplay; encrypted-media"
                      allowFullScreen
                      className="absolute inset-0 h-full w-full"
                    />
                  ) : (
                    <>
                      {backdropUrl(title.backdropPath, "w1280") && (
                        <img src={backdropUrl(title.backdropPath, "w1280")!} alt="" className="absolute inset-0 h-full w-full object-cover" />
                      )}
                      <div className="absolute inset-0 reel-scrim" />
                      {trailerKey && (
                        <button
                          onClick={() => setTrailerOpen(true)}
                          className="absolute inset-0 m-auto h-16 w-16 rounded-full border border-fa-frost/50 bg-fa-ink/70 backdrop-blur flex items-center justify-center transition hover:scale-110"
                          aria-label="Play trailer"
                        >
                          <Play className="h-7 w-7 text-fa-frost-bright translate-x-0.5" />
                        </button>
                      )}
                      <div className="absolute bottom-0 inset-x-0 p-5 flex items-end gap-4">
                        <div className="w-24 shrink-0 hidden sm:block drop-shadow-xl">
                          <PosterImage path={title.posterPath} alt={title.name} size="w185" />
                        </div>
                        <div className="space-y-1.5">
                          <div className="flex items-center gap-3 flex-wrap">
                            <h1 className="text-2xl sm:text-3xl font-light text-fa-frost-bright">{title.name}</h1>
                            {title.prediction && <PredictedRatingBadge rating={title.prediction.predictedRating} size="lg" />}
                          </div>
                          <p className="fa-caption text-fa-frost-dim">
                            {[
                              title.year,
                              formatRuntime(title.runtimeMinutes),
                              title.certification,
                              title.network,
                              title.genres.slice(0, 3).join(" / "),
                            ]
                              .filter(Boolean)
                              .join(" · ")}
                          </p>
                        </div>
                      </div>
                    </>
                  )}
                </div>

                <div className="p-5 sm:p-6 space-y-6">
                  {/* Reactions */}
                  <div className="flex items-center gap-2 flex-wrap" data-testid="reaction-bar">
                    <Popover open={ratePickerOpen} onOpenChange={setRatePickerOpen}>
                      <PopoverTrigger asChild>
                        <button className={title.userState.userRating ? "fa-button-primary" : "fa-button"}>
                          <Eye className="h-4 w-4" />
                          {title.userState.userRating ? `Seen it — you rated ${title.userState.userRating}` : "Seen it + rate"}
                        </button>
                      </PopoverTrigger>
                      <PopoverContent align="start" className="w-auto">
                        <p className="fa-overline text-fa-frost-dim mb-2">one tap — seen &amp; rated</p>
                        <div className="flex gap-1">
                          {Array.from({ length: 10 }, (_, i) => i + 1).map((value) => (
                            <button
                              key={value}
                              onClick={() => {
                                rate({ ...args, titleId: title.titleId, rating: value });
                                setRatePickerOpen(false);
                              }}
                              className={`h-9 w-8 rounded-md border text-sm tabular-nums transition ${
                                title.userState.userRating === value
                                  ? "border-fa-frost bg-fa-frost/25 text-fa-frost-bright"
                                  : "border-fa-edge bg-fa-glass text-fa-frost hover:border-fa-frost/50 hover:bg-fa-frost/15"
                              }`}
                              data-testid={`rate-${value}`}
                            >
                              {value}
                            </button>
                          ))}
                          {title.userState.userRating != null && (
                            <button
                              onClick={() => {
                                unrate(args);
                                setRatePickerOpen(false);
                              }}
                              className="fa-button-ghost text-fa-frost-dim ml-2"
                              data-testid="clear-rating"
                            >
                              Clear rating
                            </button>
                          )}
                        </div>
                        <p className="fa-caption text-fa-frost-dim/70 mt-2">
                          {title.mediaType === "Movie"
                            ? "Also logs a play on your Trakt history — cleared if you remove the rating."
                            : "Ratings sync to Trakt — your episode history is never touched."}
                        </p>
                      </PopoverContent>
                    </Popover>

                    <button
                      onClick={() =>
                        title.userState.savedForLater
                          ? unsave({ ...args, titleId: title.titleId })
                          : save({ ...args, titleId: title.titleId })
                      }
                      className={title.userState.savedForLater ? "fa-button-primary" : "fa-button"}
                      data-testid="save-button"
                    >
                      {title.userState.savedForLater ? <BookmarkCheck className="h-4 w-4" /> : <Bookmark className="h-4 w-4" />}
                      {title.userState.savedForLater ? "Saved — on your Up Next" : "Save for later"}
                    </button>

                    {title.userState.notInterested ? (
                      <button onClick={() => undoNotInterested({ ...args, titleId: title.titleId })} className="fa-button border-fa-danger/40 text-fa-danger">
                        <Undo2 className="h-4 w-4" /> Not interested — undo
                      </button>
                    ) : (
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <button className="fa-button-ghost" data-testid="not-interested">
                            <EyeOff className="h-4 w-4" /> Not interested
                          </button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="start">
                          {DISLIKE_REASONS.map((reason) => (
                            <DropdownMenuItem
                              key={reason.value}
                              onClick={() => notInterested({ ...args, titleId: title.titleId, reason: reason.value })}
                            >
                              {reason.label}
                            </DropdownMenuItem>
                          ))}
                        </DropdownMenuContent>
                      </DropdownMenu>
                    )}

                    {/* Drop — shows only; hides from Continue Watching (manual, revocable) */}
                    {title.mediaType === "Show" && (
                      title.userState.dropped ? (
                        <button onClick={() => undrop({ ...args, titleId: title.titleId })} className="fa-button border-fa-frost/40 text-fa-frost" data-testid="undrop">
                          <Undo2 className="h-4 w-4" /> Dropped — undo
                        </button>
                      ) : (
                        <button onClick={() => markDropped({ ...args, titleId: title.titleId })} className="fa-button-ghost" data-testid="drop-show">
                          <X className="h-4 w-4" /> Drop show
                        </button>
                      )
                    )}
                  </div>

                  {/* The why-line — second person, the model's own reason, above the studio's blurb.
                      Same voice and weight the hero card gives it, so opening a card no longer
                      REMOVES the one personalised sentence on it. */}
                  {title.whyThis && (
                    <p className="fa-body sm:text-base text-fa-frost max-w-2xl leading-snug" data-testid="why-this">
                      {title.whyThis}
                    </p>
                  )}

                  {/* "Through your lens" — the premise retold in your own taste vocabulary, with
                      the honest reservation behind a tap. Renders nothing at all when no copy has
                      been written, which leaves the why-line above and the synopsis below exactly
                      as they were. It sits ABOVE the synopsis and never replaces it: when the lens
                      is wrong or absent, the page still tells you what the thing is about. */}
                  <LensBlock mediaType={mediaType} tmdbId={Number(tmdbId)} overview={title.overview} />

                  {/* Synopsis */}
                  {title.overview && <p className="fa-body text-fa-frost leading-relaxed max-w-3xl">{title.overview}</p>}

                  {/* Prediction breakdown */}
                  {title.prediction && title.prediction.contributions.length > 0 && (
                    <section className="space-y-2">
                      <h2 className="fa-overline text-fa-frost-dim">why {title.prediction.predictedRating.toFixed(1)} for you</h2>
                      <div className="space-y-1.5 max-w-xl">
                        {visibleContributions(title.prediction.contributions).map((contribution) => {
                          const max = Math.max(...title.prediction!.contributions.map((c) => Math.abs(c.value))) || 1;
                          const positive = contribution.value >= 0;
                          const detail = detailFor(contribution.feature, title.contributionDetails);
                          const expanded = expandedFeature === contribution.feature;
                          const bar = (
                            <>
                              <span className="fa-caption text-fa-frost w-44 truncate text-left">{featureLabel(contribution.feature)}</span>
                              <div className="flex-1 h-2 rounded-full bg-fa-glass overflow-hidden">
                                <div
                                  className={`h-full rounded-full ${positive ? "bg-fa-success/70" : "bg-fa-danger/60"}`}
                                  style={{ width: `${Math.max(5, (Math.abs(contribution.value) / max) * 100)}%` }}
                                />
                              </div>
                              <span className="fa-caption text-fa-frost-dim tabular-nums w-12 text-right">
                                {contribution.value >= 0 ? "+" : ""}
                                {contribution.value.toFixed(1)}
                              </span>
                            </>
                          );
                          // A bar with nothing verified behind it stays a plain row — no chevron
                          // that opens onto an empty panel.
                          if (!detail) {
                            return (
                              <div key={contribution.feature} className="flex items-center gap-3 pr-[1.375rem]">
                                {bar}
                              </div>
                            );
                          }
                          return (
                            <div key={contribution.feature}>
                              <button
                                type="button"
                                onClick={() => setExpandedFeature(expanded ? null : contribution.feature)}
                                aria-expanded={expanded}
                                className="flex w-full items-center gap-3 rounded-md -mx-1 px-1 hover:bg-fa-frost/5 transition-colors"
                              >
                                {bar}
                                <ChevronDown
                                  className={`h-3.5 w-3.5 shrink-0 text-fa-frost-dim transition-transform ${expanded ? "rotate-180" : ""}`}
                                />
                              </button>
                              {expanded && (
                                <div data-testid="contribution-details" className="ml-1 mt-1.5 mb-1 space-y-1.5">
                                  {detail.people?.length ? (
                                    <div className="flex flex-wrap gap-1.5">
                                      {detail.people.map((p) => (
                                        <Link
                                          key={p.personId}
                                          to={`/person/${p.personId}`}
                                          className="inline-flex items-center gap-2 rounded-full border border-fa-edge bg-fa-glass px-2.5 py-1 hover:border-fa-frost/50 hover:bg-fa-frost/10 transition-colors"
                                        >
                                          <span className="flex h-6 w-6 items-center justify-center rounded-full bg-fa-frost/20 text-xs font-medium text-fa-frost-bright">
                                            {initials(p.name)}
                                          </span>
                                          <span className="fa-caption text-fa-frost-bright">{p.name}</span>
                                          <span className="fa-caption text-fa-frost-dim tabular-nums">
                                            you rate them {p.yourAverage.toFixed(1)}
                                          </span>
                                        </Link>
                                      ))}
                                    </div>
                                  ) : (
                                    detail.note && <p className="fa-caption text-fa-frost-dim">{detail.note}</p>
                                  )}
                                </div>
                              )}
                            </div>
                          );
                        })}
                      </div>
                    </section>
                  )}

                  {/* Credits — director, writers and cast as labelled, clickable, rateable photos */}
                  {(title.directors.length > 0 || title.writers.length > 0 || title.cast.length > 0) && (
                    <section className="space-y-4">
                      <CreditRow label={title.directors.length === 1 ? "Director" : "Directors"} people={title.directors} />
                      <CreditRow label="Writers" people={title.writers} />
                      <CreditRow label="Cast" people={title.cast} />
                    </section>
                  )}

                  {/* Where to watch */}
                  {title.tmdbId != null && (
                    <WhereToWatch mediaType={mediaType} tmdbId={title.tmdbId} />
                  )}

                  {/* More like this — semantic kNN to THIS title, re-ranked by your model; infinite.
                      Empty seed → it self-loads its first page on mount (like the genre rails). Keyed on
                      the title id so navigating between titles remounts a clean, correctly-anchored rail. */}
                  {title.titleId && (
                    <section className="space-y-2">
                      <InfiniteRecRow
                        key={`similar:${title.titleId}`}
                        title={<span className="text-fa-frost-bright">More like this</span>}
                        context={{ row: "similar", anchorTitleId: title.titleId, genre: null }}
                        seed={[]}
                        filter={DEFAULT_BROWSE_FILTER}
                      />
                    </section>
                  )}
                </div>
              </>
            )}
          </div>
        </DialogPrimitive.Content>
      </DialogPortal>
    </Dialog>
  );
}
