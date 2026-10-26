import { divergingBars } from "../../lib/tasteDna";

export interface DivergingRow {
  key: string;
  label: string;
  affinity: number;
  /** How many of your ratings stand behind the number — an integer, printed as one. */
  count: number;
}

const BAR_TONE = {
  above: "bg-fa-frost/80",
  below: "bg-fa-warning/70",
  level: "",
} as const;

/**
 * Affinities drawn against the user's own mean: a centre line at the mean, bars to the right for
 * what beats it and to the left for what falls short, on a scale fitted to the spread (geometry and
 * the reasoning in lib/tasteDna.ts). Below `sm` each label sits above its bar, so the bar gets the
 * full row instead of what a 9rem label leaves of a 390 px screen.
 */
export default function DivergingBars({
  rows,
  userMean,
  testId,
}: {
  rows: DivergingRow[];
  userMean: number;
  testId: string;
}) {
  const chart = divergingBars(
    rows.map((r) => r.affinity),
    userMean,
  );
  const mean = userMean.toFixed(1);

  return (
    <div className="space-y-3" data-testid={testId}>
      <ol className="space-y-2.5">
        {rows.map((row, index) => {
          const bar = chart.bars[index];
          const rated = `${row.count.toLocaleString("en-US")} rated`;
          const relation =
            bar.side === "level" ? `level with your ${mean} average` : `${bar.deltaLabel} against your ${mean} average`;
          return (
            <li key={row.key} className="sm:flex sm:items-center sm:gap-3" data-testid={`${testId}-row`}>
              {/* On phones the count rides on the label line, so the bar keeps the row's width. */}
              <div className="flex items-baseline justify-between gap-3 sm:w-36 sm:shrink-0">
                <span className="min-w-0 fa-body text-fa-frost truncate capitalize">{row.label}</span>
                <span className="fa-caption text-fa-frost-dim tabular-nums sm:hidden">{rated}</span>
              </div>
              <div className="mt-1 flex min-w-0 flex-1 items-center gap-3 sm:mt-0">
                <div
                  className="relative h-2.5 flex-1 rounded-full bg-fa-glass"
                  role="img"
                  aria-label={`${row.label}: ${row.affinity.toFixed(1)} out of 10, ${relation}, from ${row.count.toLocaleString("en-US")} ratings`}
                  data-testid={`${testId}-track`}
                >
                  {/* The centre line is your mean — every bar is read against it. */}
                  <div className="absolute inset-y-[-3px] left-1/2 w-px bg-fa-frost-bright/70" />
                  {bar.widthPct > 0 && (
                    <div
                      className={`absolute inset-y-0 rounded-full ${BAR_TONE[bar.side]}`}
                      style={{ left: `${bar.leftPct}%`, width: `${bar.widthPct}%` }}
                      data-testid={`${testId}-bar`}
                      data-side={bar.side}
                    />
                  )}
                </div>
                <span className="fa-caption text-fa-frost-bright tabular-nums w-8 text-right">{row.affinity.toFixed(1)}</span>
                <span
                  className={`fa-caption tabular-nums w-10 text-right ${
                    bar.side === "above" ? "text-fa-frost" : bar.side === "below" ? "text-fa-warning" : "text-fa-frost-dim"
                  }`}
                >
                  {bar.deltaLabel}
                </span>
                <span className="hidden fa-caption text-fa-frost-dim tabular-nums w-20 text-right sm:inline">{rated}</span>
              </div>
            </li>
          );
        })}
      </ol>
      {/* The scale, said out loud: the chart is fitted to the spread, so a reader must be told what
          the edge of a bar means or a 0.4 gap reads as a 4-point one. */}
      <p className="fa-caption text-fa-frost-dim flex flex-wrap justify-between gap-x-3" data-testid={`${testId}-scale`}>
        <span>← below your {mean} average</span>
        <span>a bar reaching the edge is {chart.scale.toFixed(2)} points from it</span>
        <span>above →</span>
      </p>
    </div>
  );
}
