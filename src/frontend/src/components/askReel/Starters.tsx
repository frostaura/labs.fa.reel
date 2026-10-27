import BrowseFilters from "../rec/BrowseFilters";
import PillButton from "./PillButton";
import type { BrowseFilter } from "../../lib/browseFilter";

/** Cold-start mood presets — one tap seeds a whole first turn on an otherwise empty page. */
const MOOD_PRESETS = [
  "Under 90 minutes, feel-good",
  "Something dark and gripping",
  "Family movie night",
  "True-story tension",
];

/** The cold start: what to ask when nothing has been asked yet. Gone the moment a conversation exists. */
export function MoodPresets({ onPick }: { onPick: (preset: string) => void }) {
  return (
    <div className="fa-card p-6 max-w-xl mx-auto space-y-4 text-center">
      <div className="space-y-1">
        <p className="fa-body text-fa-frost-bright">What are you in the mood for?</p>
        <p className="fa-caption text-fa-frost-dim">Tap one to start, or ask in your own words below.</p>
      </div>
      <div className="flex flex-wrap justify-center gap-2">
        {MOOD_PRESETS.map((preset) => (
          <PillButton key={preset} tone="accent" data-testid="askreel-preset" onClick={() => onPick(preset)}>
            {preset}
          </PillButton>
        ))}
      </div>
    </div>
  );
}

/**
 * Refine chips and the media toggle, under the conversation. The chips are generated from the
 * planner's reading and the account's conditions (`refineChips`) — never a direction a standing rule
 * rules out — and they wait while a turn is running rather than queueing a second one behind it.
 */
export function RefineBar({
  chips,
  streaming,
  onChip,
  filter,
  onFilterChange,
}: {
  chips: string[];
  streaming: boolean;
  onChip: (chip: string) => void;
  filter: BrowseFilter;
  onFilterChange: (next: BrowseFilter) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="askreel-refine">
      {chips.map((chip) => (
        <PillButton
          key={chip}
          tone="quiet"
          data-testid="askreel-chip"
          onClick={() => onChip(chip)}
          disabled={streaming}
          title={streaming ? "Wait for the current results to finish" : undefined}
        >
          {chip}
        </PillButton>
      ))}
      <div className="ml-auto">
        <BrowseFilters value={filter} onChange={onFilterChange} showAvailability={false} />
      </div>
    </div>
  );
}
