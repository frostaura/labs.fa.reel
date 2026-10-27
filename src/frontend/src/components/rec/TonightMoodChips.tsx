import { TONIGHT_MOODS, type TonightMood } from "../../lib/tonightMood";
import { Button } from "../ui/button";

/**
 * "Not tonight — something lighter."
 *
 * Four chips above the hero that re-sort tonight's picks in place. They change nothing standing:
 * no filter is saved, no rule is edited, no rebuild is queued, and tapping one twice puts the page
 * back exactly as it was. That restraint is the design — tonight's picks are a contract, and a
 * mood is a way of reading them rather than a reason to deal new ones.
 *
 * The signal behind them was already paid for: seven scalars and a closed mood vocabulary
 * extracted per title, which until now almost nothing read.
 */
export default function TonightMoodChips({
  value,
  onChange,
  className = "",
}: {
  value: TonightMood | null;
  onChange: (mood: TonightMood | null) => void;
  className?: string;
}) {
  return (
    // One scrolling line on a phone (two wrapped lines there pushed the first pick a chip-row
    // further below the fold), wrapping as before from `md`. The note sits outside the scroller so
    // it can never be scrolled out of sight.
    <div className={`space-y-1.5 ${className}`} data-testid="tonight-mood-chips">
      <div className="-mx-1 flex items-center gap-2 overflow-x-auto px-1 [scrollbar-width:none] md:flex-wrap md:overflow-visible">
        <span className="fa-overline shrink-0 text-3 mr-1">tonight</span>
        {TONIGHT_MOODS.map((mood) => {
          const active = value === mood.id;
          return (
            <Button
              key={mood.id}
              variant="ghost"
              aria-pressed={active}
              data-testid={`mood-${mood.id}`}
              // Tapping the active chip clears it — the way back to the model's own order is the
              // same gesture that left it, so nothing is a one-way door.
              onClick={() => onChange(active ? null : mood.id)}
              // 44 px tall on a coarse pointer (IREV3S-08 measured these at 24); a fine pointer keeps
              // the compact chip.
              className={`h-auto min-h-11 shrink-0 rounded-full border px-3 py-1.5 font-normal [@media(pointer:fine)]:min-h-0 ${
                active
                  ? "border-fa-frost/60 bg-fa-glass-strong text-fa-frost-bright hover:bg-fa-glass-strong hover:text-fa-frost-bright"
                  : "border-fa-edge bg-fa-glass text-3 hover:border-fa-frost/40 hover:bg-fa-glass hover:text-fa-frost"
              }`}
            >
              <span className="fa-overline">{mood.label}</span>
            </Button>
          );
        })}
      </div>
      {value && (
        <p className="fa-caption text-3" data-testid="mood-note">
          reordered, not filtered — nothing has been removed
        </p>
      )}
    </div>
  );
}
