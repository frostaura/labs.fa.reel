import { useEffect, useState } from "react";
import { ShieldCheck } from "lucide-react";
import {
  useGetSessionQuery,
  useRemovePinMutation,
  useSetPinMutation,
  useVerifyPinMutation,
} from "../../store/api";
import { clearUnlockToken, storeUnlockToken, useUnlockToken } from "../../lib/settingsPin";
import { Badge } from "../ui/badge";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "../ui/dialog";
import ConditionsEditor from "./ConditionsEditor";
import { pinFailureText } from "./saveOutcome";

/**
 * Content preferences on the Settings screen: the shared {@link ConditionsEditor} plus the PIN that
 * gates it.
 *
 * The controls themselves used to live here. They now live in the editor, which also mounts inside
 * the dialog on the feed — the founder asked for the conditions where the picks are, and the one
 * thing worse than filters buried in Settings is two filter forms that disagree. This file keeps
 * only what is genuinely Settings-shaped: the PIN, which is an account control rather than a
 * condition on tonight's picks.
 *
 * The PIN unlock is shared module state (`lib/settingsPin`) rather than local, so setting a PIN
 * from this block does not re-lock the editor sitting directly above it. What that store holds is
 * a short-lived server-issued token — never the raw PIN — so every flow here that needs the PIN
 * itself (change, remove) prompts for it fresh instead of reading any cached state.
 */
export default function PreferencesSection() {
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

  // The badge counts the unlock down ("Unlocked · 12m"). Render must stay pure, so the clock is
  // state: seeded lazily, advanced by a once-a-minute interval. The hard expiry itself is the
  // store's own timer, which flips `unlock` to null and re-locks for real.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 60_000);
    return () => window.clearInterval(id);
  }, []);
  const minutesLeft = unlock ? Math.max(1, Math.ceil((unlock.until - now) / 60_000)) : 0;

  const submitPin = async () => {
    if (newPin.length < 4) return;
    setPinError(null);
    try {
      // The current PIN comes from the box the user just typed into — deliberately never from
      // the unlock store, which no longer holds the raw PIN at all.
      await setPinMutation({ pin: newPin, currentPin: pinConfigured ? currentPin : undefined }).unwrap();
    } catch (error: unknown) {
      // A 423 is the wrong current PIN; a 400 is the server's rule; anything else is a failed
      // request — which used to read "Wrong current PIN." too, on a 500 or a dropped connection.
      setPinError(pinFailureText(error, "Wrong current PIN — nothing changed."));
      return;
    }
    // Hold the unlock through the change: `pinConfigured` flips true on the session refetch, and
    // without a fresh token the editor above would lock itself the instant its owner set the PIN.
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
      setRemoveError(pinFailureText(error, "Wrong PIN — it's still set."));
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
      {/* Lock status at a glance, next to where the section starts — otherwise "why can't I edit
          this on the TV" and "did that unlock actually take" are both invisible. */}
      {pinConfigured && (
        <div className="flex items-center gap-2" data-testid="pin-status">
          {locked ? (
            <Badge variant="outline">Locked</Badge>
          ) : (
            <Badge variant="success">Unlocked · {minutesLeft}m</Badge>
          )}
        </div>
      )}

      <ConditionsEditor
        saveLabel="Save preferences"
        testIds={{ editor: "prefs-section", locked: "prefs-locked", loading: "prefs-loading" }}
      />

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
                className="fa-input w-28 tabular-nums"
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
              className="fa-input w-28 tabular-nums"
            />
            <button onClick={submitPin} className="fa-button" data-testid="pin-submit">
              {pinConfigured ? "Change PIN" : "Set PIN"}
            </button>
            {pinConfigured && (
              <button
                onClick={() => setRemoveOpen(true)}
                className="fa-button-ghost text-fa-danger"
              >
                Remove
              </button>
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
              className="fa-input w-28 tabular-nums"
              data-testid="remove-pin-input"
            />
            {removeError && (
              <p role="alert" className="fa-caption text-fa-danger" data-testid="remove-pin-error">
                {removeError}
              </p>
            )}
          </div>
          <DialogFooter>
            <button onClick={() => setRemoveOpen(false)} className="fa-button-ghost">
              Cancel
            </button>
            <button
              onClick={confirmRemove}
              disabled={removePinEntry.length < 4}
              className="fa-button border-fa-danger/50 bg-fa-danger/15 text-fa-danger hover:bg-fa-danger/25 disabled:opacity-50"
              data-testid="remove-pin-confirm"
            >
              Remove PIN
            </button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
