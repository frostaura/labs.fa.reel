import { MD_BREAKPOINT_PX, useMinWidth } from "../lib/useMinWidth";
import { useEffect, useRef, useState } from "react";
import { useBrowseFilter } from "../lib/useBrowseFilter";
import { useDispatch, useSelector } from "react-redux";
import { useLocation, useNavigate } from "react-router";
import { Plus, Sparkles } from "lucide-react";
import {
  api, useGetConversationQuery, useGetFiltersQuery, useGetTurnQuery, useUnsaveForLaterMutation,
} from "../store/api";
import ChatErrorBoundary from "../components/ChatErrorBoundary";
import ConversationSwitcher from "../components/search/ConversationSwitcher";
import SearchConditions from "../components/search/SearchConditions";
import UndoToast from "../components/search/UndoToast";
import AnswerStrip from "../components/askReel/AnswerStrip";
import Composer from "../components/askReel/Composer";
import Transcript from "../components/askReel/Transcript";
import LiveSlot from "../components/askReel/LiveSlot";
import PillButton from "../components/askReel/PillButton";
import { MoodPresets, RefineBar } from "../components/askReel/Starters";
import { deriveAskView } from "../components/askReel/view";
import { type BrowseFilter } from "../lib/browseFilter";
import { selectRemovedKeys } from "../store/removedTitlesSlice";
import { refineChips, showMoodPresets } from "../lib/askReel/copy";
import { scrollTargetFor, turnSettled } from "../lib/askReel/reducer";
import {
  claimHandoff, FRESH_CONVERSATION, mediaFilterFor, nextBootstrap, NO_RESUME, PENDING_TURN_BUDGET_MS,
  PENDING_TURN_POLL_MS, prefersReducedMotion, readStoredConversationId, resumeTarget, storeConversationId,
  useAskReelChat,
} from "../lib/askReel/useAskReelChat";
import { useSpeech } from "../lib/useSpeech";
import type { AskCard } from "../lib/askReelStream";
import type { ConversationPayload } from "../lib/askReel/transcript";

/**
 * "Ask Reel" — a conversational discovery agent. The first turn comes from the search box; from
 * there it's a chat: Reel replies, streams cards that match the request, and refines across turns
 * ("lighter", "more like the first one") — never repeating a title. Each answer's cards sit under
 * the reply that produced it (chat UI v2); the page orchestrates the hook, the transcript, the
 * answer strip and the composer (WS-2 task 5's split).
 */

