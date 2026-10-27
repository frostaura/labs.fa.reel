import { Navigate } from "react-router";
import { Clapperboard } from "lucide-react";
import { useGetSessionQuery, useStartTraktAuthMutation } from "../store/api";
import { hasSessionFlag } from "../lib/sessionFlag";
import { rememberOauthState } from "../lib/traktOauth";
import Footer from "../components/Footer";
import { Button } from "../components/ui/button";
import { COARSE_TAP } from "../lib/tapTarget";
import reelMark from "../assets/brand/reel-mark.svg";

/**
 * What happens after "Sign in", in three plain steps. This replaces the fanned mock posters and the
 * invented "Neon Static" pick (8.4 for you, 7.9 crowd, "streaming now"): every title and number in
 * that card was made up, which is exactly the kind of claim this product exists not to make
 * (KILL-10). The page carries the Reel mark — the shipped product mark the app header and the tab
 * icon already use (O-19) — and no studio artwork. Real app screenshots are a parent-level brand
 * deliverable that has not arrived; until it does, the page says what Reel does instead of drawing a
 * pick that does not exist.
 */
const STEPS: { title: string; body: string }[] = [
  {
    title: "Link Trakt",
    body: "Your watch history and ratings come across. Your Trakt account is your Reel account.",
  },
  {
    title: "Reel reads your taste",
    body: "From what you've rated — what you love, what you don't, and the conditions you set.",
  },
  {
    title: "Picks you haven't seen",
    body: "Each with a predicted rating for you and where to watch it — and why it was picked, when there's a reason worth naming.",
  },
];

function HowItWorks() {
  return (
    <ol className="grid gap-3 text-left sm:grid-cols-3" data-testid="landing-steps">
      {STEPS.map((step, index) => (
        <li key={step.title} className="fa-card min-w-0 px-4 py-3 space-y-1" data-testid="landing-step">
          <p className="fa-overline text-fa-frost-dim">step {index + 1}</p>
          <p className="fa-body text-fa-frost-bright">{step.title}</p>
          <p className="fa-caption text-fa-frost">{step.body}</p>
        </li>
      ))}
    </ol>
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
      {/* A 16 px gutter on phones, and a column that may shrink below its content's intrinsic width
          (`w-full min-w-0`): a flex item defaults to `min-width: auto`, which is how one wide child
          used to widen the whole column past the viewport. */}
      <main className="flex-1 flex items-center justify-center px-4 sm:px-6">
        <div className="w-full min-w-0 max-w-3xl text-center space-y-8 reel-rise">
          <div className="space-y-4 max-w-xl mx-auto">
            {/* Decorative beside the wordmark it sits on: the heading already says "Reel". */}
            <img src={reelMark} alt="" aria-hidden className="mx-auto h-16 w-16" data-testid="landing-mark" />
            <h1 className="text-5xl font-light tracking-wide text-fa-frost-bright">Reel</h1>
            <p className="text-lg text-fa-frost/90 leading-relaxed">
              Link your Trakt profile; get live, explainable picks you haven&apos;t seen yet —
              with a predicted rating for <em>you</em>, and where to watch.
            </p>
          </div>
          <HowItWorks />
          <div className="space-y-3">
            <Button
              variant="primary"
              size="lg"
              onClick={handleSignIn}
              disabled={isLoading || isStarting}
              className={`text-base py-3 ${COARSE_TAP}`}
            >
              <Clapperboard className="h-5 w-5" />
              {isStarting ? "Opening Trakt…" : "Sign in with Trakt"}
            </Button>
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
