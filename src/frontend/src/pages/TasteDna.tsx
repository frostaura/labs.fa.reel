import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { useRef, useState } from "react";
import { Link } from "react-router";
import { Share2 } from "lucide-react";
import { toPng } from "html-to-image";
import {
  DISLIKES_FIRST_PAGE,
  useGetDislikesQuery,
  useGetNarrativeQuery,
  useGetSessionQuery,
  useGetSyncStatusQuery,
  useGetTasteDnaQuery,
} from "../store/api";
import { useSettledGate } from "../lib/useSettledGate";
import { modelReadiness, noModelSentence } from "../lib/modelReadiness";
import { profileUrl } from "../lib/tmdbImages";
import ShareCard from "../components/dna/ShareCard";
import DislikePanel from "../components/dna/DislikePanel";
import TasteNarrativePanel from "../components/dna/TasteNarrativePanel";
import TastePockets from "../components/dna/TastePockets";
import DivergingBars from "../components/dna/DivergingBars";
import LoadFailed from "../components/ui/load-failed";
import { CHART_CHROME, CHART_SERIES, seriesColour } from "../design/chartPalette";
import { Button } from "../components/ui/button";
import { COARSE_TAP } from "../lib/tapTarget";
import { driftChartLabel, histogramChartLabel } from "../lib/tasteDna";

// From the chart palette (WS-9): every series holds 3:1 on every ink surface, and the axis colour is
// the AA tertiary text the rest of the page uses.
const FROST = CHART_SERIES[0];
const FROST_DIM = CHART_CHROME.axis;
const GRID = CHART_CHROME.grid;

/** Counts are whole numbers and shares are percentages — a tooltip reading "ratings: 12.00" was a
 *  float format applied to an integer (P1, integer counts). */
const asCount = (value: number) => Math.round(value).toLocaleString("en-US");
const asShare = (value: number) => `${Math.round(value * 100)}%`;

/** How long the page waits for the narrative, the dislikes and the status before drawing anyway. */
const PANELS_CAP_MS = 3000;

function ChartTip({
  active,
  payload,
  label,
  format,
}: {
  active?: boolean;
  payload?: { name: string; value: number }[];
  label?: string | number;
  format: (value: number) => string;
}) {
  if (!active || !payload?.length) return null;
  return (
    <div className="rounded-md border border-fa-edge bg-fa-ink/95 backdrop-blur px-3 py-2">
      {label != null && <p className="fa-overline text-fa-frost-dim mb-1">{label}</p>}
      {payload.map((entry) => (
        <p key={entry.name} className="fa-caption text-fa-frost">
          {entry.name}: <span className="text-fa-frost-bright tabular-nums">{format(Number(entry.value))}</span>
        </p>
      ))}
    </div>
  );
}

