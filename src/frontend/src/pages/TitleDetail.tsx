import { useState } from "react";
import { Link, useLocation, useNavigate, useParams } from "react-router";
import { Bookmark, BookmarkCheck, ChevronDown, Eye, EyeOff, Play, RotateCw, Undo2, X } from "lucide-react";
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
import { ScoreChip } from "../components/rec/ScoreBadges";
import { CardUndoProvider } from "../components/rec/CardUndo";
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
import { featureLabel } from "../lib/featureLabel";
import { predictionWaterfall, significantContributions, signedStep } from "../lib/predictionWaterfall";
import { canGoBack, titleFailure } from "../components/title/titleRouting";
import { Popover, PopoverContent, PopoverTrigger } from "../components/ui/popover";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "../components/ui/dropdown-menu";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import { Dialog, DialogClose, DialogOverlay, DialogPortal, DialogTitle } from "../components/ui/dialog";
import { Button } from "../components/ui/button";

/** A labelled row of person chips (Director / Writers / Cast) — each a clickable, rateable avatar. */
function CreditRow({ label, people }: { label: string; people: CreditPerson[] }) {
  if (people.length === 0) return null;
  return (
    <div className="space-y-2">
      <p className="fa-overline text-3">{label}</p>
      <FadeScrollRow bleed className="flex gap-3 overflow-x-auto pb-2 [scrollbar-width:thin]">
        {people.map((p) => (
          <CastMemberCard key={p.personId} member={p} />
        ))}
      </FadeScrollRow>
    </div>
  );
}

/**
 * The top-5 breakdown bars, plus every synthetic serving-time row the server sent — the dislike
 * demotion and the taste-prior factors. Those rows are the explanation for a card sitting lower
 * than its number suggests, so they must never be crowded out by the slice: the server appends them
 * after the model's own contributions, which is exactly where a top-5 cut would drop them.
 */
const SYNTHETIC_FEATURES = [
  "dislikedSimilarity",
  "tastePriorNoHistoryGenre",
  "tastePriorGenreAffinity",
  "tastePriorMedia",
];

