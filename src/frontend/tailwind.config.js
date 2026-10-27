import plugin from "tailwindcss/plugin";

/** A colour token as Tailwind needs it: the RGB channels from index.css plus Tailwind's alpha slot. */
const channel = (name) => `rgb(var(--c-${name}) / <alpha-value>)`;

/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      // ── FrostAura brand tokens ────────────────────────────────────────────────────────────
      // Solid colours read the RGB channels in index.css (`--c-*`), so opacity modifiers work:
      // `rgb(var(--c-x) / <alpha-value>)`. Never `var(--fa-x)` here — Tailwind cannot put an
      // alpha into it and silently emits nothing for `bg-fa-ink/80`.
      // The translucent tokens stay literal rgba so a modifier can REPLACE their alpha
      // (`border-fa-edge/50`); src/design/tokens.test.ts holds them to the frost/ink channels.
      colors: {
        "fa-ink": channel("ink"),
        "fa-ink-2": channel("ink-2"),
        "fa-ink-3": channel("ink-3"),
        "fa-frost": channel("frost"),
        "fa-frost-bright": channel("frost-bright"),
        "fa-frost-dim": channel("frost-dim"),
        "fa-glass": "rgba(164, 212, 244, 0.06)",
        "fa-glass-strong": "rgba(164, 212, 244, 0.12)",
        "fa-edge": "rgba(164, 212, 244, 0.18)",
        "fa-edge-strong": "rgba(164, 212, 244, 0.32)",
        "fa-scrim": "rgba(6, 18, 31, 0.55)",
        "fa-scrim-strong": "rgba(6, 18, 31, 0.88)",
        "fa-success": channel("success"),
        "fa-warning": channel("warning"),
        "fa-danger": channel("danger"),

        // shadcn semantic tokens mapped onto the FrostAura palette (CSS vars in index.css).
        border: "hsl(var(--border))",
        input: "hsl(var(--input))",
        ring: "hsl(var(--ring))",
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        primary: {
          DEFAULT: "hsl(var(--primary))",
          foreground: "hsl(var(--primary-foreground))",
        },
        secondary: {
          DEFAULT: "hsl(var(--secondary))",
          foreground: "hsl(var(--secondary-foreground))",
        },
        destructive: {
          DEFAULT: "hsl(var(--destructive))",
          foreground: "hsl(var(--destructive-foreground))",
        },
        muted: {
          DEFAULT: "hsl(var(--muted))",
          foreground: "hsl(var(--muted-foreground))",
        },
        accent: {
          DEFAULT: "hsl(var(--accent))",
          foreground: "hsl(var(--accent-foreground))",
        },
        popover: {
          DEFAULT: "hsl(var(--popover))",
          foreground: "hsl(var(--popover-foreground))",
        },
        card: {
          DEFAULT: "hsl(var(--card))",
          foreground: "hsl(var(--card-foreground))",
        },
      },
      // Text: three steps, all AA on every ink surface — `text-1` primary, `text-2` secondary,
      // `text-3` tertiary. Use these instead of lowering a text colour's opacity.
      textColor: {
        1: channel("text-1"),
        2: channel("text-2"),
        3: channel("text-3"),
      },
      // Type scale: caption 12.5 · body 15 · card title 15 semibold · row 20 · page 30 · display 36–44.
      fontSize: {
        caption: ["12.5px", { lineHeight: "1.4" }],
        body: ["15px", { lineHeight: "1.5" }],
        "card-title": ["15px", { lineHeight: "1.35", fontWeight: "600" }],
        row: ["20px", { lineHeight: "1.3", fontWeight: "600" }],
        page: ["30px", { lineHeight: "1.2", fontWeight: "600" }],
        display: ["clamp(36px, 1.6vw + 28px, 44px)", { lineHeight: "1.1", fontWeight: "600" }],
      },
      maxWidth: {
        // The fluid shell: full width with gutters, capped where a line of cards stops helping.
        shell: "var(--fa-shell-max)",
      },
      width: {
        // One poster card width for every carousel and grid (O-9).
        card: "var(--fa-card-w)",
      },
      spacing: {
        // The shell's side gutter (16 → 24 → 32 → 40 px): `px-gutter`, and `-mx-gutter` to bleed.
        gutter: "var(--fa-gutter)",
      },
      borderRadius: {
        // Radii: control 6 · card 10 · panel 16 · full (Tailwind's own).
        control: "var(--fa-radius-control)",
        card: "var(--fa-radius-card)",
        panel: "var(--fa-radius-panel)",
        lg: "var(--radius)",
        md: "calc(var(--radius) - 2px)",
        sm: "calc(var(--radius) - 4px)",
      },
      fontFamily: {
        sans: ["Figtree Variable", "Figtree", "ui-sans-serif", "system-ui", "-apple-system", "Segoe UI", "Roboto", "sans-serif"],
        display: ["Figtree Variable", "Figtree", "ui-sans-serif", "system-ui", "-apple-system", "sans-serif"],
        mono: ["ui-monospace", "SF Mono", "Menlo", "monospace"],
      },
      backgroundImage: {
        "fa-gradient": "radial-gradient(1200px 600px at 30% 0%, rgba(164,212,244,0.10), transparent 60%), linear-gradient(180deg, #06121F 0%, #050D17 100%)",
      },
      boxShadow: {
        "fa-glass": "inset 0 1px 0 0 rgba(255,255,255,0.04), 0 1px 2px rgba(0,0,0,0.2)",
      },
      keyframes: {
        "accordion-down": {
          from: { height: "0" },
          to: { height: "var(--radix-accordion-content-height)" },
        },
        "accordion-up": {
          from: { height: "var(--radix-accordion-content-height)" },
          to: { height: "0" },
        },
      },
      animation: {
        "accordion-down": "accordion-down 0.2s ease-out",
        "accordion-up": "accordion-up 0.2s ease-out",
      },
    },
  },
  plugins: [
    // `coarse:` is any touch-first pointer (a phone, a tablet, a touch laptop) and `fine:` a mouse
    // or trackpad. Every ui/ control is a 44 × 44 target under `coarse:` (IREV3S-08, O-15; WCAG
    // 2.5.5) and keeps its compact size under a mouse. Prefer these to hand-written
    // `[@media(pointer:coarse)]:` arbitrary variants.
    plugin(({ addVariant }) => {
      addVariant("coarse", "@media (pointer: coarse)");
      addVariant("fine", "@media (pointer: fine)");
    }),
  ],
};
