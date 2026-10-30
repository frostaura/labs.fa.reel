import { useEffect, useRef, useState } from "react";
import { Check, Lock, X } from "lucide-react";
import {
  useGetFilterChipsQuery,
  useGetFiltersQuery,
  useGetSessionQuery,
  useUpdateFiltersMutation,
  useVerifyPinMutation,
  type FiltersPayload,
} from "../../store/api";
import { clearUnlockToken, storeUnlockToken, useUnlockToken } from "../../lib/settingsPin";
import {
  applySuggestion,
  costLine,
  formatMarginal,
  languageRuleValue,
  marginalFor,
  marginalNote,
  ratingRuleValue,
  suggestionLabel,
} from "../../lib/filterCost";
import {
  ALL_LANGUAGES,
  genreChips,
  genreReach,
  genreState,
  isInvertedYearRange,
  LANGUAGES,
  MATURITY_OPTIONS,
  maturityFromOption,
  maturityOptionValue,
  normalizeFilters,
  normalizeYearRange,
  parseYearInput,
  ruleControlTestId,
  sameConditions,
  YEAR_FLOOR,
  toggleGenre,
  yearCeiling,
  type GenreState,
  type RuleFocus,
} from "../../lib/pickConditions";
import { pinAttemptText } from "../../lib/pinLockout";
import { COARSE_TAP } from "../../lib/tapTarget";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { Select } from "../ui/select";
import { cn } from "../../lib/cn";
import ConditionSummary from "./ConditionSummary";
import LoadFailed from "../ui/load-failed";
import SaveStatus from "../ui/save-status";
import { saveFailureMessage, savedMessage, type SaveMessage } from "../ui/save-outcome";
import { useLeaveGuard } from "./useLeaveGuard";
import { useFilterCostMeter } from "./useFilterCostMeter";
import { CONDITIONS_SCOPE_COPY } from "../../lib/conditionsCopy";
import { useSettledGate } from "../../lib/useSettledGate";

/**
 * How long the editor holds its skeleton for the chips and the first cost answer before drawing
 * anyway. The eval stack answers both within about a second of mount; the cap is for a meter that
 * never answers, which then fills its counts in late rather than holding the form back.
 */
const REVEAL_CAP_MS = 2500;

interface Props {
  /** Fired after a successful save — the feed surface uses it to adopt the regenerated picks. */
  onSaved?: () => void;
  saveLabel?: string;
  /** Settings supplies the `fa-card` shell; the dialog already has one. */
  bare?: boolean;
  /**
   * Test ids per state. The editor mounts twice in the app (Settings and the dialog on the feed),
   * and the existing Settings suite addresses it by name — so the ids are supplied by the mount
   * rather than hard-coded here, which is what keeps this a single implementation.
   */
  testIds?: { editor?: string; locked?: string; loading?: string };
  /**
   * Open the editor ON one rule (WS-2B CR-1): once the saved rules — and, for a genre, the chips —
   * have rendered, the rule's control is scrolled into view and focused, so a reader who came from
   * "Without “English only”: +4" lands on the language allow-list rather than the top of the form.
   * A rule with no control of its own focuses the editor. Applied once per rule object.
   */
  focusRule?: RuleFocus | null;
  /**
   * Fired once the editor has drawn what it will keep drawing — the form with its chips and counts,
   * the PIN gate, or the load failure. Settings holds the cards below the editor until then, so the
   * editor growing into its settled height never pushes them down (HREV1-D03).
   */
  onReady?: () => void;
}

const GENRE_TONE: Record<GenreState, string> = {
  neutral: "border-fa-edge bg-fa-glass text-fa-frost",
  include: "border-fa-success/50 bg-fa-success/15 text-fa-success",
  exclude: "border-fa-danger/50 bg-fa-danger/15 text-fa-danger",
};

const GENRE_ARIA: Record<GenreState, string> = {
  neutral: "not filtered",
  include: "required",
  exclude: "excluded",
};

/** Each ✓/✕ is a full 44 px target on a coarse pointer (touch), compact under a mouse. */
const GENRE_BUTTON =
  "inline-flex items-center justify-center px-2 transition border-l border-fa-edge hover:bg-fa-glass-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50 [@media(pointer:coarse)]:min-h-11 [@media(pointer:coarse)]:min-w-11";

