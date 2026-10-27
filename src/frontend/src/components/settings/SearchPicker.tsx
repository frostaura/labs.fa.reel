import { useId, useMemo, useState, type ReactNode } from "react";
import { ChevronDown } from "lucide-react";
import { COARSE_TAP } from "../../lib/tapTarget";

/** How many matches the list draws at once; typing narrows it, so the cap is never a wall. */
const MAX_SHOWN = 60;

export interface SearchPickerProps<T> {
  /** The visible label above the box — what is being chosen, in the page's words. */
  label: string;
  placeholder: string;
  /** Names the list for a screen reader ("Regions", "Time zones"). */
  listLabel: string;
  /** What kind of thing the empty list failed to find, for "No region matches “…”." */
  noun: string;
  options: T[];
  keyOf: (option: T) => string;
  /** Narrows and orders `options` for a query; an empty query returns them all. */
  filter: (options: T[], query: string) => T[];
  /** The option's row: its main text and, optionally, a quieter note on the right. */
  render: (option: T) => { text: string; note?: ReactNode };
  /** The key of the option in force, or null. */
  value: string | null;
  /** What the closed box reads: the option in force, in words. */
  valueText: string;
  onSelect: (key: string) => void;
  disabled?: boolean;
  /** `data-testid` of the box; the list and each row take `<testId>-options` / `<testId>-option`. */
  testId: string;
  /** `data-testid`s that predate this component, kept so existing specs still find them. */
  listTestId?: string;
  optionTestId?: string;
}

/**
 * A labelled, searchable combobox over a long list (H-10, R1-17): type to narrow, arrows move, Enter
 * picks, Escape puts back what was there. Region and time zone both pick one of a few hundred ids
 * whose names the reader knows better than the ids, so both use this instead of a native select.
 */
export default function SearchPicker<T>({
  label,
  placeholder,
  listLabel,
  noun,
  options,
  keyOf,
  filter,
  render,
  value,
  valueText,
  onSelect,
  disabled,
  testId,
  listTestId = `${testId}-options`,
  optionTestId = `${testId}-option`,
}: SearchPickerProps<T>) {
  const id = useId();
  const inputId = `${id}-input`;
  const listId = `${id}-list`;

  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const matches = useMemo(() => filter(options, query).slice(0, MAX_SHOWN), [filter, options, query]);

  const close = () => {
    setOpen(false);
    setQuery("");
    setActive(0);
  };

  const pick = (key: string) => {
    close();
    if (key !== value) onSelect(key);
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
        pick(keyOf(matches[active]));
      }
    } else if (e.key === "Escape") {
      close();
    }
  };

  // Option ids are built from keys, which may hold "/" (time zones); ids only need to be unique.
  const optionId = (index: number) => `${listId}-${index}`;

  return (
    <div className="space-y-1.5">
      <label htmlFor={inputId} className="fa-overline text-3 block">
        {label}
      </label>
      <div className="relative w-full max-w-sm">
        <input
          id={inputId}
          type="text"
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={open && matches[active] ? optionId(active) : undefined}
          autoComplete="off"
          spellCheck={false}
          disabled={disabled}
          placeholder={placeholder}
          value={open ? query : valueText}
          onFocus={() => setOpen(true)}
          onBlur={close}
          onChange={(e) => {
            setQuery(e.target.value);
            setActive(0);
            setOpen(true);
          }}
          onKeyDown={onKeyDown}
          className={`fa-input w-full pr-9 ${COARSE_TAP}`}
          data-testid={testId}
        />
        <ChevronDown className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-fa-frost-dim" aria-hidden />
        {open && (
          <ul
            id={listId}
            role="listbox"
            aria-label={listLabel}
            className="absolute z-30 mt-1 max-h-64 w-full overflow-y-auto rounded-lg border border-fa-edge bg-fa-ink-2 py-1 shadow-fa-glass"
            data-testid={listTestId}
          >
            {matches.length === 0 ? (
              <li className="px-3 py-2 fa-caption text-3" role="presentation">
                No {noun} matches &ldquo;{query.trim()}&rdquo;.
              </li>
            ) : (
              matches.map((option, index) => {
                const key = keyOf(option);
                const row = render(option);
                return (
                  <li
                    key={key}
                    id={optionId(index)}
                    role="option"
                    aria-selected={key === value}
                    // mousedown, not click: the input's blur closes the list before a click lands.
                    onMouseDown={(e) => {
                      e.preventDefault();
                      pick(key);
                    }}
                    onMouseEnter={() => setActive(index)}
                    className={`flex cursor-pointer items-center justify-between gap-3 px-3 py-2 fa-body [@media(pointer:coarse)]:min-h-11 ${
                      index === active ? "bg-fa-glass-strong text-fa-frost-bright" : "text-fa-frost"
                    }`}
                    data-testid={optionTestId}
                    data-code={key}
                  >
                    <span className="truncate">{row.text}</span>
                    {row.note != null && <span className="fa-caption text-3 tabular-nums shrink-0">{row.note}</span>}
                  </li>
                );
              })
            )}
          </ul>
        )}
      </div>
    </div>
  );
}