function visibleContributions(all: { feature: string; value: number }[]) {
  // Bars under 0.05 are noise at one decimal and are not drawn at all (F-25) — a "+0.0" bar is a
  // claim about nothing, and five of them crowded out the two that mattered.
  const significant = significantContributions(all);
  const top = significant.slice(0, 5);
  const synthetic = significant.filter((c) => SYNTHETIC_FEATURES.includes(c.feature) && !top.includes(c));
  return [...top, ...synthetic];
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

/** How long the Similar rail's first page may take before it offers a Retry instead. */
const SIMILAR_TIMEOUT_MS = 8000;

export default function TitleDetail() {
  const { mediaType = "movie", tmdbId = "0" } = useParams();
  const navigate = useNavigate();
  const args = { mediaType, tmdbId: Number(tmdbId) };
  const { data: title, isLoading, isError, error, isFetching, refetch } = useGetTitleQuery(args);
  const location = useLocation();
  // A 404 is an answer ("no such title"), anything else is a failure to get one; both used to fall
  // into `isLoading || !title` and shimmer forever (F-9).
  const failure = !title && isError ? titleFailure(error) : null;

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

  // Opened from a card, the page underneath is still mounted and Back reveals it. Opened from a
  // deep link (a shared URL, a notification, a fresh tab) there is nothing behind it: `navigate(-1)`
  // used to leave the app or do nothing (F-8). Then Close goes to Home, replacing the history entry
  // so Back does not reopen the modal.
  const close = () => {
    if (canGoBack(location.state)) navigate(-1);
    else navigate("/home", { replace: true });
  };

  const trailerKey = youTubeVideoId(title?.trailerUrl);
  const bars = title?.prediction ? visibleContributions(title.prediction.contributions) : [];
  const waterfall = title?.prediction
    ? predictionWaterfall(title.prediction.predictedRating, title.prediction.waterfall)
    : null;

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
          /* On a phone it is a full-screen sheet (O-13): `100dvh`, so the browser's own toolbars
             never cover its bottom, and no rounded panel floating over a sliver of page it cannot
             use. From `sm` it is the centred 880 px panel. */
          className="fixed inset-0 z-50 flex h-[100dvh] w-full flex-col overflow-hidden bg-fa-ink-2 reel-rise sm:inset-x-0 sm:bottom-auto sm:top-6 sm:mx-auto sm:h-auto sm:max-h-[calc(100vh-3rem)] sm:w-[min(94vw,880px)] sm:rounded-2xl sm:border sm:border-fa-edge sm:shadow-2xl"
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
            className="absolute right-3 top-3 z-20 fa-button-ghost inline-flex min-h-11 min-w-11 items-center justify-center rounded-full p-2 bg-fa-ink/60"
            data-testid="title-close"
          >
            <X className="h-5 w-5" />
          </DialogClose>

          {/* The Similar rail's card actions raise their undo toast here (F-19) — inside the
              dialog's content, because Radix makes everything outside a modal inert. */}
          <CardUndoProvider>
          <div className="overflow-y-auto">
            {failure ? (
              <>
                <DialogTitle className="sr-only">{failure === "not-found" ? "Title not found" : "Couldn't load this title"}</DialogTitle>
                <div className="p-8 pt-14 text-center space-y-4" role="alert" data-testid={`title-${failure}`}>
                  <p className="fa-body text-fa-frost-bright">
                    {failure === "not-found" ? "Reel has no title at this address." : "Couldn't load this title."}
                  </p>
                  {/* Only the 404 gets an explanation, because only the 404 has one. Any other
                      failure may be a 500, a 403 or no answer at all, and how long it lasts is not
                      something this screen knows — so it says nothing it cannot back. */}
                  {failure === "not-found" && (
                    <p className="fa-caption text-3">
                      The link may be mistyped, or the title has left TMDB&apos;s catalogue.
                    </p>
                  )}
                  <div className="flex items-center justify-center gap-2">
                    {failure === "error" && (
                      <Button
                        onClick={() => void refetch()}
                        disabled={isFetching}
                        className="min-h-11"
                        data-testid="title-retry"
                      >
                        <RotateCw className="h-4 w-4" aria-hidden />
                        {isFetching ? "Retrying…" : "Retry"}
                      </Button>
                    )}
                    <Button variant="ghost" onClick={close} className="min-h-11" data-testid="title-failure-close">
                      Close
                    </Button>
                  </div>
                </div>
              </>
            ) : isLoading || !title ? (
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
                      <div className="absolute bottom-0 inset-x-0 p-5 flex items-end gap-4">
                        <div className="w-24 shrink-0 hidden sm:block drop-shadow-xl">
                          <PosterImage path={title.posterPath} alt={title.name} size="w185" />
                        </div>
                        <div className="space-y-1.5">
                          <div className="flex items-center gap-3 flex-wrap">
                            <h1 className="text-2xl sm:text-3xl font-light text-fa-frost-bright">{title.name}</h1>
                            {/* Once you have rated it, your number is the headline: a "for you"
                                guess beside your own score is the model talking over you (O+3). The
                                prediction stays, demoted to a note, because the gap is the evidence. */}
                            {title.userState.userRating != null ? (
                              <span
                                className="inline-flex items-baseline gap-1 rounded-full border border-fa-frost/50 bg-fa-ink/80 px-3 py-1 text-base font-medium tabular-nums text-fa-frost-bright"
                                data-testid="title-your-rating"
                              >
                                You rated {title.userState.userRating}
                              </span>
                            ) : (
                              // The same one chip the cards carry (FD-4): the banded prediction over
                              // the crowd score.
                              title.prediction && (
                                <ScoreChip
                                  predictedRating={title.prediction.predictedRating}
                                  crowdRating={title.crowdRating ?? null}
                                  crowdVotes={title.crowdVotes ?? null}
                                  band={title.prediction.expectedAbsoluteError}
                                  size="lg"
                                />
                              )
                            )}
                          </div>
                          {title.userState.userRating != null && title.prediction && (
                            <p className="fa-caption text-fa-frost" data-testid="title-prediction-note">
                              Reel predicted {title.prediction.predictedRating.toFixed(1)} for you
                            </p>
                          )}
                          <p className="fa-caption text-3">
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

                {/* The shell's gutter, not a padding of its own, so the rows below bleed to the sheet's
                    edge exactly as Home's rails bleed to the page's (WS-9, O-9). */}
                <div className="px-gutter py-5 sm:py-6 space-y-6">
                  {/* Reactions */}
                  <div className="flex items-center gap-2 flex-wrap" data-testid="reaction-bar">
                    {/* The trailer is an action like the rest, here with them — not a disc over the
                        art, where on a phone it sat on the title it was meant to sell (O-13). */}
                    {trailerKey && !trailerOpen && (
                      <Button
                        onClick={() => setTrailerOpen(true)}
                        className="min-h-11"
                        data-testid="play-trailer"
                      >
                        <Play className="h-4 w-4" aria-hidden />
                        Trailer
                      </Button>
                    )}
                    <Popover open={ratePickerOpen} onOpenChange={setRatePickerOpen}>
                      <PopoverTrigger asChild>
                        <Button
                          variant={title.userState.userRating ? "primary" : "default"}
                          className="min-h-11 [@media(pointer:fine)]:min-h-0"
                        >
                          <Eye className="h-4 w-4" aria-hidden />
                          {title.userState.userRating ? `Seen it — you rated ${title.userState.userRating}` : "Seen it + rate"}
                        </Button>
                      </PopoverTrigger>
                      <PopoverContent align="start" className="w-auto">
                        <p className="fa-overline text-3 mb-2">one tap — seen &amp; rated</p>
                        {/* Two rows of five: ten 44 px targets in one row are wider than a phone. */}
                        <div className="grid grid-cols-5 gap-1">
                          {Array.from({ length: 10 }, (_, i) => i + 1).map((value) => (
                            <Button
                              key={value}
                              onClick={() => {
                                rate({ ...args, titleId: title.titleId, rating: value });
                                setRatePickerOpen(false);
                              }}
                              aria-pressed={title.userState.userRating === value}
                              className={`h-11 w-11 p-0 font-normal [@media(pointer:fine)]:h-9 [@media(pointer:fine)]:w-9 rounded-md tabular-nums ${
                                title.userState.userRating === value
                                  ? "border-fa-frost bg-fa-frost/25 text-fa-frost-bright"
                                  : "border-fa-edge bg-fa-glass text-fa-frost hover:border-fa-frost/50 hover:bg-fa-frost/15"
                              }`}
                              data-testid={`rate-${value}`}
                            >
                              {value}
                            </Button>
                          ))}
                          {title.userState.userRating != null && (
                            <Button
                              variant="ghost"
                              onClick={() => {
                                unrate(args);
                                setRatePickerOpen(false);
                              }}
                              className="min-h-11 [@media(pointer:fine)]:min-h-0 col-span-5 text-3"
                              data-testid="clear-rating"
                            >
                              Clear rating
                            </Button>
                          )}
                        </div>
                        <p className="fa-caption text-3 mt-2">
                          {title.mediaType === "Movie"
                            ? "Also logs a play on your Trakt history — cleared if you remove the rating."
                            : "Ratings sync to Trakt — your episode history is never touched."}
                        </p>
                      </PopoverContent>
                    </Popover>

                    <Button
                      variant={title.userState.savedForLater ? "primary" : "default"}
                      onClick={() =>
                        title.userState.savedForLater
                          ? unsave({ ...args, titleId: title.titleId })
                          : save({ ...args, titleId: title.titleId })
                      }
                      className="min-h-11 [@media(pointer:fine)]:min-h-0"
                      data-testid="save-button"
                    >
                      {title.userState.savedForLater ? <BookmarkCheck className="h-4 w-4" aria-hidden /> : <Bookmark className="h-4 w-4" aria-hidden />}
                      {title.userState.savedForLater ? "Saved — on your Up Next" : "Save for later"}
                    </Button>

                    {title.userState.notInterested ? (
                      <Button onClick={() => undoNotInterested({ ...args, titleId: title.titleId })} className="min-h-11 [@media(pointer:fine)]:min-h-0 border-fa-danger/40 text-fa-danger">
                        <Undo2 className="h-4 w-4" aria-hidden /> Not interested — undo
                      </Button>
                    ) : (
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" className="min-h-11 [@media(pointer:fine)]:min-h-0" data-testid="not-interested">
                            <EyeOff className="h-4 w-4" aria-hidden /> Not interested
                          </Button>
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
                        <Button onClick={() => undrop({ ...args, titleId: title.titleId })} className="min-h-11 [@media(pointer:fine)]:min-h-0 border-fa-frost/40 text-fa-frost" data-testid="undrop">
                          <Undo2 className="h-4 w-4" aria-hidden /> Dropped — undo
                        </Button>
                      ) : (
                        <Button variant="ghost" onClick={() => markDropped({ ...args, titleId: title.titleId })} className="min-h-11 [@media(pointer:fine)]:min-h-0" data-testid="drop-show">
                          <X className="h-4 w-4" aria-hidden /> Drop show
                        </Button>
                      )
                    )}
                  </div>

                  {/* The ONE pitch line (F-14): the why-line — second person, the model's own
                      reason, above the studio's blurb, in the voice and weight the hero card gives
                      it, so opening a card no longer REMOVES the one personalised sentence on it.
                      With no why-line the lens's hook stands in (LensBlock). Everything else the
                      lens says sits behind its "More". */}
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
                  <LensBlock mediaType={mediaType} tmdbId={Number(tmdbId)} hasPitch={Boolean(title.whyThis)} />

                  {/* Synopsis */}
                  {title.overview && <p className="fa-body text-fa-frost leading-relaxed max-w-3xl">{title.overview}</p>}

                  {/* Prediction breakdown: a walk from the baseline when the server sends one
                      (F-25), then the bars that pushed it. */}
                  {title.prediction && (waterfall || bars.length > 0) && (
                    <section className="space-y-2">
                      <h2 className="fa-overline text-3">why {title.prediction.predictedRating.toFixed(1)} for you</h2>
                      {waterfall && (
                        <ol
                          className="fa-caption flex flex-wrap items-baseline gap-x-1.5 gap-y-1 text-3 max-w-2xl"
                          aria-label={`From ${waterfall.start.label} ${waterfall.start.value.toFixed(1)} to ${waterfall.end.toFixed(1)} for you`}
                          data-testid="prediction-waterfall"
                        >
                          <li>
                            {waterfall.start.label} <span className="tabular-nums text-fa-frost">{waterfall.start.value.toFixed(1)}</span>
                          </li>
                          {waterfall.steps.map((step) => (
                            <li key={step.label} data-testid="waterfall-step">
                              <span aria-hidden>→ </span>
                              <span className={`tabular-nums ${step.value >= 0 ? "text-fa-success" : "text-fa-danger"}`}>{signedStep(step.value)}</span>{" "}
                              {step.label}
                            </li>
                          ))}
                          <li>
                            <span aria-hidden>→ </span>
                            <span className="tabular-nums text-fa-frost-bright">{waterfall.end.toFixed(1)}</span> for you
                          </li>
                        </ol>
                      )}
                      <div className="space-y-1.5 max-w-xl">
                        {bars.map((contribution) => {
                          const max = Math.max(...bars.map((c) => Math.abs(c.value))) || 1;
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
                              {/* No number: a bar is its push scaled to the largest one, not rating
                                  points, and a signed decimal here read as points and contradicted
                                  the walk above it. Length and colour carry it; the walk carries the
                                  points. */}
                              <span className="sr-only">{positive ? "pushed it up" : "pushed it down"}</span>
                            </>
                          );
                          // A bar with nothing verified behind it stays a plain row — no chevron
                          // that opens onto an empty panel.
                          if (!detail) {
                            return (
                              <div key={contribution.feature} className="flex items-center gap-3 pr-[1.375rem]" data-testid="contribution-bar">
                                {bar}
                              </div>
                            );
                          }
                          return (
                            <div key={contribution.feature}>
                              <Button
                                variant="ghost"
                                data-testid="contribution-bar"
                                onClick={() => setExpandedFeature(expanded ? null : contribution.feature)}
                                aria-expanded={expanded}
                                className="h-auto w-full justify-start gap-3 whitespace-normal rounded-md -mx-1 px-1 py-0 font-normal text-fa-frost hover:bg-fa-frost/5 hover:text-fa-frost"
                              >
                                {bar}
                                <ChevronDown
                                  className={`h-3.5 w-3.5 shrink-0 text-3 transition-transform ${expanded ? "rotate-180" : ""}`}
                                  aria-hidden
                                />
                              </Button>
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
                                          <span className="fa-caption text-3 tabular-nums">
                                            you rate them {p.yourAverage.toFixed(1)}
                                          </span>
                                        </Link>
                                      ))}
                                    </div>
                                  ) : (
                                    detail.note && <p className="fa-caption text-3">{detail.note}</p>
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
                  {((title.creators?.length ?? 0) > 0 || title.directors.length > 0 || title.writers.length > 0 || title.cast.length > 0) && (
                    <section className="space-y-4">
                      {/* A show is made by its creators; its directors are per-episode and usually
                          absent (F-24). Listed whenever the API sends them. */}
                      {title.mediaType === "Show" && (
                        <CreditRow label={(title.creators?.length ?? 0) === 1 ? "Creator" : "Creators"} people={title.creators ?? []} />
                      )}
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
                        // A row of skeletons while the first page is out, and a time limit on it:
                        // a kNN plus a rerank that hangs ends in a Retry tile, not an endless
                        // shimmer (F-14). Its cards carry the shared-evidence chips the server
                        // admitted them on (RecCard), like every anchored row.
                        timeoutMs={SIMILAR_TIMEOUT_MS}
                        skeletonCount={5}
                      />
                    </section>
                  )}
                </div>
              </>
            )}
          </div>
          </CardUndoProvider>
        </DialogPrimitive.Content>
      </DialogPortal>
    </Dialog>
  );
}
