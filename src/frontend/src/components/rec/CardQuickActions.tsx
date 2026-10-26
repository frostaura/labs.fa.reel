import { useEffect, useRef, useState } from "react";
import { Bookmark, EyeOff, Star } from "lucide-react";
import { useMarkNotInterestedMutation, useRateTitleMutation, useSaveForLaterMutation } from "../../store/api";
import { Popover, PopoverAnchor, PopoverContent } from "../ui/popover";
import { DEFAULT_DISLIKE_REASON, DISLIKE_REASONS } from "../../lib/dislikeReasons";
import { useCardUndo } from "../../lib/cardUndo";

interface Props {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  /**
   * Poster scale ("sm", the default) or hero scale ("lg"). The hero is four times the size of a rail
   * card, and a 14px icon tucked into its corner reads as a rendering artefact rather than a control.
   * The same component, the same actions, the same test ids on both.
   *
   * Hero scale also changes PLACEMENT below `lg`: the hero card stacks there (art, then text), and
   * the actions become a visible row under the text instead of an overlay on the art — overlaid,
   * they sat on top of the meta and hook lines (I-10). From `lg` they are a row across the art's
   * top-right corner, which the text panel (pinned to the bottom) never reaches.
   */
  size?: "sm" | "lg";
}

/** How long a press must hold before it means "let me pick a reason" instead of "dismiss it". */
const LONG_PRESS_MS = 450;

