/**
 * A 44 × 44 px hit area on a coarse pointer (a phone, a tablet, a touch laptop), compact under a
 * mouse — WCAG 2.5.5's size on the devices it is about, without inflating dense desktop forms.
 *
 * One string for the account pages (Settings, Taste DNA, Track record, Rate, Onboarding, Lab), so the
 * rule is stated once in this lane. It belongs in WS-9's Button and Chip primitives (change request
 * filed); when it lands there, the call sites drop this and the file goes.
 */
export const COARSE_TAP = "[@media(pointer:coarse)]:min-h-11 [@media(pointer:coarse)]:min-w-11";
