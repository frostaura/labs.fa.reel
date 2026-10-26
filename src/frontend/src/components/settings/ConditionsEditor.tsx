import { useEffect, useState } from "react";
import { Lock, Minus, Plus, X } from "lucide-react";
import {
  useFilterCostQuery,
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
  cycleGenre,
  genreState,
  GENRES,
  DEFAULT_FILTERS,
  isInvertedYearRange,
  LANGUAGES,
  MATURITY_LEVELS,
  normalizeFilters,
  normalizeYearRange,
  parseYearInput,
  sameConditions,
  YEAR_FLOOR,
  yearCeiling,
  type GenreState,
} from "../../lib/pickConditions";
import ConditionSummary from "./ConditionSummary";
import LoadFailed from "../ui/load-failed";
import SaveStatus from "../ui/save-status";
import { saveFailureMessage, savedMessage, type SaveMessage } from "../ui/save-outcome";
import { useLeaveGuard } from "./useLeaveGuard";

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
}

/**
 * How long the editor waits before costing a draft. Long enough that a chip-tapping burst is one
 * request, short enough that the number feels attached to the tap that changed it.
 */
const COST_DEBOUNCE_MS = 350;

const GENRE_TONE: Record<GenreState, string> = {
  neutral: "border-fa-edge bg-fa-glass text-fa-frost hover:border-fa-frost/40",
  include: "border-fa-success/50 bg-fa-success/15 text-fa-success",
  exclude: "border-fa-danger/50 bg-fa-danger/15 text-fa-danger",
};

