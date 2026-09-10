import { useEffect, useRef, useState } from "react";
import { useBrowseFilter } from "../lib/useBrowseFilter";
import { useSelector } from "react-redux";
import { Link, useLocation, useSearchParams } from "react-router-dom";
import { AlertTriangle, Mic, RotateCcw, Send, Sparkles, Square } from "lucide-react";
import PosterImage from "../components/rec/PosterImage";
import ScoreBadges from "../components/rec/ScoreBadges";
import CardQuickActions from "../components/rec/CardQuickActions";
import BrowseFilters from "../components/rec/BrowseFilters";
import { PickConditionsSummary } from "../components/rec/PickConditionsButton";
import { useGetFiltersQuery } from "../store/api";
import ChatErrorBoundary from "../components/ChatErrorBoundary";
import { type BrowseFilter } from "../lib/browseFilter";
import { selectRemovedKeys } from "../store/removedTitlesSlice";
import { floorHeldBack, formatFloor, nextBootstrap, plural, showMoodPresets, useAskReelChat } from "../lib/useAskReelChat";
import { useSpeech } from "../lib/useSpeech";
import type { AskCard, PhaseData } from "../lib/askReelStream";

/**
 * "Ask Reel" — a conversational discovery agent. The first turn comes from the search box (?q=);
 * from there it's a chat: Reel replies, streams personally-scored cards pulled live from TMDB,
 * re-orders them as hyper-personal LLM fit lands, and refines across turns ("only movies",
 * "less intense", "more like the first one") — never repeating a title.
 */
// No "Only movies" / "Only TV" here: the media segment in `BrowseFilters` sits in this same row
// and is the control that actually narrows discovery server-side. The chips only phrased the same
// request as free text, so tapping one and the toggle beside it could disagree on screen.
const REFINE_CHIPS = ["Something lighter", "More intense", "More like the first one"];

/** Cold-start mood presets — one tap seeds a whole first turn on an otherwise empty page. */
const MOOD_PRESETS = [
  "Under 90 minutes, feel-good",
  "Something dark and gripping",
  "Family movie night",
  "True-story tension",
];

/** Maps the browse toggle to the wire values the server understands ([] = no constraint). */
function mediaTypesFor(filter: BrowseFilter): string[] {
  if (filter.media === "movie") return ["movie"];
  if (filter.media === "show") return ["tv"];
  return [];
}

// Every counted noun goes through `plural`. A narrow first turn genuinely does reach "Found 1
// titles" and "Scoring 1 picks for you", and the live region reads these out loud.
function narrate(phase: PhaseData | null): string {
  switch (phase?.stage) {
    case "searching":
      return "Scanning the catalogue…";
    case "discovered":
      return `Found ${phase.found} ${plural(phase.found, "title")} — matching them to your taste…`;
    case "embedding":
      return `Reading ${phase.found} ${plural(phase.found, "plot")}…`;
    case "scoring":
      return `Scoring ${phase.found} ${plural(phase.found, "pick")} for you…`;
    case "ranking":
      return "Ranking your picks…";
    default:
      return "Thinking…";
  }
}

/**
 * The route. The page body is wrapped rather than the route element itself, so a render failure
 * inside the transcript or the grid is contained on the page instead of blanking the shell —
 * see {@link ChatErrorBoundary}. The `key` bump remounts the whole conversation on "Start again",
 * which is what makes the retry a real recovery and not a re-throw.
 */
export default function SearchResults() {
  const [crashKey, setCrashKey] = useState(0);
  return (
    <ChatErrorBoundary onReset={() => setCrashKey((k) => k + 1)}>
      <AskReel key={crashKey} />
    </ChatErrorBoundary>
  );
}

