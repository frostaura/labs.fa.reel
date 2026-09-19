import { forwardRef } from "react";
import type { TasteDnaPayload } from "../../store/api";
import { shareCardModel } from "../../lib/shareCard";

/**
 * The dedicated Taste DNA share template: a fixed 1080×1350 (4:5 portrait, the social-feed
 * aspect) card rendered offscreen and rasterised by `toPng` — never the live dashboard.
 *
 * Everything here is deliberate about surviving html-to-image's offscreen clone:
 *   - fixed pixel geometry via inline styles, so nothing measures the viewport;
 *   - the genre fingerprint is plain divs driven by the pure `lib/tasteDna.ts` geometry —
 *     no Recharts, whose live-DOM measurement is why the old export came out half blank;
 *   - the wordmark is inline SVG (the export used to filter out every <img>, so a linked
 *     asset would vanish from the capture);
 *   - no external images at all, so nothing can taint the canvas.
 *
 * Colors are the app palette, hardcoded: the card must render identically wherever the PNG
 * lands, not track a future theme.
 */

const INK = "#06121F";
const INK_2 = "#0B1B30";
const EDGE = "rgba(164,212,244,0.14)";
const FROST = "#A4D4F4";
const FROST_BRIGHT = "#E7F4FD";
const FROST_DIM = "#5C8AB4";
const GLASS = "rgba(164,212,244,0.08)";

function Wordmark() {
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 18 }}>
      <svg width="64" height="64" viewBox="0 0 40 40" fill="none" aria-hidden>
        <defs>
          <linearGradient id="share-reel-grad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#BAE6FD" />
            <stop offset="100%" stopColor="#60A5FA" />
          </linearGradient>
        </defs>
        <path d="M32.6 15.16 A 13.5 13.5 0 1 1 24.84 7.4" stroke="url(#share-reel-grad)" strokeWidth="2.25" strokeLinecap="round" opacity="0.8" />
        <path d="M16 13.7 L 27.2 20 L 16 26.3 Z" fill="url(#share-reel-grad)" stroke="url(#share-reel-grad)" strokeWidth="2.6" strokeLinejoin="round" />
        <g transform="translate(31 9)">
          <path d="M0 -4.2 L 1 -1 L 4.2 0 L 1 1 L 0 4.2 L -1 1 L -4.2 0 L -1 -1 Z" fill="#E0F2FE" />
        </g>
      </svg>
      <span style={{ fontSize: 52, fontWeight: 300, letterSpacing: 4, color: FROST_BRIGHT }}>reel</span>
    </div>
  );
}

interface Props {
  data: TasteDnaPayload;
  displayName: string | null;
}

