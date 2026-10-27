import { acceptPwaUpdate, dismissPwaUpdate, usePwaUpdateReady } from "../lib/pwaUpdate";
import { Button } from "./ui/button";
import { ToastPortal } from "./ui/toast-stack";

/**
 * The "New version — reload" toast. Sits in the shared toast column (above the mobile tab bar, and
 * stacked with any other toast rather than over it); never takes focus by itself.
 */
export default function UpdatePrompt() {
  const ready = usePwaUpdateReady();
  if (!ready) return null;

  return (
    <ToastPortal>
      <div
        role="status"
        aria-live="polite"
        data-testid="update-prompt"
        className="flex items-center gap-3 rounded-xl border border-fa-edge bg-fa-ink/95 px-4 py-3 shadow-lg backdrop-blur-md"
      >
        <p className="fa-caption text-fa-frost flex-1">A new version of Reel is ready.</p>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={dismissPwaUpdate}
          className="rounded-full text-caption focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
        >
          Later
        </Button>
        <Button
          type="button"
          variant="primary"
          size="sm"
          onClick={acceptPwaUpdate}
          className="rounded-full text-caption focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50"
        >
          Reload
        </Button>
      </div>
    </ToastPortal>
  );
}
