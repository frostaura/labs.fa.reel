import { useEffect, useState } from "react";
import { useGetSessionQuery } from "../../store/api";
import { useUnlockToken } from "../../lib/settingsPin";
import { Badge } from "../ui/badge";
import ConditionsEditor from "./ConditionsEditor";

/**
 * Content preferences on the Settings screen: the shared {@link ConditionsEditor}, with the PIN's
 * lock state at a glance above it.
 *
 * The controls themselves live in the editor, which also mounts inside the dialog on the feed — the
 * founder asked for the conditions where the picks are, and the one thing worse than filters buried
 * in Settings is two filter forms that disagree. Setting, changing and removing the PIN is an
 * account control rather than a condition on tonight's picks, so it is its own card (`PinCard`)
 * in the account column beside this one.
 */
export default function PreferencesSection({ onReady }: { onReady?: () => void }) {
  const { data: session } = useGetSessionQuery();
  const unlock = useUnlockToken();
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
        onReady={onReady}
      />
    </>
  );
}
