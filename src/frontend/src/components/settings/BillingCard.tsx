import { useState } from "react";
import { Sparkles } from "lucide-react";
import { useCreateCheckoutMutation, useGetBillingStatusQuery } from "../../store/api";
import { isPaidTier, planLine } from "./planCopy";
import { Button } from "../ui/button";
import { COARSE_TAP } from "../../lib/tapTarget";

/**
 * Plan + upgrade. Rendered only while billing is configured on the server (Settings decides): the
 * card used to offer an Upgrade that could only ever answer "Paid plans are coming soon".
 * On a paid or founder tier it confirms the plan; on Free it opens Stripe Checkout, and a failed
 * checkout says what happened instead of promising a future.
 */
export default function BillingCard() {
  const { data } = useGetBillingStatusQuery();
  const [checkout, { isLoading }] = useCreateCheckoutMutation();
  const [failure, setFailure] = useState<string | null>(null);

  if (!data || !data.configured) return null;
  const paid = isPaidTier(data.tier);

  const upgrade = async () => {
    setFailure(null);
    const res = await checkout();
    if ("data" in res && res.data?.url) {
      window.location.href = res.data.url;
      return;
    }
    // The server explains a refusal in `error` (already on a paid plan, billing switched off since
    // the page loaded); anything else is a failed request. Either way nothing was charged.
    const reason = "error" in res ? (res.error as { data?: { error?: unknown } } | undefined)?.data?.error : res.data?.error;
    setFailure(
      typeof reason === "string" && reason.trim().length > 0
        ? `${reason.trim()} Nothing was charged.`
        : "Couldn't open checkout — nothing was charged. Try again in a moment.",
    );
  };

  return (
    <section className="fa-card p-5 space-y-3" data-testid="billing-card">
      <h2 className="fa-section-title">Plan</h2>
      {paid ? (
        <p className="fa-body text-fa-frost-bright" data-testid="plan-line">{planLine(data.tier)}</p>
      ) : (
        <>
          <p className="fa-caption text-fa-frost-dim" data-testid="plan-line">{planLine(data.tier)}</p>
          <Button variant="primary" onClick={upgrade} disabled={isLoading} className={COARSE_TAP}>
            <Sparkles className="h-4 w-4" /> {isLoading ? "Opening…" : "Upgrade"}
          </Button>
          {failure && (
            <p role="alert" className="fa-caption text-fa-danger" data-testid="checkout-failure">
              {failure}
            </p>
          )}
        </>
      )}
    </section>
  );
}