function AskReel() {
  const [params, setParams] = useSearchParams();
  const seed = params.get("q") ?? "";
  const [filter, setFilter] = useBrowseFilter("search");
  const [draft, setDraft] = useState("");
  const {
    turns, status, phase, cards, outsideCards, reason, send, resume, loadMore, reask, stop,
    setMediaTypes, exhausted, appending, canRetry, conflict, belowFloor, droppedByFloor,
    holdingPrevious, arrived,
  } = useAskReelChat();
  // The bar itself is not on the wire and should not be: it is the account's own standing
  // condition, the same `minPredictedRating` PickConditionsSummary reads two elements up the page.
  // Reading it from the one source keeps the tray's number and the conditions summary from ever
  // disagreeing about what the user set.
  const { data: accountFilters } = useGetFiltersQuery();
  const speech = useSpeech((text) => send(text, false));

  // The media toggle is per-device and persists across visits, so a resumed thread starts with a
  // filter the server has never been told about — and `reask` (the retry, and the toggle itself)
  // would then re-ask under the wrong constraint. Pushed on mount, and declared ABOVE the
  // bootstrap effect so it lands before the seeded turn goes out.
  useEffect(() => {
    setMediaTypes(mediaTypesFor(filter));
  }, [filter, setMediaTypes]);

  // A search from the header box (?q=) starts a fresh conversation; landing on /search with no
  // query resumes the most recent one. The seed is CONSUMED — cleared from the URL the moment it
  // is sent — because it used to sit there for the life of the thread, so a reload three
  // refinements in re-fired it as a fresh turn and replaced the whole conversation with a
  // single-turn answer to the original question. `nextBootstrap` holds the "already handled" rule
  // (and is unit-tested); the ref is what survives a re-render without re-triggering anything.
  const handledSeed = useRef<string | null>(null);
  const seedStarted = useRef(false);
  useEffect(() => {
    const { action, handled } = nextBootstrap(seed, handledSeed.current);
    handledSeed.current = handled;
    if (action === "seed") {
      seedStarted.current = true;
      send(seed, true);
      setParams((prev) => {
        const next = new URLSearchParams(prev);
        next.delete("q");
        return next;
      }, { replace: true });
      return;
    }
    if (action !== "resume") return;
    fetch("/api/search/conversations/latest", { credentials: "include" })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        // Not cancelled on cleanup — a StrictMode remount would drop the only response, and the
        // resume is what makes a reload durable. Guarded on `seedStarted` instead: once a search
        // has begun, a late-arriving old conversation must never replace it.
        if (!seedStarted.current && d?.id && d.turns?.length) resume(d.turns, d.id);
      })
      .catch(() => undefined);
  }, [seed, send, resume, setParams]);

  // The media filter is NOT applied here any more. Hiding cards after the fact is what produced
  // "No matches" on a turn that had returned eight perfectly good shows: the server had never been
  // told the user wanted movies, so it had no chance to go and find any. It is now sent upstream
  // and the server narrows discovery, so what arrives already matches.
  const removedSet = new Set(useSelector(selectRemovedKeys));
  const visible = cards.filter((c) => !removedSet.has(c.titleId));
  const outsideVisible = outsideCards.filter((c) => !removedSet.has(c.titleId));
  const streaming = status === "streaming";

  // The hook carries the server's sentence verbatim; the page only decides where it goes.
  const conflictMessage = conflict?.trim() ? conflict : null;

  // The score floor's other half. The server ranks everything it found and then holds back what
  // scored under the bar it keeps for this account — which is a decision made ON the user's behalf
  // and was, until now, invisible: the picks simply never existed as far as the screen was
  // concerned. The count is the tray's OWN contents wherever it has any (see `floorHeldBack`), and
  // the bar comes from the account's saved conditions — null when the user has set none, which
  // falls back to the unnumbered wording rather than blanking the tray.
  const belowFloorVisible = belowFloor.filter((c) => !removedSet.has(c.titleId));
  const heldBack = floorHeldBack(belowFloorVisible.length, droppedByFloor);
  const floorLabel = formatFloor(accountFilters?.minPredictedRating);

  // Re-ask on a filter change rather than filtering locally. `setMediaTypes` is read by the next
  // request, so it must land before the re-ask. `reask` — not `send` — because the user asked the
  // question once: replaying it through `send` stamped another copy of their bubble into the
  // transcript on every single toggle click.
  const changeFilter = (next: typeof filter) => {
    setFilter(next);
    setMediaTypes(mediaTypesFor(next));
    reask();
  };

  const submit = (text: string) => {
    if (!text.trim()) return;
    setDraft("");
    send(text, false);
  };

  // The settled outcome, announced once the stream stops. Kept separate from the streaming
  // narration below so the two live regions never talk over each other.
  const settledCount = visible.length + outsideVisible.length;
  // What a stopped turn says, in one place: the sr-only live region and the on-screen card must
  // not be able to disagree, and the count is the turn's OWN — cards held over from the previous
  // answer are still on screen and are explicitly not what arrived before the user stopped.
  const stoppedMessage =
    arrived === 0
      ? "Stopped before any picks arrived."
      : `Stopped — ${arrived} ${plural(arrived, "pick")} arrived before you stopped.`;
  const settled = streaming
    ? ""
    : status === "error"
      ? "Search hit a snag."
      : status === "stopped"
        ? stoppedMessage
        : status === "done"
          ? settledCount === 0
            ? "No matches for that one."
            : `${settledCount} ${plural(settledCount, "pick")} ready.`
          : "";

  return (
    // Bottom padding clears the composer AND, on a phone, the tab bar underneath it — otherwise
    // the last row of cards and the "Show me more" button sit permanently behind the composer.
    <div className="space-y-5 pb-44 md:pb-32">
      <div>
        <h1 className="text-2xl font-light text-fa-frost-bright flex items-center gap-2">
          <Sparkles className="h-5 w-5 text-fa-frost" /> Ask Reel
        </h1>
        {/* The same summary the feed carries, for the same reason: every standing condition
            applies to search too, and a thread that quietly obeys a "no documentary" saved on
            another screen is a thread the user cannot reason about. The browse toggle rides along
            and stays dismissible from here. */}
        <PickConditionsSummary className="mt-2" browse={filter} onBrowseChange={changeFilter} />
      </div>

      {/* Always mounted so it is a live region by the time it has something to say. Announces the
          settled outcome only; the phase narration has its own live region inside the banner. */}
      <p className="sr-only" role="status" aria-live="polite" data-testid="askreel-status">{settled}</p>

      {/* Transcript.
          A `log` landmark, announced politely: Reel's replies arrive whenever the model finishes,
          so a screen-reader user is told about them only if the region says it updates. The
          speaker labels are sr-only because sighted users read the bubble alignment and the
          sparkle for the same information — without them the whole thread is announced as one
          undifferentiated run of sentences with no way to tell question from answer. */}
      <div className="space-y-3" data-testid="askreel-transcript" role="log" aria-live="polite">
        {turns.map((turn, i) =>
          turn.role === "user" ? (
            <div key={i} className="flex justify-end">
              <p className="fa-body bg-fa-frost/15 text-fa-frost-bright rounded-2xl rounded-br-sm px-4 py-2 max-w-[80%]">
                <span className="sr-only">You said</span>
                {turn.text}
              </p>
            </div>
          ) : (
            <div key={i} className="flex items-start gap-3 max-w-[85%]" data-testid="askreel-reply">
              <Sparkles className="h-5 w-5 text-fa-frost shrink-0 mt-1" />
              <p className="fa-body text-fa-frost-bright">
                <span className="sr-only">Reel said</span>
                {turn.text}
              </p>
            </div>
          ),
        )}
      </div>

      {streaming && (
        <div data-testid="askreel-banner" className="fa-card relative overflow-hidden p-4 flex items-center gap-4">
          <span className="relative flex h-8 w-8 items-center justify-center shrink-0">
            <span className="absolute inline-flex h-full w-full rounded-full bg-fa-frost/20 animate-ping" />
            <Sparkles className="h-4 w-4 text-fa-frost" />
          </span>
          <div className="flex-1 min-w-0">
            {/* Live: the phase sentence changes several times per turn and is the only feedback a
                screen-reader user gets while Reel is working. */}
            <p
              data-testid="askreel-narration"
              aria-live="polite"
              aria-atomic="true"
              className="fa-caption text-fa-frost-bright truncate"
            >
              {narrate(phase)}
            </p>
            {/* The sweep only moves if the track is a positioning context: `fa-progress-slide`
                animates `left` from -40% to 100%, and on a statically-positioned child that
                animation applies to nothing at all — the bar sat frozen at one third for the whole
                turn, which reads as a hang exactly when the page is claiming to be working. Same
                markup as the onboarding build-up, which had it right. */}
            <div className="relative mt-2 h-1 w-full overflow-hidden rounded bg-fa-frost/10">
              <div className="absolute inset-y-0 rounded bg-fa-frost/60 fa-progress-slide" />
            </div>
          </div>
          {phase && phase.found > 0 && (
            <span className="fa-caption text-fa-frost-dim shrink-0 tabular-nums">
              {phase.scored > 0 ? `${phase.scored} scored` : `${phase.found} found`}
            </span>
          )}
          {/* A turn can run for tens of seconds against a slow model, and until now the only way
              out was to navigate away — the stream kept running and kept billing. */}
          <button
            type="button"
            onClick={stop}
            data-testid="askreel-stop"
            aria-label="Stop searching"
            className="fa-caption inline-flex items-center gap-1.5 rounded-full border border-fa-frost/30 px-3 py-1 text-fa-frost-dim shrink-0 hover:text-fa-frost-bright hover:border-fa-frost/60 hover:bg-fa-frost/10 transition-colors"
          >
            <Square className="h-3 w-3" /> Stop
          </button>
        </div>
      )}

      {/* Cold-start mood presets — a fresh /search with nothing asked yet gets four one-tap
          seeds ABOVE the composer instead of a blank page. Gone the moment a conversation
          exists (typed, streamed, or resumed) — from then on the refine chips own this space. */}
      {showMoodPresets(turns.length, cards.length + outsideCards.length, streaming) && (
        <div className="fa-card p-6 max-w-xl mx-auto space-y-4 text-center">
          <div className="space-y-1">
            <p className="fa-body text-fa-frost-bright">What are you in the mood for?</p>
            <p className="fa-caption text-fa-frost-dim">Tap one to start, or ask in your own words below.</p>
          </div>
          <div className="flex flex-wrap justify-center gap-2">
            {MOOD_PRESETS.map((preset) => (
              <button
                key={preset}
                type="button"
                data-testid="askreel-preset"
                onClick={() => send(preset)}
                className="fa-caption rounded-full border border-fa-frost/25 px-4 py-1.5 text-fa-frost hover:text-fa-frost-bright hover:border-fa-frost/50 hover:bg-fa-frost/10 transition-colors"
              >
                {preset}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Refine chips + media filter. Gated on the TRANSCRIPT, not on cards: a resumed thread has
          a conversation but no cards until it is asked again, and hiding the refine controls there
          left the user with a transcript they could not act on. */}
      {turns.length > 0 && (
        <div className="flex flex-wrap items-center gap-2">
          {REFINE_CHIPS.map((chip) => (
            <button
              key={chip}
              data-testid="askreel-chip"
              onClick={() => send(chip, false)}
              disabled={streaming}
              // Both the tooltip and aria-disabled, so a disabled chip explains itself instead of
              // just greying out — to pointers and to assistive tech alike.
              title={streaming ? "Wait for the current results to finish" : undefined}
              aria-disabled={streaming}
              className="fa-caption rounded-full border border-fa-frost/20 px-3 py-1 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/40 disabled:opacity-40 transition-colors"
            >
              {chip}
            </button>
          ))}
          <div className="ml-auto">
            <BrowseFilters value={filter} onChange={changeFilter} showAvailability={false} />
          </div>
        </div>
      )}

      {/* What the request and the account's standing conditions cannot both have. Named out loud,
          above the results, because the alternative is a thin answer with no explanation — the
          user asked for something their own saved exclusions rule out, and only the server can see
          both halves. Server-written and server-formatted; the page only places it. */}
      {conflictMessage && (
        <div
          className="fa-card border-fa-warning/30 p-4 flex items-start gap-3"
          data-testid="askreel-conflict"
        >
          <AlertTriangle className="h-4 w-4 text-fa-warning shrink-0 mt-0.5" />
          <p className="fa-caption text-fa-frost-bright">{conflictMessage}</p>
        </div>
      )}

      {/* A stopped turn is a state, not an absence. Pressing Stop before the first card used to
          leave `status: "idle"` over a transcript, which every branch below reads as "nothing to
          say" — so the page rendered a void: no cards, no message, no retry, and no way back to
          the answer that asking the question had already cleared. This card is the way back, and
          it sits ABOVE the results so that whatever did arrive (and whatever is being held from
          the previous answer) stays where it is instead of being replaced by an explanation. */}
      {status === "stopped" && (
        <div
          className="fa-card p-4 flex flex-wrap items-center justify-center gap-3 text-center"
          data-testid="askreel-stopped"
        >
          <p className="fa-caption text-fa-frost-dim">{stoppedMessage}</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      )}

      {/* One message, never two. A caught server exception arrives as a `done` frame with no
          results, which used to render the empty state AND the failure reason side by side —
          "try a different angle" next to "search hit a snag". The hook now classifies that frame
          as an error, so exactly one of these three branches is live, and the failure branch
          finally offers the retry that Ask Reel has never had. */}
      {status === "error" && cards.length === 0 ? (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-3" data-testid="semantic-error">
          <AlertTriangle className="h-6 w-6 text-fa-warning mx-auto" />
          <p className="fa-body text-fa-frost-bright">Search hit a snag</p>
          <p className="fa-caption text-fa-frost-dim">Reel couldn’t reach the search engine. It’s usually momentary.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} />}
        </div>
      ) : status === "resumed" && visible.length === 0 && outsideVisible.length === 0 ? (
        // A resumed thread is NOT an empty answer. Restoring a transcript used to land on
        // "No matches for that one" under the user's own question — the page reporting a verdict
        // on a search it never ran. Threads from before picks were stored simply have no cards,
        // and saying so is the honest version.
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-2" data-testid="askreel-transcript-only">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-caption text-fa-frost-dim">
            Transcript only — this thread predates saved picks. Ask again to bring it back.
          </p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      ) : status === "done" && visible.length === 0 && outsideVisible.length === 0 ? (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-2" data-testid="semantic-empty">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-body text-fa-frost-bright">No matches for that one</p>
          <p className="fa-caption text-fa-frost-dim">Try a different angle — a mood, a vibe, or “more like &lt;something&gt;”.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      ) : (
        <>
          {/* The seam. While a new turn is working, the previous answer stays up rather than the
              page going blank for fifteen seconds — but it must never read as an answer to the
              question just asked, so it is labelled and dimmed until the first card of the new
              turn replaces it. */}
          {holdingPrevious && (visible.length > 0 || outsideVisible.length > 0) && (
            <p className="fa-caption text-fa-frost-dim" data-testid="askreel-previous-answer">
              {streaming
                ? "Previous answer — held until the new one arrives."
                : "Previous answer — the last question never replaced it."}
            </p>
          )}
          <div
            className={`grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-4 transition-opacity ${holdingPrevious ? "opacity-40" : ""}`}
            data-testid="semantic-results"
          >
            {visible.map((card) => (
              <ResultCard key={card.titleId} card={card} />
            ))}
          </div>

          {/* The honest channel: titles that trip something the user excluded, shown ONLY when
              the clean picks could not fill the floor — flagged and fenced off under their own
              header, each captioned server-side with which exclusion it trips, never mixed into
              the grid above. */}
          {outsideVisible.length > 0 && (
            <div
              className={`space-y-3 border-t border-fa-frost/10 pt-5 transition-opacity ${holdingPrevious ? "opacity-40" : ""}`}
              data-testid="askreel-outside"
            >
              <div>
                <p className="fa-body text-fa-frost-dim">Outside your request — closest we could get</p>
                <p className="fa-caption text-fa-frost-dim/70">These match the vibe but hit something you excluded.</p>
              </div>
              <div className="grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-4">
                {outsideVisible.map((card) => (
                  <ResultCard key={card.titleId} card={card} />
                ))}
              </div>
            </div>
          )}

          {/* The floor, shown rather than assumed. Same fence as the outside-request channel, but
              collapsed: these picks are not being recommended, they are being accounted for. The
              count is what the tray actually holds, falling back to the server's own scalar only
              on a turn where the cards were not sent down (see `floorHeldBack`) — a label the user
              can count and disprove has to agree with its contents. The bar comes from the
              account's own conditions, formatted to the one decimal the server's sentence uses:
              naming it turns "Reel held some back" into something the user can act on, because
              the number in it is one they chose and can change. */}
          {heldBack > 0 && !streaming && (
            <details className="border-t border-fa-frost/10 pt-5" data-testid="askreel-below-floor">
              <summary className="fa-body text-fa-frost-dim cursor-pointer marker:text-fa-frost/40 hover:text-fa-frost-bright">
                {floorLabel
                  ? `Show the ${heldBack} under your ${floorLabel} bar`
                  : `Show the ${heldBack} ${plural(heldBack, "pick")} Reel held back`}
              </summary>
              <p className="fa-caption text-fa-frost-dim/70 mt-1">
                These matched the request but scored below the bar Reel holds itself to.
              </p>
              {belowFloorVisible.length > 0 && (
                <div className="mt-3 grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-4">
                  {belowFloorVisible.map((card) => (
                    <ResultCard key={card.titleId} card={card} />
                  ))}
                </div>
              )}
            </details>
          )}
        </>
      )}

      {/* Paging is a decision the user makes, not a scroll position.
          This used to be an IntersectionObserver sentinel with `rootMargin: 600px`, which was
          inside the viewport on first paint — so every single message fired a SECOND full-price
          /ask turn, whose interpret reply was generated, billed and then discarded. An explicit
          button costs one request per message and reads honestly ("there is more if you want
          it") instead of silently spending. */}
      {visible.length > 0 && (
        <div className="flex flex-col items-center gap-2">
          {!exhausted && (
            <button
              type="button"
              data-testid="askreel-more"
              onClick={loadMore}
              disabled={streaming || appending}
              className="fa-caption rounded-full border border-fa-frost/30 px-4 py-1.5 text-fa-frost-bright hover:border-fa-frost/60 hover:bg-fa-frost/10 disabled:opacity-40 transition-colors"
            >
              {appending ? "Finding more like these…" : "Show me more"}
            </button>
          )}
          {exhausted && (
            <p className="fa-caption text-fa-frost-dim text-center">
              That&apos;s everything on this thread — refine it above for a different angle.
            </p>
          )}
        </div>
      )}

      {/* The provenance footnote ("12 picks ranked for you, from 50 pulled"). Only ever shown
          alongside actual results — on an empty answer it was a second, contradictory message
          under the empty state. */}
      {reason && status === "done" && !appending && (visible.length > 0 || outsideVisible.length > 0) && (
        <p className="fa-caption text-fa-frost-dim text-center">{reason}</p>
      )}

      {/* A failure that arrives while cards are already on screen (a page that blew up) must not
          wipe the reel — it gets a quiet inline retry instead of the full-bleed error card. */}
      {status === "error" && cards.length > 0 && (
        <div className="flex flex-col items-center gap-2" data-testid="askreel-page-error">
          <p className="fa-caption text-fa-warning">That last request hit a snag.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} />}
        </div>
      )}

      {speech.error && (
        <p className="fa-caption text-fa-warning text-center" role="alert" data-testid="askreel-mic-error">
          {speech.error}
        </p>
      )}

      {/* Composer.
          It sits ABOVE the mobile tab bar, not under it. Both are `fixed` at `bottom-0` and the
          tab bar is `z-40`, so on every phone width Send and the input were behind it and a tap
          on Send navigated to /dna — invisible to any visibility assertion, since the composer
          renders perfectly, just covered. `3.5rem` is the tab bar's height and the safe-area
          inset is added on top of it (the bar pads itself by the same inset). From `md` up the
          tab bar is gone, so the composer returns to the very bottom. `askreel-mobile.spec.ts`
          hit-tests this with `elementFromPoint` at three widths. */}
      <form
        onSubmit={(e) => { e.preventDefault(); submit(draft); }}
        className="fixed bottom-[calc(3.5rem+env(safe-area-inset-bottom))] md:bottom-0 inset-x-0 z-30 border-t border-fa-frost/10 bg-background/95 backdrop-blur px-4 py-3"
      >
        <div className="max-w-3xl mx-auto flex items-center gap-2">
          <input
            data-testid="askreel-composer"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            aria-label="Ask Reel"
            placeholder="Ask for more, or refine — “something funnier”, “more like the second one”…"
            className="flex-1 rounded-full bg-fa-frost/10 px-4 py-2 fa-body text-fa-frost-bright placeholder:text-fa-frost-dim/60 focus:outline-none focus:ring-1 focus:ring-fa-frost/40"
          />
          {speech.supported && (
            <button
              type="button"
              onClick={() => (speech.listening ? speech.stop() : speech.start())}
              disabled={streaming}
              aria-label="Voice search"
              data-testid="askreel-mic"
              className={`rounded-full p-2.5 transition-colors disabled:opacity-40 ${
                speech.listening ? "bg-fa-frost/40 text-fa-frost-bright animate-pulse" : "bg-fa-frost/10 text-fa-frost-dim hover:text-fa-frost-bright"
              }`}
            >
              <Mic className="h-4 w-4" />
            </button>
          )}
          <button
            type="submit"
            disabled={!draft.trim() || streaming}
            data-testid="askreel-send"
            className="rounded-full bg-fa-frost/20 p-2.5 text-fa-frost-bright hover:bg-fa-frost/30 disabled:opacity-40 transition-colors"
            aria-label="Send"
          >
            <Send className="h-4 w-4" />
          </button>
        </div>
      </form>
    </div>
  );
}

