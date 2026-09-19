import { useEffect, useState } from "react";
import { BellOff, BellRing } from "lucide-react";
import {
  useLazyGetVapidPublicKeyQuery,
  useSubscribePushMutation,
  useUnsubscribePushMutation,
} from "../../store/api";
import { base64UrlToUint8Array } from "../../lib/pushKeys";

/**
 * "unsupported" hides the card entirely (no Push API, or no service-worker registration — which
 * includes dev, where the kill-switch worker owns /sw.js). "denied" keeps the card visible with
 * an explanation, because the user can only fix it in browser settings and should know why the
 * toggle vanished.
 */
type PushState = "checking" | "unsupported" | "off" | "on" | "denied" | "busy";

/** Settings card: opt in/out of the Friday weekly-digest Web Push notification. */
export default function PushOptIn() {
  const [state, setState] = useState<PushState>("checking");
  const [fetchVapidKey] = useLazyGetVapidPublicKeyQuery();
  const [subscribePush] = useSubscribePushMutation();
  const [unsubscribePush] = useUnsubscribePushMutation();

  useEffect(() => {
    let cancelled = false;
    (async () => {
      if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
        return "unsupported" as const;
      }
      const registration = await navigator.serviceWorker.getRegistration();
      if (!registration || !registration.pushManager) {
        return "unsupported" as const;
      }
      if (Notification.permission === "denied") {
        return "denied" as const;
      }
      const subscription = await registration.pushManager.getSubscription();
      return subscription ? ("on" as const) : ("off" as const);
    })()
      .catch(() => "unsupported" as const)
      .then((next) => {
        if (!cancelled) setState(next);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (state === "unsupported" || state === "checking") {
    return null;
  }

  const enable = async () => {
    setState("busy");
    try {
      const permission = await Notification.requestPermission();
      if (permission !== "granted") {
        setState(permission === "denied" ? "denied" : "off");
        return;
      }
      const registration = await navigator.serviceWorker.ready;
      const { key } = await fetchVapidKey().unwrap();
      const subscription = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: base64UrlToUint8Array(key),
      });
      const json = subscription.toJSON();
      await subscribePush({
        endpoint: subscription.endpoint,
        keys: { p256dh: json.keys?.p256dh ?? "", auth: json.keys?.auth ?? "" },
      }).unwrap();
      setState("on");
    } catch {
      setState("off");
    }
  };

  const disable = async () => {
    setState("busy");
    try {
      const registration = await navigator.serviceWorker.getRegistration();
      const subscription = await registration?.pushManager.getSubscription();
      if (subscription) {
        // Unsubscribe from the browser first; the server row is best-effort — a dead row gets
        // pruned by the digest job's gone/failure handling anyway.
        await subscription.unsubscribe();
        await unsubscribePush({ endpoint: subscription.endpoint })
          .unwrap()
          .catch(() => undefined);
      }
      setState("off");
    } catch {
      setState("on");
    }
  };

  return (
    <section className="fa-card p-5 space-y-3" data-testid="push-opt-in">
      <h2 className="fa-section-title">Weekly digest notifications</h2>
      <p className="fa-caption text-fa-frost-dim">
        One notification on Fridays when your &ldquo;new &amp; high for you&rdquo; picks land. Nothing else, ever.
      </p>
      {state === "denied" ? (
        <p className="fa-caption text-fa-danger">
          Notifications are blocked for this site in your browser settings — allow them there, then come back.
        </p>
      ) : (
        <button
          type="button"
          onClick={state === "on" ? disable : enable}
          disabled={state === "busy"}
          className="fa-button disabled:opacity-40"
          data-testid="push-toggle"
        >
          {state === "on" ? (
            <>
              <BellOff className="h-4 w-4" /> Turn off
            </>
          ) : (
            <>
              <BellRing className="h-4 w-4" /> Turn on
            </>
          )}
        </button>
      )}
    </section>
  );
}
