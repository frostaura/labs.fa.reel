import { Bookmark, EyeOff } from "lucide-react";
import { useMarkNotInterestedMutation, useSaveForLaterMutation } from "../../store/api";

interface Props {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
}

/**
 * One-tap card actions — "Not for me" and "Save" — overlaid on a recommendation poster, so acting
 * on a pick no longer costs a navigation into the detail modal and back (the top usability finding
 * of the QA pass). Sits top-LEFT: top-right belongs to {@link ScoreBadges}, bottom-left to the
 * provider logos.
 *
 * The `ContinueWatchingRow` drop-button pattern: absolutely positioned inside the card's `group`
 * Link, `e.preventDefault()` so the tap acts instead of navigating, revealed on hover/focus. On
 * coarse pointers there is no hover, so the buttons idle at reduced opacity instead of invisible.
 *
 * Both actions remove the card from every suggestion surface through the existing
 * `removedTitlesSlice` flow — the mutations dispatch `titleRemoved` on success — so there is no
 * un-save toggle here: a saved card is gone from the rail by design (it lives on /saved, where
 * unsave exists and restores it).
 */
export default function CardQuickActions({ titleId, mediaType, tmdbId, name }: Props) {
  const [markNotInterested] = useMarkNotInterestedMutation();
  const [saveForLater] = useSaveForLaterMutation();

  if (tmdbId == null) return null;

  const buttonClass =
    "pointer-events-auto rounded-full bg-fa-ink/70 p-1.5 text-fa-frost-dim backdrop-blur-sm transition " +
    "opacity-0 group-hover:opacity-100 focus:opacity-100 focus-visible:opacity-100 [@media(pointer:coarse)]:opacity-60";
  const ref = { titleId, mediaType: mediaType.toLowerCase(), tmdbId };

  return (
    <div className="absolute left-1.5 top-1.5 flex flex-col gap-1.5" data-testid="card-quick-actions">
      <button
        type="button"
        onClick={(e) => {
          e.preventDefault();
          markNotInterested({ ...ref, reason: "not-for-me" });
        }}
        aria-label={`Not for me — ${name}`}
        title="Not for me"
        className={`${buttonClass} hover:text-fa-danger`}
        data-testid="quick-not-interested"
      >
        <EyeOff className="h-3.5 w-3.5" />
      </button>
      <button
        type="button"
        onClick={(e) => {
          e.preventDefault();
          saveForLater(ref);
        }}
        aria-label={`Save for later — ${name}`}
        title="Save for later"
        className={`${buttonClass} hover:text-fa-frost-bright`}
        data-testid="quick-save"
      >
        <Bookmark className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}
