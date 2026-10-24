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
  failureCopy,
  needsReconnect,
  pollAdvance,
  ratingsNeededCopy,
  reset,
  type IngestKind,
  type Insight,
  type RatingsFloor,
} from "../store/onboardingSlice";

/**
 * The status API v2 fields this page reads. `SyncStatus` (store/api/sync.ts, WS-9's) does not
 * declare them yet; until it does they are read as optional here, and an older backend that omits
 * them simply never reaches the needs-ratings branch.
 */
type StatusWithReason = SyncStatus & {
  degradedReason?: string | null;
  notEnoughRatings?: RatingsFloor | null;
};
import type { AppDispatch, RootState } from "../store";

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
      <div className="fa-stat-value">{display.toLocaleString()}</div>
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

  // `needsRatings` keeps the stream and the poll alive: rating enough titles elsewhere lets the
  // next train run, and the poll is what moves this page on when it does.
  const streaming = onboarding.stage !== "reveal" && onboarding.stage !== "failed";
  useOnboardingStream(streaming);

  // Authoritative fallback: the status poll can advance a stalled show (poll wins).
  const { data: syncStatus, refetch: refetchStatus } = useGetSyncStatusQuery(undefined, {
    pollingInterval: streaming ? 15_000 : 0,
  });
  useEffect(() => {
    if (!syncStatus) return;
    const status = syncStatus as StatusWithReason;
    dispatch(
      pollAdvance({
        pipelineStage: status.pipelineStage,
        degradedReason: status.degradedReason ?? null,
        notEnoughRatings: status.notEnoughRatings ?? null,
      })
    );
  }, [syncStatus, dispatch]);

  // "Try again" asks the server for a sync and restarts the show, rather than reloading the page
  // into the same stopped state (what the old "Retry sync" did).
  const retry = async () => {
    await triggerSync().unwrap().catch(() => undefined);
    dispatch(reset());
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
    <div className="min-h-screen flex items-center justify-center px-6 relative overflow-hidden">
      <div className={`w-full max-w-2xl space-y-8 ${melting ? "reel-melt" : ""}`}>
        <div className="text-center space-y-2">
          <div className="text-3xl font-light tracking-wide text-fa-frost-bright">Reel</div>
          {/* The display name, as everywhere else in the app — the slug ("deanmar09") is an
              identifier, not a greeting. The slug is the last resort for a profile with no name. */}
          {session && (
            <p className="fa-caption text-fa-frost-dim" data-testid="onboarding-greeting">
              Hello, {session.displayName?.trim() || onboarding.traktUser || session.traktSlug}
            </p>
          )}
          <p className="fa-body text-fa-frost" data-testid="stage-copy">
            {STAGE_COPY[onboarding.stage]}
          </p>
          {onboarding.stalled && streaming && (
            <p className="fa-caption text-fa-frost-dim animate-pulse" data-testid="stall-notice">
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
          <p className="fa-caption text-fa-frost-dim text-center">
            {onboarding.modelProgress.phase}
            {onboarding.modelProgress.total > 0 &&
              ` · ${onboarding.modelProgress.processed.toLocaleString()} / ${onboarding.modelProgress.total.toLocaleString()}`}
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
              <p className="fa-caption text-fa-frost-dim">
                Model trained on your ratings — every pick comes with the reason and a predicted rating.
              </p>
            </div>
            <button onClick={enterApp} className="fa-button-primary text-base px-6 py-3" data-testid="enter-app">
              See tonight&apos;s picks
            </button>
          </div>
        )}

        {onboarding.stage === "needsRatings" && (
          <div className="fa-card border-fa-frost/30 px-6 py-5 text-center space-y-3 reel-rise" data-testid="needs-ratings">
            <p className="fa-body text-fa-frost-bright" data-testid="needs-ratings-copy">
              {ratingsNeededCopy(onboarding.ratingsFloor)}
            </p>
            <p className="fa-caption text-fa-frost-dim">
              Your model learns from what you&apos;ve rated. Titles you&apos;ve watched and never scored are the
              quickest to rate — this page picks up as soon as there are enough.
            </p>
            <Link to="/rate" className="fa-button-primary inline-flex" data-testid="needs-ratings-rate">
              Rate a few titles
            </Link>
          </div>
        )}

        {onboarding.stage === "failed" && (
          <div role="alert" className="fa-card border-fa-danger/40 px-6 py-4 text-center space-y-3" data-testid="onboarding-failed">
            <p className="fa-body text-fa-danger" data-testid="onboarding-failed-reason">
              {failureCopy(onboarding.degradedReason, onboarding.error)}
            </p>
            {needsReconnect(onboarding.degradedReason) ? (
              <button onClick={reconnect} disabled={reconnecting} className="fa-button-primary" data-testid="onboarding-reconnect">
                {reconnecting ? "Opening Trakt…" : "Reconnect Trakt"}
              </button>
            ) : (
              <button onClick={retry} disabled={retrying} className="fa-button" data-testid="onboarding-retry">
                {retrying ? "Starting…" : "Try again"}
              </button>
            )}
            {reconnectFailed && (
              <p className="fa-caption text-fa-danger">Couldn&apos;t reach Trakt. Try again in a moment.</p>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
