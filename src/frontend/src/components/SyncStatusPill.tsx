import { useSelector } from "react-redux";
import type { RootState } from "../store";
import { relTime } from "../lib/format";
import { derivePill, type PillTone, type PillView } from "../lib/syncPill";
import { rememberOauthState } from "../lib/traktOauth";
import { useGetSyncStatusQuery, useStartTraktAuthMutation, type SyncStatus } from "../store/api";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "./ui/tooltip";

const DOT: Record<PillTone, string> = {
  ok: "bg-fa-success",
  active: "bg-fa-frost",
  warn: "bg-fa-warning",
  danger: "bg-fa-danger",
};

const LABEL_TONE: Record<PillTone, string> = {
  ok: "text-fa-frost-dim",
  active: "text-fa-frost",
  warn: "text-fa-warning",
  danger: "text-fa-danger",
};

/**
 * Header status. Every word comes from `derivePill` (lib/syncPill.ts), which is where the rules
 * live: health outranks progress, only work the user started or is waiting on shows as progress,
 * the cross-reference pass is shown by name with no denominator, and a failing or retrying job is
 * a warning with its reason — never a cheerful "100%".
 *
 * The visible pill is the tooltip trigger; a separate visually-hidden `role="status"` region
 * carries the label so a screen reader hears state changes without the percentage chatter.
 */
export default function SyncStatusPill() {
  const sync = useSelector((s: RootState) => s.syncUi);
  // Subscribes without its own interval: RealtimeSync owns the cadence for every subscriber.
  const { data: status, isError } = useGetSyncStatusQuery();
  const view = derivePill({ status, statusFailed: isError, live: sync.live, summary: sync.summary });

  return (
    <>
      <span role="status" aria-live="polite" aria-atomic="true" className="sr-only" data-testid="sync-announce">
        {view ? `Sync status: ${view.label}` : ""}
      </span>
      {view && <PillBody view={view} status={status} />}
    </>
  );
}

function ReconnectTraktButton({ label }: { label: string }) {
  const [startTraktAuth, { isLoading, isError }] = useStartTraktAuthMutation();

  const reconnect = async () => {
    try {
      const { authorizeUrl, state } = await startTraktAuth().unwrap();
      rememberOauthState(state);
      window.location.assign(authorizeUrl);
    } catch {
      // isError renders the failure inline below; nothing awaits this handler.
    }
  };

  return (
    <button
      type="button"
      onClick={reconnect}
      disabled={isLoading}
      data-testid="sync-reconnect"
      className="inline-flex items-center gap-2 rounded-full border border-fa-danger/60 bg-fa-glass px-3 py-1 fa-caption text-fa-danger transition hover:border-fa-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-danger/60 disabled:opacity-60"
    >
      <span className="h-1.5 w-1.5 rounded-full shrink-0 bg-fa-danger" aria-hidden />
      <span data-testid="sync-health-label" className="truncate max-w-[10rem]">
        {isLoading ? "Opening Trakt…" : isError ? "Couldn't reach Trakt — retry" : label}
      </span>
    </button>
  );
}

function PillBody({ view, status }: { view: PillView; status: SyncStatus | undefined }) {
  const trigger =
    view.mode === "active" ? (
      <span
        tabIndex={0}
        data-testid="sync-pill"
        data-tone={view.tone}
        className="inline-flex flex-col gap-1 rounded-2xl border border-fa-edge bg-fa-glass px-3 py-1.5 cursor-default focus-visible:outline-none focus-visible:border-fa-frost/40 min-w-[9rem] max-w-[16rem]"
      >
        <span className={`inline-flex items-center gap-2 fa-caption ${LABEL_TONE[view.tone]}`}>
          <span
            className={`fa-status-orb shrink-0 ${view.tone === "warn" ? "fa-status-failed" : "fa-status-running"}`}
            aria-hidden
          />
          <span className="truncate" data-testid="sync-pill-label">{view.label}</span>
          {view.pct != null && (
            <span className="tabular-nums ml-auto shrink-0 text-fa-frost">{Math.round(view.pct)}%</span>
          )}
        </span>
        {view.pct != null && (
          <span className="block h-1 rounded-full bg-fa-ink-3 overflow-hidden" aria-hidden>
            <span
              className="block h-full rounded-full bg-gradient-to-r from-fa-frost to-fa-frost-bright transition-[width] duration-500 ease-out"
              style={{ width: `${Math.max(4, view.pct)}%` }}
            />
          </span>
        )}
      </span>
    ) : view.reconnect ? (
      <ReconnectTraktButton label={view.label} />
    ) : (
      <span
        tabIndex={0}
        data-testid="sync-health"
        data-health={view.tone}
        className="inline-flex items-center gap-2 rounded-full border border-fa-edge bg-fa-glass px-3 py-1 fa-caption cursor-default focus-visible:outline-none focus-visible:border-fa-frost/40"
      >
        <span className={`h-1.5 w-1.5 rounded-full shrink-0 ${DOT[view.tone]}`} aria-hidden />
        <span
          data-testid="sync-health-label"
          className={`truncate ${LABEL_TONE[view.tone]} ${
            // A problem is worth the header space on a phone; an all-clear is a dot.
            view.tone === "ok" ? "hidden sm:inline max-w-[22rem]" : "max-w-[10rem]"
          }`}
        >
          {view.label}
        </span>
      </span>
    );

  return (
    <TooltipProvider delayDuration={150}>
      <Tooltip>
        <TooltipTrigger asChild>{trigger}</TooltipTrigger>
        <TooltipContent side="bottom" align="end" className="max-w-72 px-3 py-2.5" data-testid="sync-tooltip">
          <p className="fa-section-title mb-1.5">{view.title}</p>
          {view.count && (
            <p className="fa-caption text-fa-frost mb-1.5 tabular-nums">
              {view.count.current.toLocaleString("en-US")} / {view.count.total.toLocaleString("en-US")}
            </p>
          )}
          {view.lines.length > 0 && (
            <ul className="space-y-1">
              {view.lines.map((line) => (
                <li key={line} className="fa-caption text-fa-frost-dim flex gap-1.5">
                  <span aria-hidden className="text-fa-frost/50">·</span>
                  {line}
                </li>
              ))}
            </ul>
          )}
          {/* Everything else that needs attention, so a warning never hides behind the headline. */}
          {view.issues.length > 1 && (
            <ul className="mt-2 pt-2 border-t border-fa-edge/50 space-y-1" data-testid="sync-issues">
              {view.issues.slice(1).map((issue) => (
                <li key={issue.label} className="fa-caption flex flex-col">
                  <span className={issue.tone === "danger" ? "text-fa-danger" : "text-fa-warning"}>{issue.label}</span>
                  <span className="text-fa-frost-dim">{issue.detail}</span>
                </li>
              ))}
            </ul>
          )}
          {status && (
            <dl className="mt-2 pt-2 border-t border-fa-edge/50 space-y-1 fa-caption">
              <div className="flex justify-between gap-4">
                <dt className="text-fa-frost-dim">Library</dt>
                <dd className="text-fa-frost tabular-nums">{relTime(status.lastDeltaSyncAt)}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-fa-frost-dim">Trakt list</dt>
                <dd className="text-fa-frost tabular-nums">
                  {status.outboxDeadLetters > 0
                    ? <span className="text-fa-danger">{status.outboxDeadLetters.toLocaleString("en-US")} failed</span>
                    : status.outboxPending > 0
                      ? `${status.outboxPending.toLocaleString("en-US")} queued`
                      : `updated ${relTime(status.lastListSyncAt)}`}
                </dd>
              </div>
            </dl>
          )}
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
