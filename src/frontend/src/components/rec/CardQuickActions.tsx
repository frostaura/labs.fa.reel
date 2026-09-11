import { useEffect, useRef, useState } from "react";
import { Bookmark, EyeOff, Star } from "lucide-react";
import { useMarkNotInterestedMutation, useRateTitleMutation, useSaveForLaterMutation } from "../../store/api";
import { Popover, PopoverAnchor, PopoverContent } from "../ui/popover";
import { DEFAULT_DISLIKE_REASON, DISLIKE_REASONS } from "../../lib/dislikeReasons";

interface Props {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  /**
   * Poster scale ("sm", the default) or hero scale ("lg"). The hero is four times the size of a rail
   * card, and a 14px icon tucked into its corner reads as a rendering artefact rather than a control.
   * Only the metrics change — the same component, the same actions, the same test ids on both.
   */
  size?: "sm" | "lg";
}

/** How long a press must hold before it means "let me pick a reason" instead of "dismiss it". */
const LONG_PRESS_MS = 450;

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
 * TARGET SIZE. On a phone these are the smallest real controls in the app — they measured 26 x 26
 * against WCAG 2.1 AA's 44 x 44 — and the fix cannot simply be a bigger pill, because the pill is
 * jewellery on a 163 px poster and a 44 px disc over the artwork reads as a badge, not a control.
 * So the hit area and the glyph are separated below `md`: the `<button>` is a transparent
 * `min-h-11 min-w-11` box, the visible pill is a `<span>` centred inside it, and the icon does not
 * change size at all. The column's own gap goes to zero there because each box already carries
 * ~9 px of its own margin — three 44 px boxes stack to 132 px inside a 245 px poster with no
 * overlap and no spill, which `askreel-mobile.spec.ts` asserts rather than assumes. From `md` up
 * every one of those classes is reverted and the overlay is unchanged.
 *
 * Both actions remove the card from every suggestion surface through the existing
 * `removedTitlesSlice` flow — the mutations dispatch `titleRemoved` on success — so there is no
 * un-save toggle here: a saved card is gone from the rail by design (it lives on /saved, where
 * unsave exists and restores it).
 *
 * REASONS. This surface used to hardcode `not-for-me`, so the app's busiest dismissal path fed
 * `DislikeSignal` its flattest possible signal while the four-way picker existed only in the modal.
 * It now offers all four — but never at the cost of the one-tap path, which is the whole point of
 * the overlay. A plain tap still dismisses instantly with the default reason; the picker is a
 * SECONDARY gesture: press-and-hold (the only affordance a touch device has spare), right-click, or
 * ArrowDown/Alt+Enter from the keyboard. A long press that opens the picker swallows its own click,
 * so holding never dismisses and then also asks.
 *
 * RATING. The third action is a 1–10 popover, and it is here rather than only in the detail modal
 * because a rating is the one input that closes the loop: Reel predicted a number, the user supplies
 * theirs, and until now supplying it cost a navigation into the modal and back — which is why 2% of
 * ratings are made in the app and the rest arrive from Trakt hours later. Rating from a suggestion
 * surface also means "I have seen this", so the play is logged (`markWatched`) exactly as the /rate
 * grid does, and the title leaves every suggestion surface through the same removedTitlesSlice flow
 * as the other two actions — `rateTitle` dispatches `titleRemoved` on success.
 */
