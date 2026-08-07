import { useState } from "react";
import { ShieldCheck } from "lucide-react";
import { useGetSessionQuery, useRemovePinMutation, useSetPinMutation } from "../../store/api";
import { clearUnlockedPin, storeUnlockedPin, useUnlockedPin } from "../../lib/settingsPin";
import ConditionsEditor from "./ConditionsEditor";

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
 * from this block does not re-lock the editor sitting directly above it.
 */
export default function PreferencesSection() {
  const { data: session } = useGetSessionQuery();
  const [setPinMutation] = useSetPinMutation();
  const [removePin] = useRemovePinMutation();
  const unlockedPin = useUnlockedPin();

  const [newPin, setNewPin] = useState("");
  const [flash, setFlash] = useState<string | null>(null);

  const pinConfigured = session?.pinConfigured ?? false;
  const locked = pinConfigured && unlockedPin === null;

  return (
    <>
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
          <div className="flex items-center gap-2">
            <input
              type="password"
              inputMode="numeric"
              maxLength={8}
              value={newPin}
              onChange={(e) => setNewPin(e.target.value.replace(/\D/g, ""))}
              placeholder={pinConfigured ? "new PIN" : "set PIN"}
              aria-label={pinConfigured ? "New settings PIN" : "Set a settings PIN"}
              className="fa-input w-28 tabular-nums"
            />
            <button
              onClick={async () => {
                if (newPin.length < 4) return;
                await setPinMutation({ pin: newPin, currentPin: unlockedPin ?? undefined }).unwrap().catch(() => undefined);
                // Hold the unlock through the change: `pinConfigured` flips true on the session
                // refetch, and without this the editor above would lock itself the instant its
                // owner set the PIN.
                storeUnlockedPin(newPin);
                setNewPin("");
                setFlash("PIN updated.");
                window.setTimeout(() => setFlash(null), 2000);
              }}
              className="fa-button"
            >
              {pinConfigured ? "Change PIN" : "Set PIN"}
            </button>
            {pinConfigured && (
              <button
                onClick={async () => {
                  await removePin({ pin: unlockedPin ?? "" }).unwrap().catch(() => undefined);
                  clearUnlockedPin();
                }}
                className="fa-button-ghost text-fa-danger"
              >
                Remove
              </button>
            )}
            {flash && <span className="fa-caption text-fa-success">{flash}</span>}
          </div>
        </section>
      )}
    </>
  );
}
