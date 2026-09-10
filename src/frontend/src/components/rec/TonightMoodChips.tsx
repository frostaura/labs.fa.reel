import { TONIGHT_MOODS, type TonightMood } from "../../lib/tonightMood";

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
}: {
  value: TonightMood | null;
  onChange: (mood: TonightMood | null) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="tonight-mood-chips">
      <span className="fa-overline text-fa-frost-dim mr-1">tonight</span>
      {TONIGHT_MOODS.map((mood) => {
        const active = value === mood.id;
        return (
          <button
            key={mood.id}
            type="button"
            aria-pressed={active}
            data-testid={`mood-${mood.id}`}
            // Tapping the active chip clears it — the way back to the model's own order is the
            // same gesture that left it, so nothing is a one-way door.
            onClick={() => onChange(active ? null : mood.id)}
            className={`fa-overline rounded-full border px-3 py-1.5 transition ${
              active
                ? "border-fa-frost/60 bg-fa-glass-strong text-fa-frost-bright"
                : "border-fa-edge bg-fa-glass text-fa-frost-dim hover:border-fa-frost/40 hover:text-fa-frost"
            }`}
          >
            {mood.label}
          </button>
        );
      })}
      {value && (
        <span className="fa-caption text-fa-frost-dim" data-testid="mood-note">
          reordered, not filtered — nothing has been removed
        </span>
      )}
    </div>
  );
}
