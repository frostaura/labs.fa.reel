import { useState } from "react";
import { Sparkles, X } from "lucide-react";
import { useGetModelLearnedQuery } from "../../store/api";
import { dismissLearned, isLearnedDismissed, learnedCopy } from "../../lib/learnedCopy";

/**
 * "Your model just learned" — the one place a Trakt rating becomes legible.
 *
 * The founder's complaint was that ratings sync instantly and nothing visibly happens. Score-only
 * Preserve updates answer that within minutes for the numbers on the cards; this answers it across
 * the retrain boundary, where the change is large enough to be worth a sentence: what the new
 * artifact learned from, whether it now runs closer to his own scores, and how much of tonight's
 * page moved because of it.
 *
 * Renders nothing at all when the server has nothing honest to say (204 → null) — no predecessor
 * artifact, no evaluation, no new labels between the two trains.
 */
export default function ModelLearnedBand() {
  const { data: learned } = useGetModelLearnedQuery();
  // Read once, on mount, and then owned locally: reading localStorage during render would make the
  // band flicker on every parent re-render, and the dismissal is per-retrain anyway.
  const [dismissedAt, setDismissedAt] = useState<string | null>(null);

  if (!learned) return null;
  if (dismissedAt === learned.trainedAt || isLearnedDismissed(learned.trainedAt)) return null;

  return (
    <section
      data-testid="model-learned-band"
      className="fa-card flex items-start gap-3 px-4 py-3 border-fa-frost/30"
    >
      <Sparkles className="h-4 w-4 text-fa-frost shrink-0 mt-0.5" aria-hidden />
      <p className="fa-caption text-fa-frost-bright flex-1">{learnedCopy(learned)}</p>
      <button
        type="button"
        data-testid="dismiss-model-learned"
        aria-label="Dismiss"
        onClick={() => {
          dismissLearned(learned.trainedAt);
          setDismissedAt(learned.trainedAt);
        }}
        className="shrink-0 rounded-md p-1 text-fa-frost-dim transition hover:text-fa-frost-bright focus:outline-none focus:text-fa-frost-bright"
      >
        <X className="h-3.5 w-3.5" />
      </button>
    </section>
  );
}