/**
 * The conditions behind tonight's picks — genres (three-state), the two rating floors, release
 * years, languages, maturity and keyword exclusions — in one editor.
 *
 * It also costs itself as you edit. The founder's rules leave roughly 421 of 5,319 titles eligible
 * and this editor used to say nothing at all about that, so "why do my picks keep getting worse"
 * was unanswerable in the one place the trade is actually made. The counts are shown and NOTHING is
 * changed on their account — the founder decision is explicit that the app names the cost and the
 * user decides. Suggestions are one-tap and land in the DRAFT, never in the saved conditions.
 *
 * There is exactly ONE of these. It renders inside Settings and inside the dialog on the feed;
 * two copies of a filter form is how a "minimum rating" quietly comes to mean two different things
 * depending on which screen you set it from. The pure rules it applies live in
 * `lib/pickConditions.ts` and are unit-tested there.
 */
export default function ConditionsEditor({ onSaved, saveLabel = "Save conditions", bare, testIds, focusRule, onReady }: Props) {
  const { data: session } = useGetSessionQuery();
  const { data: saved, isError: loadFailed, isFetching: loadingSaved, refetch: refetchSaved } = useGetFiltersQuery();
  const [updateFilters, { isLoading: saving }] = useUpdateFiltersMutation();
  const [verifyPin] = useVerifyPinMutation();
  const unlockToken = useUnlockToken();

  const [draft, setDraft] = useState<FiltersPayload | null>(null);
  const [years, setYears] = useState({ min: "", max: "" });
  const [status, setStatus] = useState<SaveMessage | null>(null);
  const [pinEntry, setPinEntry] = useState("");
  const [pinError, setPinError] = useState<string | null>(null);

  // Adopted during render rather than in an effect (React's documented alternative to a syncing
  // effect — it re-renders before commit instead of cascading). Seeding in an effect meant every
  // visit committed one frame of this component's own defaults — nothing excluded, English only,
  // rating floor off — and only then snapped to the account's real filters, which reads as the app
  // forgetting your settings. Self-limiting: `draft` is non-null afterwards, so this runs once.
  // `normalizeFilters` is what absorbs a server that has not shipped the crowd/year fields yet.
  if (saved && draft === null) {
    const initial = normalizeFilters(saved);
    setDraft(initial);
    setYears({ min: initial.minYear?.toString() ?? "", max: initial.maxYear?.toString() ?? "" });
  }

  const pinConfigured = session?.pinConfigured ?? false;
  const locked = pinConfigured && unlockToken === null;

  // Unsaved means "the draft would save different rules", not "a control was touched": toggling a
  // genre round to where it started is clean again. It stays true through a failed save, because
  // nothing was saved — the marker and the leave guard both read it, the guard even while a lapsed
  // unlock has put the PIN gate over the draft.
  //
  // `justSaved` bridges the gap between a successful PUT and the refetch of the saved rules it
  // invalidates; without it "Unsaved changes" flickers up beside "Saved" for one round trip. It is
  // dropped as soon as the server's copy changes, so the server stays the baseline.
  const [justSaved, setJustSaved] = useState<FiltersPayload | null>(null);
  const [baselineSource, setBaselineSource] = useState(saved);
  if (saved !== baselineSource) {
    setBaselineSource(saved);
    setJustSaved(null);
  }
  const baseline = justSaved ?? saved;
  const dirty = draft !== null && baseline !== undefined && !sameConditions(draft, baseline);
  useLeaveGuard(dirty);

  // A success fades; a failure stays until the next save (SaveStatus). The timer is keyed on the
  // message object so a newer message is never cleared by an older one's timer.
  useEffect(() => {
    if (!status || status.persistent) return;
    const handle = window.setTimeout(() => setStatus(null), 2500);
    return () => window.clearTimeout(handle);
  }, [status]);

  const shell = bare ? "space-y-5" : "fa-card p-5 space-y-5";

  // The cost of the draft, debounced, with any request for an older draft aborted (H-6).
  const { report: cost, settled: costSettled } = useFilterCostMeter(draft, locked);
  // The catalogue's genre chips (H-7). A failed read is its own state below, never a missing list.
  const {
    data: chipReport,
    isError: chipsFailed,
    isFetching: chipsLoading,
    refetch: refetchChips,
  } = useGetFilterChipsQuery();

  // One reveal, not three (HREV1-D03): the saved rules, the chip list and the first cost answer each
  // used to land on their own frame, and each grew the form — the chips wrapping to four rows, then
  // the count line, the suggestions and a note under every costed control — so everything under
  // the editor jumped once per read. The skeleton now stays until all three have answered (or
  // failed), or until the cap, and the form is drawn whole.
  const chipsSettled = chipReport !== undefined || chipsFailed;
  const revealed = useSettledGate(draft !== null && chipsSettled && costSettled, REVEAL_CAP_MS);
  const ready = locked || (draft === null && loadFailed) || (draft !== null && revealed);
  useEffect(() => {
    if (ready) onReady?.();
  }, [ready, onReady]);

  // Where the rule this editor was opened on lives, once it has rendered (see `focusRule`).
  const editorRef = useRef<HTMLElement>(null);
  const focusedRule = useRef<RuleFocus | null>(null);
  useEffect(() => {
    const root = editorRef.current;
    if (!focusRule || !root || focusedRule.current === focusRule) return;
    const id = ruleControlTestId(focusRule);
    const control = id ? root.querySelector<HTMLElement>(`[data-testid="${CSS.escape(id)}"]`) : null;
    // A genre's chip may not be drawn yet: wait for the chip list (or its failure) before settling
    // for the editor's top.
    if (!control && id?.startsWith("genre-") && !chipReport && !chipsFailed) return;
    const target = control ?? root;
    if (target === root) root.setAttribute("tabindex", "-1");
    target.scrollIntoView({ block: "center" });
    target.focus({ preventScroll: true });
    focusedRule.current = focusRule;
  });

  const unlock = async () => {
    try {
      const result = await verifyPin({ pin: pinEntry }).unwrap();
      if (result.valid && result.unlockToken) {
        // What gets kept is the server-minted unlock token, never the PIN the user just typed.
        storeUnlockToken(result.unlockToken, result.expiresAt);
        setPinEntry("");
        setPinError(null);
        return;
      }
      setPinError("Wrong PIN");
    } catch (error: unknown) {
      // A wrong PIN, the five-wrong-PINs pause (429 pin_locked, with how long) and a request that
      // failed are three different sentences — the last two are not "Wrong PIN" (WS-8 CR-6).
      setPinError(pinAttemptText(error, "Wrong PIN"));
    }
  };

  if (locked) {
    return (
      <section className={bare ? "space-y-3" : "fa-card p-5 space-y-3"} data-testid={testIds?.locked ?? "conditions-locked"}>
        <h2 className="fa-section-title flex items-center gap-2">
          <Lock className="h-4 w-4" /> Content preferences
        </h2>
        <p className="fa-caption text-fa-frost-dim">Locked with your settings PIN.</p>
        {/* A save that hit the lock lands here: the draft is kept, and this says why it did not save. */}
        {status?.kind === "locked" && (
          <p role="alert" className="fa-caption text-fa-danger" data-testid="conditions-locked-reason">
            {status.text}
            {dirty ? " Your unsaved changes are kept — unlock to save them." : ""}
          </p>
        )}
        <div className="flex items-center gap-2">
          <input
            type="password"
            inputMode="numeric"
            maxLength={8}
            value={pinEntry}
            onChange={(e) => setPinEntry(e.target.value.replace(/\D/g, ""))}
            onKeyDown={(e) => e.key === "Enter" && unlock()}
            placeholder="PIN"
            aria-label="Settings PIN"
            className={cn("fa-input w-28 tabular-nums", COARSE_TAP)}
            data-testid="pin-input"
          />
          <Button onClick={unlock} className={COARSE_TAP} data-testid="pin-unlock">
            Unlock
          </Button>
          {pinError && (
            <span role="alert" className="fa-caption text-fa-danger" data-testid="pin-unlock-error">
              {pinError}
            </span>
          )}
        </div>
      </section>
    );
  }

  // Until the saved filters land there is no honest value to draw: rendering the controls from this
  // component's defaults is what produced the flash. A skeleton says "loading" instead of lying.
  // A failed read of the saved rules is its own state: the skeleton below used to shimmer for ever.
  if (!draft && loadFailed) {
    return (
      <LoadFailed
        what="your conditions"
        onRetry={() => void refetchSaved()}
        retrying={loadingSaved}
        variant="inline"
        testId="conditions-load-error"
      />
    );
  }
  if (!draft || !revealed) {
    return (
      // Keyed apart from the editor: React would otherwise reuse this section and its bars as the
      // editor's own section and blocks, and report each bar "moving" to where the block it became
      // sits — a layout shift of 0.17 at 390 px, with nothing on screen actually moving.
      <section
        key="conditions-skeleton"
        className={bare ? "space-y-3" : "fa-card p-5 space-y-3"}
        data-testid={testIds?.loading ?? "conditions-loading"}
      >
        <div className="h-5 w-48 rounded reel-shimmer" />
        <div className="h-20 rounded reel-shimmer" />
        <div className="h-20 rounded reel-shimmer" />
      </section>
    );
  }

  const ceiling = yearCeiling();
  const inverted = isInvertedYearRange(draft);
  const languages = draft.languages;
  const allLanguages = languages.includes(ALL_LANGUAGES);

  /** Clamps and un-inverts the year boxes, and writes the corrected text back so the user sees it. */
  const commitYears = () => {
    const range = normalizeYearRange(draft, { floor: YEAR_FLOOR, ceiling });
    setDraft({ ...draft, ...range });
    setYears({ min: range.minYear?.toString() ?? "", max: range.maxYear?.toString() ?? "" });
  };

  const toggleLanguage = (code: string) => {
    // Picking a specific language while "all" is on narrows to just that language.
    if (allLanguages) {
      setDraft({ ...draft, languages: [code] });
      return;
    }
    const next = languages.includes(code) ? languages.filter((l) => l !== code) : [...languages, code];
    // English is the floor — clearing every language reverts to the English-only default rather
    // than an empty (which would read as "no titles").
    setDraft({ ...draft, languages: next.length > 0 ? next : ["en"] });
  };

  const save = async () => {
    // Normalised on the way out, not only on blur: a save straight from a focused year box would
    // otherwise ship whatever was half-typed in it.
    const range = normalizeYearRange(draft, { floor: YEAR_FLOOR, ceiling });
    const payload = normalizeFilters({ ...draft, ...range });
    setDraft(payload);
    setYears({ min: range.minYear?.toString() ?? "", max: range.maxYear?.toString() ?? "" });
    setStatus(null);
    try {
      await updateFilters({ filters: payload, unlockToken: unlockToken?.token }).unwrap();
    } catch (error: unknown) {
      // The status decides the copy: 423 is the PIN, 400 is the server's own reason, anything else
      // is "nothing changed" with a Retry. One catch-all used to call a 500 a PIN lock, in green.
      const message = saveFailureMessage(error);
      // A 423 means the unlock aged out server-side (or the token is from a rotated key ring):
      // drop the stale token so the PIN gate re-shows instead of every save failing the same way.
      // The draft survives the lock screen — it is this component's state, not the server's.
      if (message.kind === "locked") {
        clearUnlockToken();
      }
      setStatus(message);
      return;
    }
    setJustSaved(payload);
    setStatus(savedMessage("Saved — your picks are being re-cut."));
    onSaved?.();
  };

  return (
    <section key="conditions-editor" ref={editorRef} className={shell} data-testid={testIds?.editor ?? "conditions-editor"}>
      {/* Suppressed in the dialog, which supplies its own DialogTitle — two identical headings
          stacked on top of each other read as a rendering bug. */}
      {!bare && (
        <div>
          <h2 className="fa-section-title">Conditions for tonight&apos;s picks</h2>
          {/* The same sentence as the Home conditions dialog, from one source (conditionsCopy.ts).
              Ask Reel keeps titles under the minimum behind a tray and the typeahead's global leg
              applies no rules, so the sentence says "shape" — never "airtight" or "all search"
              (R1-26, IR1S-06). */}
          <p className="fa-caption text-fa-frost-dim mt-1" data-testid="conditions-scope">
            {CONDITIONS_SCOPE_COPY}
          </p>
        </div>
      )}

      {/* What is on right now, before any of the controls. The whole point of the summary is that
          "why does my feed look like this" is answerable without reading six sections. */}
      <div className="space-y-1.5">
        <p className="fa-overline text-fa-frost-dim">active now</p>
        <ConditionSummary filters={draft} />
        {/* What the draft costs, live. "Leave" rather than "exclude": the gap between the catalogue
            total and the eligible count is partly these rules and partly titles already watched,
            rated or dismissed, and the per-rule numbers below are the honest attribution. */}
        {costLine(cost) && (
          <p className="fa-caption text-fa-frost mt-1" data-testid="filter-cost-line">
            {costLine(cost)}
          </p>
        )}
        {(cost?.suggestions.length ?? 0) > 0 && (
          <div className="flex flex-wrap items-center gap-1.5 pt-1" data-testid="filter-cost-suggestions">
            <span className="fa-caption text-fa-frost-dim">Widest options:</span>
            {cost!.suggestions.map((suggestion) => (
              <Button
                key={`${suggestion.kind}:${suggestion.value}`}
                variant="chip"
                size="chip"
                onClick={() => setDraft(applySuggestion(draft, suggestion))}
                data-testid="cost-suggestion"
                data-kind={suggestion.kind}
                data-value={suggestion.value}
                title="Applies to this draft — nothing is saved until you save"
                className={cn("gap-1.5 border-fa-frost/40", COARSE_TAP)}
              >
                {suggestionLabel(suggestion)}
                <span className="tabular-nums text-fa-success">{formatMarginal(suggestion.gain)}</span>
              </Button>
            ))}
          </div>
        )}
      </div>

      {/* ── Genres ──────────────────────────────────────────────────────────────────────────
          Each genre carries an explicit ✓ (require) and ✕ (exclude); tapping the lit one clears it.
          The chips come from the catalogue (H-7), so every chip offered removes something. */}
      <div className="space-y-2">
        <p className="fa-overline text-fa-frost-dim">genres</p>
        <p className="fa-caption text-fa-frost-dim">
          <Check className="inline h-3 w-3 text-fa-success" aria-hidden /> requires at least one of the ticked genres ·{" "}
          <X className="inline h-3 w-3 text-fa-danger" aria-hidden /> hides every title in a crossed-out one. Tap a lit
          mark again to clear it.
        </p>
        <div className="flex flex-wrap gap-1.5" data-testid="genre-chips">
          {genreChips(chipReport, draft).map((chip) => {
            const state = genreState(draft, chip.slug);
            // Only excluded chips carry a number, and only a positive one: the badge answers "what
            // is this chip costing me", which is a question an inactive chip does not raise.
            const marginal = state === "exclude" ? formatMarginal(marginalFor(cost, "ExcludeGenre", chip.slug)) : null;
            const reach = chipReport ? genreReach(chip) : null;
            return (
              <div
                key={chip.slug}
                role="group"
                aria-label={`${chip.label} — ${GENRE_ARIA[state]}${reach ? `, ${reach}` : ""}${
                  marginal ? `, ${marginal} titles if allowed` : ""
                }`}
                title={reach ?? undefined}
                className={`inline-flex items-stretch overflow-hidden rounded-full border fa-caption ${GENRE_TONE[state]}`}
                data-testid={`genre-${chip.slug}`}
                data-state={state}
              >
                <span className="inline-flex items-center gap-1 py-1 pl-2.5 pr-2 capitalize">
                  {chip.label}
                  {/* A saved rule under the catalogue bar: kept on screen so it can be cleared, and
                      honest that it removes little or nothing. The note and the marginal count take
                      the chip's own full-strength tone: dimmed with opacity, they fell under 4.5:1 on
                      the excluded chip's danger tint (HREV8-A11Y-01). */}
                  {chip.belowBar && (
                    <span className="normal-case" data-testid={`genre-below-bar-${chip.slug}`}>
                      ({chip.movies + chip.shows === 0 ? "matches nothing in the catalogue" : `only ${genreReach(chip)}`})
                    </span>
                  )}
                  {marginal && (
                    <span className="tabular-nums normal-case" data-testid={`genre-marginal-${chip.slug}`}>
                      {marginal}
                    </span>
                  )}
                </span>
                <button
                  type="button"
                  onClick={() => setDraft(toggleGenre(draft, chip.slug, "include"))}
                  aria-pressed={state === "include"}
                  aria-label={`Require ${chip.label}`}
                  className={`${GENRE_BUTTON} ${state === "include" ? "bg-fa-success/25" : "opacity-70"}`}
                  data-testid={`genre-${chip.slug}-include`}
                >
                  <Check className="h-3.5 w-3.5" />
                </button>
                <button
                  type="button"
                  onClick={() => setDraft(toggleGenre(draft, chip.slug, "exclude"))}
                  aria-pressed={state === "exclude"}
                  aria-label={`Exclude ${chip.label}`}
                  className={`${GENRE_BUTTON} ${state === "exclude" ? "bg-fa-danger/25" : "opacity-70"}`}
                  data-testid={`genre-${chip.slug}-exclude`}
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
            );
          })}
          {!chipReport && !chipsFailed && (
            <span className="h-7 w-48 rounded-full reel-shimmer" data-testid="genre-chips-loading" aria-hidden />
          )}
        </div>
        {!chipReport && chipsFailed && (
          <LoadFailed
            what="the genre list"
            onRetry={() => void refetchChips()}
            retrying={chipsLoading}
            variant="inline"
            testId="genre-chips-error"
          />
        )}
      </div>

      {/* ── The two ratings, adjacent and clearly labelled — they are different numbers. ────── */}
      <div className="space-y-4">
        <RatingFloor
          testId="crowd-rating-slider"
          label="minimum crowd rating"
          hint="What everyone else thinks. Hide titles the community scores below this."
          value={draft.minCrowdRating}
          onChange={(minCrowdRating) => setDraft({ ...draft, minCrowdRating })}
          accent="text-fa-warning"
          note={
            draft.minCrowdRating != null
              ? marginalNote(
                  marginalFor(cost, "MinCrowdRating", ratingRuleValue(draft.minCrowdRating)),
                  "turn this floor off",
                )
              : null
          }
          noteTestId="crowd-rating-marginal"
        />
        <RatingFloor
          testId="min-rating-slider"
          label="minimum predicted rating (for you)"
          hint="What the model thinks you'll give it. 0 = show everything."
          value={draft.minPredictedRating}
          onChange={(minPredictedRating) => setDraft({ ...draft, minPredictedRating })}
          accent="text-fa-frost-bright"
          // Said plainly rather than left as a conspicuous blank beside the crowd floor's number:
          // this floor is applied per title by the model, so it has no catalogue-wide count to give.
          note={
            draft.minPredictedRating != null
              ? "Not in the counts above — this one is applied per title by the model, after scoring."
              : null
          }
          noteTestId="min-rating-note"
        />
      </div>

      {/* ── Release years ───────────────────────────────────────────────────────────────────── */}
      <div className="space-y-2">
        <p className="fa-overline text-fa-frost-dim">release year</p>
        <p className="fa-caption text-fa-frost-dim">Leave a box empty for open-ended.</p>
        <div className="flex items-center gap-2">
          <input
            type="number"
            inputMode="numeric"
            min={YEAR_FLOOR}
            max={ceiling}
            value={years.min}
            onChange={(e) => {
              setYears({ ...years, min: e.target.value });
              setDraft({ ...draft, minYear: parseYearInput(e.target.value) });
            }}
            onBlur={commitYears}
            placeholder="from"
            aria-label="Earliest release year"
            className={cn("fa-input w-24 tabular-nums", COARSE_TAP)}
            data-testid="min-year"
          />
          <span className="fa-caption text-fa-frost-dim">to</span>
          <input
            type="number"
            inputMode="numeric"
            min={YEAR_FLOOR}
            max={ceiling}
            value={years.max}
            onChange={(e) => {
              setYears({ ...years, max: e.target.value });
              setDraft({ ...draft, maxYear: parseYearInput(e.target.value) });
            }}
            onBlur={commitYears}
            placeholder="to"
            aria-label="Latest release year"
            className={cn("fa-input w-24 tabular-nums", COARSE_TAP)}
            data-testid="max-year"
          />
          {(draft.minYear != null || draft.maxYear != null) && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                setDraft({ ...draft, minYear: null, maxYear: null });
                setYears({ min: "", max: "" });
              }}
              className={COARSE_TAP}
            >
              Any year
            </Button>
          )}
        </div>
        {/* What each bound is holding back, counted separately — they are two controls, and a user
            who would happily reach back to 1990 may not want anything after 2020. */}
        {marginalNote(
          draft.minYear != null ? marginalFor(cost, "MinYear", String(draft.minYear)) : null,
          `allow titles before ${draft.minYear}`,
        ) && (
          <p className="fa-caption text-fa-frost-dim" data-testid="min-year-marginal">
            {marginalNote(marginalFor(cost, "MinYear", String(draft.minYear)), `allow titles before ${draft.minYear}`)}
          </p>
        )}
        {marginalNote(
          draft.maxYear != null ? marginalFor(cost, "MaxYear", String(draft.maxYear)) : null,
          `allow titles after ${draft.maxYear}`,
        ) && (
          <p className="fa-caption text-fa-frost-dim" data-testid="max-year-marginal">
            {marginalNote(marginalFor(cost, "MaxYear", String(draft.maxYear)), `allow titles after ${draft.maxYear}`)}
          </p>
        )}
        {/* Said out loud rather than silently corrected mid-typing: an inverted range matches
            nothing, so a blank feed would otherwise have no visible cause. It is swapped on blur. */}
        {inverted && (
          <p className="fa-caption text-fa-warning" data-testid="year-inverted">
            That range runs backwards — it&apos;ll be read as {draft.maxYear}–{draft.minYear}.
          </p>
        )}
      </div>

      {/* ── Languages — an allow-list. English-only is the default. ─────────────────────────── */}
      <div className="space-y-2">
        <p className="fa-overline text-fa-frost-dim">languages</p>
        <p className="fa-caption text-fa-frost-dim">
          Only recommend titles in these languages. English only by default.
        </p>
        <div className="flex flex-wrap gap-1.5">
          <Chip
            selected={allLanguages}
            onClick={() => setDraft({ ...draft, languages: allLanguages ? ["en"] : [ALL_LANGUAGES] })}
            className={COARSE_TAP}
            data-testid="lang-all"
          >
            All languages
          </Chip>
          {LANGUAGES.map(({ code, label }) => {
            const on = !allLanguages && languages.includes(code);
            return (
              <Chip
                key={code}
                selected={on}
                onClick={() => toggleLanguage(code)}
                className={cn(COARSE_TAP, allLanguages && "opacity-50")}
                data-testid={`lang-${code}`}
              >
                {label}
              </Chip>
            );
          })}
        </div>
        {/* One number for the whole allow-list, because there is one control for it — and the
            counterfactual is "every language", which is what the All chip beside it does. */}
        {!allLanguages && marginalNote(marginalFor(cost, "Language", languageRuleValue(languages)), "allow every language") && (
          <p className="fa-caption text-fa-frost-dim" data-testid="language-marginal">
            {marginalNote(marginalFor(cost, "Language", languageRuleValue(languages)), "allow every language")}
          </p>
        )}
      </div>

      {/* ── Maturity ────────────────────────────────────────────────────────────────────────── */}
      <div className="space-y-2">
        <p className="fa-overline text-fa-frost-dim" aria-hidden>
          maturity ceiling
        </p>
        {/* The Select primitive, like the Region and Time-zone pickers beside it (HREV1-D08): the
            native control drew the operating system's own dropdown in the middle of the form. */}
        <Select
          label="Maturity ceiling"
          hideLabel
          options={MATURITY_OPTIONS}
          value={maturityOptionValue(draft.maturityCeiling)}
          onValueChange={(value) => setDraft({ ...draft, maturityCeiling: maturityFromOption(value) })}
          className="w-40"
          data-testid="maturity-ceiling"
        />
      </div>

      {/* ── Keyword exclusions ──────────────────────────────────────────────────────────────── */}
      <div className="space-y-2">
        <p className="fa-overline text-fa-frost-dim">excluded keywords</p>
        <KeywordEditor
          keywords={draft.excludeKeywords}
          onChange={(excludeKeywords) => setDraft({ ...draft, excludeKeywords })}
        />
      </div>

      <div className="flex flex-wrap items-center gap-3 border-t border-fa-edge/50 pt-4">
        <Button variant="primary" onClick={save} disabled={saving} className={COARSE_TAP} data-testid="save-prefs">
          {saving ? "Saving…" : saveLabel}
        </Button>
        {dirty && !saving && (
          <span className="fa-caption text-fa-warning" data-testid="conditions-unsaved">
            Unsaved changes
          </span>
        )}
        <SaveStatus message={status} onRetry={save} retrying={saving} testId="conditions-save-status" />
      </div>
    </section>
  );
}

