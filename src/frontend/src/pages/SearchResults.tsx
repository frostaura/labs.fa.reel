import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useBrowseFilter } from "../lib/useBrowseFilter";
import { useSelector } from "react-redux";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { AlertTriangle, Mic, Plus, RotateCcw, Send, Sparkles, Square } from "lucide-react";
import PosterImage from "../components/rec/PosterImage";
import ScoreBadges from "../components/rec/ScoreBadges";
import CardQuickActions from "../components/rec/CardQuickActions";
import BrowseFilters from "../components/rec/BrowseFilters";
import { PickConditionsSummary } from "../components/rec/PickConditionsButton";
import {
  useGetConversationQuery, useGetFiltersQuery, useGetTurnQuery, useUnsaveForLaterMutation,
} from "../store/api";
import ChatErrorBoundary from "../components/ChatErrorBoundary";
import ConversationSwitcher from "../components/search/ConversationSwitcher";
import UndoToast from "../components/search/UndoToast";
import { type BrowseFilter } from "../lib/browseFilter";
import { relTime } from "../lib/format";
import { selectRemovedKeys } from "../store/removedTitlesSlice";
import {
  floorHeldBack, floorPartition, footnote, formatFloor, listPhrase, mediaFilterFor, nextBootstrap,
  plural, readStoredConversationId, resumeTarget, showMoodPresets, storeConversationId,
  useAskReelChat, showsFootnote, failedTurnMessage, FRESH_CONVERSATION, NO_RESUME,
  PENDING_TURN_POLL_MS, PENDING_TURN_BUDGET_MS, bubbleOverflows, prefersReducedMotion,
  scrollTargetFor, skeletonCount,
} from "../lib/useAskReelChat";
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
  const navigate = useNavigate();
  const location = useLocation();
  // The question arrives in the navigation, not the URL. `?q=` was a round trip through the
  // address bar — read the param, fire the turn, delete the param — and a reload landing before
  // the delete re-fired the question as a FRESH conversation, throwing away every refinement made
  // since. The turn id rides along from the search box so that a StrictMode remount (which resets
  // the ref guarding this) re-sends the same exchange and is replayed rather than billed twice.
  const askState = location.state as { ask?: string; askTurnId?: string } | null;
  const seed = askState?.ask ?? "";
  const seedTurnId = askState?.askTurnId;
  const [filter, setFilter] = useBrowseFilter("search");
  const [draft, setDraft] = useState("");
  const {
    turns, status, phase, cards, outsideCards, reason, send, resume, startNew, loadMore, reask,
    retryTurn, stop, setMediaTypes, exhausted, appending, pageable, paged, canRetry, conflict,
    conflictTerms, belowFloor, droppedByFloor, holdingPrevious, arrived, removed, lastCardsAt,
    lastTurnAt, resumedTail, answeredQuery, conversationId, errorReason, unsettled, settledAnswer,
    pageQuery,
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

  // A search from the header box starts a fresh conversation; landing on /search with nothing to
  // ask resumes this tab's thread. The seed is CONSUMED — the router state is cleared the moment it
  // is sent — and `nextBootstrap` holds the "already handled" rule (unit-tested), keyed on the TURN
  // ID rather than the text, so asking the same phrase twice is two exchanges and a double-invoke
  // of the same one is not. The ref survives a re-render; a StrictMode REMOUNT resets it, which is
  // what the shared turn id covers — the second send is the same exchange and the server replays it.
  const handledSeed = useRef<string | null>(null);
  const seedStarted = useRef(false);
  // What a cold /search resumes: THIS tab's remembered thread, and the account's newest one only
  // for a browser that has never asked anything. Resuming `/latest` unconditionally is how two
  // tabs — or a phone and a laptop — silently converged, with a reload dropping the reader into
  // the other device's conversation. Read once per mount: re-reading on every render would make a
  // turn that stores a new id re-key this query mid-conversation.
  const [threadId, setThreadId] = useState(() => readStoredConversationId());
  const [shouldResume, setShouldResume] = useState(false);

  useEffect(() => {
    const { action, handled } = nextBootstrap(seedTurnId ?? seed, handledSeed.current);
    handledSeed.current = handled;
    if (action === "seed" && seed) {
      seedStarted.current = true;
      send(seed, true, seedTurnId);
      navigate(".", { replace: true, state: null });
      return;
    }
    if (action === "resume") setShouldResume(true);
  }, [seed, seedTurnId, send, navigate]);

  // Three answers, and one of them is "do not ask": a tab that pressed `+ New` stored a marker
  // rather than clearing its id, so the reload lands on the cold start instead of falling through
  // to `/conversations/latest` and resurrecting the thread the user had just left (SB-02).
  const resumeFrom = resumeTarget(threadId);
  const { data: resumed, error: resumeError, refetch: refetchConversation } = useGetConversationQuery(resumeFrom, {
    skip: !shouldResume || resumeFrom === NO_RESUME,
  });

  useEffect(() => {
    // Guarded on `seedStarted`, not cancelled on cleanup: once a search has begun, a late-arriving
    // stored conversation must never replace it.
    if (!resumed || seedStarted.current) return;
    resume(resumed);
    // The toggle and the grid have to agree about what produced what is under it — a restored
    // answer that ran under "Movies" beside a control reading "All" is a control that lies.
    const media = mediaFilterFor(resumed.mediaTypes);
    if (media !== filter.media) setFilter({ ...filter, media });
    // `filter` is deliberately NOT a dependency: this reconciles the toggle TO the payload once,
    // and re-running it on every toggle change would fight the user for control of it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resumed, resume, setFilter]);

  // A stored id the server does not have any more — deleted from another device, or past
  // retention. Forget it rather than asking for it on every visit from here.
  useEffect(() => {
    if (resumeError && threadId) storeConversationId(null);
  }, [resumeError, threadId]);

  // A resumed thread whose last turn is STILL RUNNING — the tab that started it went away before
  // the stream finished, and the server kept going.
  //
  // "Still working on that one…" is only honest if the page then finds out. `GET /turns/{id}` is
  // what that is for — it answers 202 while Pending and the finished turn afterwards — and this is
  // its first caller. Today's resume payload projects no turn ids, so the fallback is to re-read
  // the conversation itself; both land on the same place, which is re-resuming the thread WHOLE
  // rather than patching one row into a transcript that has cards and a reason hanging off it.
  //
  // Bounded at a minute. Past that the honest thing is to stop asking: a turn that has not
  // finished in sixty seconds is not going to be watched into existence, and a page quietly
  // polling a dead thread for the length of a visit is worse than one that stops.
  const watchingPending = resumedTail?.status === "pending";
  const pendingTurnId = watchingPending ? resumedTail?.turnId ?? null : null;
  // The budget is spent per WATCH, not per mount, and expiry is derived rather than reset: a
  // re-read of the thread produces a fresh `resumedTail` every five seconds, so a boolean cleared
  // in an effect body would restart the minute on every tick (and cascade a render doing it).
  const watchKey = watchingPending ? pendingTurnId ?? conversationId ?? "thread" : null;
  const [expiredWatch, setExpiredWatch] = useState<string | null>(null);
  useEffect(() => {
    if (!watchKey) return;
    const budget = setTimeout(() => setExpiredWatch(watchKey), PENDING_TURN_BUDGET_MS);
    return () => clearTimeout(budget);
  }, [watchKey]);
  const watchExpired = watchKey !== null && expiredWatch === watchKey;

  const { data: watchedTurn } = useGetTurnQuery(pendingTurnId ?? "", {
    skip: !pendingTurnId || watchExpired,
    pollingInterval: PENDING_TURN_POLL_MS,
  });

  useEffect(() => {
    // Settled — Completed or Failed, both worth showing. Re-read the thread rather than splicing
    // the turn's own cards in beside a grid the server assembled under different rules.
    if (watchedTurn && watchedTurn.status !== "Pending") refetchConversation();
  }, [watchedTurn, refetchConversation]);

  useEffect(() => {
    // No turn id on the payload, which is every server today. The conversation is the only thing
    // there is to ask, and its resume payload carries the turn's status in `turns[].status`.
    if (!watchingPending || pendingTurnId || watchExpired) return;
    const tick = setInterval(() => refetchConversation(), PENDING_TURN_POLL_MS);
    return () => clearInterval(tick);
  }, [watchingPending, pendingTurnId, watchExpired, refetchConversation]);

  // The media filter is NOT applied here any more. Hiding cards after the fact is what produced
  // "No matches" on a turn that had returned eight perfectly good shows: the server had never been
  // told the user wanted movies, so it had no chance to go and find any. It is now sent upstream
  // and the server narrows discovery, so what arrives already matches.
  // Read as the array the store holds, not as a set built in the render: the same reference across
  // renders is what lets the save watcher below be a derivation rather than an effect.
  const removedKeys = useSelector(selectRemovedKeys);
  const removedSet = new Set(removedKeys);
  const streaming = status === "streaming";

  /**
   * F4-09. Saving a card takes it out of the answer instantly and said nothing at all: the grid
   * went 10 → 9, the live region announced a COUNT ("9 picks ready"), and a full-text scan for
   * anything matching /[Ss]aved/ came back null. A one-tap control with a destructive-looking
   * effect and no acknowledgement is indistinguishable from a bug, and there was no way back
   * short of finding the title again on /saved.
   *
   * Raised on the tap and confirmed by the CONSEQUENCE rather than by the click: `saveForLater`
   * dispatches `titleRemoved` only once the server has accepted it, so a card that is in the
   * removed set is one that really did go. A toast fired optimistically would offer an undo for
   * something that had not happened.
   *
   * A derivation, not an effect — the toast is on screen exactly while the card it names is gone
   * and the user has not dismissed it, which is a fact about the current render and not a thing
   * to synchronise. `unsaveForLater` dispatches `titleRestored`, so the undo puts the card back
   * in the grid by the same route the save took it out.
   */
  const [savePending, setSavePending] = useState<AskCard | null>(null);
  const [unsave] = useUnsaveForLaterMutation();
  const justSaved = savePending && removedKeys.includes(savePending.titleId) ? savePending : null;

  // F6-02. The page never moved: `scrollY` stayed at 0 for a whole 19.4-second turn, so a long
  // question filled the viewport and the banner, the narration, the progress bar and every card
  // were below the fold for the entire time. These two anchors are what "the live turn" and "what
  // the Stop left you with" mean as positions on the page; `scrollTargetFor` decides which of them
  // (if either) this status is about, and keying the effect on THAT rather than on the status keeps
  // it to the two transitions that change what the page is about.
  const liveRef = useRef<HTMLDivElement>(null);
  const settledRef = useRef<HTMLDivElement>(null);
  const scrollTarget = scrollTargetFor(status);
  useEffect(() => {
    if (!scrollTarget) return;
    const el = scrollTarget === "live" ? liveRef.current : settledRef.current;
    el?.scrollIntoView({ behavior: prefersReducedMotion() ? "auto" : "smooth", block: "start" });
  }, [scrollTarget]);

  // The floor, on a turn that never reached `done`.
  //
  // Ranking and the floor are the server's, and they run when the turn completes. Stop a page ten
  // seconds in and `done` never arrives — so the cards that streamed keep whatever score they had,
  // and sub-floor picks sat in the RECOMMENDED grid under a tray announcing that picks were held
  // back for being below the same bar. The client cannot rank, but it can apply the one standing
  // condition the user set to the cards they can see, so the grid and the tray stop contradicting
  // each other. Only on a stopped turn: every other state has the server's own answer.
  const stopped = status === "stopped";
  const floorBar = accountFilters?.minPredictedRating;
  // …and on a turn that is still streaming, which is F4-08. The server scores every candidate and
  // only reveals the split at `done`, so 23–24 posters sat on screen for between 11.5 and 18.6
  // seconds and then collapsed to 10 with no transition and no announcement — half of F6-04's shift
  // budget, and the reason the document went 4 482 → 4 851 px on a frame nobody asked for. A card
  // whose score comes back under the bar the user set belongs under the fence from that moment.
  //
  // Bounded to `unsettled` — the ids the server has NOT ranked. The same call over the whole grid
  // would re-floor finished work: the answer a stopped PAGE was extending, and a previous answer
  // handed back whole, are both already floored and committed server-side.
  const unsettledIds = new Set(unsettled);
  const clientFloor = streaming || stopped ? floorBar : null;
  const cleanPartition = floorPartition(cards.filter((c) => !removedSet.has(c.titleId)), clientFloor, unsettledIds);
  const outsidePartition = floorPartition(outsideCards.filter((c) => !removedSet.has(c.titleId)), clientFloor, unsettledIds);
  const visible = cleanPartition.kept;
  const outsideVisible = outsidePartition.kept;
  /** What the client's own pass moved out of the grid, for the tray to account for. */
  const clientHeldBack = [...cleanPartition.held, ...outsidePartition.held];
  /**
   * How much of the grid is still being held open for this turn. Placeholders sit AFTER the cards
   * that have arrived and shrink as they do, so the grid's height is monotone from the first frame
   * instead of being zero until the batch lands (F6-04). Never over a held previous answer — that
   * grid is finished work and is not the one being filled.
   */
  const pendingSlots = streaming && !holdingPrevious ? Math.max(0, skeletonCount(phase) - visible.length) : 0;

  // The hook carries the server's sentence verbatim; the page only decides where it goes.
  const conflictMessage = conflict?.trim() ? conflict : null;
  // Where it goes is UNDER the reply it qualifies, when there is one. The notice used to float
  // above the grid with nothing connecting it to the sentence the server had already rewritten —
  // two statements about the same collision, a control and a banner apart. The `conflict` flag on
  // the reply is what makes the anchor possible; without a reply on screen (a conflict frame
  // arriving before the model's, a resumed thread) it falls back to where it was.
  const lastAssistantIndex = turns.reduce((last, t, i) => (t.role === "assistant" ? i : last), -1);

  // The score floor's other half. The server ranks everything it found and then holds back what
  // scored under the bar it keeps for this account — which is a decision made ON the user's behalf
  // and was, until now, invisible: the picks simply never existed as far as the screen was
  // concerned. The count is the tray's OWN contents wherever it has any (see `floorHeldBack`), and
  // the bar comes from the account's saved conditions — null when the user has set none, which
  // falls back to the unnumbered wording rather than blanking the tray.
  const belowFloorVisible = [
    ...belowFloor.filter((c) => !removedSet.has(c.titleId)),
    // A streaming or stopped turn's sub-floor cards join the tray they belong in rather than
    // staying in the grid. Appended, so the label counts what the tray actually holds — the
    // previous turn's cut AND this one's — which is the number a user can open it and verify.
    ...clientHeldBack,
  ];
  const heldBack = floorHeldBack(belowFloorVisible.length, stopped ? null : droppedByFloor);
  const floorLabel = formatFloor(floorBar);

  // Re-ask on a filter change rather than filtering locally. `setMediaTypes` is read by the next
  // request, so it must land before the re-ask. `reask` — not `send` — because the user asked the
  // question once: replaying it through `send` stamped another copy of their bubble into the
  // transcript on every single toggle click.
  const changeFilter = (next: typeof filter) => {
    setFilter(next);
    setMediaTypes(mediaTypesFor(next));
    reask();
  };

  /**
   * `+ New`, and what a deleted current thread falls back to.
   *
   * Both halves of the fresh state move together: the hook stores the {@link FRESH_CONVERSATION}
   * marker so a RELOAD lands on the cold start, and this visit's own resume state is cleared so a
   * later re-render cannot key the resume query back to the thread just left. Clearing the stored
   * id alone was SB-02 — it made a tab that had asked plenty look like one that never had, and
   * `/conversations/latest` handed the thread straight back.
   */
  const beginFreshThread = () => {
    startNew();
    setDraft("");
    setThreadId(FRESH_CONVERSATION);
    setShouldResume(false);
  };

  const submit = (text: string) => {
    if (!text.trim()) return;
    setDraft("");
    send(text, false);
  };

  // The settled outcome, announced once the stream stops. Kept separate from the streaming
  // narration below so the two live regions never talk over each other.
  const settledCount = visible.length + outsideVisible.length;
  /**
   * A Stop that gave the previous answer back whole — nothing of the abandoned turn arrived, and
   * what is on screen is the finished answer it was going to replace (F6-06). Distinct from a Stop
   * that kept a half-streamed partial, which is unranked, unfloored and says so.
   */
  const keptWholeAnswer = stopped && arrived === 0 && settledAnswer && settledCount > 0;
  // Counted off the SCREEN, not off the wire: the line claims what a reader can count, and the
  // eligibility filter and the stopped-turn floor both remove cards the server did send.
  const footnoteText = footnote(reason, settledCount, paged);
  // What a stopped turn says, in one place: the sr-only live region and the on-screen card must
  // not be able to disagree, and the count is the turn's OWN — cards held over from the previous
  // answer are still on screen and are explicitly not what arrived before the user stopped.
  // …and it says what those picks ARE: unranked. Ranking happens at `done`, so cards from a turn
  // that never got there are in arrival order with whatever score they had when the stream ended.
  // Presenting them as an answer — a ranked, floored, finished one — is the claim this sentence
  // withdraws, and the reason the sub-floor ones above are moved to the tray rather than shown.
  const stoppedMessage =
    arrived === 0
      ? "Stopped before any picks arrived."
      : `Stopped — ${arrived} ${plural(arrived, "pick")} arrived before you stopped. Unranked — stopped before Reel finished.`;
  const settled = streaming
    ? ""
    : status === "error"
      ? "Search hit a snag."
      : status === "blocked"
        ? "That question is still being answered."
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
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-2xl font-light text-fa-frost-bright flex items-center gap-2">
            <Sparkles className="h-5 w-5 text-fa-frost" /> Ask Reel
          </h1>
          {/* Threads are a thing you can have more than one of, now that the server keeps them.
              `+ New` is the way OUT of a conversation — without it the only way to stop refining
              one thread was to reload and hope, since a reload resumes rather than resets. */}
          <div className="flex items-center gap-2">
            <button
              type="button"
              data-testid="askreel-new"
              onClick={beginFreshThread}
              disabled={streaming}
              className="fa-caption inline-flex min-h-11 min-w-11 items-center justify-center gap-1.5 rounded-full border border-fa-frost/25 px-4 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/50 hover:bg-fa-frost/10 disabled:opacity-40 transition-colors"
            >
              <Plus className="h-3.5 w-3.5" /> New
            </button>
            <ConversationSwitcher
              currentId={conversationId}
              // On the same terms as `+ New` beside it. F6-01: this was the one control that could
              // walk away from a running turn, and doing so bound the in-flight answer to the
              // thread the user had just arrived in.
              disabled={streaming}
              onOpen={(id) => {
                // Opening a thread makes it this tab's thread, on the same terms as starting one
                // here — so the next reload comes back to it rather than to whatever the account
                // touched last. `seedStarted` is cleared because the guard it holds is about a
                // question this visit asked, and switching threads is the user saying otherwise.
                seedStarted.current = false;
                storeConversationId(id);
                setThreadId(id);
                setShouldResume(true);
              }}
              onCurrentDeleted={beginFreshThread}
            />
          </div>
        </div>
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
            <UserBubble key={i} text={turn.text} />
          ) : (
            <div key={i} className="space-y-2 max-w-[85%]">
              <div className="flex items-start gap-3" data-testid="askreel-reply">
                <Sparkles className="h-5 w-5 text-fa-frost shrink-0 mt-1" />
                <p className="fa-body text-fa-frost-bright">
                  <span className="sr-only">Reel said</span>
                  {turn.text}
                  {/* The server rewrote this sentence before sending it, and says so on the frame.
                      Marking the bubble is the cheap half of the honesty: a reader scanning a
                      thread can see which answers were constrained without re-reading them. */}
                  {turn.conflict && (
                    <span
                      data-testid="askreel-reply-conflict"
                      className="fa-caption ml-2 inline-flex items-center rounded-full border border-fa-warning/40 px-2 py-0.5 align-middle text-fa-warning"
                    >
                      under your conditions
                    </span>
                  )}
                </p>
              </div>
              {i === lastAssistantIndex && conflictMessage && (
                <ConflictNotice message={conflictMessage} terms={conflictTerms} />
              )}
            </div>
          ),
        )}

        {/* SB-03. The last question of a resumed thread may never have been answered — the user
            pressed Stop, or the turn crashed — and the server correctly replays the last COMPLETED
            answer instead. Without this row the page read "You said: something surreal and
            dreamlike" and then, directly under it, someone else's answer, with nothing anywhere
            saying the question had gone unanswered. It sits at the END of the transcript because
            that is where the question it is about is. */}
        {resumedTail?.status === "failed" && (
          <div className="flex flex-wrap items-center gap-3" data-testid="askreel-failed-turn">
            <p className="fa-caption text-fa-frost-dim">{failedTurnMessage(resumedTail.failureCode)}</p>
            {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
          </div>
        )}
        {/* …and the other half of the same fact: the turn is not finished, it is still running.
            Nothing to retry and nothing to apologise for — the page just has to find out, which is
            what the poll above does. */}
        {resumedTail?.status === "pending" && (
          <p className="fa-caption text-fa-frost-dim" data-testid="askreel-pending-turn">
            {watchExpired
              ? "That one is taking a while. Ask again to start over."
              : "Still working on that one…"}
          </p>
        )}
      </div>

      {/* The seam a resume lands on.
          Returning to a thread should feel like never having left it, and the one thing that
          honestly cannot be hidden is WHEN you left: a grid restored in silence reads as a live
          answer to a question you have not asked this visit. Dated, so the answer's age is on
          screen rather than inferred from the cards. `removed` rides with it because a card that
          silently disappears between two views of the same answer is indistinguishable from a bug
          — it went because the account has since acted on it, and saying so is cheap. F4-10: the
          note named two of the five interactions that cause this ("watched or rated") and the one
          that actually fired was the third, so a user who had pressed Save was told they had
          watched something they had not. The three a person does deliberately are named; the
          other two (dropped, not-interested) take the card away on the spot and need no
          explanation a reload later.

          The date comes from `lastCardsAt`, which is when the thread last stored CARDS — falling
          back to `lastTurnAt`, which is the only stamp a transcript-only thread has. Older servers
          send neither for such a thread, and it then falls through undated to the
          `askreel-transcript-only` card below, which states the reason instead of a time.

          …and WHICH question the grid answers, when that is not the last one asked. A thread whose
          newest turn was stopped or crashed replays the last COMPLETED answer, so without naming it
          the seam let a perfectly good grid read as the answer to a question that never got one
          (SB-03). Said only in that case: on an ordinary resume the grid answers the question
          directly above it and naming it would be noise. */}
      {status === "resumed" && (lastCardsAt ?? lastTurnAt) && (
        <div className="flex flex-wrap items-baseline gap-x-2" data-testid="askreel-seam">
          <p className="fa-caption text-fa-frost-dim">
            Picked up where you left off · {relTime((lastCardsAt ?? lastTurnAt) as string)}
          </p>
          {resumedTail && answeredQuery && <SeamQuestion question={answeredQuery} />}
          {/* "from this answer", because the count is not the grid's alone.
              `removed` is the eligibility re-filter's total across BOTH stored lists — the picks
              and the below-floor tray — and the tray is now restored beside them, so a note
              sitting next to a grid of six and reading "2 picks are gone" invited exactly one
              reading: count the grid, find six, and conclude two of the eight it should hold are
              missing. Two may have gone from under the fence. Naming the answer rather than the
              grid is the whole of the fix; the cause is unchanged and is the same for both. */}
          {removed > 0 && (
            <p className="fa-caption text-fa-frost-dim/70" data-testid="askreel-removed">
              {/* `’` and not `&apos;`: every other sentence on this page uses the typographic
                  apostrophe, and the escape rendered a straight quote in the middle of a paragraph
                  whose neighbours are curly. */}
              {removed} {plural(removed, "pick")} {removed === 1 ? "is" : "are"} gone from this answer — you’ve since watched, rated or saved {removed === 1 ? "it" : "them"}.
            </p>
          )}
        </div>
      )}

      {/* Where the page scrolls to when a turn starts (F6-02). Zero-height on purpose — it marks
          the boundary between "what you said" and "what Reel is doing about it", and everything
          below it is the live turn. `scroll-mt` clears the sticky header, which would otherwise
          sit on top of the banner the scroll exists to reveal. */}
      <div ref={liveRef} className="scroll-mt-20" aria-hidden data-testid="askreel-live-anchor" />

      {/* 409 — the same question is already being answered, here or on another device. Nothing on
          screen is wrong, so nothing on screen is cleared; this is not the error card, because
          nothing failed. "Check again" re-sends the SAME turn id, which either takes the 409 again
          or comes back as the finished answer — asking afresh would run and bill it twice. */}
      {status === "blocked" && (
        <div
          className="fa-card p-4 flex flex-wrap items-center justify-center gap-3 text-center"
          data-testid="askreel-in-progress"
        >
          <p className="fa-caption text-fa-frost-dim">
            That question is already being answered — give it a moment.
          </p>
          <RetryButton onRetry={retryTurn} disabled={streaming} label="Check again" />
        </div>
      )}

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
            className="fa-caption inline-flex min-h-11 min-w-11 shrink-0 items-center justify-center gap-1.5 rounded-full border border-fa-frost/30 px-4 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/60 hover:bg-fa-frost/10 transition-colors"
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
                className="fa-caption inline-flex min-h-11 min-w-11 items-center justify-center rounded-full border border-fa-frost/25 px-4 text-fa-frost hover:text-fa-frost-bright hover:border-fa-frost/50 hover:bg-fa-frost/10 transition-colors"
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
              className="fa-caption inline-flex min-h-11 min-w-11 items-center justify-center rounded-full border border-fa-frost/20 px-4 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/40 disabled:opacity-40 transition-colors"
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
          because the alternative is a thin answer with no explanation — the user asked for
          something their own saved exclusions rule out, and only the server can see both halves.

          This is the FALLBACK position. When Reel's reply is on screen the notice renders directly
          under it (see `lastAssistantIndex`), attached to the sentence it explains; here is where
          it goes when there is no reply to attach to — a conflict frame that arrives before the
          model's, or a resumed thread whose assistant row was empty. */}
      {conflictMessage && lastAssistantIndex < 0 && (
        <ConflictNotice message={conflictMessage} terms={conflictTerms} />
      )}

      {/* A stopped turn is a state, not an absence. Pressing Stop before the first card used to
          leave `status: "idle"` over a transcript, which every branch below reads as "nothing to
          say" — so the page rendered a void: no cards, no message, no retry, and no way back to
          the answer that asking the question had already cleared. This card is the way back, and
          it sits ABOVE the results so that whatever did arrive (and whatever is being held from
          the previous answer) stays where it is instead of being replaced by an explanation. */}
      {status === "stopped" && (
        <div
          ref={settledRef}
          className="fa-card p-4 flex flex-wrap items-center justify-center gap-3 text-center scroll-mt-20"
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
      {status === "error" && errorReason === "conversation" ? (
        // 404. The thread this tab remembered is gone — deleted from another device, or past
        // retention. It is NOT a crash and it is NOT a fork: the id has been forgotten, so the next
        // question opens a new thread, and saying which of those happened is the whole difference
        // between "Reel is broken" and "that conversation ended".
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-3" data-testid="askreel-conversation-gone">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-body text-fa-frost-bright">That thread isn’t here any more</p>
          <p className="fa-caption text-fa-frost-dim">
            It was deleted, or it aged out. Asking again starts a new one.
          </p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      ) : status === "error" && cards.length === 0 ? (
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-3" data-testid="semantic-error">
          <AlertTriangle className="h-6 w-6 text-fa-warning mx-auto" />
          <p className="fa-body text-fa-frost-bright">Search hit a snag</p>
          <p className="fa-caption text-fa-frost-dim">Reel couldn’t reach the search engine. It’s usually momentary.</p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} />}
        </div>
      ) : status === "resumed" && lastCardsAt == null && resumedTail == null && visible.length === 0 && outsideVisible.length === 0 ? (
        // A resumed thread is NOT an empty answer. Restoring a transcript used to land on
        // "No matches for that one" under the user's own question — the page reporting a verdict
        // on a search it never ran.
        //
        // The `lastCardsAt == null` gate is half of what makes this copy true, and F4-04 is the
        // other half arriving late. Before the turn record there were no stored cards at all, so
        // this was the only branch a resume could ever reach; gating on `lastCardsAt` was supposed
        // to close that, and did not, because a turn that FAILED before storing anything also
        // leaves `lastCardsAt` null. A seven-minute-old thread whose only answer was aborted got
        // the failure row and this card on one screen: two "Ask again" buttons under two mutually
        // exclusive explanations of the same empty grid.
        //
        // `resumedTail == null` is "the last assistant row completed" — the one state in which
        // "there are no stored cards" really does mean the thread predates them. A failed or
        // pending tail has its own row in the transcript, which is the correct account and is
        // already on screen.
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-2" data-testid="askreel-transcript-only">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-caption text-fa-frost-dim">
            Transcript only — this thread predates saved results. Ask again to bring it back.
          </p>
          {canRetry && <RetryButton onRetry={reask} disabled={streaming} label="Ask again" />}
        </div>
      ) : status === "resumed" && lastCardsAt != null && visible.length === 0 && outsideVisible.length === 0 ? (
        // A thread that DID store cards and has none to show: every one of them was dropped by the
        // eligibility re-filter, which is a fact about the person, not about the thread's age.
        //
        // `lastCardsAt != null` is now stated rather than inherited. It used to be implied by the
        // branch above having already taken every null case; since F4-04 narrowed that branch, a
        // failed-before-storing thread would otherwise fall through to here and be told it had
        // watched or rated away an answer that was never found.
        <div className="fa-card p-8 text-center max-w-xl mx-auto space-y-2" data-testid="askreel-all-removed">
          <Sparkles className="h-6 w-6 text-fa-frost-dim mx-auto" />
          <p className="fa-caption text-fa-frost-dim">
            You’ve watched or rated everything this thread found. Ask again for a fresh set.
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
          {/* …and the Stop's own seam (F6-06). The hold ends when the turn does, so this grid is no
              longer "previous" and is no longer dimmed — it is the answer, whole, at full contrast,
              with its tray and its footnote back. What it still owes the reader is WHICH question
              it answers, because the one directly above it in the transcript is the one that was
              abandoned. */}
          {keptWholeAnswer && pageQuery && (
            <p className="fa-caption text-fa-frost-dim" data-testid="askreel-kept-answer">
              Still your answer to “{pageQuery}” — the question you stopped never replaced it.
            </p>
          )}
          <div
            className={`grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-4 transition-opacity ${holdingPrevious ? "opacity-40" : ""}`}
            data-testid="semantic-results"
          >
            {visible.map((card) => (
              <ResultCard key={card.titleId} card={card} onSaveTapped={setSavePending} />
            ))}
            {Array.from({ length: pendingSlots }, (_, i) => (
              <CardSkeleton key={`slot-${i}`} />
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
                  <ResultCard key={card.titleId} card={card} onSaveTapped={setSavePending} />
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
          {heldBack > 0 && (
            <details
              className={`border-t border-fa-frost/10 pt-5 transition-opacity ${holdingPrevious ? "opacity-40" : ""}`}
              data-testid="askreel-below-floor"
            >
              {/* `min-h-11` and padding rather than `flex`: a `display: flex` summary loses its
                  own disclosure marker, and the triangle is the affordance that says the block
                  opens. The box still measures 44 px, which is what a thumb needs. */}
              <summary
                data-testid="askreel-below-floor-toggle"
                className="fa-body min-h-11 cursor-pointer py-3 text-fa-frost-dim marker:text-fa-frost/40 hover:text-fa-frost-bright"
              >
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
                    <ResultCard key={card.titleId} card={card} onSaveTapped={setSavePending} />
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
          it") instead of silently spending.

          …and it is offered only over an answer a page can legitimately extend. `pageable` is the
          hook's own rule (see `canPage`), so the control and `loadMore` cannot disagree: a held
          previous answer, a stopped turn, a failure and a 409 all render nothing here. R1-04 was
          this button sitting under a HELD answer after a Stop — pressing it replayed the question
          that was never answered and silently adopted the earlier question's cards as page one.
          `appending` keeps it on screen, disabled, for the length of its own page. */}
      {visible.length > 0 && (pageable || appending || exhausted) && (
        <div className="flex flex-col items-center gap-2">
          {(pageable || appending) && (
            <button
              type="button"
              data-testid="askreel-more"
              onClick={loadMore}
              disabled={streaming || appending}
              className="fa-caption inline-flex min-h-11 min-w-11 items-center justify-center rounded-full border border-fa-frost/30 px-4 text-fa-frost-bright hover:border-fa-frost/60 hover:bg-fa-frost/10 disabled:opacity-40 transition-colors"
            >
              {appending ? "Finding more like these…" : "Show me more"}
            </button>
          )}
          {exhausted && !appending && (
            <p className="fa-caption text-fa-frost-dim text-center">
              That&apos;s everything on this thread — refine it above for a different angle.
            </p>
          )}
        </div>
      )}

      {/* The provenance footnote ("12 picks ranked for you, from 50 pulled"). Only ever shown
          alongside actual results — on an empty answer it was a second, contradictory message
          under the empty state.

          On a PAGED answer the server's sentence is about one turn and the grid is several, which
          is R1-03: "24 picks ranked for you, from 60 pulled" sat under 37 cards. `footnote` is
          what reconciles the two — see it for why neither sentence alone can be true here. */}
      {footnoteText && showsFootnote(status, appending, settledCount, settledAnswer) && (
        <p className="fa-caption text-fa-frost-dim text-center" data-testid="askreel-footnote">{footnoteText}</p>
      )}

      {/* A failure that arrives while cards are already on screen (a page that blew up) must not
          wipe the reel — it gets a quiet inline retry instead of the full-bleed error card. NOT for
          a 404: that has its own card above, and both firing put two "Ask again" buttons under two
          different explanations of the same thing. */}
      {status === "error" && errorReason !== "conversation" && cards.length > 0 && (
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

      {justSaved && (
        <UndoToast
          id={justSaved.titleId}
          testId="askreel-save-toast"
          message={`Saved “${justSaved.name}” — it’s on your Saved list.`}
          onUndo={() => {
            if (justSaved.tmdbId != null) {
              unsave({ titleId: justSaved.titleId, mediaType: justSaved.mediaType.toLowerCase(), tmdbId: justSaved.tmdbId });
            }
            setSavePending(null);
          }}
          onExpire={() => setSavePending(null)}
        />
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
              className={`inline-flex min-h-11 min-w-11 items-center justify-center rounded-full transition-colors disabled:opacity-40 ${
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
            className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-full bg-fa-frost/20 text-fa-frost-bright hover:bg-fa-frost/30 disabled:opacity-40 transition-colors"
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
 * One question the user asked, capped at six lines.
 *
 * F6-02's other half. The bubble has no maximum height, so a 1 000-character question — the length
 * the server accepts and the history keeps — renders ~1 100 px tall and hides the entire answer
 * below it. Six lines is enough to recognise a question you just asked; the rest is one tap away,
 * and the toggle is offered only when there IS a rest, measured rather than inferred from the
 * character count (the same 400 characters are four lines wide and eight narrow).
 *
 * The latch matters: expanding removes the clamp, so a re-measure would find no overflow and take
 * the "Show less" away with it. Measured only while collapsed.
 */
function UserBubble({ text }: { text: string }) {
  const textRef = useRef<HTMLSpanElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [clampable, setClampable] = useState(false);

  useLayoutEffect(() => {
    const el = textRef.current;
    if (!el || expanded) return;
    setClampable(bubbleOverflows(el.scrollHeight, el.clientHeight));
  }, [text, expanded]);

  return (
    <div className="flex flex-col items-end gap-1">
      <p
        className="fa-body bg-fa-frost/15 text-fa-frost-bright rounded-2xl rounded-br-sm px-4 py-2 max-w-[80%]"
        data-testid="askreel-user-bubble"
      >
        <span className="sr-only">You said</span>
        {/* The two classes are exclusive rather than combined: `line-clamp-6` sets
            `display: -webkit-box`, so a `block` sitting beside it can win on stylesheet order and
            silently un-clamp the bubble. */}
        <span ref={textRef} className={expanded ? "block whitespace-pre-wrap" : "whitespace-pre-wrap line-clamp-6"}>
          {text}
        </span>
      </p>
      {clampable && (
        <button
          type="button"
          data-testid="askreel-bubble-toggle"
          aria-expanded={expanded}
          onClick={() => setExpanded((v) => !v)}
          className="fa-caption inline-flex min-h-11 min-w-11 items-center justify-center rounded-full border border-fa-frost/20 px-4 text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/40 transition-colors"
        >
          {expanded ? "Show less" : "Show more"}
        </button>
      )}
    </div>
  );
}

/**
 * Which question the restored grid answers — one line of it.
 *
 * F6-08: the seam interpolated `answeredQuery` raw, so a resumed thread whose last question was
 * the 1 000 characters the server accepts rendered the whole thing as one caption. At 375 the seam
 * alone was taller than the viewport, on the single element whose entire job is to be glanceable.
 *
 * Clamped rather than truncated, and that distinction is the whole design: the full question stays
 * in the DOM and on the `title`, so a hover, a screen reader and a copy-paste all still get the
 * real thing, and one tap opens it in place. Truncating in JavaScript would have to guess at a
 * character count that is four lines wide and eight narrow — the same reason {@link UserBubble}
 * measures instead of counting.
 *
 * A `button` rather than a `p` with a handler: it is a real control, it needs to be reachable from
 * the keyboard, and `aria-expanded` is what tells a screen-reader user that there is more.
 */
function SeamQuestion({ question }: { question: string }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <button
      type="button"
      data-testid="askreel-seam-answers"
      title={question}
      aria-expanded={expanded}
      onClick={() => setExpanded((v) => !v)}
      className={`fa-caption max-w-full text-left text-fa-frost-dim/70 hover:text-fa-frost-dim transition-colors ${
        expanded ? "" : "truncate"
      }`}
    >
      These answer “{question}”.
    </button>
  );
}

/**
 * The collision notice: the server's sentence, and the terms it is about.
 *
 * The prose is server-written and rendered verbatim — it knows both halves (what was asked and
 * what the account has saved) and the page knows neither. The term list beside it is the page's
 * one addition, and it earns its place by being DATA rather than prose: it is the scannable,
 * actionable part, it sits next to the pointer at Conditions, and it is the only thing on screen
 * that can name an avoid-keyword collision ("anime", "musical") — those carry an empty
 * `excludedGenres`, so before `excludedKeywords` reached the client there was nothing to name.
 */
function ConflictNotice({ message, terms }: { message: string; terms: string[] }) {
  return (
    <div className="fa-card border-fa-warning/30 p-4 flex items-start gap-3" data-testid="askreel-conflict">
      <AlertTriangle className="h-4 w-4 text-fa-warning shrink-0 mt-0.5" />
      <div className="space-y-1">
        <p className="fa-caption text-fa-frost-bright">{message}</p>
        {terms.length > 0 && (
          <p className="fa-caption text-fa-frost-dim/80" data-testid="askreel-conflict-terms">
            Rules out {listPhrase(terms)}.
          </p>
        )}
      </div>
    </div>
  );
}

/**
 * One Ask Reel result card — identical for clean picks and the outside-request channel (the only
 * difference between the two is WHERE they render and what their server-written `why` says).
 */
function ResultCard({ card, onSaveTapped }: { card: AskCard; onSaveTapped: (card: AskCard) => void }) {
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
          /* The save tap, observed rather than owned. `CardQuickActions` belongs to the rec
             surfaces and fires the same three actions everywhere; only Ask Reel needs to know
             that a save happened HERE, because only here does the card vanish out of an answer
             the page is still explaining in a footnote. `display: contents` so the wrapper adds
             no box — the overlay stays positioned against the card, not against this span. */
          <span style={{ display: "contents" }} onClickCapture={(e) => {
            if ((e.target as Element).closest?.('[data-testid="quick-save"]')) onSaveTapped(card);
          }}>
            <CardQuickActions
              titleId={card.titleId}
              mediaType={card.mediaType}
              tmdbId={card.tmdbId}
              name={card.name}
            />
          </span>
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
 * A card-shaped hole in the grid, at the size a card will fill.
 *
 * Poster, name and year — deliberately not the why-line, which arrives with the rerank and which a
 * streaming card does not have either. `aria-hidden` because it says nothing: the live region and
 * the banner's narration are what tell a screen-reader user the turn is working.
 */
function CardSkeleton() {
  return (
    <div aria-hidden data-testid="askreel-skeleton">
      <div className="reel-poster reel-shimmer" />
      <p className="mt-2 fa-body select-none">&nbsp;</p>
      <p className="fa-caption select-none">&nbsp;</p>
    </div>
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
      className="fa-caption inline-flex min-h-11 min-w-11 items-center justify-center gap-1.5 rounded-full border border-fa-frost/30 px-4 text-fa-frost-bright hover:border-fa-frost/60 hover:bg-fa-frost/10 disabled:opacity-40 transition-colors"
    >
      <RotateCcw className="h-3.5 w-3.5" /> {label}
    </button>
  );
}
