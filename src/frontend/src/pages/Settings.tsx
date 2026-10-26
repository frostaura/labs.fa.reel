import { useEffect, useRef, useState } from "react";
import { Link, useLocation } from "react-router";
import { RefreshCw } from "lucide-react";
import PreferencesSection from "../components/settings/PreferencesSection";
import PreferenceTags from "../components/settings/PreferenceTags";
import BillingCard from "../components/settings/BillingCard";
import PushOptIn from "../components/settings/PushOptIn";
import LoadFailed from "../components/ui/load-failed";
import SaveStatus from "../components/ui/save-status";
import { useLeaveGuard } from "../components/settings/useLeaveGuard";
import { modelReadiness, pipelineSentence } from "../lib/modelReadiness";
import { relTime } from "../lib/format";
import { rememberOauthState } from "../lib/traktOauth";
import RegionPicker from "../components/settings/RegionPicker";
import {
  emailSaveMessage,
  saveFailureMessage,
  savedMessage,
  type SaveMessage,
} from "../components/ui/save-outcome";
import {
  useGetBillingStatusQuery,
  useGetFilterChipsQuery,
  useGetFiltersQuery,
  useGetSessionQuery,
  useGetSettingsQuery,
  useGetSyncStatusQuery,
  useStartTraktAuthMutation,
  useTriggerSyncMutation,
  useUpdateSettingsMutation,
  type SyncStatus,
} from "../store/api";
import { Button } from "../components/ui/button";
import { COARSE_TAP } from "../lib/tapTarget";

/**
 * Reconnect Trakt (H-2): the same sign-in the landing page starts, from the one screen a user with a
 * broken link actually visits. Prominent when the link is broken, quiet otherwise — it is also the
 * way to refresh a link that is technically active but stuck.
 */
function ReconnectTrakt({ sync }: { sync: SyncStatus | undefined }) {
  const [startTraktAuth, { isLoading, isError }] = useStartTraktAuthMutation();
  const broken =
    (sync?.connectionStatus != null && sync.connectionStatus !== "Active") || modelReadiness(sync)?.kind === "reconnect";

  const reconnect = async () => {
    try {
      const { authorizeUrl, state } = await startTraktAuth().unwrap();
      rememberOauthState(state);
      window.location.assign(authorizeUrl);
    } catch {
      // isError renders the failure below; nothing awaits this handler.
    }
  };

  return (
    <div className="space-y-1.5 border-t border-fa-edge/50 pt-3" data-testid="trakt-reconnect">
      {broken ? (
        <p className="fa-caption text-fa-danger" role="status" data-testid="trakt-connection-broken">
          Reel has lost access to your Trakt account, so nothing new comes across until you reconnect.
        </p>
      ) : (
        <p className="fa-caption text-fa-frost-dim">
          If picks stop following what you watch on Trakt, reconnecting signs you in to Trakt again. Nothing you&apos;ve
          rated is lost.
        </p>
      )}
      <Button
        variant={broken ? "primary" : "ghost"}
        onClick={reconnect}
        disabled={isLoading}
        className={COARSE_TAP}
        data-testid="trakt-reconnect-button"
      >
        {isLoading ? "Opening Trakt…" : "Reconnect Trakt"}
      </Button>
      {isError && (
        <p role="alert" className="fa-caption text-fa-danger" data-testid="trakt-reconnect-error">
          Couldn&apos;t reach Trakt&apos;s sign-in. Nothing changed — try again in a moment.
        </p>
      )}
    </div>
  );
}

/**
 * The stored email, shown and editable. It exists for Stripe receipts, so it is offered only while
 * billing is switched on — or while an address is already on file, which the user must always be
 * able to see and remove. The old card asked for an email "for when paid plans land": a promise,
 * not a feature (KILL-10).
 */
