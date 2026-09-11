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
import { Link } from "react-router-dom";
import { Share2 } from "lucide-react";
import { toPng } from "html-to-image";
import { useGetSessionQuery, useGetTasteDnaQuery } from "../store/api";
import { profileUrl } from "../lib/tmdbImages";
import { genreBarGeometry, meanMarkerPct } from "../lib/tasteDna";
import ShareCard from "../components/dna/ShareCard";
import DislikePanel from "../components/dna/DislikePanel";
import TasteNarrativePanel from "../components/dna/TasteNarrativePanel";
import TastePockets from "../components/dna/TastePockets";

const FROST = "#A4D4F4";
const FROST_DIM = "#5C8AB4";
const GRID = "rgba(164,212,244,0.12)";
const DRIFT_COLORS = ["#A4D4F4", "#7CE3B6", "#F6C667", "#C4B5FD", "#FDA4AF"];

function ChartTip({ active, payload, label }: { active?: boolean; payload?: { name: string; value: number }[]; label?: string }) {
  if (!active || !payload?.length) return null;
  return (
    <div className="rounded-md border border-fa-edge bg-fa-ink/95 backdrop-blur px-3 py-2">
      {label != null && <p className="fa-overline text-fa-frost-dim mb-1">{label}</p>}
      {payload.map((entry) => (
        <p key={entry.name} className="fa-caption text-fa-frost">
          {entry.name}: <span className="text-fa-frost-bright tabular-nums">{Number(entry.value).toFixed(2)}</span>
        </p>
      ))}
    </div>
  );
}

