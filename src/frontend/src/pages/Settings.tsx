import { useState } from "react";
import { Link } from "react-router";
import { RefreshCw } from "lucide-react";
import PreferencesSection from "../components/settings/PreferencesSection";
import PreferenceTags from "../components/settings/PreferenceTags";
import BillingCard from "../components/settings/BillingCard";
import PushOptIn from "../components/settings/PushOptIn";
import {
  useGetSessionQuery,
  useGetSyncStatusQuery,
  useTriggerSyncMutation,
  useUpdateSettingsMutation,
} from "../store/api";

/** Minimal ISO-3166 alpha-2 set for v0; replaced by a full searchable list with the M3 surface. */
const REGIONS = ["US", "GB", "DE", "FR", "NL", "ZA", "AU", "CA", "ES", "IT", "PT", "BR", "JP"];

function EmailCapture({ onSave }: { onSave: (email: string) => Promise<unknown> }) {
  const [email, setEmail] = useState("");
  const [saved, setSaved] = useState(false);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!email.includes("@")) return;
    await onSave(email);
    setSaved(true);
    window.setTimeout(() => setSaved(false), 2000);
  };
  return (
    <section className="fa-card p-5 space-y-3" data-testid="email-capture">
      <h2 className="fa-section-title">Email</h2>
      <p className="fa-caption text-fa-frost-dim">
        For when paid plans land — we’ll let you know first. Optional; never shared.
      </p>
      <form onSubmit={submit} className="flex items-center gap-2">
        <input
          type="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          placeholder="you@example.com"
          className="flex-1 rounded-full bg-fa-frost/10 px-4 py-2 fa-body text-fa-frost-bright placeholder:text-fa-frost-dim/60 focus:outline-none focus:ring-1 focus:ring-fa-frost/40"
        />
        <button type="submit" disabled={!email.includes("@")} className="fa-button-primary disabled:opacity-40">
          {saved ? "Saved ✓" : "Save"}
        </button>
      </form>
    </section>
  );
}

export default function Settings() {
  const { data: session } = useGetSessionQuery();
  const { data: sync } = useGetSyncStatusQuery();
  const [triggerSync, { isLoading: syncing }] = useTriggerSyncMutation();
  const [updateSettings] = useUpdateSettingsMutation();
  const [savedFlash, setSavedFlash] = useState(false);

  const handleRegionChange = async (region: string) => {
    await updateSettings({ region }).unwrap().catch(() => undefined);
    setSavedFlash(true);
    window.setTimeout(() => setSavedFlash(false), 1500);
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
            <p>Pipeline: {sync?.pipelineStage ?? session?.pipelineStage ?? "…"}</p>
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
            value={session?.region ?? "US"}
            onChange={(e) => handleRegionChange(e.target.value)}
          >
            {REGIONS.map((r) => (
              <option key={r} value={r} className="bg-fa-ink-2">
                {r}
              </option>
            ))}
          </select>
          {savedFlash && <span className="fa-caption text-fa-success">Saved</span>}
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

      <BillingCard />

      <PushOptIn />

      <EmailCapture onSave={(email) => updateSettings({ email })} />

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
