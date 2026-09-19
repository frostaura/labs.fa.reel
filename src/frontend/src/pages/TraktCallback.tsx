import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router";
import { useExchangeTraktCodeMutation } from "../store/api";
import {
  consumeOauthState,
  describeCallbackRejection,
  parseCallbackParams,
  peekOauthState,
} from "../lib/traktOauth";

type Status = "exchanging" | "error";

/**
 * The registered Trakt redirect URI (dev: http://localhost:5173/auth/trakt/callback).
 * Validates the state nonce against the one stored before redirect, forwards the code to the
 * API (which verifies its own HMAC-signed state independently), then enters the app.
 */
export default function TraktCallback() {
  const location = useLocation();
  const navigate = useNavigate();
  const [exchange] = useExchangeTraktCodeMutation();
  const [exchangeError, setExchangeError] = useState<string | null>(null);
  const started = useRef(false);

  const params = useMemo(() => parseCallbackParams(location.search), [location.search]);

  // Every rejection reason is a pure function of the URL and the stored nonce, so it is derived
  // rather than pushed into state from the effect — that avoids the cascading render of
  // painting "Linking…" first and correcting it a frame later.
  const validationError = useMemo(
    () => describeCallbackRejection(params, peekOauthState()),
    [params]
  );

  const message = validationError ?? exchangeError;
  const status: Status = message ? "error" : "exchanging";

  useEffect(() => {
    if (started.current) return; // StrictMode double-invoke guard — the code is single-use
    started.current = true;

    // Clear the nonce on every arrival, valid or not, matching the original single-use contract.
    consumeOauthState();
    if (validationError || !params.code || !params.state) return;

    exchange({ code: params.code, state: params.state })
      .unwrap()
      .then((session) => {
        navigate(session.onboarded ? "/home" : "/onboarding", { replace: true });
      })
      .catch(() => {
        setExchangeError("Could not complete sign-in with Trakt. Please try again.");
      });
  }, [params, validationError, exchange, navigate]);

  return (
    <div className="min-h-screen flex items-center justify-center px-6">
      <div className="text-center space-y-4">
        {status === "exchanging" ? (
          <>
            <div className="text-2xl font-light text-fa-frost animate-pulse">Reel</div>
            <p className="fa-body text-fa-frost-dim">Linking your Trakt profile…</p>
          </>
        ) : (
          <div className="fa-card px-8 py-6 space-y-3 max-w-md">
            <p className="fa-section-title text-fa-danger">Sign-in didn&apos;t complete</p>
            <p className="fa-body text-fa-frost/90">{message}</p>
            <Link to="/" className="fa-button inline-flex">
              Back to sign-in
            </Link>
          </div>
        )}
      </div>
    </div>
  );
}
