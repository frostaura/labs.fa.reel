import { Navigate } from "react-router";
import { Clapperboard } from "lucide-react";
import { useGetSessionQuery, useStartTraktAuthMutation } from "../store/api";
import { hasSessionFlag } from "../lib/sessionFlag";
import { rememberOauthState } from "../lib/traktOauth";
import Footer from "../components/Footer";
import ScoreBadges from "../components/rec/ScoreBadges";

/** The fanned side cards: pure-CSS gradient posters + a genre label. No assets, no network. */
const MOCK_POSTERS: { genre: string; gradient: string; tilt: string }[] = [
  { genre: "Sci-fi", gradient: "linear-gradient(160deg, #12304b 0%, #0a1a2c 55%, #251d3d 100%)", tilt: "-rotate-6 translate-y-3" },
  { genre: "Thriller", gradient: "linear-gradient(160deg, #2b1d33 0%, #131020 60%, #06121f 100%)", tilt: "-rotate-3 translate-y-1" },
  { genre: "Drama", gradient: "linear-gradient(160deg, #3b2a1c 0%, #1d1410 60%, #0a0c14 100%)", tilt: "rotate-3 translate-y-1" },
  { genre: "Comedy", gradient: "linear-gradient(160deg, #1c3b32 0%, #0f2019 60%, #071019 100%)", tilt: "rotate-6 translate-y-3" },
];

/**
 * The visual sell: what a Reel pick actually looks like, before asking anyone to sign in — a fan
 * of poster-style mock cards with one fully-rendered card (real ScoreBadges, real why-line) in
 * front. Entirely local CSS/DOM; deliberately no TMDB art on a page that exists to earn trust.
 */
function PosterMockRow() {
  return (
    <div className="max-w-3xl mx-auto flex items-end justify-center" aria-hidden>
      {MOCK_POSTERS.slice(0, 2).map((p) => (
        <MockPoster key={p.genre} poster={p} />
      ))}

      {/* The featured card — a real pick's anatomy: badges up top, why-line below. */}
      <div className="relative z-10 w-36 sm:w-40 shrink-0 -mx-3 rounded-lg bg-fa-ink-2 p-1.5 pb-2 shadow-fa-glass ring-1 ring-fa-edge text-left">
        <div
          className="relative aspect-[2/3] rounded-md overflow-hidden"
          style={{ backgroundImage: "linear-gradient(155deg, #14385a 0%, #0e2038 45%, #38214a 100%)" }}
        >
          <ScoreBadges predictedRating={8.4} crowdRating={7.9} />
          <div className="absolute inset-x-0 bottom-0 h-1/2 reel-scrim" />
          <p className="absolute bottom-2 left-2 right-2 fa-caption text-fa-frost-bright font-medium">
            Neon Static
          </p>
        </div>
        <p className="mt-1.5 px-0.5 fa-caption text-fa-frost/80 leading-snug">
          Shares Blade Runner&apos;s neon melancholy — and it&apos;s streaming now.
        </p>
      </div>

      {MOCK_POSTERS.slice(2).map((p) => (
        <MockPoster key={p.genre} poster={p} />
      ))}
    </div>
  );
}

function MockPoster({ poster }: { poster: (typeof MOCK_POSTERS)[number] }) {
  return (
    <div
      className={`relative w-20 sm:w-24 shrink-0 aspect-[2/3] rounded-lg overflow-hidden ring-1 ring-fa-edge shadow-fa-glass ${poster.tilt}`}
      style={{ backgroundImage: poster.gradient }}
    >
      <span className="absolute bottom-1.5 left-2 fa-overline text-fa-frost-dim">{poster.genre}</span>
    </div>
  );
}

export default function Landing() {
  // First-time visitors have never signed in here, so probing /auth/me only logs a guaranteed
  // 401 to their console. Skip the probe until the browser has established a session at least
  // once (flag set by the OAuth exchange / any successful probe, cleared on logout). Only this
  // page skips — protected routes (RequireAuth) always probe, so a lost flag can't lock anyone out.
  const { data: session, isLoading } = useGetSessionQuery(undefined, { skip: !hasSessionFlag() });
  const [startTraktAuth, { isLoading: isStarting, isError }] = useStartTraktAuthMutation();

  if (session) {
    return <Navigate to={session.onboarded ? "/home" : "/onboarding"} replace />;
  }

  const handleSignIn = async () => {
    try {
      const { authorizeUrl, state } = await startTraktAuth().unwrap();
      rememberOauthState(state);
      window.location.assign(authorizeUrl);
    } catch {
      // The mutation's own isError drives the message below. onClick never awaits
      // this handler, so letting the rejection escape only yields an unhandled one.
    }
  };

  return (
    <div className="min-h-screen flex flex-col">
      <main className="flex-1 flex items-center justify-center px-6">
        <div className="max-w-3xl text-center space-y-8 reel-rise">
          <div className="space-y-4 max-w-xl mx-auto">
            <h1 className="text-5xl font-light tracking-wide text-fa-frost-bright">Reel</h1>
            <p className="text-lg text-fa-frost/90 leading-relaxed">
              Link your Trakt profile; get live, explainable picks you haven&apos;t seen yet —
              with a predicted rating for <em>you</em>, and where to watch.
            </p>
          </div>
          <PosterMockRow />
          <div className="space-y-3">
            <button
              onClick={handleSignIn}
              disabled={isLoading || isStarting}
              className="fa-button-primary text-base px-6 py-3"
            >
              <Clapperboard className="h-5 w-5" />
              {isStarting ? "Opening Trakt…" : "Sign in with Trakt"}
            </button>
            {isError && (
              <p className="fa-caption text-fa-danger">
                Couldn&apos;t reach the sign-in service. Try again in a moment.
              </p>
            )}
            <p className="fa-caption text-fa-frost-dim">
              Your Trakt account is your Reel account — nothing else to create.
            </p>
          </div>
        </div>
      </main>
      <Footer />
    </div>
  );
}
