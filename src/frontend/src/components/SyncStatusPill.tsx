import { useSelector } from "react-redux";
import type { RootState } from "../store";
import { syncExplainer } from "../lib/syncCopy";
import { useGetSyncStatusQuery } from "../store/api";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "./ui/tooltip";

/** Compact "2m ago" / "3h ago" / "just now" formatting for the last-sync stamps. */
function relTime(iso: string | null | undefined): string {
  if (!iso) return "never";
  const deltaMs = Date.now() - new Date(iso).getTime();
  const minutes = Math.floor(deltaMs / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

/**
 * Header sync surface. During an active job it shows a live, determinate progress bar (what's
 * syncing / where / out-of-what). When idle it stays visible as a health chip — last library +
 * list sync times, queued/failed write-backs, and connection issues — so a problem is legible at
 * a glance instead of hiding behind a silent spinner. Hover reveals the detail (and any error).
 */
export default function SyncStatusPill() {
  const sync = useSelector((s: RootState) => s.syncUi);
  const { data: status } = useGetSyncStatusQuery();

  // ── Active job: live progress ────────────────────────────────────────────────────────────
  if (sync.active) {
    const explainer = syncExplainer(sync.kind);
    const pct = sync.pct;
    const hasCount = sync.current != null && sync.total != null;
    return (
      <TooltipProvider delayDuration={150}>
        <Tooltip>
          <TooltipTrigger asChild>
            <span
              tabIndex={0}
              data-testid="sync-pill"
              className="inline-flex flex-col gap-1 rounded-2xl border border-fa-edge bg-fa-glass px-3 py-1.5 cursor-default focus:outline-none focus:border-fa-frost/40 min-w-[9rem] max-w-[16rem]"
            >
              <span className="inline-flex items-center gap-2 fa-caption text-fa-frost-dim">
                <span className="fa-status-orb fa-status-running shrink-0" />
                <span className="truncate">{sync.label ?? "Syncing"}</span>
                {pct != null && (
                  <span className="tabular-nums ml-auto shrink-0 text-fa-frost">{Math.round(pct)}%</span>
                )}
              </span>
              {pct != null && (
                <span className="block h-1 rounded-full bg-fa-ink-3 overflow-hidden" aria-hidden>
                  <span
                    className="block h-full rounded-full bg-gradient-to-r from-fa-frost to-fa-frost-bright transition-[width] duration-500 ease-out"
                    style={{ width: `${Math.max(4, Math.min(100, pct))}%` }}
                  />
                </span>
              )}
            </span>
          </TooltipTrigger>
          <TooltipContent side="bottom" align="end" className="max-w-64 px-3 py-2.5">
            <p className="fa-section-title mb-1.5">{explainer.title}</p>
            {hasCount && (
              <p className="fa-caption text-fa-frost mb-1.5 tabular-nums">
                {sync.current!.toLocaleString()} / {sync.total!.toLocaleString()}
              </p>
            )}
            <ul className="space-y-1">
              {explainer.lines.map((line) => (
                <li key={line} className="fa-caption text-fa-frost-dim flex gap-1.5">
                  <span aria-hidden className="text-fa-frost/50">·</span>
                  {line}
                </li>
              ))}
            </ul>
          </TooltipContent>
        </Tooltip>
      </TooltipProvider>
    );
  }

  // ── Idle: persistent health chip ─────────────────────────────────────────────────────────
  if (!status) return null;

  const connectionBad = status.connectionStatus !== "Active";
  const deadLetters = status.outboxDeadLetters > 0;
  const queued = status.outboxPending > 0;
  const health: "ok" | "warn" | "error" =
    connectionBad || deadLetters ? "error" : queued ? "warn" : "ok";

  const dot =
    health === "error" ? "bg-fa-danger" : health === "warn" ? "bg-amber-400" : "bg-fa-success";
  const label =
    health === "error"
      ? connectionBad
        ? "Reconnect Trakt"
        : "List sync failed"
      : health === "warn"
        ? "List syncing…"
        : `Synced ${relTime(status.lastDeltaSyncAt)}`;

  return (
    <TooltipProvider delayDuration={150}>
      <Tooltip>
        <TooltipTrigger asChild>
          <span
            tabIndex={0}
            data-testid="sync-health"
            data-health={health}
            className="inline-flex items-center gap-2 rounded-full border border-fa-edge bg-fa-glass px-3 py-1 fa-caption text-fa-frost-dim cursor-default focus:outline-none focus:border-fa-frost/40"
          >
            <span className={`h-1.5 w-1.5 rounded-full shrink-0 ${dot}`} aria-hidden />
            <span className="hidden sm:inline truncate max-w-[10rem]">{label}</span>
          </span>
        </TooltipTrigger>
        <TooltipContent side="bottom" align="end" className="max-w-72 px-3 py-2.5">
          <p className="fa-section-title mb-1.5">
            {health === "error" ? "Sync needs attention" : health === "warn" ? "Sync in progress" : "Everything's synced"}
          </p>
          <dl className="space-y-1 fa-caption">
            <div className="flex justify-between gap-4">
              <dt className="text-fa-frost-dim">Library</dt>
              <dd className="text-fa-frost tabular-nums">{relTime(status.lastDeltaSyncAt)}</dd>
            </div>
            <div className="flex justify-between gap-4">
              <dt className="text-fa-frost-dim">Trakt list</dt>
              <dd className="text-fa-frost tabular-nums">
                {queued || deadLetters ? (
                  <span className={deadLetters ? "text-fa-danger" : "text-amber-400"}>
                    {status.outboxPending > 0 && `${status.outboxPending} queued`}
                    {deadLetters && `${status.outboxPending > 0 ? " · " : ""}${status.outboxDeadLetters} failed`}
                  </span>
                ) : (
                  `updated ${relTime(status.lastListSyncAt)}`
                )}
              </dd>
            </div>
            {connectionBad && (
              <div className="flex justify-between gap-4">
                <dt className="text-fa-frost-dim">Connection</dt>
                <dd className="text-fa-danger">{status.connectionStatus} — re-link needed</dd>
              </div>
            )}
          </dl>
          {status.outboxLastError && (health === "error" || health === "warn") && (
            <p className="fa-caption text-fa-frost-dim mt-2 pt-2 border-t border-fa-edge/50">
              <span className="text-fa-frost/60">Last error:</span> {status.outboxLastError}
            </p>
          )}
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