export default function CardQuickActions({ titleId, mediaType, tmdbId, name, size = "sm" }: Props) {
  const [markNotInterested] = useMarkNotInterestedMutation();
  const [saveForLater] = useSaveForLaterMutation();
  const [rateTitle] = useRateTitleMutation();
  const [reasonsOpen, setReasonsOpen] = useState(false);
  const [rateOpen, setRateOpen] = useState(false);
  const longPressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Set the moment a hold matures into the picker; consumed by the click that the same press
  // fires immediately afterwards, which must NOT also dismiss the card.
  const suppressClick = useRef(false);

  const cancelLongPress = () => {
    if (longPressTimer.current !== null) {
      clearTimeout(longPressTimer.current);
      longPressTimer.current = null;
    }
  };

  useEffect(() => cancelLongPress, []);

  if (tmdbId == null) return null;

  const large = size === "lg";
  // Below `md` the BUTTON is the tap target and the `<span>` inside it is the thing you can see:
  // a transparent 44 x 44 box centred on a pill that stays exactly poster-sized. From `md` up the
  // box collapses back onto the pill (`md:min-h-0 md:min-w-0`) and the overlay is pixel-for-pixel
  // what it has always been on a desktop.
  const buttonClass =
    "pointer-events-auto inline-flex min-h-11 min-w-11 md:min-h-0 md:min-w-0 items-center justify-center " +
    "rounded-full text-fa-frost-dim transition " +
    "opacity-0 group-hover:opacity-100 focus:opacity-100 focus-visible:opacity-100 [@media(pointer:coarse)]:opacity-60";
  const pillClass = `flex items-center justify-center rounded-full bg-fa-ink/70 ${large ? "p-2" : "p-1.5"} backdrop-blur-sm`;
  const iconClass = large ? "h-4 w-4" : "h-3.5 w-3.5";
  const ref = { titleId, mediaType: mediaType.toLowerCase(), tmdbId };

  const rate = (rating: number) => {
    setRateOpen(false);
    // A pick you are rating from a suggestion surface is one you have seen — the eligibility rule
    // guarantees it carried no footprint until now — so the play is logged, as on the /rate grid.
    rateTitle({ ...ref, rating, markWatched: true });
  };

  const dismiss = (reason: string) => {
    setReasonsOpen(false);
    markNotInterested({ ...ref, reason });
  };

  const openReasons = () => {
    cancelLongPress();
    suppressClick.current = true;
    setReasonsOpen(true);
  };

  return (
    <div
      className={`absolute flex flex-col ${
        large
          ? "left-1 top-1 gap-0 md:left-3 md:top-3 md:gap-2"
          : "left-0 top-0 gap-0 md:left-1.5 md:top-1.5 md:gap-1.5"
      }`}
      data-testid="card-quick-actions"
    >
      <Popover open={reasonsOpen} onOpenChange={setReasonsOpen}>
        <PopoverAnchor asChild>
          <button
            type="button"
            onClick={(e) => {
              e.preventDefault();
              if (suppressClick.current) {
                suppressClick.current = false;
                return;
              }
              dismiss(DEFAULT_DISLIKE_REASON);
            }}
            onPointerDown={() => {
              cancelLongPress();
              longPressTimer.current = setTimeout(openReasons, LONG_PRESS_MS);
            }}
            onPointerUp={cancelLongPress}
            onPointerLeave={cancelLongPress}
            onPointerCancel={cancelLongPress}
            onContextMenu={(e) => {
              e.preventDefault();
              openReasons();
            }}
            onKeyDown={(e) => {
              // Keyboard equivalent of the hold. Space/Enter stay the fast path.
              if (e.key === "ArrowDown" || (e.key === "Enter" && e.altKey)) {
                e.preventDefault();
                openReasons();
              }
            }}
            aria-label={`Not for me — ${name}`}
            aria-haspopup="menu"
            aria-expanded={reasonsOpen}
            title="Not for me — hold or right-click to say why"
            className={`${buttonClass} hover:text-fa-danger ${reasonsOpen ? "opacity-100" : ""}`}
            data-testid="quick-not-interested"
          >
            <span className={pillClass}>
              <EyeOff className={iconClass} />
            </span>
          </button>
        </PopoverAnchor>
        <PopoverContent
          align="start"
          side="right"
          className="w-auto p-1.5"
          data-testid="quick-reason-picker"
        >
          <p className="fa-overline text-fa-frost-dim px-2 pb-1.5">why not?</p>
          <div className="flex flex-col">
            {DISLIKE_REASONS.map((reason) => (
              <button
                key={reason.value}
                type="button"
                onClick={(e) => {
                  e.preventDefault();
                  dismiss(reason.value);
                }}
                className="fa-body rounded-md px-2 py-1.5 text-left text-fa-frost transition hover:bg-fa-frost/10 hover:text-fa-frost-bright"
                data-testid={`quick-reason-${reason.value}`}
              >
                {reason.label}
              </button>
            ))}
          </div>
        </PopoverContent>
      </Popover>
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
        <span className={pillClass}>
              <Bookmark className={iconClass} />
            </span>
      </button>
      <Popover open={rateOpen} onOpenChange={setRateOpen}>
        <PopoverAnchor asChild>
          <button
            type="button"
            onClick={(e) => {
              e.preventDefault();
              setRateOpen((open) => !open);
            }}
            aria-label={`Rate ${name}`}
            aria-haspopup="menu"
            aria-expanded={rateOpen}
            title="Rate it — you have seen this one"
            className={`${buttonClass} hover:text-fa-warning ${rateOpen ? "opacity-100" : ""}`}
            data-testid="quick-rate"
          >
            <span className={pillClass}>
              <Star className={iconClass} />
            </span>
          </button>
        </PopoverAnchor>
        <PopoverContent align="start" side="right" className="w-auto p-2" data-testid="quick-rate-picker">
          <p className="fa-overline text-fa-frost-dim px-0.5 pb-1.5">your score out of 10</p>
          <div className="flex gap-1">
            {Array.from({ length: 10 }, (_, i) => i + 1).map((value) => (
              <button
                key={value}
                type="button"
                onClick={(e) => {
                  e.preventDefault();
                  rate(value);
                }}
                aria-label={`${value} out of 10`}
                className="h-8 w-7 rounded-md border border-fa-edge bg-fa-glass fa-caption tabular-nums text-fa-frost transition hover:border-fa-frost/40 hover:text-fa-frost-bright"
                data-testid={`quick-rate-${value}`}
              >
                {value}
              </button>
            ))}
          </div>
        </PopoverContent>
      </Popover>
    </div>
  );
}
