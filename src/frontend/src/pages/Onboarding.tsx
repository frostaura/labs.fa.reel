import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { useDispatch, useSelector } from "react-redux";
import { BarChart3, CalendarRange, Clapperboard, Sparkles, Tags, UserRound } from "lucide-react";
import {
  api,
  useGetSessionQuery,
  useGetSyncStatusQuery,
  useStartTraktAuthMutation,
  useTriggerSyncMutation,
  useUpdateSettingsMutation,
  type SyncStatus,
} from "../store/api";
import { useOnboardingStream } from "../lib/onboardingStream";
import { rememberOauthState } from "../lib/traktOauth";
import { useCountUp } from "../lib/useCountUp";
import {
  FIRST_MODEL_TIMING,
  awaitingModelTiming,
  failureCopy,
  needsReconnect,
  pollAdvance,
  ratingsNeededCopy,
  retryStarted,
  type IngestKind,
  type Insight,
  type StatusSnapshot,
} from "../store/onboardingSlice";
import { saveFailureMessage } from "../components/ui/save-outcome";
import type { AppDispatch, RootState } from "../store";
import { Button } from "../components/ui/button";
import { COARSE_TAP } from "../lib/tapTarget";

/**
 * The status API v2 fields this page reads. `SyncStatus` (store/api/sync.ts, WS-9's) does not
 * declare them yet; until it does they are read as optional here, and an older backend that omits
 * them simply never reaches the needs-ratings or awaiting-model branches.
 */
type StatusWithReason = SyncStatus & Omit<StatusSnapshot, "pipelineStage">;

/** What the last "Try again" came to, said under the failure card. */
type RetryNote = { kind: "refused"; text: string } | { kind: "queued" };

const COUNTER_LABELS: Record<IngestKind, string> = {
  movies: "Movies",
  shows: "Shows",
  episodes: "Episodes",
  ratings: "Ratings",
};

const STAGE_COPY: Record<string, string> = {
  connecting: "Linking your Trakt profile…",
  ingesting: "Pulling in everything you've ever watched…",
  profiling: "Reading your taste…",
  fitting: "Teaching your model…",
  reveal: "Your taste DNA is ready.",
  needsRatings: "Almost there — Reel needs a few more of your ratings.",
  awaitingModel: "You've rated enough — your model is next.",
  failed: "Building your profile stopped.",
};

const INSIGHT_ICONS: Record<Insight["kind"], typeof Sparkles> = {
  library: Clapperboard,
  genre: Tags,
  era: CalendarRange,
  creator: UserRound,
  tone: Sparkles,
  stat: BarChart3,
};

function Ticker({ kind, value }: { kind: IngestKind; value: number }) {
  const display = useCountUp(value);
  return (
    <div className="fa-card px-4 py-3 text-center reel-rise">
      <div className="fa-stat-value">{display.toLocaleString("en-US")}</div>
      <div className="fa-stat-label">{COUNTER_LABELS[kind]}</div>
    </div>
  );
}

/**
 * The live build-up show: the wait IS the wow. Every number and insight on screen is a real
 * byproduct of the pipeline — never theatre. The reveal melts the frost veil into the feed.
 */
