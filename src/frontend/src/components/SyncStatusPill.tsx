import { useEffect, useRef, useState, type MouseEvent, type PointerEvent, type ReactNode } from "react";
import { useSelector } from "react-redux";
import { Info } from "lucide-react";
import type { RootState } from "../store";
import { relTime } from "../lib/format";
import { derivePill, type PillTone, type PillView } from "../lib/syncPill";
import { rememberOauthState } from "../lib/traktOauth";
import { useGetSyncStatusQuery, useStartTraktAuthMutation, type SyncStatus } from "../store/api";
import { Button } from "./ui/button";
import { Popover, PopoverAnchor, PopoverContent, PopoverTrigger } from "./ui/popover";

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
 * The visible pill is a button that opens the details popover — tap on a phone, click or Enter
 * anywhere, and a mouse hover peeks at it. It was a hover/focus tooltip on a bare `<span>`, which
 * a touch screen can never open, so a phone showed "Cross-reference failed" with no way to learn
 * why (R1-20). A separate visually-hidden `role="status"` region carries the label so a screen
 * reader hears state changes without the percentage chatter.
 */
export default function SyncStatusPill() {
  const sync = useSelector((s: RootState) => s.syncUi);
  // Subscribes without its own interval: RealtimeSync owns the cadence for every subscriber.
  const { data: status, isError } = useGetSyncStatusQuery();
  const view = derivePill({
    status,
    statusFailed: isError,
    live: sync.live,
    summary: sync.summary,
    summaryAt: sync.summaryAt,
  });

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
    <Button
      type="button"
      variant="destructive"
      size="sm"
      onClick={reconnect}
      disabled={isLoading}
      data-testid="sync-reconnect"
      className="rounded-full px-3 py-1 text-caption focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-danger/60"
    >
      <span className="h-1.5 w-1.5 rounded-full shrink-0 bg-fa-danger" aria-hidden />
      <span data-testid="sync-health-label" className="truncate max-w-[10rem]">
        {isLoading ? "Opening Trakt…" : isError ? "Couldn't reach Trakt — retry" : label}
      </span>
    </Button>
  );
}

/** Hover-to-peek delay before a peeked popover closes, so the pointer can cross the gap to it. */
const PEEK_CLOSE_MS = 150;

/**
 * Open state for the details popover. A tap, click or Enter opens it and it stays open until
 * dismissed (outside tap, Escape, or the trigger again). A mouse hover only peeks: it opens without
 * moving focus and closes when the pointer leaves the pill and the popover, unless a click pinned
 * it. Touch and pen pointers never peek — on a phone a tap is the only way in, and it is enough.
 */
function useDetailsPopover() {
  const [open, setOpen] = useState(false);
  const [pinned, setPinned] = useState(false);
  const closeTimer = useRef<number | null>(null);

  const cancelClose = () => {
    if (closeTimer.current != null) window.clearTimeout(closeTimer.current);
    closeTimer.current = null;
  };
  useEffect(() => cancelClose, []);

  return {
    open,
    pinned,
    onOpenChange(next: boolean) {
      cancelClose();
      setOpen(next);
      setPinned(next);
    },
    /** A click on the trigger while a hover peek shows the popover pins it instead of closing it. */
    onTriggerClick(event: MouseEvent) {
      if (open && !pinned) {
        event.preventDefault();
        setPinned(true);
      }
    },
    hover: {
      onPointerEnter(event: PointerEvent) {
        if (event.pointerType !== "mouse") return;
        cancelClose();
        setOpen(true);
      },
      onPointerLeave(event: PointerEvent) {
        if (event.pointerType !== "mouse" || pinned) return;
        cancelClose();
        closeTimer.current = window.setTimeout(() => setOpen(false), PEEK_CLOSE_MS);
      },
    },
  };
}

function PillBody({ view, status }: { view: PillView; status: SyncStatus | undefined }) {
  const details = useDetailsPopover();
  const triggerProps = { onClick: details.onTriggerClick, "aria-haspopup": "dialog" as const };

  let anchor: ReactNode;
  if (view.mode === "active") {
    anchor = (
      <PopoverTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          {...triggerProps}
          data-testid="sync-pill"
          data-tone={view.tone}
          className="inline-flex flex-col items-stretch justify-start gap-1 rounded-2xl border border-fa-edge bg-fa-glass px-3 py-1.5 font-normal text-left min-w-[9rem] max-w-[16rem] hover:bg-fa-glass focus-visible:outline-none focus-visible:border-fa-frost/40"
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
        </Button>
      </PopoverTrigger>
    );
  } else if (view.reconnect) {
    // The pill itself is the one-tap fix, so the details get their own small trigger beside it.
    anchor = (
      <span className="inline-flex items-center gap-1">
        <ReconnectTraktButton label={view.label} />
        <PopoverTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            {...triggerProps}
            aria-label="Why Trakt needs reconnecting"
            data-testid="sync-details"
            className="h-7 w-7 rounded-full text-fa-danger hover:text-fa-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-danger/60"
          >
            <Info className="h-4 w-4" aria-hidden />
          </Button>
        </PopoverTrigger>
      </span>
    );
  } else {
    anchor = (
      <PopoverTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          {...triggerProps}
          data-testid="sync-health"
          data-health={view.tone}
          className="inline-flex items-center gap-2 rounded-full border border-fa-edge bg-fa-glass px-3 py-1 text-caption font-normal hover:bg-fa-glass focus-visible:outline-none focus-visible:border-fa-frost/40"
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
          {/* The all-clear is a bare dot on a phone; the button still needs a name there. */}
          {view.tone === "ok" && <span className="sr-only sm:hidden">{view.label}</span>}
        </Button>
      </PopoverTrigger>
    );
  }

  return (
    <Popover open={details.open} onOpenChange={details.onOpenChange}>
      <PopoverAnchor asChild>
        <span className="inline-flex" {...details.hover}>
          {anchor}
        </span>
      </PopoverAnchor>
      <PopoverContent
        side="bottom"
        align="end"
        aria-label="Sync details"
        // A hover peek must not pull focus out of whatever the user was doing.
        onOpenAutoFocus={(event) => {
          if (!details.pinned) event.preventDefault();
        }}
        {...details.hover}
        className="w-auto max-w-72 px-3 py-2.5"
        data-testid="sync-tooltip"
      >
        <p className="fa-section-title mb-1.5">{view.title}</p>
        {view.count && (
          <p className="fa-caption text-fa-frost mb-1.5 tabular-nums">
            {view.count.current.toLocaleString("en-US")} / {view.count.total.toLocaleString("en-US")}
          </p>
        )}
        {view.lines.length > 0 && (
          <ul className="space-y-1">
            {view.lines.map((line, index) => (
              <li key={`${index}:${line}`} className="fa-caption text-fa-frost-dim flex gap-1.5">
                <span aria-hidden className="text-3">·</span>
                {line}
              </li>
            ))}
          </ul>
        )}
        {/* Everything else that needs attention, so a warning never hides behind the headline. */}
        {view.issues.length > 1 && (
          <ul className="mt-2 pt-2 border-t border-fa-edge/50 space-y-1" data-testid="sync-issues">
            {/* Two issues can share a label (two unknown job kinds both read "A background job
                failed"), so the key carries the position too. */}
            {view.issues.slice(1).map((issue, index) => (
              <li key={`${index}:${issue.label}`} className="fa-caption flex flex-col">
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
      </PopoverContent>
    </Popover>
  );
}