/** The free hook: who you are, in data. Built screenshot-clean — this page is the viral loop. */
export default function TasteDna() {
  const { currentData: data, isError, isFetching, refetch } = useGetTasteDnaQuery();
  const { data: session } = useGetSessionQuery();
  // Nothing on this page needs the model — it reads the ratings — so a missing model is a line of
  // context, never a gate or a failure (R1-57).
  const { data: sync, isLoading: syncLoading } = useGetSyncStatusQuery();
  // The page is drawn once, whole (HREV1-D03). The narrative panel above the stats used to grow from
  // a 160 px shimmer when its own read landed, the dislikes panel appeared under the pockets, and
  // the no-model line appeared under the header — each moving every chart below it (CLS 0.22 at
  // 390, 0.06 at 834). These are the same cached reads the panels make; the page only waits on them.
  const { isLoading: narrativeLoading } = useGetNarrativeQuery();
  const { isLoading: dislikesLoading } = useGetDislikesQuery(DISLIKES_FIRST_PAGE);
  const panelsSettled = useSettledGate(!narrativeLoading && !dislikesLoading && !syncLoading, PANELS_CAP_MS);
  const noModel = noModelSentence(modelReadiness(sync));
  const templateRef = useRef<HTMLDivElement>(null);
  const [sharing, setSharing] = useState(false);
  const [shareError, setShareError] = useState<string | null>(null);

  // Three states before the page, never two: a failed read used to shimmer for ever, which reads as
  // "still thinking" to someone who will wait for it.
  if (!data || !panelsSettled) {
    if (!data && isError) {
      return <LoadFailed what="your taste DNA" onRetry={() => void refetch()} retrying={isFetching} testId="dna-error" />;
    }
    return <div className="h-72 rounded-2xl reel-shimmer" data-testid="dna-loading" />;
  }

  // Nothing rated yet: every chart below would be an empty frame. The one useful thing to say is
  // where the ratings come from.
  if (data.ratingsCount === 0) {
    return (
      <section className="fa-card px-6 py-8 text-center space-y-3" data-testid="dna-empty">
        <h1 className="text-2xl font-light text-fa-frost-bright">Your taste DNA</h1>
        <p className="fa-body text-fa-frost">
          Nothing to chart yet — the taste charts are built from the titles you&apos;ve rated, and there are none so far.
        </p>
        <Link to="/rate" className={`fa-button-primary inline-flex ${COARSE_TAP}`} data-testid="dna-empty-rate">
          Rate a few titles
        </Link>
      </section>
    );
  }

  const driftData = data.drift.map((d) => ({ year: d.year, ...d.shares }));
  const driftGenres = data.topGenres.slice(0, 5).map((g) => g.genre);

  // Export the dedicated offscreen template (components/dna/ShareCard), never this page.
  // Screenshotting the live dashboard is what produced the half-blank card: its Recharts
  // sections measure themselves from the live DOM and rasterise as empty boxes in the clone.
  // The template is plain divs + inline SVG, fixed-size, with no <img> to filter or taint.
  const share = async () => {
    if (!templateRef.current) return;
    setSharing(true);
    setShareError(null);
    try {
      const dataUrl = await toPng(templateRef.current, {
        backgroundColor: "#06121F",
        pixelRatio: 2,
        skipFonts: false, // Figtree is self-hosted (@fontsource) — same-origin, embeddable.
      });
      const link = document.createElement("a");
      link.download = "my-reel-taste-dna.png";
      link.href = dataUrl;
      link.click();
    } catch {
      setShareError("Couldn’t render your card — try again.");
    } finally {
      setSharing(false);
    }
  };

  return (
    <>
      {/* Outside the spaced column: the off-screen template is its first child otherwise, and the
          header's space-y margin then collapses through the column's top edge — the whole page sat
          32 px lower than the shimmer it replaced, a layout shift of everything on screen. */}
      <ShareCard ref={templateRef} data={data} displayName={session?.displayName ?? null} />
      <div className="space-y-8">
        <div className="flex items-start justify-between gap-3">
          <div>
            <h1 className="text-2xl font-light text-fa-frost-bright">Your taste DNA</h1>
            <p className="fa-caption text-fa-frost-dim mt-1">
              {/* "rated", not "watched" — deliberately. Every title you have ever RATED feeds this,
                  with no cutoff; titles you watched without rating do not feed a feature yet, and the
                  copy must not claim them until they do. */}
              every title you&apos;ve ever rated counts — all {data.ratingsCount.toLocaleString("en-US")} of them · always
              yours, always free
            </p>
          </div>
          <div className="shrink-0 text-right space-y-1">
            <Button variant="ghost" onClick={share} disabled={sharing} data-testid="dna-share" className={COARSE_TAP}>
              <Share2 className="h-4 w-4" /> {sharing ? "Rendering…" : "Share card"}
            </Button>
            {shareError && (
              <p role="alert" className="fa-caption text-fa-danger" data-testid="dna-share-error">
                {shareError}
              </p>
            )}
          </div>
        </div>

        {noModel && (
          <p className="fa-card px-4 py-3 fa-caption text-fa-frost" data-testid="dna-no-model">
            {noModel} Everything below comes from what you&apos;ve rated, watched and dismissed, so none of it waits for the model.
          </p>
        )}

        {/* What Reel believes about you, in words — and your correction to it. Above the charts on
            purpose: the charts describe the data, this describes the person the data was read as,
            and this is the text every downstream model call is actually told. */}
        <TasteNarrativePanel />

        {/* Headline stats */}
        <section className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">hours watched</p>
            <p className="fa-stat-value">{Math.round(data.hoursWatched).toLocaleString("en-US")}</p>
          </div>
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">titles</p>
            <p className="fa-stat-value">{data.titlesWatched.toLocaleString("en-US")}</p>
          </div>
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">show completion</p>
            <p className="fa-stat-value">{Math.round(data.showCompletionRate * 100)}%</p>
          </div>
          <div className="fa-card px-4 py-3">
            <p className="fa-stat-label">contrarian score</p>
            <p className="fa-stat-value">
              {data.contrarianScore > 0 ? "+" : ""}
              {data.contrarianScore.toFixed(1)}
            </p>
            <p className="fa-caption text-fa-frost-dim">{data.contrarianScore > 0.3 ? "kinder than the crowd" : data.contrarianScore < -0.3 ? "harsher than the crowd" : "with the crowd"}</p>
          </div>
        </section>

        <div className="grid lg:grid-cols-2 gap-6">
          {/* Genre fingerprint — diverging from your own mean, scaled to the spread (lib/tasteDna.ts).
              On a 0–10 fill every genre sat at 65–72% of the track and the chart said nothing. */}
          <section className="fa-card p-5">
            <h2 className="fa-section-title mb-1">Genre fingerprint</h2>
            <p className="fa-caption text-fa-frost-dim mb-3">
              How each genre you rate a lot scores against your own {data.userMean.toFixed(1)} average.
            </p>
            <DivergingBars
              testId="genre-fingerprint"
              userMean={data.userMean}
              rows={data.topGenres.map((g) => ({
                key: g.genre,
                label: g.genre.replace("-", " "),
                affinity: g.affinity,
                count: g.count,
              }))}
            />
          </section>

          {/* Eras — the same chart, so the two read on the same terms. */}
          <section className="fa-card p-5">
            <h2 className="fa-section-title mb-1">Eras</h2>
            <p className="fa-caption text-fa-frost-dim mb-3">
              Each decade you&apos;ve rated at least five titles from, against the same average.
            </p>
            <DivergingBars
              testId="era-fingerprint"
              userMean={data.userMean}
              rows={data.eras.map((e) => ({
                key: String(e.decade),
                label: `${e.decade}s`,
                affinity: e.affinity,
                count: e.count,
              }))}
            />
          </section>
        </div>

        {/* The pockets retrieval actually pulls from — the answer to "does it know I also like X". */}
        <TastePockets clusters={data.clusters} />

        {/* What you've said no to — the strongest negative signal, made legible and revocable. */}
        <DislikePanel />

        {/* Taste drift */}
        {driftData.length > 1 && (
          <section className="fa-card p-5">
            <h2 className="fa-section-title mb-2">Taste drift</h2>
            <p className="fa-caption text-fa-frost-dim mb-3">share of your loved ratings per genre, year by year</p>
            {/* One named image, not Recharts' unnamed role="application" tab stop (PH2-02): the
                accessibility layer is off, and the name reads the chart's own numbers. */}
            <div role="img" aria-label={driftChartLabel(data.drift, driftGenres)} data-testid="drift-chart">
              <ResponsiveContainer width="100%" height={240}>
                <LineChart data={driftData} accessibilityLayer={false}>
                  <CartesianGrid stroke={GRID} vertical={false} />
                  <XAxis dataKey="year" type="category" tick={{ fill: FROST_DIM, fontSize: 11 }} />
                  <YAxis tickFormatter={(v) => `${Math.round(v * 100)}%`} tick={{ fill: FROST_DIM, fontSize: 11 }} width={40} />
                  <Tooltip content={<ChartTip format={asShare} />} />
                  <Legend wrapperStyle={{ fontSize: 11, color: FROST_DIM }} />
                  {driftGenres.map((genre, index) => (
                    <Line
                      key={genre}
                      dataKey={genre}
                      stroke={seriesColour(index)}
                      strokeWidth={2}
                      dot={false}
                      name={genre.replace("-", " ")}
                    />
                  ))}
                </LineChart>
              </ResponsiveContainer>
            </div>
          </section>
        )}

        <div className="grid lg:grid-cols-2 gap-6">
          {/* Creators */}
          <section className="fa-card p-5">
            <h2 className="fa-section-title mb-3">Creator affinities</h2>
            <div className="space-y-2.5">
              {data.creators.slice(0, 8).map((creator) => (
                <div key={creator.name} className="flex items-center gap-3">
                  <div className="h-8 w-8 rounded-full overflow-hidden bg-fa-ink-3 shrink-0">
                    {profileUrl(creator.profilePath) ? (
                      <img src={profileUrl(creator.profilePath)!} alt="" loading="lazy" className="h-full w-full object-cover" />
                    ) : (
                      <div className="h-full w-full flex items-center justify-center fa-caption text-fa-frost-dim">
                        {creator.name[0]}
                      </div>
                    )}
                  </div>
                  {/* Name above the bar on phones, beside it from sm (P1): a 10rem name left a 390 px
                      screen a bar too short to compare. */}
                  <div className="min-w-0 flex-1 sm:flex sm:items-center sm:gap-3" data-testid="creator-row">
                    <span className="block fa-body text-fa-frost truncate sm:w-40 sm:shrink-0">{creator.name}</span>
                    <div className="mt-1 flex flex-1 items-center gap-3 sm:mt-0">
                      <div className="flex-1 h-2 rounded-full bg-fa-glass overflow-hidden">
                        <div className="h-full rounded-full bg-fa-frost/70" style={{ width: `${(creator.affinity / 10) * 100}%` }} />
                      </div>
                      <span className="fa-caption text-fa-frost-bright tabular-nums w-8 text-right">{creator.affinity.toFixed(1)}</span>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </section>

          {/* Top rated actors — explicit person ratings */}
          {data.topActors.length > 0 && (
            <section className="fa-card p-5" data-testid="top-actors">
              <h2 className="fa-section-title mb-3">Top rated actors</h2>
              <div className="space-y-2.5">
                {data.topActors.map((actor) => (
                  <Link key={actor.personId} to={`/person/${actor.personId}`} className="flex items-center gap-3 group">
                    <div className="h-8 w-8 rounded-full overflow-hidden bg-fa-ink-3 shrink-0">
                      {profileUrl(actor.profilePath) ? (
                        <img src={profileUrl(actor.profilePath)!} alt="" loading="lazy" className="h-full w-full object-cover" />
                      ) : (
                        <div className="h-full w-full flex items-center justify-center fa-caption text-fa-frost-dim">
                          {actor.name[0]}
                        </div>
                      )}
                    </div>
                    <div className="min-w-0 flex-1 sm:flex sm:items-center sm:gap-3">
                      <span className="block fa-body text-fa-frost group-hover:text-fa-frost-bright truncate sm:w-40 sm:shrink-0">
                        {actor.name}
                      </span>
                      <div className="mt-1 flex flex-1 items-center gap-3 sm:mt-0">
                        <div className="flex-1 h-2 rounded-full bg-fa-glass overflow-hidden">
                          <div className="h-full rounded-full bg-fa-frost/70" style={{ width: `${(actor.rating / 10) * 100}%` }} />
                        </div>
                        <span className="fa-caption text-fa-frost-bright tabular-nums w-8 text-right">{actor.rating}</span>
                      </div>
                    </div>
                  </Link>
                ))}
              </div>
            </section>
          )}

          {/* Ratings histogram */}
          <section className="fa-card p-5">
            <h2 className="fa-section-title mb-2">How you rate</h2>
            <div role="img" aria-label={histogramChartLabel(data.histogram, data.userMean)} data-testid="rating-histogram-chart">
              <ResponsiveContainer width="100%" height={240}>
                <BarChart data={data.histogram} accessibilityLayer={false}>
                  <CartesianGrid stroke={GRID} vertical={false} />
                  <XAxis dataKey="rating" tick={{ fill: FROST_DIM, fontSize: 11 }} />
                  <YAxis tick={{ fill: FROST_DIM, fontSize: 11 }} width={36} />
                  <Tooltip content={<ChartTip format={asCount} />} cursor={{ fill: "rgba(164,212,244,0.06)" }} />
                  <Bar dataKey="count" fill={FROST} radius={[4, 4, 0, 0]} name="ratings" />
                </BarChart>
              </ResponsiveContainer>
            </div>
            <p className="fa-caption text-fa-frost-dim text-center">your mean: {data.userMean.toFixed(1)}</p>
          </section>
        </div>
      </div>
    </>
  );
}