/**
 * One Ask Reel result card — identical for clean picks and the outside-request channel (the only
 * difference between the two is WHERE they render and what their server-written `why` says).
 */
function ResultCard({ card }: { card: AskCard }) {
  const location = useLocation();
  return (
    <Link
      to={`/title/${card.mediaType.toLowerCase()}/${card.tmdbId}`}
      state={{ backgroundLocation: location }}
      className="group block focus:outline-none reel-rise"
    >
      {/* The focus ring lives here, not on the <a>: the link sets `focus:outline-none` so the
          browser default does not cut across the poster, which left keyboard navigation with NO
          visible focus at all. `group-focus-visible` puts it back, on the card, and only for
          keyboard focus. */}
      <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03] group-focus-visible:ring-2 group-focus-visible:ring-fa-frost/60 group-focus-visible:ring-offset-2">
        <PosterImage path={card.posterPath} alt={card.name} size="w185" />
        {/* The shared stack: prediction over crowd, no-overlap, scrimmed — and it owns the
            null/NaN guards, so a title with no community score shows one badge, never a dash. */}
        <ScoreBadges
          predictedRating={card.predictedRating}
          crowdRating={card.crowdRating}
          crowdVotes={card.crowdVotes}
        />
        {/* Quick actions and the Seen pill share the top-left corner; a seen title gets the pill
            (dismissing or queueing something already watched is meaningless), everything else gets
            the one-tap actions. ResultCard deliberately does NOT converge onto RecCard — the Ask
            payload has `why`/`seen` and lacks runtime/providers, so only the overlay is shared. */}
        {card.seen ? (
          <span className="absolute top-2 left-2 rounded-full bg-fa-ink/80 px-2 py-0.5 fa-caption text-fa-frost-dim ring-1 ring-fa-edge">
            Seen
          </span>
        ) : (
          <CardQuickActions
            titleId={card.titleId}
            mediaType={card.mediaType}
            tmdbId={card.tmdbId}
            name={card.name}
          />
        )}
      </div>
      <p className="mt-2 fa-body text-fa-frost-bright truncate" title={card.name}>{card.name}</p>
      <p className="fa-caption text-fa-frost-dim">{card.year ?? ""}</p>
      {card.why && (
        <p className="fa-caption text-fa-frost/80 line-clamp-2" title={card.why}>{card.why}</p>
      )}
    </Link>
  );
}

/**
 * The retry Ask Reel never had. Before this, a failed turn left the user with nothing but the
 * option to retype their question — so it replays the last one in place, adding no second copy of
 * their bubble to the transcript.
 */
function RetryButton({ onRetry, disabled, label = "Try again" }: { onRetry: () => void; disabled: boolean; label?: string }) {
  return (
    <button
      type="button"
      onClick={onRetry}
      disabled={disabled}
      data-testid="askreel-retry"
      className="inline-flex items-center gap-1.5 rounded-full border border-fa-frost/30 px-3 py-1 fa-caption text-fa-frost-bright hover:border-fa-frost/60 hover:bg-fa-frost/10 disabled:opacity-40 transition-colors"
    >
      <RotateCcw className="h-3.5 w-3.5" /> {label}
    </button>
  );
}