function EmailCapture({ billingConfigured }: { billingConfigured: boolean }) {
  const { data: settings, isError, isFetching, refetch } = useGetSettingsQuery();
  const [updateSettings, { isLoading: saving }] = useUpdateSettingsMutation();
  // What the last confirmed save stored, until the read catches up. The query hook keeps showing its
  // PREVIOUS result while the save's refetch is in flight — and for good if that refetch fails — so
  // without this the box flashed the old address back after "Saved." (and could stay on it).
  const [confirmed, setConfirmed] = useState<string | null>(null);
  const serverStored = settings?.email ?? "";
  if (confirmed !== null && serverStored === confirmed) setConfirmed(null);
  const stored = confirmed ?? serverStored;
  // The box holds the user's edit, or — until they type — whatever is on file. Deriving it this way
  // (rather than seeding state in an effect) means the stored address is in the box on first paint.
  const [edit, setEdit] = useState<string | null>(null);
  const email = edit ?? stored;
  const [status, setStatus] = useState<SaveMessage | null>(null);

  useEffect(() => {
    if (!status || status.persistent) return;
    const handle = window.setTimeout(() => setStatus(null), 2000);
    return () => window.clearTimeout(handle);
  }, [status]);

  const trimmed = email.trim();
  const changed = settings !== undefined && trimmed !== stored;
  const valid = trimmed.length === 0 || trimmed.includes("@");
  useLeaveGuard(changed, "your email address");

  if (isError && !settings) {
    return <LoadFailed what="your email settings" onRetry={() => void refetch()} retrying={isFetching} variant="inline" testId="email-load-error" />;
  }
  // Kept on screen while its own message shows, so removing the last stored address (which hides
  // the card when billing is off) still gets to say "Removed." before it goes.
  if (!settings || (!billingConfigured && stored.length === 0 && !status)) return null;

  const save = async () => {
    if (!changed || !valid) return;
    setStatus(null);
    try {
      const result = await updateSettings({ email: trimmed }).unwrap();
      const message = emailSaveMessage(trimmed, result.email);
      setStatus(message);
      if (message.tone === "success") {
        setConfirmed(result.email ?? "");
        setEdit(null);
      }
    } catch (error: unknown) {
      setStatus(saveFailureMessage(error));
    }
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    void save();
  };

  return (
    <section className="fa-card p-5 space-y-3" data-testid="email-capture">
      <h2 className="fa-section-title">Email</h2>
      <p className="fa-caption text-fa-frost-dim">
        {billingConfigured
          ? "Where your plan receipts go. Optional; never shared. Clear the box to remove it."
          : "On file from earlier. Reel sends nothing to it — paid plans are switched off. Clear the box to remove it."}
      </p>
      <form onSubmit={submit} className="flex flex-wrap items-center gap-2">
        <label htmlFor="settings-email" className="sr-only">
          Email address
        </label>
        <input
          id="settings-email"
          type="email"
          value={email}
          onChange={(e) => setEdit(e.target.value)}
          placeholder="you@example.com"
          className={`flex-1 min-w-0 rounded-full bg-fa-frost/10 px-4 py-2 fa-body text-fa-frost-bright placeholder:text-3 focus:outline-none focus:ring-1 focus:ring-fa-frost/40 ${COARSE_TAP}`}
          data-testid="email-input"
        />
        <Button type="submit" variant="primary" disabled={!changed || !valid || saving} className={COARSE_TAP} data-testid="email-save">
          {saving ? "Saving…" : trimmed.length === 0 && stored.length > 0 ? "Remove" : "Save"}
        </Button>
      </form>
      <SaveStatus message={status} onRetry={() => void save()} retrying={saving} testId="email-save-status" />
    </section>
  );
}