/** The free hook: who you are, in data. Built screenshot-clean — this page is the viral loop. */
export default function TasteDna() {
  const { data } = useGetTasteDnaQuery();
  const { data: session } = useGetSessionQuery();
  const templateRef = useRef<HTMLDivElement>(null);
  const [sharing, setSharing] = useState(false);
  const [shareError, setShareError] = useState<string | null>(null);

  if (!data) {
    return <div className="h-72 rounded-2xl reel-shimmer" />;
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
    <div className="space-y-8">
      <ShareCard ref={templateRef} data={data} displayName={session?.displayName ?? null} />
      <div className="flex items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-light text-fa-frost-bright">Your taste DNA</h1>
          <p className="fa-caption text-fa-frost-dim mt-1">
            {/* "rated", not "watched" — deliberately. Every title you have ever RATED feeds this,
                with no cutoff; titles you watched without rating do not feed a feature yet, and the
                copy must not claim them until they do. */}
            every title you&apos;ve ever rated counts — all {data.ratingsCount.toLocaleString()} of them, with recent
            ones counting a little more · always yours, always free
          </p>
        </div>
        <div className="shrink-0 text-right space-y-1">
          <button
            onClick={share}
            disabled={sharing}
            data-testid="dna-share"
            className="fa-button-ghost flex items-center gap-2 disabled:opacity-50"
          >
            <Share2 className="h-4 w-4" /> {sharing ? "Rendering…" : "Share card"}
          </button>
          {shareError && (
            <p role="alert" className="fa-caption text-fa-danger" data-testid="dna-share-error">
              {shareError}
            </p>
          )}
        </div>
      </div>

      {/* What Reel believes about you, in words — and your correction to it. Above the charts on
          purpose: the charts describe the data, this describes the person the data was read as,
          and this is the text every downstream model call is actually told. */}
      <TasteNarrativePanel />

      {/* Headline stats */}
      <section className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <div className="fa-card px-4 py-3">
          <p className="fa-stat-label">hours watched</p>
          <p className="fa-stat-value">{Math.round(data.hoursWatched).toLocaleString()}</p>
        </div>
        <div className="fa-card px-4 py-3">
          <p className="fa-stat-label">titles</p>
          <p className="fa-stat-value">{data.titlesWatched.toLocaleString()}</p>
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
        {/* Genre fingerprint */}
        <section className="fa-card p-5">
          <h2 className="fa-section-title mb-1">Genre fingerprint</h2>
          {/* The track is a literal 0–10 axis and the bar is the affinity itself; the tick is the
              user's own mean, so above/below reads against their bar rather than the scale's
              midpoint. Why a fill rather than a mean-diverging span: lib/tasteDna.ts. */}
          <p className="fa-caption text-fa-frost-dim mb-3">
            0–10, against your own {data.userMean.toFixed(1)} average — the tick
          </p>
          <div className="space-y-2.5">
            {data.topGenres.map((g) => {
              const bar = genreBarGeometry(g.affinity, data.userMean);
              return (
                <div key={g.genre} className="flex items-center gap-3">
                  <span className="fa-body text-fa-frost w-36 truncate capitalize">{g.genre.replace("-", " ")}</span>
                  <div
                    className="relative flex-1 h-2.5 rounded-full bg-fa-glass overflow-hidden"
                    role="img"
                    aria-label={`${g.genre.replace("-", " ")}: ${g.affinity.toFixed(1)} out of 10, ${
                      bar.aboveMean ? "at or above" : "below"
                    } your ${data.userMean.toFixed(1)} average`}
                  >
                    <div
                      className={`absolute inset-y-0 left-0 rounded-full ${bar.aboveMean ? "bg-fa-frost/80" : "bg-fa-frost-dim/70"}`}
                      style={{ width: `${bar.fillPct}%` }}
                    />
                    {/* The mean tick — the reference every bar is read against. */}
                    <div
                      className="absolute inset-y-0 w-px bg-fa-frost-bright/70"
                      style={{ left: `${meanMarkerPct(data.userMean)}%` }}
                    />
                  </div>
                  <span className="fa-caption text-fa-frost-bright tabular-nums w-8 text-right">{g.affinity.toFixed(1)}</span>
                  <span className="fa-caption text-fa-frost-dim tabular-nums w-12 text-right">{g.count}×</span>
                </div>
              );
            })}
          </div>
        </section>

        {/* Eras */}
        <section className="fa-card p-5">
          <h2 className="fa-section-title mb-2">Eras</h2>
          <ResponsiveContainer width="100%" height={280}>
            <BarChart data={data.eras} barCategoryGap="25%">
              <CartesianGrid stroke={GRID} vertical={false} />
              <XAxis dataKey="decade" type="category" tick={{ fill: FROST_DIM, fontSize: 11 }} tickFormatter={(d) => `${d}s`} />
              <YAxis domain={[0, 10]} tick={{ fill: FROST_DIM, fontSize: 11 }} width={28} />
              <Tooltip content={<ChartTip />} cursor={{ fill: "rgba(164,212,244,0.06)" }} />
              <Bar dataKey="affinity" fill={FROST} radius={[4, 4, 0, 0]} name="affinity" />
            </BarChart>
          </ResponsiveContainer>
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
          <ResponsiveContainer width="100%" height={240}>
            <LineChart data={driftData}>
              <CartesianGrid stroke={GRID} vertical={false} />
              <XAxis dataKey="year" type="category" tick={{ fill: FROST_DIM, fontSize: 11 }} />
              <YAxis tickFormatter={(v) => `${Math.round(v * 100)}%`} tick={{ fill: FROST_DIM, fontSize: 11 }} width={40} />
              <Tooltip content={<ChartTip />} />
              <Legend wrapperStyle={{ fontSize: 11, color: FROST_DIM }} />
              {driftGenres.map((genre, index) => (
                <Line
                  key={genre}
                  dataKey={genre}
                  stroke={DRIFT_COLORS[index]}
                  strokeWidth={2}
                  dot={false}
                  name={genre.replace("-", " ")}
                />
              ))}
            </LineChart>
          </ResponsiveContainer>
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
                <span className="fa-body text-fa-frost w-40 truncate">{creator.name}</span>
                <div className="flex-1 h-2 rounded-full bg-fa-glass overflow-hidden">
                  <div className="h-full rounded-full bg-fa-frost/70" style={{ width: `${(creator.affinity / 10) * 100}%` }} />
                </div>
                <span className="fa-caption text-fa-frost-bright tabular-nums w-8 text-right">{creator.affinity.toFixed(1)}</span>
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
                  <span className="fa-body text-fa-frost group-hover:text-fa-frost-bright w-40 truncate">{actor.name}</span>
                  <div className="flex-1 h-2 rounded-full bg-fa-glass overflow-hidden">
                    <div className="h-full rounded-full bg-fa-frost/70" style={{ width: `${(actor.rating / 10) * 100}%` }} />
                  </div>
                  <span className="fa-caption text-fa-frost-bright tabular-nums w-8 text-right">{actor.rating}</span>
                </Link>
              ))}
            </div>
          </section>
        )}

        {/* Ratings histogram */}
        <section className="fa-card p-5">
          <h2 className="fa-section-title mb-2">How you rate</h2>
          <ResponsiveContainer width="100%" height={240}>
            <BarChart data={data.histogram}>
              <CartesianGrid stroke={GRID} vertical={false} />
              <XAxis dataKey="rating" tick={{ fill: FROST_DIM, fontSize: 11 }} />
              <YAxis tick={{ fill: FROST_DIM, fontSize: 11 }} width={36} />
              <Tooltip content={<ChartTip />} cursor={{ fill: "rgba(164,212,244,0.06)" }} />
              <Bar dataKey="count" fill={FROST} radius={[4, 4, 0, 0]} name="ratings" />
            </BarChart>
          </ResponsiveContainer>
          <p className="fa-caption text-fa-frost-dim text-center">your mean: {data.userMean.toFixed(1)}</p>
        </section>
      </div>
    </div>
  );
}
