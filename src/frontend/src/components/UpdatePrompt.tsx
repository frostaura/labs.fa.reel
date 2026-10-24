import { acceptPwaUpdate, dismissPwaUpdate, usePwaUpdateReady } from "../lib/pwaUpdate";

/** The "New version — reload" toast. Sits above the mobile tab bar; never takes focus by itself. */
export default function UpdatePrompt() {
  const ready = usePwaUpdateReady();
  if (!ready) return null;

  return (
    <div
      role="status"
      aria-live="polite"
      data-testid="update-prompt"
      className="fixed inset-x-4 bottom-24 md:bottom-6 md:left-auto md:right-6 md:inset-x-auto z-50 mx-auto max-w-sm rounded-2xl border border-fa-edge bg-fa-ink/95 backdrop-blur-md px-4 py-3 shadow-2xl flex items-center gap-3"
    >
      <p className="fa-caption text-fa-frost flex-1">A new version of Reel is ready.</p>
      <button
        type="button"
        onClick={dismissPwaUpdate}
        className="rounded-full px-3 py-1.5 fa-caption text-fa-frost-dim transition hover:text-fa-frost-bright focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
      >
        Later
      </button>
      <button
        type="button"
        onClick={acceptPwaUpdate}
        className="rounded-full border border-fa-frost/40 bg-fa-glass-strong px-3 py-1.5 fa-caption text-fa-frost-bright transition hover:border-fa-frost focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
      >
        Reload
      </button>
    </div>
  );
}
