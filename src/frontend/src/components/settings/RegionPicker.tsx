import { useId, useMemo, useState } from "react";
import { ChevronDown } from "lucide-react";
import { filterRegions, regionOptions } from "./regions";

/** How many matches the list draws at once; typing narrows it, so the cap is never a wall. */
const MAX_SHOWN = 60;

/**
 * Where you watch, as a labelled, searchable combobox over every ISO 3166-1 region (H-10). Type a
 * name or a code ("south", "za"); arrows move, Enter picks, Escape puts back what was there. The
 * region drives where-to-watch, so the label says so rather than leaving a two-letter code to explain
 * itself.
 */
export default function RegionPicker({
  value,
  onSelect,
  disabled,
}: {
  value: string | null;
  onSelect: (code: string) => void;
  disabled?: boolean;
}) {
  const id = useId();
  const inputId = `${id}-input`;
  const listId = `${id}-list`;
  const options = useMemo(() => regionOptions(), []);
  const current = options.find((o) => o.code === value) ?? null;
  const currentText = current ? `${current.name} (${current.code})` : value ?? "";

  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const matches = useMemo(() => filterRegions(options, query).slice(0, MAX_SHOWN), [options, query]);

  const close = () => {
    setOpen(false);
    setQuery("");
    setActive(0);
  };

  const pick = (code: string) => {
    close();
    if (code !== value) onSelect(code);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setOpen(true);
      setActive((i) => Math.min(matches.length - 1, i + 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((i) => Math.max(0, i - 1));
    } else if (e.key === "Enter") {
      if (open && matches[active]) {
        e.preventDefault();
        pick(matches[active].code);
      }
    } else if (e.key === "Escape") {
      close();
    }
  };

  return (
    <div className="space-y-1.5">
      <label htmlFor={inputId} className="fa-overline text-fa-frost-dim block">
        Your region
      </label>
      <div className="relative w-full max-w-sm">
        <input
          id={inputId}
          type="text"
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={open && matches[active] ? `${listId}-${matches[active].code}` : undefined}
          autoComplete="off"
          spellCheck={false}
          disabled={disabled}
          placeholder="Search by country or code"
          value={open ? query : currentText}
          onFocus={() => setOpen(true)}
          onBlur={close}
          onChange={(e) => {
            setQuery(e.target.value);
            setActive(0);
            setOpen(true);
          }}
          onKeyDown={onKeyDown}
          className="fa-input w-full pr-9"
          data-testid="region-select"
        />
        <ChevronDown className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-fa-frost-dim" aria-hidden />
        {open && (
          <ul
            id={listId}
            role="listbox"
            aria-label="Regions"
            className="absolute z-30 mt-1 max-h-64 w-full overflow-y-auto rounded-lg border border-fa-edge bg-fa-ink-2 py-1 shadow-fa-glass"
            data-testid="region-options"
          >
            {matches.length === 0 ? (
              <li className="px-3 py-2 fa-caption text-fa-frost-dim" role="presentation">
                No region matches &ldquo;{query.trim()}&rdquo;.
              </li>
            ) : (
              matches.map((option, index) => (
                <li
                  key={option.code}
                  id={`${listId}-${option.code}`}
                  role="option"
                  aria-selected={option.code === value}
                  // mousedown, not click: the input's blur closes the list before a click lands.
                  onMouseDown={(e) => {
                    e.preventDefault();
                    pick(option.code);
                  }}
                  onMouseEnter={() => setActive(index)}
                  className={`flex cursor-pointer items-center justify-between gap-3 px-3 py-2 fa-body [@media(pointer:coarse)]:min-h-11 ${
                    index === active ? "bg-fa-glass-strong text-fa-frost-bright" : "text-fa-frost"
                  }`}
                  data-testid="region-option"
                  data-code={option.code}
                >
                  <span className="truncate">{option.name}</span>
                  <span className="fa-caption text-fa-frost-dim tabular-nums">{option.code}</span>
                </li>
              ))
            )}
          </ul>
        )}
      </div>
    </div>
  );
}
