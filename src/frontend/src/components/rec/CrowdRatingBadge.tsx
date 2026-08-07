import { Star } from "lucide-react";
import { formatVotes } from "../../lib/format";

interface Props {
  rating: number;
  votes?: number | null;
  size?: "sm" | "lg";
}

/**
 * The community score, sat beside the personal prediction so "8.4 for you" finally has something to
 * be measured against — a 8.4-for-you on a title the world rates 5.9 is a very different
 * recommendation from one the world rates 8.6, and until now the card said nothing about which it
 * was.
 *
 * Deliberately NOT a variant of {@link PredictedRatingBadge}. The two numbers must never be
 * confusable at a glance, so they differ on three axes at once, not one: colour (warm amber vs the
 * frost of everything personal), mark (a filled star vs none) and wording ("crowd" vs "for you").
 * Colour alone would fail for the ~8% of men who cannot rely on it.
 *
 * The vote count rides along in the label, quietly — a 9.1 from 12 votes is not a 9.1 from 40,000,
 * and a score with no crowd behind it is closer to noise than to a second opinion.
 */
export default function CrowdRatingBadge({ rating, votes, size = "sm" }: Props) {
  const count = formatVotes(votes);
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full border border-fa-warning/40 bg-fa-ink/80 backdrop-blur-sm font-medium text-fa-warning tabular-nums ${
        size === "lg" ? "px-3 py-1 text-base" : "px-2 py-0.5 text-xs"
      }`}
      title={count ? `Community score — ${count} votes` : "Community score"}
      data-testid="crowd-rating"
    >
      <Star className={size === "lg" ? "h-3.5 w-3.5 fill-current" : "h-3 w-3 fill-current"} aria-hidden />
      {rating.toFixed(1)}
      <span className="fa-overline text-fa-warning/60 font-normal">
        crowd{count ? ` ${count}` : ""}
      </span>
    </span>
  );
}