/** One 0–9 floor slider. Two of these sit next to each other, so the label carries the difference. */
function RatingFloor({
  label,
  hint,
  value,
  onChange,
  testId,
  accent,
  note,
  noteTestId,
}: {
  label: string;
  hint: string;
  value: number | null;
  onChange: (next: number | null) => void;
  testId: string;
  accent: string;
  /** What this floor is costing, or why it has no count. Absent while the meter is in flight. */
  note?: string | null;
  noteTestId?: string;
}) {
  const current = value ?? 0;
  return (
    <div className="space-y-2">
      <p className="fa-overline text-fa-frost-dim">{label}</p>
      <p className="fa-caption text-fa-frost-dim">{hint}</p>
      <div className="flex items-center gap-4 max-w-md">
        <input
          type="range"
          min={0}
          max={9}
          step={0.5}
          value={current}
          aria-label={label}
          onChange={(e) => onChange(Number(e.target.value) || null)}
          className={cn("fa-range flex-1", COARSE_TAP)}
          style={{ "--fa-range-fill": `${((current / 9) * 100).toFixed(0)}%` } as React.CSSProperties}
          data-testid={testId}
        />
        <span className={`fa-metric-sm w-14 text-right tabular-nums ${accent}`}>
          {current > 0 ? current.toFixed(1) : "off"}
        </span>
      </div>
      {note && (
        <p className="fa-caption text-fa-frost-dim" data-testid={noteTestId}>
          {note}
        </p>
      )}
    </div>
  );
}

