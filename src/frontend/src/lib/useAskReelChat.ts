/**
 * Compatibility seam for importers outside the Ask Reel lane. The hook and its helpers live in
 * `lib/askReel/` (WS-2 task 5); this re-export exists only until those importers point there.
 */
export { mintHandoff } from "./askReel/useAskReelChat";