export default function Settings() {
  const { data: session } = useGetSessionQuery();
  const { data: sync } = useGetSyncStatusQuery();
  const { data: billing } = useGetBillingStatusQuery();
  const [triggerSync, { isLoading: syncing }] = useTriggerSyncMutation();
  const [updateSettings, { isLoading: savingRegion }] = useUpdateSettingsMutation();
  const [regionStatus, setRegionStatus] = useState<SaveMessage | null>(null);
  const [lastRegion, setLastRegion] = useState<string | null>(null);

  // `/settings#region` — where-to-watch's "edit region" link (FD-13's default shows the region with
  // an edit link) — lands on the region card, scrolled clear of the header and focused, so the next
  // Tab is the picker. The card is not data-dependent, so it exists on the first commit; the
  // picker's own input is not focused, because focusing it opens its list. The conditions editor
  // above it grows from a skeleton when its rules and chips land, which pushes the card down, so it
  // is scrolled to again once both have settled (the same cached queries — no extra request).
  const { hash } = useLocation();
  const regionRef = useRef<HTMLElement>(null);
  const filters = useGetFiltersQuery();
  const chips = useGetFilterChipsQuery();
  const conditionsSettled = !filters.isLoading && !chips.isLoading;
  useEffect(() => {
    if (hash !== "#region") return;
    const card = regionRef.current;
    if (!card) return;
    card.scrollIntoView({ block: "start" });
    card.focus({ preventScroll: true });
  }, [hash, conditionsSettled]);

  useEffect(() => {
    if (!regionStatus || regionStatus.persistent) return;
    const handle = window.setTimeout(() => setRegionStatus(null), 1500);
    return () => window.clearTimeout(handle);
  }, [regionStatus]);

  // Unwrapped: the select is bound to the session's region, so a failed save snaps the control back
  // to what is stored — and the message says so instead of flashing "Saved" over it.
  const handleRegionChange = async (region: string) => {
    setLastRegion(region);
    setRegionStatus(null);
    try {
      const result = await updateSettings({ region }).unwrap();
      setRegionStatus(
        result.region === region
          ? savedMessage("Saved.")
          : { ...saveFailureMessage({ status: 400 }), text: `${region} wasn't accepted — nothing changed.` },
      );
    } catch (error: unknown) {
      setRegionStatus(saveFailureMessage(error));
    }
  };

  return (
    // One column on mobile (source order preserved); a two-column card grid from lg up, where a
    // stack of narrow cards in a max-w-2xl gutter wasted the whole right half of the screen.
    // `lg:space-y-0` matters: space-y margins survive inside grid cells and would double the gap.
    <div className="max-w-2xl space-y-6 lg:max-w-5xl lg:grid lg:grid-cols-2 lg:gap-6 lg:space-y-0 lg:items-start">
      <h1 className="text-2xl font-light text-fa-frost-bright lg:col-span-2">Settings</h1>

      {/* Conditions first (H-6): they decide what every pick is drawn from, and they are what people
          come to this page to change. Wide, dense content that needs the full row. */}
      <div className="lg:col-span-2" data-testid="settings-conditions">
        <PreferencesSection />
      </div>

      <section className="fa-card p-5 space-y-4" data-testid="settings-trakt">
        <h2 className="fa-section-title">Trakt account</h2>
        <div className="flex items-center gap-3">
          {session?.avatarUrl && (
            <img src={session.avatarUrl} alt="" className="h-10 w-10 rounded-full object-cover" />
          )}
          <div className="min-w-0">
            <p className="fa-body text-fa-frost-bright">{session?.displayName}</p>
            <a
              href={`https://trakt.tv/users/${session?.traktSlug}`}
              target="_blank"
              rel="noreferrer"
              className={`fa-caption fa-link break-all inline-flex items-center ${COARSE_TAP}`}
            >
              trakt.tv/users/{session?.traktSlug}
            </a>
          </div>
        </div>
        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-fa-edge/50 pt-3">
          <div className="fa-caption text-fa-frost-dim min-w-0 flex-1">
            {/* A sentence, from the status that explains it (KILL-11): never the raw stage. */}
            <p className="text-fa-frost" data-testid="settings-pipeline">
              {pipelineSentence(sync) ?? "Checking what Reel is doing…"}
            </p>
            <p data-testid="settings-last-sync">
              {sync?.lastDeltaSyncAt ? `Last synced with Trakt ${relTime(sync.lastDeltaSyncAt)}.` : "Not synced with Trakt yet."}
            </p>
          </div>
          <Button onClick={() => triggerSync()} disabled={syncing} className={COARSE_TAP}>
            <RefreshCw className={`h-4 w-4 ${syncing ? "animate-spin" : ""}`} />
            Sync now
          </Button>
        </div>
        <ReconnectTrakt sync={sync} />
      </section>

      <section
        ref={regionRef}
        id="region"
        tabIndex={-1}
        aria-labelledby="settings-region-title"
        className="fa-card p-5 space-y-3 scroll-mt-20 focus:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
        data-testid="settings-region"
      >
        <h2 id="settings-region-title" className="fa-section-title">
          Region
        </h2>
        <p className="fa-caption text-fa-frost-dim">
          Where you watch. It decides which streaming, rental and purchase options Reel shows for each title.
        </p>
        <RegionPicker value={session?.region ?? null} onSelect={(code) => void handleRegionChange(code)} disabled={savingRegion} />
        <SaveStatus
          message={regionStatus}
          onRetry={lastRegion ? () => void handleRegionChange(lastRegion) : undefined}
          retrying={savingRegion}
          testId="region-save-status"
        />
      </section>

      {/* Router Link, not <a href> — a full document reload just to reach /rate threw away the
          store (and the settings the user may not have noticed were still saving). */}
      <Link to="/rate" className="fa-card p-5 flex items-center justify-between gap-3 hover:border-fa-frost/40 transition-colors" data-testid="sharpen-link">
        <div>
          <h2 className="fa-section-title">Sharpen your recommendations</h2>
          <p className="fa-caption text-fa-frost-dim">Rate a few popular titles you’ve seen — the model gets sharper.</p>
        </div>
        <span className="fa-body text-fa-frost shrink-0">Rate →</span>
      </Link>

      {/* Billing is invisible until it is real: no plan card and no "coming soon" while Stripe is
          not configured on the server. */}
      {billing?.configured && <BillingCard />}

      <PushOptIn />

      <EmailCapture billingConfigured={billing?.configured ?? false} />

      <div className="lg:col-span-2">
        <PreferenceTags />
      </div>
    </div>
  );
}