export default function Onboarding() {
  const dispatch = useDispatch<AppDispatch>();
  const navigate = useNavigate();
  const onboarding = useSelector((s: RootState) => s.onboarding);
  const { data: session } = useGetSessionQuery();
  const [updateSettings] = useUpdateSettingsMutation();
  const [triggerSync, { isLoading: retrying }] = useTriggerSyncMutation();
  const [startTraktAuth, { isLoading: reconnecting, isError: reconnectFailed }] = useStartTraktAuthMutation();
  const [melting, setMelting] = useState(false);
  const [retryNote, setRetryNote] = useState<RetryNote | null>(null);
  // A note about the last retry is stale once the show has got anywhere: drop it then (derived
  // during render, so it never paints over a later, unrelated stop). `connecting` is the restart
  // itself, so it keeps the note.
  if (retryNote && onboarding.stage !== "failed" && onboarding.stage !== "connecting") {
    setRetryNote(null);
  }

  // `needsRatings` and `awaitingModel` keep the stream and the poll alive: rating enough titles
  // elsewhere, or the nightly train, moves the show on, and the poll is what notices.
  const streaming = onboarding.stage !== "reveal" && onboarding.stage !== "failed";
  // After an accepted "Try again" the page keeps watching even if the server still reports the stop,
  // so the run it queued can lift the show when it gets further.
  const watching = streaming || retryNote?.kind === "queued";
  useOnboardingStream(watching);

  // Authoritative fallback: the status poll can advance a stalled show (poll wins). Keyed on the
  // response's timestamp, not its object: RTK hands back the SAME object for an unchanged payload,
  // and "still stopped, exactly as before" must re-apply after the show was restarted locally.
  const {
    data: syncStatus,
    fulfilledTimeStamp,
    refetch: refetchStatus,
  } = useGetSyncStatusQuery(undefined, { pollingInterval: watching ? 15_000 : 0 });
  useEffect(() => {
    if (!syncStatus) return;
    const status = syncStatus as StatusWithReason;
    dispatch(
      pollAdvance({
        pipelineStage: status.pipelineStage,
        degradedReason: status.degradedReason ?? null,
        notEnoughRatings: status.notEnoughRatings ?? null,
        lastFailures: status.lastFailures ?? null,
        retrain: status.retrain ?? null,
      })
    );
  }, [syncStatus, fulfilledTimeStamp, dispatch]);

  // "Try again" asks the server for a sync. Only an accepted request restarts the show — a refused
  // one keeps the stop on screen and says so, rather than faking progress — and the restarted show
  // is then put wherever the server's next status says it is.
  const retry = async () => {
    setRetryNote(null);
    try {
      await triggerSync().unwrap();
    } catch (error: unknown) {
      const reason = saveFailureMessage(error);
      setRetryNote({
        kind: "refused",
        text: reason.kind === "rejected" ? `Couldn't start a sync: ${reason.text}` : "Couldn't start a sync — nothing changed. Try again in a moment.",
      });
      return;
    }
    setRetryNote({ kind: "queued" });
    dispatch(retryStarted());
    void refetchStatus();
  };

  const reconnect = async () => {
    try {
      const { authorizeUrl, state } = await startTraktAuth().unwrap();
      rememberOauthState(state);
      window.location.assign(authorizeUrl);
    } catch {
      // `reconnectFailed` renders the message below.
    }
  };

  // Prefetch the feed the moment the reveal is reachable — entry must feel instant.
  useEffect(() => {
    if (onboarding.stage === "fitting" || onboarding.stage === "reveal") {
      dispatch(api.util.prefetch("getFeed", undefined, { force: false }));
    }
  }, [onboarding.stage, dispatch]);

  const enterApp = async () => {
    setMelting(true);
    updateSettings({ onboarded: true });
    window.setTimeout(() => navigate("/home"), 620);
  };

  const counters = Object.entries(onboarding.counters) as [IngestKind, number][];

  return (
    // `main` and the `h1`: this page renders outside the app shell, so it supplies its own landmark
    // and heading (axe: landmark-one-main, page-has-heading-one, region).
    <main className="min-h-screen flex items-center justify-center px-6 relative overflow-hidden">
      <div className={`w-full max-w-2xl space-y-8 ${melting ? "reel-melt" : ""}`}>
        <div className="text-center space-y-2">
          <h1 className="text-3xl font-light tracking-wide text-fa-frost-bright">Reel</h1>
          {/* The display name, as everywhere else in the app — the slug ("deanmar09") is an
              identifier, not a greeting. The slug is the last resort for a profile with no name. */}
          {session && (
            <p className="fa-caption text-3" data-testid="onboarding-greeting">
              Hello, {session.displayName?.trim() || onboarding.traktUser || session.traktSlug}
            </p>
          )}
          <p className="fa-body text-fa-frost" data-testid="stage-copy">
            {STAGE_COPY[onboarding.stage]}
          </p>
          {onboarding.stalled && streaming && (
            <p className="fa-caption text-3 animate-pulse" data-testid="stall-notice">
              Still working — large libraries take a minute…
            </p>
          )}
        </div>

        {counters.length > 0 && (
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3" data-testid="tickers">
            {counters.map(([kind, found]) => (
              <Ticker key={kind} kind={kind} value={found} />
            ))}
          </div>
        )}

        {onboarding.insights.length > 0 && (
          <div className="space-y-2" data-testid="insights">
            {onboarding.insights.slice(-4).map((insight) => {
              const Icon = INSIGHT_ICONS[insight.kind] ?? Sparkles;
              return (
                <div key={insight.id} className="fa-card px-4 py-2.5 flex items-center gap-3 reel-rise">
                  <Icon className="h-4 w-4 text-fa-frost shrink-0" />
                  <p className="fa-body text-fa-frost/90">{insight.text}</p>
                </div>
              );
            })}
          </div>
        )}

        {onboarding.stage === "fitting" && onboarding.modelProgress && (
          <p className="fa-caption text-3 text-center">
            {onboarding.modelProgress.phase}
            {onboarding.modelProgress.total > 0 &&
              ` · ${onboarding.modelProgress.processed.toLocaleString("en-US")} / ${onboarding.modelProgress.total.toLocaleString("en-US")}`}
          </p>
        )}

        {streaming && (
          <div className="relative h-1 overflow-hidden rounded-full bg-fa-glass">
            <div className="absolute inset-y-0 rounded-full bg-fa-frost/60 fa-progress-slide" />
          </div>
        )}

        {onboarding.stage === "reveal" && !melting && (
          <div className="text-center space-y-4 reel-rise" data-testid="reveal">
            {/* No headline here — the stage copy above already announces "Your taste DNA is
                ready.", and this card sits directly beneath it. */}
            <div className="fa-card px-8 py-6 space-y-3 border-fa-frost/30">
              <Sparkles className="h-6 w-6 text-fa-frost mx-auto" />
              <p className="fa-caption text-3">
                Model trained on your ratings — every pick comes with the reason and a predicted rating.
              </p>
            </div>
            <Button variant="primary" size="lg" onClick={enterApp} className={`text-base py-3 ${COARSE_TAP}`} data-testid="enter-app">
              See tonight&apos;s picks
            </Button>
          </div>
        )}

        {onboarding.stage === "needsRatings" && (
          <div className="fa-card border-fa-frost/30 px-6 py-5 text-center space-y-3 reel-rise" data-testid="needs-ratings">
            <p className="fa-body text-fa-frost-bright" data-testid="needs-ratings-copy">
              {ratingsNeededCopy(onboarding.ratingsFloor)}
            </p>
            <p className="fa-caption text-3">
              Your model learns from what you&apos;ve rated, and titles you&apos;ve watched and never scored are the
              quickest to rate. Once there are enough, {FIRST_MODEL_TIMING} — this page moves on by
              itself when it&apos;s ready.
            </p>
            <Link to="/rate" className={`fa-button-primary inline-flex ${COARSE_TAP}`} data-testid="needs-ratings-rate">
              Rate a few titles
            </Link>
          </div>
        )}

        {onboarding.stage === "awaitingModel" && (
          <div className="fa-card border-fa-frost/30 px-6 py-5 text-center space-y-3 reel-rise" data-testid="awaiting-model">
            <p className="fa-body text-fa-frost-bright" data-testid="awaiting-model-copy">
              {awaitingModelTiming(onboarding.hasActiveModel)}.
            </p>
            <p className="fa-caption text-3">
              Nothing to do but wait — this page moves on by itself when the model is ready, so you can close it and
              come back. More ratings make the {onboarding.hasActiveModel ? "next" : "first"} model sharper.
            </p>
            <Link to="/rate" className={`fa-button inline-flex ${COARSE_TAP}`} data-testid="awaiting-model-rate">
              Rate more while you wait
            </Link>
          </div>
        )}

        {onboarding.stage === "failed" && (
          <div role="alert" className="fa-card border-fa-danger/40 px-6 py-4 text-center space-y-3" data-testid="onboarding-failed">
            <p className="fa-body text-fa-danger" data-testid="onboarding-failed-reason">
              {failureCopy(onboarding.degradedReason, onboarding.error)}
            </p>
            {needsReconnect(onboarding.degradedReason) ? (
              <Button variant="primary" onClick={reconnect} disabled={reconnecting} className={COARSE_TAP} data-testid="onboarding-reconnect">
                {reconnecting ? "Opening Trakt…" : "Reconnect Trakt"}
              </Button>
            ) : (
              <Button onClick={retry} disabled={retrying} className={COARSE_TAP} data-testid="onboarding-retry">
                {retrying ? "Starting…" : "Try again"}
              </Button>
            )}
            {reconnectFailed && (
              <p className="fa-caption text-fa-danger">Couldn&apos;t reach Trakt. Try again in a moment.</p>
            )}
            {retryNote?.kind === "refused" && (
              <p className="fa-caption text-fa-danger" data-testid="onboarding-retry-refused">
                {retryNote.text}
              </p>
            )}
            {retryNote?.kind === "queued" && (
              <p className="fa-caption text-3" data-testid="onboarding-retry-queued">
                Reel queued a fresh sync, but the server still reports this stop. Nothing is lost — this page moves on
                by itself if the sync gets further.
              </p>
            )}
          </div>
        )}
      </div>
    </main>
  );
}
