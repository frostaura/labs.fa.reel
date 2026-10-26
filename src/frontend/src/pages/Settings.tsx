import { useEffect, useState } from "react";
import { Link } from "react-router";
import { RefreshCw } from "lucide-react";
import PreferencesSection from "../components/settings/PreferencesSection";
import PreferenceTags from "../components/settings/PreferenceTags";
import BillingCard from "../components/settings/BillingCard";
import PushOptIn from "../components/settings/PushOptIn";
import LoadFailed from "../components/ui/load-failed";
import SaveStatus from "../components/ui/save-status";
import { useLeaveGuard } from "../components/settings/useLeaveGuard";
import { pipelineLabel } from "../lib/modelReadiness";
import {
  emailSaveMessage,
  saveFailureMessage,
  savedMessage,
  type SaveMessage,
} from "../components/ui/save-outcome";
import {
  useGetBillingStatusQuery,
  useGetSessionQuery,
  useGetSettingsQuery,
  useGetSyncStatusQuery,
  useTriggerSyncMutation,
  useUpdateSettingsMutation,
} from "../store/api";

/** Minimal ISO-3166 alpha-2 set for v0; replaced by a full searchable list with the M3 surface. */
const REGIONS = ["US", "GB", "DE", "FR", "NL", "ZA", "AU", "CA", "ES", "IT", "PT", "BR", "JP"];

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
          className="flex-1 min-w-0 rounded-full bg-fa-frost/10 px-4 py-2 fa-body text-fa-frost-bright placeholder:text-fa-frost-dim/60 focus:outline-none focus:ring-1 focus:ring-fa-frost/40"
          data-testid="email-input"
        />
        <button type="submit" disabled={!changed || !valid || saving} className="fa-button-primary disabled:opacity-40" data-testid="email-save">
          {saving ? "Saving…" : trimmed.length === 0 && stored.length > 0 ? "Remove" : "Save"}
        </button>
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

      <section className="fa-card p-5 space-y-4">
        <h2 className="fa-section-title">Trakt account</h2>
        <div className="flex items-center gap-3">
          {session?.avatarUrl && (
            <img src={session.avatarUrl} alt="" className="h-10 w-10 rounded-full object-cover" />
          )}
          <div>
            <p className="fa-body text-fa-frost-bright">{session?.displayName}</p>
            <a
              href={`https://trakt.tv/users/${session?.traktSlug}`}
              target="_blank"
              rel="noreferrer"
              className="fa-caption fa-link"
            >
              trakt.tv/users/{session?.traktSlug}
            </a>
          </div>
        </div>
        <div className="flex items-center justify-between border-t border-fa-edge/50 pt-3">
          <div className="fa-caption text-fa-frost-dim">
            {/* In words, from the status that explains it: the raw stage read "Degraded" to an
                account that had rated enough and was only waiting for its first model (R1-57). */}
            <p data-testid="settings-pipeline">Pipeline: {pipelineLabel(sync) ?? session?.pipelineStage ?? "…"}</p>
            <p>
              Last sync:{" "}
              {sync?.lastDeltaSyncAt ? new Date(sync.lastDeltaSyncAt).toLocaleString() : "never"}
            </p>
            {sync?.connectionStatus && sync.connectionStatus !== "Active" && (
              <p className="text-fa-danger">Connection: {sync.connectionStatus} — re-link from the landing page.</p>
            )}
          </div>
          <button onClick={() => triggerSync()} disabled={syncing} className="fa-button">
            <RefreshCw className={`h-4 w-4 ${syncing ? "animate-spin" : ""}`} />
            Sync now
          </button>
        </div>
      </section>

      <section className="fa-card p-5 space-y-3">
        <h2 className="fa-section-title">Region</h2>
        <p className="fa-caption text-fa-frost-dim">
          Drives where-to-watch availability and provider badges.
        </p>
        <div className="flex items-center gap-3">
          <select
            className="fa-input fa-select"
            aria-label="Region"
            value={session?.region ?? "US"}
            onChange={(e) => handleRegionChange(e.target.value)}
            disabled={savingRegion}
            data-testid="region-select"
          >
            {REGIONS.map((r) => (
              <option key={r} value={r} className="bg-fa-ink-2">
                {r}
              </option>
            ))}
          </select>
          <SaveStatus
            message={regionStatus}
            onRetry={lastRegion ? () => void handleRegionChange(lastRegion) : undefined}
            retrying={savingRegion}
            testId="region-save-status"
          />
        </div>
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

      {/* Conditions editor + PIN — wide, dense content that needs the full row. */}
      <div className="lg:col-span-2">
        <PreferencesSection />
      </div>
    </div>
  );
}
