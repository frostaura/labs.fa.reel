import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Link, useLocation, useNavigate } from "react-router";
import { AlertTriangle, Eye, Search, Sparkles } from "lucide-react";
import { useSearchTypeaheadGlobalQuery, useSearchTypeaheadQuery } from "../../store/api";
import { shouldQueryGlobal } from "../../lib/globalTypeahead";
import { mintHandoff } from "../../lib/useAskReelChat";
import PosterImage from "../rec/PosterImage";
import PredictedRatingBadge from "../rec/PredictedRatingBadge";

/**
 * One box, two modes: typing streams instant typeahead matches (personal lens — predicted
 * ratings, watched badges, filters airtight); the pinned "Ask Reel" row (or Enter) runs
 * natural-language search. A hint, never a silent mode switch.
 *
 * Spec §7 asks for title AND person results, and the dropdown is a proper combobox: Up/Down move
 * an active option, Enter takes it (falling back to Ask Reel only when nothing is active), Escape
 * dismisses. Before that there was no keyboard path to a typeahead result at all — Enter was hard
 * wired to Ask Reel, so a keyboard or screen-reader user could never reach the list.
 */

const LISTBOX_ID = "reel-typeahead-listbox";
const optionId = (index: number) => `reel-typeahead-opt-${index}`;

/** A keyboard-selectable row, in render order: titles first, then people. */
interface Option {
  path: string;
  /** Titles open as an overlay over the current page; people are a full route. */
  overlay: boolean;
}