function KeywordEditor({ keywords, onChange }: { keywords: string[]; onChange: (next: string[]) => void }) {
  const [input, setInput] = useState("");

  const add = () => {
    const value = input.trim().toLowerCase();
    if (value && !keywords.includes(value)) {
      onChange([...keywords, value]);
    }
    setInput("");
  };

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {keywords.map((keyword) => (
        <span key={keyword} className="inline-flex items-center gap-1 rounded-full border border-fa-edge bg-fa-glass px-2.5 py-1 fa-caption text-fa-frost">
          {keyword}
          <Button
            variant="ghost"
            size="icon-sm"
            onClick={() => onChange(keywords.filter((k) => k !== keyword))}
            aria-label={`remove ${keyword}`}
            data-testid={`keyword-${keyword}-remove`}
            className={cn("h-5 w-5 rounded-full hover:text-fa-danger", COARSE_TAP)}
          >
            <X className="h-3 w-3" />
          </Button>
        </span>
      ))}
      <input
        value={input}
        onChange={(e) => setInput(e.target.value)}
        onKeyDown={(e) => e.key === "Enter" && add()}
        onBlur={add}
        placeholder="add keyword…"
        aria-label="Add an excluded keyword"
        className="fa-input h-8 w-36 text-sm [@media(pointer:coarse)]:h-11"
        data-testid="keyword-input"
      />
    </div>
  );
}