/** Maps the browse toggle to the wire values the server understands ([] = no constraint). */
function mediaTypesFor(filter: BrowseFilter): string[] {
  if (filter.media === "movie") return ["movie"];
  if (filter.media === "show") return ["tv"];
  return [];
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
  // since. Router state is not free of that either: it lives in the history ENTRY, so it outlives
  // its navigation, which is what {@link claimHandoff} is for. The turn id rides along from the
  // search box as this hand-off's name and as the turn's idempotency key, so a send that genuinely
  // does go out twice is replayed from the stored turn rather than billed a second time.
  const askState = location.state as { ask?: string; askTurnId?: string } | null;
  const seed = askState?.ask ?? "";
  const seedTurnId = askState?.askTurnId;

  const [filter, setFilter] = useBrowseFilter("search");
  const [draft, setDraft] = useState("");
  const chat = useAskReelChat();
  const {
    turns, status, send, resume, startNew, reask, setMediaTypes,
    resumedTail, conversationId, hasLocalTurn, plan,
  } = chat;
  const storeDispatch = useDispatch();
  // The bar itself is not on the wire and should not be: it is the account's own standing
  // condition, the same `minPredictedRating` PickConditionsSummary reads two elements up the page.
  // Reading it from the one source keeps the tray's number and the conditions summary from ever
  // disagreeing about what the user set.
  const { data: accountFilters } = useGetFiltersQuery();
  // The long placeholder wraps to two clipped lines in a 44 px box on a phone; the examples
  // are a desktop nicety, not the instruction.
  const composerWide = useMinWidth(MD_BREAKPOINT_PX);
  const speech = useSpeech((text) => send(text, false));

  // The media toggle is per-device and persists across visits, so a resumed thread starts with a
  // filter the server has never been told about — and `reask` (the retry, and the toggle itself)
  // would then re-ask under the wrong constraint. Pushed on mount, and declared ABOVE the
  // bootstrap effect so it lands before the seeded turn goes out.
  useEffect(() => {
    setMediaTypes(mediaTypesFor(filter));
  }, [filter, setMediaTypes]);

  // A search from the header box starts a fresh conversation; landing on /search with nothing to
  // ask resumes this tab's thread. The seed is CONSUMED — the router state is cleared BEFORE the
  // turn goes out — and `nextBootstrap` holds the "already handled" rule (unit-tested), keyed on the TURN
  // ID rather than the text, so asking the same phrase twice is two exchanges and a double-invoke
  // of the same one is not. The ref survives both a re-render and StrictMode's mount → unmount →
  // remount (which re-runs effects but keeps refs), so the second pass reads `handled` and decides
  // nothing; the shared turn id is the backstop for a send that does go out twice anyway.
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
    // `claimHandoff` is the liveness test AND the consumption: it answers true at most once per
    // hand-off, and only for one this document minted. A question restored from the history entry
    // — a reload, a Back — therefore resumes the thread instead of being asked a second time.
    const { action, handled, clear } = nextBootstrap(
      seedTurnId ?? seed, handledSeed.current, () => claimHandoff(seedTurnId),
    );
    handledSeed.current = handled;
    // FIRST, not last. `navigate` writes through to `history.replaceState` synchronously, so
    // clearing before the request goes out leaves no window in which a reload could inherit the
    // question and ask it a second time. Clearing afterwards left exactly that window open.
    if (clear) navigate(".", { replace: true, state: null });
    if (action === "seed" && seed) {
      seedStarted.current = true;
      send(seed, true, seedTurnId);
      return;
    }
    if (action === "resume") setShouldResume(true);
  }, [seed, seedTurnId, send, navigate]);

  // Three answers, and one of them is "do not ask": a tab that pressed `+ New` stored a marker
  // rather than clearing its id, so the reload lands on the cold start instead of falling through
  // to `/conversations/latest` and resurrecting the thread the user had just left (SB-02).
  const resumeFrom = resumeTarget(threadId);
  /**
   * Whether this page is asking for a conversation at all — `+ New`, and a deleted current thread,
   * both say no. Named once and read twice: RTK Query's `data` survives a skip (it is "the latest
   * result this hook has seen", not "the result for this argument"), so an effect that reads it
   * without re-checking the skip condition is reading the thread the page has just left. That put
   * the transcript of a DELETED thread back on screen one render after `+ New` had cleared it.
   */
  const resumeSkipped = !shouldResume || resumeFrom === NO_RESUME;
  const { data: resumed, error: resumeError, refetch: refetchConversation } = useGetConversationQuery(resumeFrom, {
    skip: resumeSkipped,
    // R3-03's backstop. The invalidation below keeps the cache warm while the tab is here, but a
    // turn answered in ANOTHER tab, or a fetch that never landed because the user navigated away
    // mid-flight, leaves this entry describing a thread that has moved on — and the entry outlives
    // the route by `keepUnusedDataFor`. A mount is the one moment the page has nothing of its own
    // to lose by asking again.
    refetchOnMountOrArgChange: true,
  });

  /**
   * R3-03. A completed `/ask` streams outside RTK Query, so nothing invalidated the conversation it
   * had just changed: leave `/search` by the header mark or the tab bar and come back with Back,
   * and the route re-mounted onto the payload from the LAST fetch — 21 + 21 cards on screen came
   * back as 14 + 10, under the footnote of a turn two pages earlier, and it did not self-correct.
   *
   * Invalidated on every exit from a turn rather than on `done` alone — see {@link turnSettled} —
   * and by the bare tag, which reaches the thread list and every `getConversation` entry whatever
   * argument keyed it (a thread resumed through the deprecated `latest` alias is cached under that
   * word, not under its id).
   */
  const settledFrom = useRef(status);
  useEffect(() => {
    const previous = settledFrom.current;
    settledFrom.current = status;
    if (turnSettled(previous, status)) storeDispatch(api.util.invalidateTags(["Conversation"]));
  }, [status, storeDispatch]);

  /**
   * The payload this page has already decided about, and the argument it arrived under.
   *
   * Both halves are load-bearing. The ARGUMENT is what separates "the user asked for a different
   * thread" from "the fetch we just invalidated came back" — an explicit switch re-keys the query.
   * The PAYLOAD is what keeps the decision to one per payload: the effect below now depends on
   * more than the data (it has to read `hasLocalTurn`), so an unrelated re-render would otherwise
   * re-apply a payload still sitting in the cache — which resurrected the transcript of a thread
   * the user had just deleted, one render after `+ New` had cleared it.
   */
  const decided = useRef<{ arg: string; payload: ConversationPayload } | null>(null);

  useEffect(() => {
    // Guarded on `seedStarted`, not cancelled on cleanup: once a search has begun, a late-arriving
    // stored conversation must never replace it.
    if (!resumed || resumeSkipped || seedStarted.current) return;
    // One decision per payload. Anything already ruled on is inert however many times an unrelated
    // dependency re-fires this effect.
    const seen = decided.current;
    if (seen?.payload === resumed && seen.arg === resumeFrom) return;
    // …and once a turn of THIS visit has produced what is on screen, the reducer is ahead of the
    // payload. The invalidation above makes a refetch land while that answer is up, and applying it
    // would replace a live twenty-one-card answer with the server's account of it — correct data,
    // arriving as a state change nobody asked for, mid-read. A thread the user explicitly switched
    // to is a different argument and is never blocked by this.
    const reducerIsAhead = hasLocalTurn && seen?.arg === resumeFrom;
    decided.current = { arg: resumeFrom, payload: resumed };
    if (reducerIsAhead) return;
    resume(resumed);
    // The toggle and the grid have to agree about what produced what is under it — a restored
    // answer that ran under "Movies" beside a control reading "All" is a control that lies.
    const media = mediaFilterFor(resumed.mediaTypes);
    if (media !== filter.media) setFilter({ ...filter, media });
    // `filter` is deliberately NOT a dependency: this reconciles the toggle TO the payload once,
    // and re-running it on every toggle change would fight the user for control of it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resumed, resume, setFilter, hasLocalTurn, resumeFrom, resumeSkipped]);

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

  // The media filter is NOT applied on the client: it is sent upstream and the server narrows
  // discovery. Read as the array the store holds, so the save watcher below is a derivation.
  const removedKeys = useSelector(selectRemovedKeys);
  const streaming = status === "streaming";

  /**
   * F4-09. Saving a card takes it out of the answer instantly; the toast is raised on the tap and
   * confirmed by the CONSEQUENCE (the card entering the removed set once the server accepted the
   * save), so the undo is never offered for something that did not happen. A derivation, not an
   * effect: the toast is on screen exactly while the card it names is gone and undismissed.
   */
  const [savePending, setSavePending] = useState<AskCard | null>(null);
  const [unsave] = useUnsaveForLaterMutation();
  const justSaved = savePending && removedKeys.includes(savePending.titleId) ? savePending : null;

  // F6-02. Two anchors: "the live turn" and "what the Stop left you with". `scrollTargetFor`
  // decides which (if either) this status is about, so the page moves on the two transitions that
  // change what it is about and never under a reader.
  const liveRef = useRef<HTMLDivElement>(null);
  const settledRef = useRef<HTMLDivElement>(null);
  const scrollTarget = scrollTargetFor(status);
  useEffect(() => {
    if (!scrollTarget) return;
    const el = scrollTarget === "live" ? liveRef.current : settledRef.current;
    el?.scrollIntoView({ behavior: prefersReducedMotion() ? "auto" : "smooth", block: "start" });
  }, [scrollTarget]);

  // The bar is the account's own standing condition, read from the one source the conditions
  // summary reads, so the tray's number and the summary can never disagree.
  const view = deriveAskView(chat, { removedKeys, floorBar: accountFilters?.minPredictedRating });

  // Re-ask on a filter change rather than filtering locally — `reask`, not `send`, because the user
  // asked the question once and the toggle must not stamp another copy of their bubble.
  const changeFilter = (next: typeof filter) => {
    setFilter(next);
    setMediaTypes(mediaTypesFor(next));
    reask();
  };

  /**
   * `+ New`, and what a deleted current thread falls back to. The hook stores the
   * {@link FRESH_CONVERSATION} marker so a RELOAD lands on the cold start, and this visit's resume
   * state is cleared so a re-render cannot key the resume query back to the thread just left (SB-02).
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

  // Generated from the planner's reading and the account's conditions — never a direction a
  // standing rule rules out (see `refineChips`).
  const chips = refineChips({
    concepts: plan?.concepts ?? [],
    hasCards: view.visible.length > 0,
    excludedGenres: accountFilters?.excludeGenres ?? [],
    minYear: accountFilters?.minYear ?? null,
    maxYear: accountFilters?.maxYear ?? null,
  });

  const liveSlot = <LiveSlot chat={chat} view={view} settledRef={settledRef} />;

  return (
    // Bottom padding clears the composer AND, on a phone, the tab bar underneath it.
    <div className="space-y-5 pb-44 md:pb-32">
      <div>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-2xl font-light text-fa-frost-bright flex items-center gap-2">
            <Sparkles className="h-5 w-5 text-fa-frost" /> Ask Reel
          </h1>
          {/* `+ New` is the way OUT of a conversation; the switcher is the way back to another. */}
          <div className="flex items-center gap-2">
            <PillButton tone="quiet" data-testid="askreel-new" onClick={beginFreshThread} disabled={streaming}>
              <Plus className="h-3.5 w-3.5" aria-hidden /> New
            </PillButton>
            <ConversationSwitcher
              currentId={conversationId}
              // F6-01: walking away from a running turn bound the in-flight answer to the thread
              // just arrived in.
              disabled={streaming}
              onOpen={(id) => {
                seedStarted.current = false;
                storeConversationId(id);
                setThreadId(id);
                setShouldResume(true);
              }}
              onCurrentDeleted={beginFreshThread}
            />
          </div>
        </div>
        {/* Every standing condition applies to search too — named in full and editable from here
            (F6-17), and collapsed to one "N conditions" button on a phone (chat UI v2). */}
        <SearchConditions className="mt-2" browse={filter} onBrowseChange={changeFilter} />
      </div>

      {/* Always mounted so it is a live region by the time it has something to say. Announces the
          settled outcome only; the narration has its own region inside the banner. */}
      <p className="sr-only" role="status" aria-live="polite" data-testid="askreel-status">{view.announcement}</p>

      <Transcript
        chat={chat}
        view={view}
        liveSlot={liveSlot}
        answer={<AnswerStrip chat={chat} view={view} onSaveTapped={setSavePending} />}
        liveAnchor={liveRef}
        watchExpired={watchExpired}
        onSaveTapped={setSavePending}
      />

      {/* Cold-start mood presets — gone the moment a conversation exists. */}
      {showMoodPresets(turns.length, chat.cards.length + chat.outsideCards.length, streaming) && (
        <MoodPresets onPick={(preset) => send(preset)} />
      )}

      {/* Refine chips + media filter. Gated on the TRANSCRIPT, not on cards: a resumed thread with
          no cards still has a conversation to act on. */}
      {turns.length > 0 && (
        <RefineBar
          chips={chips}
          streaming={streaming}
          onChip={(chip) => send(chip, false)}
          filter={filter}
          onFilterChange={changeFilter}
        />
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

      <Composer draft={draft} onDraftChange={setDraft} onSubmit={submit} streaming={streaming} wide={composerWide} speech={speech} />
    </div>
  );
}