/**
 * One-tap card actions — "Not for me" and "Save" — overlaid on a recommendation poster, so acting
 * on a pick no longer costs a navigation into the detail modal and back (the top usability finding
 * of the QA pass). On rail cards it sits top-LEFT (the hero places it itself — see `size`): top-right belongs to {@link ScoreBadges}, bottom-left to the
 * provider logos.
 *
 * The `ContinueWatchingRow` drop-button pattern: absolutely positioned inside the card's `group`
 * card, a SIBLING of the card's link rather than inside it (a button inside a link is two
 * interactive elements nested, which a keyboard and a screen reader both trip on), revealed on
 * hover/focus. `e.preventDefault()` stays so a host that does nest it still acts instead of
 * navigating. On
 * coarse pointers there is no hover, so the buttons idle at reduced opacity instead of invisible.
 *
 * TARGET SIZE. On a phone these are the smallest real controls in the app — they measured 26 x 26
 * against WCAG 2.1 AA's 44 x 44 — and the fix cannot simply be a bigger pill, because the pill is
 * jewellery on a 163 px poster and a 44 px disc over the artwork reads as a badge, not a control.
 * So the hit area and the glyph are separated on any coarse pointer: the `<button>` is a transparent
 * `min-h-11 min-w-11` box, the visible pill is a `<span>` centred inside it, and the icon does not
 * change size at all. The column's own gap goes to zero there because each box already carries
 * ~9 px of its own margin — three 44 px boxes stack to 132 px inside a 245 px poster with no
 * overlap and no spill, which `askreel-mobile.spec.ts` asserts rather than assumes. Only a fine
 * pointer from `md` up reverts those classes, so the desktop overlay is unchanged.
 *
 * Both actions remove the card from every suggestion surface through the existing
 * `removedTitlesSlice` flow — the mutations dispatch `titleRemoved` on success — so there is no
 * toggle here: an acted-on card is gone from the rail by design. The way back is the undo toast
 * the page raises through `CardUndoProvider` once the action has succeeded (F-19).
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
  const announce = useCardUndo();
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
  // On a coarse pointer (a phone, a tablet, a touch laptop) the BUTTON is the tap target and the
  // `<span>` inside it is the thing you can see: a transparent 44 x 44 box centred on a pill that
  // stays exactly poster-sized. Only a fine pointer from `md` up collapses the box back onto the
  // pill (`md:[@media(pointer:fine)]:min-h-0`), so a desktop overlay is pixel-for-pixel what it has
  // always been and a 1024 px iPad still gets 44 px targets (O-15).
  //
  // The hero's row (below `lg`) is not over art, so there is nothing to reveal it against: it is
  // always visible, every box is a full 44 x 44 target, and the hover-reveal returns at `lg` with
  // the overlay.
  const buttonClass = large
    ? "pointer-events-auto inline-flex min-h-11 min-w-11 lg:[@media(pointer:fine)]:min-h-0 lg:[@media(pointer:fine)]:min-w-0 items-center justify-center " +
      "rounded-full text-fa-frost transition " +
      "lg:text-fa-frost-dim lg:opacity-0 lg:group-hover:opacity-100 lg:focus:opacity-100 lg:focus-visible:opacity-100 lg:[@media(pointer:coarse)]:opacity-60"
    : "pointer-events-auto inline-flex min-h-11 min-w-11 md:[@media(pointer:fine)]:min-h-0 md:[@media(pointer:fine)]:min-w-0 items-center justify-center " +
      "rounded-full text-fa-frost-dim transition " +
      "opacity-0 group-hover:opacity-100 focus:opacity-100 focus-visible:opacity-100 [@media(pointer:coarse)]:opacity-60"
  const pillClass = large
    ? "flex items-center justify-center rounded-full border border-fa-edge bg-fa-glass p-2.5 lg:border-transparent lg:bg-fa-ink/70 lg:p-2 backdrop-blur-sm"
    : "flex items-center justify-center rounded-full bg-fa-ink/70 p-1.5 backdrop-blur-sm";
  const iconClass = large ? "h-4 w-4" : "h-3.5 w-3.5";
  const ref = { titleId, mediaType: mediaType.toLowerCase(), tmdbId };

  // Each action raises its undo only once the server has confirmed it: an undo offered for a write
  // that then failed would reverse nothing and say otherwise. A failure is already visible — the
  // card stays on the rail — so it needs no toast of its own.
  const undoable = { titleId, mediaType: ref.mediaType, tmdbId, name };

  const rate = (rating: number) => {
    setRateOpen(false);
    // A pick you are rating from a suggestion surface is one you have seen — the eligibility rule
    // guarantees it carried no footprint until now — so the play is logged, as on the /rate grid.
    rateTitle({ ...ref, rating, markWatched: true })
      .unwrap()
      .then(() => announce?.({ kind: "rate", ...undoable, rating }))
      .catch(() => undefined);
  };

  const dismiss = (reason: string) => {
    setReasonsOpen(false);
    markNotInterested({ ...ref, reason })
      .unwrap()
      .then(() => announce?.({ kind: "dismiss", ...undoable }))
      .catch(() => undefined);
  };

  const save = () => {
    saveForLater(ref)
      .unwrap()
      .then(() => announce?.({ kind: "save", ...undoable }))
      .catch(() => undefined);
  };

  const openReasons = () => {
    cancelLongPress();
    suppressClick.current = true;
    setReasonsOpen(true);
  };

  return (
    <div
      className={
        large
          ? "relative z-10 flex flex-row gap-2 px-3 pb-3 sm:px-4 lg:absolute lg:right-3 lg:top-3 lg:p-0"
          : "absolute z-10 flex flex-col left-0 top-0 gap-0 md:[@media(pointer:fine)]:left-1.5 md:[@media(pointer:fine)]:top-1.5 md:[@media(pointer:fine)]:gap-1.5"
      }
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
            className={`${buttonClass} hover:text-fa-danger ${reasonsOpen ? "opacity-100 lg:opacity-100" : ""}`}
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
          collisionPadding={12}
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
                className="fa-body min-h-11 rounded-md px-2 py-1.5 text-left text-fa-frost transition hover:bg-fa-frost/10 hover:text-fa-frost-bright [@media(pointer:fine)]:min-h-0"
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
          save();
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
            className={`${buttonClass} hover:text-fa-warning ${rateOpen ? "opacity-100 lg:opacity-100" : ""}`}
            data-testid="quick-rate"
          >
            <span className={pillClass}>
              <Star className={iconClass} />
            </span>
          </button>
        </PopoverAnchor>
        {/* Two rows of five, not one row of ten: at 44 px a target (coarse pointers) a single row
            is 476 px wide and ran off a phone. Radix keeps it inside the viewport with 12 px to
            spare on every side, flipping or shifting it rather than clipping it (O-15). */}
        <PopoverContent
          align="start"
          side="bottom"
          collisionPadding={12}
          className="w-auto p-2"
          data-testid="quick-rate-picker"
        >
          <p className="fa-overline text-fa-frost-dim px-0.5 pb-1.5">your score out of 10</p>
          <div className="grid grid-cols-5 gap-1">
            {Array.from({ length: 10 }, (_, i) => i + 1).map((value) => (
              <button
                key={value}
                type="button"
                onClick={(e) => {
                  e.preventDefault();
                  rate(value);
                }}
                aria-label={`${value} out of 10`}
                className="h-11 w-11 [@media(pointer:fine)]:h-8 [@media(pointer:fine)]:w-9 rounded-md border border-fa-edge bg-fa-glass fa-caption tabular-nums text-fa-frost transition hover:border-fa-frost/40 hover:text-fa-frost-bright"
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