const ShareCard = forwardRef<HTMLDivElement, Props>(function ShareCard({ data, displayName }, ref) {
  const model = shareCardModel(data);

  return (
    // Offscreen but rendered: `display:none` would give the rasteriser nothing to measure.
    <div style={{ position: "fixed", left: -9999, top: 0, pointerEvents: "none" }} aria-hidden>
      <div
        ref={ref}
        style={{
          width: 1080,
          height: 1350,
          background: `radial-gradient(900px 500px at 30% 0%, rgba(164,212,244,0.10), transparent 60%), linear-gradient(180deg, ${INK} 0%, #050D17 100%)`,
          color: FROST,
          padding: 72,
          display: "flex",
          flexDirection: "column",
          fontFamily: '"Figtree", ui-sans-serif, system-ui, sans-serif',
          boxSizing: "border-box",
        }}
      >
        <Wordmark />

        <div style={{ marginTop: 48 }}>
          {displayName && (
            <p style={{ margin: 0, fontSize: 34, fontWeight: 500, color: FROST_BRIGHT }}>{displayName}</p>
          )}
          <p style={{ margin: "6px 0 0", fontSize: 26, color: FROST_DIM }}>
            Taste DNA · {data.ratingsCount.toLocaleString("en-US")} ratings decoded
          </p>
        </div>

        {/* Headline stats */}
        <div style={{ display: "flex", gap: 20, marginTop: 44 }}>
          {model.stats.map((stat) => (
            <div
              key={stat.label}
              style={{ flex: 1, background: GLASS, border: `1px solid ${EDGE}`, borderRadius: 20, padding: "26px 28px" }}
            >
              <p style={{ margin: 0, fontSize: 19, letterSpacing: 2, textTransform: "uppercase", color: FROST_DIM }}>
                {stat.label}
              </p>
              <p style={{ margin: "10px 0 0", fontSize: 46, fontWeight: 300, color: FROST_BRIGHT, fontVariantNumeric: "tabular-nums" }}>
                {stat.value}
              </p>
            </div>
          ))}
        </div>

        {/* Genre fingerprint — plain divs on the shared 0–10 geometry. */}
        <div style={{ marginTop: 52, flex: 1 }}>
          <p style={{ margin: 0, fontSize: 24, fontWeight: 500, color: FROST_BRIGHT }}>Genre fingerprint</p>
          <p style={{ margin: "6px 0 0", fontSize: 19, color: FROST_DIM }}>
            0–10, against a personal {model.meanLabel} average — the tick
          </p>
          <div style={{ marginTop: 28, display: "flex", flexDirection: "column", gap: 22 }}>
            {model.genres.map((genre) => (
              <div key={genre.label} style={{ display: "flex", alignItems: "center", gap: 24 }}>
                <span style={{ width: 250, fontSize: 25, textTransform: "capitalize", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
                  {genre.label}
                </span>
                <div style={{ position: "relative", flex: 1, height: 16, borderRadius: 999, background: GLASS, overflow: "hidden" }}>
                  <div
                    style={{
                      position: "absolute",
                      insetBlock: 0,
                      left: 0,
                      width: `${genre.fillPct}%`,
                      borderRadius: 999,
                      background: genre.aboveMean ? "rgba(164,212,244,0.85)" : "rgba(92,138,180,0.7)",
                    }}
                  />
                  <div style={{ position: "absolute", insetBlock: 0, width: 2, left: `${model.meanPct}%`, background: "rgba(231,244,253,0.75)" }} />
                </div>
                <span style={{ width: 64, textAlign: "right", fontSize: 25, color: FROST_BRIGHT, fontVariantNumeric: "tabular-nums" }}>
                  {genre.affinity}
                </span>
              </div>
            ))}
          </div>
        </div>

        {/* Top creator affinities */}
        {model.creators.length > 0 && (
          <div style={{ marginTop: 20 }}>
            <p style={{ margin: 0, fontSize: 24, fontWeight: 500, color: FROST_BRIGHT }}>Creator affinities</p>
            <div style={{ display: "flex", gap: 20, marginTop: 20 }}>
              {model.creators.map((creator) => (
                <div
                  key={creator.name}
                  style={{ flex: 1, background: INK_2, border: `1px solid ${EDGE}`, borderRadius: 16, padding: "20px 24px", display: "flex", alignItems: "baseline", justifyContent: "space-between", gap: 12 }}
                >
                  <span style={{ fontSize: 23, color: FROST_BRIGHT, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
                    {creator.name}
                  </span>
                  <span style={{ fontSize: 23, color: FROST, fontVariantNumeric: "tabular-nums" }}>{creator.affinity}</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Footer */}
        <div style={{ marginTop: 44, display: "flex", justifyContent: "space-between", alignItems: "center", borderTop: `1px solid ${EDGE}`, paddingTop: 30 }}>
          <span style={{ fontSize: 22, color: FROST_DIM, letterSpacing: 1 }}>reel — your taste, decoded</span>
          <span style={{ fontSize: 22, color: FROST_DIM, fontVariantNumeric: "tabular-nums" }}>{model.dateLabel}</span>
        </div>
      </div>
    </div>
  );
});

export default ShareCard;
