import { useState } from "react";
import { ShieldCheck } from "lucide-react";
import {
  useGetSessionQuery,
  useRemovePinMutation,
  useSetPinMutation,
  useVerifyPinMutation,
} from "../../store/api";
import { clearUnlockToken, storeUnlockToken, useUnlockToken } from "../../lib/settingsPin";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "../ui/dialog";
import { SAVE_FAILED_TEXT } from "../ui/save-outcome";
import { Button } from "../ui/button";
import { pinAttemptText } from "../../lib/pinLockout";
import { COARSE_TAP } from "../../lib/tapTarget";
import { cn } from "../../lib/cn";

/**
 * The settings PIN card: set, change or remove the PIN that locks content preferences.
 *
 * It lives beside the conditions rather than under them (HREV1-D07): on a wide screen the editor
 * takes the main column and the account cards — this one among them — stack in the column beside
 * it. The PIN unlock is shared module state (`lib/settingsPin`), so setting a PIN here does not
 * re-lock the editor next door. What that store holds is a short-lived server-issued token — never
 * the raw PIN — so every flow here that needs the PIN itself (change, remove) prompts for it fresh
 * instead of reading any cached state.
 */
export default function PinCard() {
  const { data: session } = useGetSessionQuery();
  const [setPinMutation] = useSetPinMutation();
  const [removePin] = useRemovePinMutation();
  const [verifyPin] = useVerifyPinMutation();
  const unlock = useUnlockToken();

  const [currentPin, setCurrentPin] = useState("");
  const [newPin, setNewPin] = useState("");
  const [flash, setFlash] = useState<string | null>(null);
  const [pinError, setPinError] = useState<string | null>(null);

  const [removeOpen, setRemoveOpen] = useState(false);
  const [removePinEntry, setRemovePinEntry] = useState("");
  const [removeError, setRemoveError] = useState<string | null>(null);

  const pinConfigured = session?.pinConfigured ?? false;
  const locked = pinConfigured && unlock === null;

  const submitPin = async () => {
    if (newPin.length < 4) return;
    setPinError(null);
    try {
      // The current PIN comes from the box the user just typed into — deliberately never from
      // the unlock store, which no longer holds the raw PIN at all.
      await setPinMutation({ pin: newPin, currentPin: pinConfigured ? currentPin : undefined }).unwrap();
    } catch (error: unknown) {
      // A 423 is the wrong current PIN; a 429 is the five-wrong-PINs pause, with how long; a 400 is
      // the server's rule; anything else is a failed request — which used to read "Wrong current
      // PIN." too, on a 500 or a dropped connection.
      setPinError(pinAttemptText(error, "Wrong current PIN — nothing changed.", SAVE_FAILED_TEXT));
      return;
    }
    // Hold the unlock through the change: `pinConfigured` flips true on the session refetch, and
    // without a fresh token the conditions editor would lock itself the instant its owner set the PIN.
    const verified = await verifyPin({ pin: newPin }).unwrap().catch(() => null);
    if (verified?.valid && verified.unlockToken) {
      storeUnlockToken(verified.unlockToken, verified.expiresAt);
    }
    setCurrentPin("");
    setNewPin("");
    setFlash("PIN updated.");
    window.setTimeout(() => setFlash(null), 2000);
  };

  const confirmRemove = async () => {
    try {
      await removePin({ pin: removePinEntry }).unwrap();
    } catch (error: unknown) {
      setRemoveError(pinAttemptText(error, "Wrong PIN — it's still set.", SAVE_FAILED_TEXT));
      return;
    }
    // Nothing is locked any more, so a lingering unlock token is just stale state — drop it.
    clearUnlockToken();
    setRemoveOpen(false);
    setRemovePinEntry("");
    setRemoveError(null);
    setFlash("PIN removed.");
    window.setTimeout(() => setFlash(null), 2000);
  };

  return (
    <>
      {/* PIN management. Hidden while locked — the point of the lock is that someone without the
          PIN cannot change it. */}
      {!locked && (
        <section className="fa-card p-5 space-y-2" data-testid="pin-management">
          <p className="fa-overline text-fa-frost-dim flex items-center gap-1.5">
            <ShieldCheck className="h-3.5 w-3.5" /> settings pin
          </p>
          <p className="fa-caption text-fa-frost-dim">
            {pinConfigured
              ? "A PIN locks content preferences — the account creator keeps control on shared screens."
              : "Optionally lock content preferences with a 4–8 digit PIN (e.g. for kids' profiles)."}
          </p>
          <div className="flex flex-wrap items-center gap-2">
            {pinConfigured && (
              <input
                type="password"
                inputMode="numeric"
                maxLength={8}
                value={currentPin}
                onChange={(e) => setCurrentPin(e.target.value.replace(/\D/g, ""))}
                placeholder="current PIN"
                aria-label="Current settings PIN"
                className={cn("fa-input w-28 tabular-nums", COARSE_TAP)}
                data-testid="current-pin-input"
              />
            )}
            <input
              type="password"
              inputMode="numeric"
              maxLength={8}
              value={newPin}
              onChange={(e) => setNewPin(e.target.value.replace(/\D/g, ""))}
              placeholder={pinConfigured ? "new PIN" : "set PIN"}
              data-testid="new-pin-input"
              aria-label={pinConfigured ? "New settings PIN" : "Set a settings PIN"}
              className={cn("fa-input w-28 tabular-nums", COARSE_TAP)}
            />
            <Button onClick={submitPin} className={COARSE_TAP} data-testid="pin-submit">
              {pinConfigured ? "Change PIN" : "Set PIN"}
            </Button>
            {pinConfigured && (
              <Button variant="ghost" onClick={() => setRemoveOpen(true)} className={cn("text-fa-danger hover:text-fa-danger", COARSE_TAP)}>
                Remove
              </Button>
            )}
            {flash && <span className="fa-caption text-fa-success">{flash}</span>}
            {pinError && (
              <span role="alert" className="fa-caption text-fa-danger" data-testid="pin-error">
                {pinError}
              </span>
            )}
          </div>
        </section>
      )}

      {/* Removing the PIN is the one action that must never fire off a cached unlock: it re-prompts
          for the PIN here, and the backend refuses unlock tokens on the DELETE for the same reason. */}
      <Dialog
        open={removeOpen}
        onOpenChange={(open) => {
          setRemoveOpen(open);
          if (!open) {
            setRemovePinEntry("");
            setRemoveError(null);
          }
        }}
      >
        <DialogContent className="max-w-sm">
          <DialogHeader>
            <DialogTitle>Remove the settings PIN?</DialogTitle>
            <DialogDescription>
              Anyone using this device will be able to edit content conditions.
            </DialogDescription>
          </DialogHeader>
          <div className="px-6 space-y-2">
            <input
              type="password"
              inputMode="numeric"
              maxLength={8}
              value={removePinEntry}
              onChange={(e) => setRemovePinEntry(e.target.value.replace(/\D/g, ""))}
              onKeyDown={(e) => e.key === "Enter" && confirmRemove()}
              placeholder="PIN"
              aria-label="Confirm your settings PIN"
              className={cn("fa-input w-28 tabular-nums", COARSE_TAP)}
              data-testid="remove-pin-input"
            />
            {removeError && (
              <p role="alert" className="fa-caption text-fa-danger" data-testid="remove-pin-error">
                {removeError}
              </p>
            )}
          </div>
          <DialogFooter>
            <Button variant="ghost" onClick={() => setRemoveOpen(false)} className={COARSE_TAP}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={confirmRemove}
              disabled={removePinEntry.length < 4}
              className={COARSE_TAP}
              data-testid="remove-pin-confirm"
            >
              Remove PIN
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
