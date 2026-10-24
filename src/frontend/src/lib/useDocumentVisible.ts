import { useSyncExternalStore } from "react";

function subscribe(onChange: () => void): () => void {
  document.addEventListener("visibilitychange", onChange);
  return () => document.removeEventListener("visibilitychange", onChange);
}

const getSnapshot = () => document.visibilityState !== "hidden";
// No document on the server; a render there is never a hidden tab.
const getServerSnapshot = () => true;

/** True while the tab is visible. A hidden tab stops polling outright (H-16, I-12). */
export function useDocumentVisible(): boolean {
  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}