export default function SearchBox() {
  const [query, setQuery] = useState("");
  const [debounced, setDebounced] = useState("");
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const navigate = useNavigate();
  const location = useLocation();
  const boxRef = useRef<HTMLDivElement>(null);
  const activeRef = useRef<HTMLAnchorElement | null>(null);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(query.trim()), 220);
    return () => window.clearTimeout(handle);
  }, [query]);

  // `isError` is destructured on purpose: without it a 500 rendered exactly like a genuine
  // no-match, so a broken search backend looked like an obscure film.
  const { data, isFetching, isError } = useSearchTypeaheadQuery(debounced, {
    skip: debounced.length < 2,
  });

  const titles = data?.titles ?? [];
  const people = data?.people ?? [];

  // The global TMDB leg fires only after the LOCAL query has resolved sparse — a lookup beyond
  // the ~4k ingested pool ("jeanne dielman" → something, not nothing). Gating on `data` being
  // present keeps it strictly after local resolution for the CURRENT term.
  const globalEligible = !isError && data !== undefined && shouldQueryGlobal(debounced, titles.length);
  const { data: globalData, isFetching: isGlobalFetching } = useSearchTypeaheadGlobalQuery(debounced, {
    skip: !globalEligible,
  });
  const globalRows = globalEligible ? (globalData?.global ?? []) : [];

  const options: Option[] = [
    ...titles.map((t) => ({ path: `/title/${t.mediaType.toLowerCase()}/${t.tmdbId}`, overlay: true })),
    ...people.map((p) => ({ path: `/person/${p.personId}`, overlay: false })),
    // Global rows navigate exactly like local title rows — the detail endpoint hydrates on demand.
    ...globalRows.map((g) => ({ path: `/title/${g.mediaType}/${g.tmdbId}`, overlay: true })),
  ];
  // Clamped rather than reset in an effect: a shorter result set must never leave the active
  // descendant pointing at an option that no longer exists.
  const active = activeIndex < options.length ? activeIndex : -1;

  // Close on outside click.
  useEffect(() => {
    const onPointerDown = (event: PointerEvent) => {
      if (boxRef.current && !boxRef.current.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, []);

  // Keep the keyboard-active row in view once the list is long enough to scroll.
  useEffect(() => {
    activeRef.current?.scrollIntoView({ block: "nearest" });
  }, [active]);

  /**
   * Hand the question to /search in the navigation itself, not in the URL.
   *
   * `?q=` was a round trip through the address bar: the page read the param, fired the turn, then
   * deleted the param — and a reload landing before that delete re-fired the question as a fresh
   * conversation, throwing away every refinement made since. Router state is carried by the
   * history entry instead, so there is nothing to clean up and nothing to re-fire.
   *
   * The turn id is minted HERE, with the navigation, and recorded as THIS document's hand-off —
   * see {@link mintHandoff}. It does two jobs. It is the turn's idempotency key, so a /search that
   * genuinely re-sends it is replayed from the stored turn rather than billed a second time; and
   * it is what lets /search tell a live hand-off from one that merely came back with the history
   * entry on a reload or a Back, which it used to re-ask as a phantom turn.
   */
  const askReel = () => {
    const ask = query.trim();
    if (ask.length === 0) return;
    setOpen(false);
    setActiveIndex(-1);
    navigate("/search", { state: { ask, askTurnId: mintHandoff() } });
  };

  const go = (option: Option) => {
    setOpen(false);
    setActiveIndex(-1);
    navigate(option.path, option.overlay ? { state: { backgroundLocation: location } } : undefined);
  };

  const move = (delta: number) => {
    if (options.length === 0) return;
    setOpen(true);
    const n = options.length;
    setActiveIndex(active < 0 ? (delta > 0 ? 0 : n - 1) : (active + delta + n) % n);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      move(e.key === "ArrowDown" ? 1 : -1);
      return;
    }
    if (e.key === "Enter") {
      e.preventDefault();
      // Enter takes the active option; with nothing active it still means "ask Reel".
      if (active >= 0 && options[active]) go(options[active]);
      else askReel();
      return;
    }
    if (e.key === "Escape") {
      setOpen(false);
      setActiveIndex(-1);
    }
  };

  // ≥5 words or comparator phrasing promotes the Ask row — a hint, not a switch.
  const looksNatural =
    query.trim().split(/\s+/).length >= 5 || /\b(like|but|similar to|vibe|feels|without)\b/i.test(query);

  const showDropdown = open && debounced.length >= 2;

  return (
    <div ref={boxRef} className="relative w-full max-w-md" data-testid="search-box">
      <div className="relative">
        <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-fa-frost-dim pointer-events-none" />
        <input
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
            setActiveIndex(-1);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={onKeyDown}
          placeholder="Search — or ask for a vibe…"
          className="fa-input w-full pl-9 pr-3 h-9 text-sm"
          data-testid="search-input"
          role="combobox"
          aria-expanded={showDropdown}
          aria-controls={LISTBOX_ID}
          aria-autocomplete="list"
          aria-activedescendant={active >= 0 ? optionId(active) : undefined}
          aria-label="Search titles and people, or ask Reel"
        />
      </div>

      {showDropdown && (
        <div className="absolute top-11 inset-x-0 z-50 rounded-lg border border-fa-edge bg-fa-ink-2/95 backdrop-blur-md shadow-2xl overflow-hidden reel-rise">
          {/* Ask Reel — pinned; promoted to top when the query reads like a sentence */}
          {looksNatural && <AskRow query={query} onAsk={askReel} promoted />}

          {isError ? (
            <div className="px-4 py-3 fa-caption text-fa-warning flex items-center gap-2" role="alert" data-testid="typeahead-error">
              <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
              Search is unreachable right now — Ask Reel below still works.
            </div>
          ) : isFetching && options.length === 0 ? (
            <div className="px-4 py-3 fa-caption text-fa-frost-dim">searching…</div>
          ) : options.length === 0 && !(globalEligible && isGlobalFetching) ? (
            <div className="px-4 py-3 fa-caption text-fa-frost-dim" data-testid="typeahead-empty">
              Nothing by that name.
            </div>
          ) : (
            <div id={LISTBOX_ID} role="listbox" aria-label="Search suggestions" className="max-h-[70vh] overflow-y-auto">
              {titles.length > 0 && (
                <div role="group" aria-label="Titles">
                  {titles.map((title, i) => (
                    <Link
                      key={title.titleId}
                      ref={i === active ? activeRef : undefined}
                      id={optionId(i)}
                      role="option"
                      aria-selected={i === active}
                      to={`/title/${title.mediaType.toLowerCase()}/${title.tmdbId}`}
                      state={{ backgroundLocation: location }}
                      onClick={() => setOpen(false)}
                      className={`flex items-center gap-3 px-3 py-2 transition ${i === active ? "bg-fa-glass" : "hover:bg-fa-glass"}`}
                      data-testid="typeahead-result"
                    >
                      {/* PosterImage, not a bare <img>: it carries the shimmer and the
                          gradient-initials fallback for both a null path and a broken URL. */}
                      <div className="w-8 shrink-0">
                        <PosterImage path={title.posterPath} alt={title.name} size="w185" />
                      </div>
                      <div className="min-w-0 flex-1">
                        <p className="fa-body text-fa-frost-bright truncate">{title.name}</p>
                        <p className="fa-caption text-fa-frost-dim">
                          {title.year ?? ""} · {title.mediaType === "Movie" ? "film" : "series"}
                        </p>
                      </div>
                      {title.isFullyWatched && (
                        <span className="inline-flex items-center gap-1 fa-caption text-fa-frost-dim shrink-0">
                          <Eye className="h-3.5 w-3.5" /> seen
                        </span>
                      )}
                      {title.predictedRating != null && !title.isFullyWatched && (
                        <PredictedRatingBadge rating={title.predictedRating} />
                      )}
                    </Link>
                  ))}
                </div>
              )}

              {/* People. The server has always returned them; the dropdown simply never drew them,
                  so spec §7's "instant title/person results" was half-shipped. */}
              {people.length > 0 && (
                <div role="group" aria-label="People">
                  <p className="fa-overline px-3 pt-2 pb-1 text-fa-frost-dim" aria-hidden="true">People</p>
                  {people.map((person, i) => {
                    const index = titles.length + i;
                    return (
                      <Link
                        key={person.personId}
                        ref={index === active ? activeRef : undefined}
                        id={optionId(index)}
                        role="option"
                        aria-selected={index === active}
                        to={`/person/${person.personId}`}
                        onClick={() => setOpen(false)}
                        className={`flex items-center gap-3 px-3 py-2 transition ${index === active ? "bg-fa-glass" : "hover:bg-fa-glass"}`}
                        data-testid="typeahead-person"
                      >
                        <div className="w-8 shrink-0">
                          <PosterImage path={person.profilePath} alt={person.name} size="w185" />
                        </div>
                        <div className="min-w-0 flex-1">
                          <p className="fa-body text-fa-frost-bright truncate">{person.name}</p>
                          <p className="fa-caption text-fa-frost-dim truncate">
                            {person.knownFor ?? "person"}
                          </p>
                        </div>
                      </Link>
                    );
                  })}
                </div>
              )}

              {/* Beyond your catalogue — raw TMDB lookup rows for a sparse local shortlist.
                  Lookup, not suggestion: never scored, never eligibility-filtered, no for-you
                  badge. Selecting one navigates like a local row; the detail endpoint hydrates
                  the title on demand. */}
              {globalEligible && (globalRows.length > 0 || isGlobalFetching) && (
                <div role="group" aria-label="Beyond your catalogue">
                  <p className="fa-overline px-3 pt-2 pb-1 text-fa-frost-dim flex items-baseline gap-2" aria-hidden="true">
                    Beyond your catalogue
                    <span className="fa-caption normal-case tracking-normal">via TMDB</span>
                  </p>
                  {globalRows.length === 0 ? (
                    <div className="px-4 pb-3 fa-caption text-fa-frost-dim" data-testid="typeahead-global-loading">
                      Searching everywhere…
                    </div>
                  ) : (
                    globalRows.map((row, i) => {
                      const index = titles.length + people.length + i;
                      return (
                        <Link
                          key={`${row.mediaType}-${row.tmdbId}`}
                          ref={index === active ? activeRef : undefined}
                          id={optionId(index)}
                          role="option"
                          aria-selected={index === active}
                          to={`/title/${row.mediaType}/${row.tmdbId}`}
                          state={{ backgroundLocation: location }}
                          onClick={() => setOpen(false)}
                          className={`flex items-center gap-3 px-3 py-2 transition ${index === active ? "bg-fa-glass" : "hover:bg-fa-glass"}`}
                          data-testid="typeahead-global-result"
                        >
                          <div className="w-8 shrink-0">
                            <PosterImage path={row.posterPath} alt={row.name} size="w185" />
                          </div>
                          <div className="min-w-0 flex-1">
                            <p className="fa-body text-fa-frost-bright truncate">{row.name}</p>
                            <p className="fa-caption text-fa-frost-dim">
                              {row.year ?? ""} · {row.mediaType === "movie" ? "film" : "series"}
                            </p>
                          </div>
                        </Link>
                      );
                    })
                  )}
                </div>
              )}
            </div>
          )}

          {!looksNatural && <AskRow query={query} onAsk={askReel} />}
        </div>
      )}
    </div>
  );
}

function AskRow({ query, onAsk, promoted = false }: { query: string; onAsk: () => void; promoted?: boolean }) {
  return (
    <button
      onClick={onAsk}
      className={`flex w-full items-center gap-3 px-3 py-2.5 text-left transition hover:bg-fa-frost/10 ${
        promoted ? "border-b border-fa-edge/50 bg-fa-frost/5" : "border-t border-fa-edge/50"
      }`}
      data-testid="ask-reel-row"
    >
      <Sparkles className="h-4 w-4 text-fa-frost shrink-0" />
      <span className="fa-body text-fa-frost truncate">
        Ask Reel: <span className="text-fa-frost-bright">“{query.trim()}”</span>
      </span>
    </button>
  );
}
