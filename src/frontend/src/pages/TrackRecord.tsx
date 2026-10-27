import { useState } from "react";
import { Link } from "react-router";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { Target } from "lucide-react";
import LoadFailed from "../components/ui/load-failed";
import { Chip } from "../components/ui/chip";
import { COARSE_TAP } from "../lib/tapTarget";
import CrossReferenceBand from "../components/rec/CrossReferenceBand";
import {
  useGetAccuracyQuery,
  useGetCrossReferenceStatsQuery,
  useGetSyncStatusQuery,
  type AccuracyMoment,
  type PopulationNote,
} from "../store/api";
import { modelReadiness, noModelSentence } from "../lib/modelReadiness";
import { posterUrl } from "../lib/tmdbImages";
import { verdictSurface } from "../lib/verdictCopy";
import { relTime } from "../lib/format";
import {
  NO_MODEL_SOURCE_NOTE,
  PROVENANCE_NOTE,
  WINDOW_LABELS,
  agreementNote,
  biasNote,
  headlineSentence,
  learnedSentence,
  num,
  offeredWindows,
  pct,
  signed,
  singleWindowNote,
  sourceNote,
} from "../lib/trackRecordCopy";
import { xrefBacktestSentence, xrefOnlineNote, xrefVerdictLabel } from "../lib/xrefCopy";
import { CHART_CHROME, CHART_SERIES } from "../design/chartPalette";

// From the chart palette (WS-9): every series holds 3:1 on every ink surface, and the axis colour is
// the AA tertiary text the rest of the page uses.
const FROST = CHART_SERIES[0];
const AMBER = CHART_SERIES[1];
const FROST_DIM = CHART_CHROME.axis;
const GRID = CHART_CHROME.grid;

/**
 * Who a block's numbers are about, and how the account's own choices picked that group (G-8). Every
 * rate on this page is over titles you CHOSE to watch, so none is a random sample; the note says
 * which way that leans, beside the number rather than in a footnote.
 */
function Population({ note, testId }: { note: PopulationNote | undefined | null; testId: string }) {
  if (!note) return null;
  return (
    <p className="fa-caption text-3 mt-2" data-testid={testId}>
      <span className="text-fa-frost">{note.name}.</span> {note.selectionNote}
    </p>
  );
}

function ChartTip({ active, payload, label }: { active?: boolean; payload?: { name: string; value: number }[]; label?: string }) {
  if (!active || !payload?.length) return null;
  return (
    <div className="rounded-md border border-fa-edge bg-fa-ink/95 backdrop-blur px-3 py-2">
      {label != null && <p className="fa-overline text-3 mb-1">{label}</p>}
      {payload.map((entry) => (
        <p key={entry.name} className="fa-caption text-fa-frost">
          {entry.name}: <span className="text-fa-frost-bright tabular-nums">{Number(entry.value).toFixed(2)}</span>
        </p>
      ))}
    </div>
  );
}

function MomentCard({ moment }: { moment: AccuracyMoment }) {
  const poster = posterUrl(moment.posterPath, "w185");
  const gap = moment.predicted - moment.actual;
  const missed = Math.abs(gap) > 1;
  const where = verdictSurface(moment.surface);

  return (
    <Link
      to={moment.tmdbId ? `/title/${moment.mediaType.toLowerCase()}/${moment.tmdbId}` : "/track-record"}
      className="w-32 shrink-0 group"
      data-testid="moment-card"
    >
      <div className="aspect-[2/3] rounded-lg overflow-hidden bg-fa-ink-3 border border-fa-edge/50 group-hover:border-fa-frost/40 transition">
        {poster ? (
          <img src={poster} alt="" loading="lazy" className="h-full w-full object-cover" />
        ) : (
          <div className="h-full w-full flex items-center justify-center fa-caption text-3 px-2 text-center">
            {moment.name}
          </div>
        )}
      </div>
      <p className="fa-caption text-fa-frost-bright mt-1.5 truncate">{moment.name}</p>
      <p className="fa-caption tabular-nums">
        <span className={missed ? "text-fa-warning" : "text-fa-success"}>
          {moment.predicted.toFixed(1)} → {moment.actual}
        </span>
      </p>
      {(where || moment.daysToOutcome != null) && (
        <p className="fa-caption text-3 truncate">
          {where ?? "scored"}
          {moment.daysToOutcome != null && ` · ${moment.daysToOutcome}d`}
        </p>
      )}
    </Link>
  );
}