const GENRE_ARIA: Record<GenreState, string> = {
  neutral: "not filtered",
  include: "required",
  exclude: "excluded",
};

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
export default function ConditionsEditor({ onSaved, saveLabel = "Save conditions", bare, testIds }: Props) {
  const { data: session } = useGetSessionQuery();
  const { data: saved, isError: loadFailed, isFetching: loadingSaved, refetch: refetchSaved } = useGetFiltersQuery();
  const [updateFilters, { isLoading: saving }] = useUpdateFiltersMutation();
  const [verifyPin] = useVerifyPinMutation();
  const unlockToken = useUnlockToken();

  const [draft, setDraft] = useState<FiltersPayload | null>(null);
  const [years, setYears] = useState({ min: "", max: "" });
  const [status, setStatus] = useState<SaveMessage | null>(null);
  const [pinEntry, setPinEntry] = useState("");
  const [pinError, setPinError] = useState(false);

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

  // The costed draft trails the edited one. Serialising it is what makes the effect stable: a fresh
  // object identity on every keystroke would re-arm the timer for ever and the request would never
  // fire, and RTK Query keys its cache on the serialised arg anyway.
  const [costDraft, setCostDraft] = useState<FiltersPayload | null>(null);
  const draftKey = draft ? JSON.stringify(draft) : "";
  useEffect(() => {
    if (draftKey === "") return;
    const handle = window.setTimeout(() => setCostDraft(JSON.parse(draftKey) as FiltersPayload), COST_DEBOUNCE_MS);
    return () => window.clearTimeout(handle);
  }, [draftKey]);
  // Errors are swallowed on purpose: the meter is an aid, and an editor that refuses to render
  // because a count failed would be a worse screen than one that quietly shows no counts.
  const { data: cost } = useFilterCostQuery(costDraft ?? DEFAULT_FILTERS, {
    skip: costDraft === null || locked,
  });

  const unlock = async () => {
    const result = await verifyPin({ pin: pinEntry }).unwrap().catch(() => null);
    if (result?.valid && result.unlockToken) {
      // What gets kept is the server-minted unlock token, never the PIN the user just typed.
      storeUnlockToken(result.unlockToken, result.expiresAt);
      setPinEntry("");
      setPinError(false);
    } else {
      setPinError(true);
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
            className="fa-input w-28 tabular-nums"
            data-testid="pin-input"
          />
          <button onClick={unlock} className="fa-button">
            Unlock
          </button>
          {pinError && <span className="fa-caption text-fa-danger">Wrong PIN</span>}
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
  if (!draft) {
    return (
      <section className={bare ? "space-y-3" : "fa-card p-5 space-y-3"} data-testid={testIds?.loading ?? "conditions-loading"}>
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
    <section className={shell} data-testid={testIds?.editor ?? "conditions-editor"}>
      {/* Suppressed in the dialog, which supplies its own DialogTitle — two identical headings
          stacked on top of each other read as a rendering bug. */}
      {!bare && (
        <div>
          <h2 className="fa-section-title">Conditions for tonight&apos;s picks</h2>
          {/* Only what the server enforces. The top-bar typeahead is a name lookup: it drops titles
              your exclusions rule out without saying so, but not the "for you" floor, and its
              global leg applies none of them — "all search" was false (R1-26). */}
          <p className="fa-caption text-fa-frost-dim mt-1" data-testid="conditions-scope">
            These are airtight for the hero, every row and Ask Reel. Quick search in the top bar looks titles up by
            name and only partly applies them, so it can show a title these rules would leave out.
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
              <button
                key={`${suggestion.kind}:${suggestion.value}`}
                type="button"
                onClick={() => setDraft(applySuggestion(draft, suggestion))}
                data-testid="cost-suggestion"
                data-kind={suggestion.kind}
                data-value={suggestion.value}
                title="Applies to this draft — nothing is saved until you save"
                className="inline-flex items-center gap-1.5 rounded-full border border-fa-frost/40 bg-fa-glass px-2.5 py-1 fa-caption text-fa-frost transition hover:border-fa-frost/60 hover:bg-fa-glass-strong hover:text-fa-frost-bright"
              >
                {suggestionLabel(suggestion)}
                <span className="tabular-nums text-fa-success">{formatMarginal(suggestion.gain)}</span>
              </button>
            ))}
          </div>
        )}
      </div>

      {/* ── Genres ──────────────────────────────────────────────────────────────────────────
          Three states, not two. The list has always had an `includeGenres` half; it simply had no
          UI, so "give me horror" was inexpressible and only "never horror" could be said. */}
      <div className="space-y-2">
        <p className="fa-overline text-fa-frost-dim">genres</p>
        <p className="fa-caption text-fa-frost-dim">
          Tap once to require, twice to exclude, a third time to clear.
        </p>
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 fa-caption">
          <span className="inline-flex items-center gap-1.5 text-fa-success">
            <span className="inline-flex h-4 w-4 items-center justify-center rounded-full border border-fa-success/50 bg-fa-success/15">
              <Plus className="h-2.5 w-2.5" />
            </span>
            include — must have at least one
          </span>
          <span className="inline-flex items-center gap-1.5 text-fa-danger">
            <span className="inline-flex h-4 w-4 items-center justify-center rounded-full border border-fa-danger/50 bg-fa-danger/15">
              <Minus className="h-2.5 w-2.5" />
            </span>
            exclude — must have none
          </span>
        </div>
        <div className="flex flex-wrap gap-1.5">
          {GENRES.map((genre) => {
            const state = genreState(draft, genre);
            // Only excluded chips carry a number, and only a positive one: the badge answers "what
            // is this chip costing me", which is a question an inactive chip does not raise.
            const marginal = state === "exclude" ? formatMarginal(marginalFor(cost, "ExcludeGenre", genre)) : null;
            return (
              <button
                key={genre}
                type="button"
                onClick={() => setDraft(cycleGenre(draft, genre))}
                aria-label={
                  marginal
                    ? `${genre.replace("-", " ")} — ${GENRE_ARIA[state]}, ${marginal} titles if allowed`
                    : `${genre.replace("-", " ")} — ${GENRE_ARIA[state]}`
                }
                className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-1 fa-caption capitalize transition ${GENRE_TONE[state]}`}
                data-testid={`genre-${genre}`}
                data-state={state}
              >
                {state === "include" && <Plus className="h-3 w-3" />}
                {state === "exclude" && <Minus className="h-3 w-3" />}
                {genre.replace("-", " ")}
                {marginal && (
                  <span className="tabular-nums normal-case opacity-80" data-testid={`genre-marginal-${genre}`}>
                    {marginal}
                  </span>
                )}
              </button>
            );
          })}
        </div>
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
            className="fa-input w-24 tabular-nums"
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
            className="fa-input w-24 tabular-nums"
            data-testid="max-year"
          />
          {(draft.minYear != null || draft.maxYear != null) && (
            <button
              type="button"
              onClick={() => {
                setDraft({ ...draft, minYear: null, maxYear: null });
                setYears({ min: "", max: "" });
              }}
              className="fa-button-ghost fa-caption"
            >
              Any year
            </button>
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
          <button
            type="button"
            onClick={() => setDraft({ ...draft, languages: allLanguages ? ["en"] : [ALL_LANGUAGES] })}
            className={`rounded-full border px-2.5 py-1 fa-caption transition ${
              allLanguages
                ? "border-fa-frost/60 bg-fa-frost/15 text-fa-frost-bright"
                : "border-fa-edge bg-fa-glass text-fa-frost hover:border-fa-frost/40"
            }`}
            data-testid="lang-all"
          >
            All languages
          </button>
          {LANGUAGES.map(({ code, label }) => {
            const on = !allLanguages && languages.includes(code);
            return (
              <button
                key={code}
                type="button"
                onClick={() => toggleLanguage(code)}
                className={`rounded-full border px-2.5 py-1 fa-caption transition ${
                  on
                    ? "border-fa-frost/60 bg-fa-frost/15 text-fa-frost-bright"
                    : "border-fa-edge bg-fa-glass text-fa-frost hover:border-fa-frost/40"
                } ${allLanguages ? "opacity-50" : ""}`}
                data-testid={`lang-${code}`}
              >
                {label}
              </button>
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
        <p className="fa-overline text-fa-frost-dim">maturity ceiling</p>
        <select
          className="fa-input fa-select w-40"
          value={draft.maturityCeiling ?? ""}
          aria-label="Maturity ceiling"
          onChange={(e) => setDraft({ ...draft, maturityCeiling: e.target.value || null })}
        >
          {MATURITY_LEVELS.map((level) => (
            <option key={level} value={level} className="bg-fa-ink-2">
              {level === "" ? "none" : level === "R" ? "R / TV-MA" : level}
            </option>
          ))}
        </select>
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
        <button onClick={save} disabled={saving} className="fa-button-primary" data-testid="save-prefs">
          {saving ? "Saving…" : saveLabel}
        </button>
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
          className="fa-range flex-1"
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
          <button onClick={() => onChange(keywords.filter((k) => k !== keyword))} aria-label={`remove ${keyword}`}>
            <X className="h-3 w-3 text-fa-frost-dim hover:text-fa-danger" />
          </button>
        </span>
      ))}
      <input
        value={input}
        onChange={(e) => setInput(e.target.value)}
        onKeyDown={(e) => e.key === "Enter" && add()}
        onBlur={add}
        placeholder="add keyword…"
        aria-label="Add an excluded keyword"
        className="fa-input h-8 w-36 text-sm"
      />
    </div>
  );
}
