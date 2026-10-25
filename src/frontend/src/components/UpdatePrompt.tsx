import { acceptPwaUpdate, dismissPwaUpdate, usePwaUpdateReady } from "../lib/pwaUpdate";
import { Button } from "./ui/button";

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
      <Button
        type="button"
        variant="ghost"
        size="sm"
        onClick={dismissPwaUpdate}
        className="rounded-full fa-caption focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
      >
        Later
      </Button>
      <Button
        type="button"
        variant="primary"
        size="sm"
        onClick={acceptPwaUpdate}
        className="rounded-full fa-caption focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
      >
        Reload
      </Button>
    </div>
  );
}