/**
 * The track record — free, linked from the avatar menu for everyone.
 *
 * Everything on this page is deliberately unflattering where the data is unflattering. It exists
 * because the alternative — a product that shows a confident 8.4 on every card and never says how
 * often that 8.4 was right — is the version nobody should trust. The founder's decision was
 * "honest from day one", which means this ships before the numbers are good, not after.
 */
export default function TrackRecord() {
  const [window_, setWindow] = useState<string>("90d");
  // `currentData`, not `data`: `data` keeps the PREVIOUS window's payload while a new window loads
  // or after it fails, which would print 90-day numbers under an "all time" button.
  const { currentData: data, isError, isFetching, refetch } = useGetAccuracyQuery({ window: window_ });
  // Whether there is a model to score anything yet. Without one, "rate a few titles it has already
  // put a number on" sends the user looking for numbers that do not exist (R1-57).
  const { data: sync } = useGetSyncStatusQuery();
  const noModel = noModelSentence(modelReadiness(sync));

  // Only the windows that hold a different set of scored calls (G-9). Until the first payload lands
  // there is no summary, so the switch shows the window being loaded and nothing else.
  // Held across loads: `currentData` is empty while another window loads, and the switch must not
  // collapse under the user's finger. Refreshed from every payload that does land.
  const [summary, setSummary] = useState<{ from: typeof data; offered: string[]; note: string | null } | null>(null);
  if (data && summary?.from !== data) {
    setSummary({ from: data, offered: offeredWindows(data.windows), note: singleWindowNote(data.windows) });
  }
  // Before any summary: while loading, only the window being asked for (no buttons to jump about);
  // after a failure, every window, so another one is still one tap away.
  const offered = summary?.offered ?? (isError ? offeredWindows(undefined) : [window_]);
  const singleNote = summary?.note ?? null;

  // The window switch stays on screen through every state: a skeleton or an error that also took
  // the buttons away would leave no way to step back to the window that did load.
  const header = (
    <div className="flex items-start justify-between gap-4 flex-wrap">
      <div>
        <h1 className="text-2xl font-light text-fa-frost-bright flex items-center gap-2">
          <Target className="h-6 w-6 text-fa-frost" /> Reel's track record
        </h1>
        <p className="fa-caption text-3 mt-1">
          How wrong Reel has been about you, and what it is doing about it.
        </p>
      </div>
      {offered.length > 1 ? (
        <div className="flex items-center gap-1" role="group" aria-label="Time window" data-testid="window-switch">
          {offered.map((value) => (
            <Chip key={value} selected={window_ === value} onClick={() => setWindow(value)} className={COARSE_TAP}>
              {WINDOW_LABELS[value] ?? value}
            </Chip>
          ))}
        </div>
      ) : (
        singleNote && (
          <p className="fa-caption text-3 max-w-xs sm:text-right" data-testid="single-window-note">
            {singleNote}
          </p>
        )
      )}
    </div>
  );

  // Three states before the page. A failed read used to shimmer for ever (`isLoading || !data`
  // stays true on an error), so "the honest number" page was the one that never admitted a failure.
  if (!data) {
    return (
      <div className="space-y-6">
        {header}
        {isError ? (
          <LoadFailed what="the track record" onRetry={() => void refetch()} retrying={isFetching} testId="track-record-error" />
        ) : (
          <div className="h-72 rounded-2xl reel-shimmer" data-testid="track-record-loading" />
        )}
      </div>
    );
  }

  const empty = data.headline.n === 0;
  const calibration = data.calibration
    .filter((b) => b.n > 0)
    .map((b) => ({
      band: `${b.low.toFixed(0)}–${b.high.toFixed(0)}`,
      predicted: b.meanPredicted,
      actual: b.meanActual,
      n: b.n,
    }));

  return (
    <div className="space-y-6" data-testid="track-record">
      {header}

      {/* Headline — the one sentence the page exists to say. */}
      <section className="fa-card p-5" data-testid="headline">
        <p className="fa-overline text-3">the honest number</p>
        <p className="text-3xl font-light text-fa-frost-bright mt-1" data-testid="headline-sentence">
          {headlineSentence(data)}
        </p>
        {/* Only on the empty page: numbers already on screen were scored by a model, and a line
            saying there is none would contradict them (seen on the slot with the Active artifact
            retired under 65 scored cards). */}
        {empty && noModel && (
          <p className="fa-body text-fa-frost mt-2" data-testid="track-record-no-model">
            {noModel}
          </p>
        )}
        <p className="fa-caption text-3 mt-2" data-testid="source-note">
          {empty && noModel ? NO_MODEL_SOURCE_NOTE : sourceNote(data)}
        </p>
        {!empty && <Population note={data.populations?.headline} testId="headline-population" />}
        {/* The empty state's way forward. The page fills as you rate titles Reel had already scored
            for you; /rate offers what you've watched and never rated (plus popular titles), so the
            button says that rather than promising a list of scored titles it does not show. */}
        {empty && (
          <Link to="/rate" className={`fa-button-primary inline-flex mt-3 ${COARSE_TAP}`} data-testid="track-record-empty-rate">
            Rate what you&apos;ve watched
          </Link>
        )}

        {!empty && (
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 mt-4">
            <div className="fa-card px-4 py-3">
              <p className="fa-stat-label">average miss</p>
              <p className="fa-stat-value">{num(data.headline.meanAbsoluteError)}</p>
              <p className="fa-caption text-3 mt-1">rating points, either way</p>
            </div>
            <div className="fa-card px-4 py-3">
              <p className="fa-stat-label">bias</p>
              <p className="fa-stat-value">{signed(data.headline.bias)}</p>
              <p className="fa-caption text-3 mt-1">{biasNote(data.headline.bias)}</p>
            </div>
            <div className="fa-card px-4 py-3">
              <p className="fa-stat-label">agreement</p>
              <p className="fa-stat-value">{num(data.headline.correlation, 3)}</p>
              <p className="fa-caption text-3 mt-1">{agreementNote(data.headline.correlation)}</p>
            </div>
            <div className="fa-card px-4 py-3">
              <p className="fa-stat-label">scored on</p>
              <p className="fa-stat-value">{data.headline.n}</p>
              <p className="fa-caption text-3 mt-1">
                predictions you have since rated
              </p>
            </div>
          </div>
        )}
        {/* Every rating is a new scored call, so the way to a fairer number is always one tap away —
            not only on the empty page (G-19). */}
        {!empty && (
          <Link to="/rate" className={`fa-link fa-caption inline-flex items-center mt-3 ${COARSE_TAP}`} data-testid="track-record-rate">
            Rate what you&apos;ve watched →
          </Link>
        )}
      </section>

      {/* Calibration — the shape of the wrongness, not just its size. */}
      <section className="fa-card p-5" data-testid="calibration">
        <h2 className="fa-section-title mb-1">Where the number lands</h2>
        <p className="fa-caption text-3 mb-3">
          For every band Reel predicts in, what you actually gave. A model that meant what it said
          would have the two bars level in every band.
        </p>
        {calibration.length === 0 ? (
          <p className="fa-body text-3" data-testid="calibration-empty">
            No bands to draw yet — nothing Reel has scored has come back with a rating.
          </p>
        ) : (
          <ResponsiveContainer width="100%" height={280}>
            <BarChart data={calibration} barCategoryGap="25%">
              <CartesianGrid stroke={GRID} vertical={false} />
              <XAxis dataKey="band" type="category" tick={{ fill: FROST_DIM, fontSize: 11 }} />
              <YAxis domain={[0, 10]} tick={{ fill: FROST_DIM, fontSize: 11 }} width={28} />
              <Tooltip content={<ChartTip />} cursor={{ fill: "rgba(164,212,244,0.06)" }} />
              <Legend wrapperStyle={{ fontSize: 11, color: FROST_DIM }} />
              <Bar dataKey="predicted" fill={FROST} radius={[4, 4, 0, 0]} name="Reel said" />
              <Bar dataKey="actual" fill={AMBER} radius={[4, 4, 0, 0]} name="you said" />
            </BarChart>
          </ResponsiveContainer>
        )}
      </section>

      {/* Moments — the specific claims, newest first. */}
      <section className="fa-card p-5" data-testid="moments">
        <h2 className="fa-section-title mb-1">Recent calls</h2>
        <p className="fa-caption text-3 mb-3">
          What Reel said, what you said. Newest first.
        </p>
        {data.moments.length === 0 ? (
          <p className="fa-body text-3" data-testid="moments-empty">
            No calls to show yet. Rate something Reel has already put a number on and it appears here.
          </p>
        ) : (
          <div className="flex gap-3 overflow-x-auto pb-2">
            {data.moments.map((m) => (
              <MomentCard key={`${m.titleId}-${m.ratedAt}`} moment={m} />
            ))}
          </div>
        )}
      </section>

      {/* Conversion — the honest half. These numbers are about the loop, not the model. */}
      <section className="fa-card p-5" data-testid="conversion">
        <h2 className="fa-section-title mb-1">The queue, end to end</h2>
        <p className="fa-caption text-3 mb-3">
          Shown → saved → watched. This measures whether the loop closes, which is a different
          question from whether the number was right.
        </p>
        <Population note={data.populations?.conversion} testId="conversion-population" />
        <div className="mb-3" />
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">shown → saved</p>
            <p className="fa-stat-value">{pct(data.conversion.shownToSaved, 1)}</p>
            <p className="fa-caption text-3 mt-1">
              {data.conversion.saved} of {data.conversion.shown || "—"} titles
            </p>
          </div>
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">saved → watched</p>
            <p className="fa-stat-value">{pct(data.conversion.savedToWatched, 1)}</p>
            <p className="fa-caption text-3 mt-1">{data.conversion.watched} watched</p>
          </div>
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">shown → rated</p>
            <p className="fa-stat-value">{pct(data.conversion.shownToRated, 1)}</p>
            <p className="fa-caption text-3 mt-1">{data.conversion.rated} rated</p>
          </div>
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">days to watch</p>
            <p className="fa-stat-value">{num(data.conversion.medianDaysToWatch, 1)}</p>
            <p className="fa-caption text-3 mt-1">median, once saved</p>
          </div>
        </div>
      </section>

      {/* The counterfactual — the one number that says whether Reel helped at all. */}
      {data.counterfactual.length > 0 && (
        <section className="fa-card p-5" data-testid="counterfactual">
          <h2 className="fa-section-title mb-1">Because of Reel, vs found yourself</h2>
          <p className="fa-caption text-3 mb-3">
            Your own ratings, split by how the title reached you. A negative gap means Reel steered
            you toward films you liked less than the ones you found on your own.
          </p>
          <Population note={data.populations?.counterfactual} testId="counterfactual-population" />
          <div className="mb-3" />
          <div className="space-y-1">
            {data.counterfactual.map((w) => (
              <p key={w.window} className="fa-body">
                <span className="text-3">
                  {w.days == null ? "all time" : `last ${w.days} days`}:
                </span>{" "}
                via Reel {num(w.viaReelMeanRating)} ({w.viaReelCount}) · found yourself{" "}
                {num(w.otherMeanRating)} ({w.otherCount}) ·{" "}
                <span className={w.delta != null && w.delta < 0 ? "text-fa-danger" : "text-fa-success"}>
                  {signed(w.delta)}
                </span>
              </p>
            ))}
          </div>
        </section>
      )}

      {/* The cross-reference pass, in words. Its statistics (AUC, intervals, prompt generations) moved
          to the lab (G-10, KILL-11); what stays is what a verdict is allowed to do to your page and
          why, which the Home pill points here for. The band — the running pass and what its last run
          concluded — moved here from Home, which now keeps one status line (FD-15). */}
      <div className="space-y-3">
        <CrossReferenceBand />
        <CrossReferenceSummary window={window_} />
      </div>

      {/* What changed since last time — the "and what I'm doing about it" half. */}
      {data.learnedThisWeek && (
        <section className="fa-card p-5" data-testid="learned">
          <h2 className="fa-section-title mb-1">What Reel learned since last retrain</h2>
          <p className="fa-body text-fa-frost mt-2" data-testid="learned-sentence">
            {learnedSentence(data.learnedThisWeek)}
          </p>
          <p className="fa-caption text-3 mt-2">
            Retrained {relTime(data.learnedThisWeek.trainedAt)} · bias{" "}
            {signed(data.learnedThisWeek.calibrationBias)} · average miss{" "}
            {num(data.learnedThisWeek.calibrationMae)}
          </p>
        </section>
      )}

      <p className="fa-caption text-3" data-testid="track-record-provenance">
        {PROVENANCE_NOTE} <Link to="/dna" className="underline underline-offset-2 hover:text-fa-frost">See your taste profile</Link>.
      </p>
    </div>
  );
}

/**
 * The cross-reference pass in plain words, from /api/metrics/cross-reference (WS-5 task 11). Renders
 * nothing until there is something measured to say, and its own failure inline — never the page's.
 */
function CrossReferenceSummary({ window }: { window: string }) {
  const { currentData, isError, isFetching, refetch } = useGetCrossReferenceStatsQuery({ window });
  if (!currentData) {
    return isError ? (
      <LoadFailed
        what="the cross-reference results"
        onRetry={() => void refetch()}
        retrying={isFetching}
        variant="inline"
        testId="cross-reference-error"
      />
    ) : null;
  }

  const { crossReference: xref, population } = currentData;
  // The generation running now decides what a verdict may do; an older one is history for the lab.
  const backtest = xref.backtests.find((b) => b.current) ?? xref.backtests[0];
  if (!backtest && xref.online.length === 0) return null;

  return (
    <section className="fa-card p-5" data-testid="cross-reference">
      <h2 className="fa-section-title mb-1">Cross-reference</h2>
      <p className="fa-caption text-3">
        A second pass reads every eligible title beside the ones you have actually rated, and says
        keep, rule out, or not sure. What that verdict is allowed to do to your page was decided by
        testing it against your own history.
      </p>
      <Population note={population} testId="cross-reference-population" />

      {backtest && (
        <p className="fa-body text-fa-frost mt-3" data-testid="cross-reference-backtest">
          {xrefBacktestSentence(backtest)}
        </p>
      )}

      {/* The online half. It cannot reach significance and says so per verdict rather than once at
          the bottom: only ~2% of ratings are made in the app, so this corroborates the backtest and
          never replaces it. */}
      <div className="mt-4">
        <p className="fa-overline text-3">since it went live</p>
        {xref.online.length === 0 ? (
          <p className="fa-caption text-3 mt-1" data-testid="cross-reference-online-empty">
            Nothing it has judged has reached you and come back with a rating yet.
          </p>
        ) : (
          <div className="space-y-1 mt-1">
            {xref.online.map((o) => (
              <p key={o.verdict} className="fa-body" data-testid="cross-reference-online-row">
                <span className="text-3">{xrefVerdictLabel(o.verdict)}:</span>{" "}
                <span className={o.answerable ? "text-fa-frost" : "text-3"}>{xrefOnlineNote(o)}</span>
              </p>
            ))}
          </div>
        )}
      </div>
    </section>
  );
}
