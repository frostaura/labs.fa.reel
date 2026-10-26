import type { AvailabilityFilter, BrowseFilter, MediaFilter } from "../../lib/browseFilter";

/** Segmented browse-filter controls: media type + release availability. */
export default function BrowseFilters({
  value,
  onChange,
  showAvailability = true,
}: {
  value: BrowseFilter;
  onChange: (next: BrowseFilter) => void;
  showAvailability?: boolean;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="browse-filters">
      <Segment<MediaFilter>
        options={[
          ["all", "All"],
          ["movie", "Movies"],
          ["show", "TV"],
        ]}
        value={value.media}
        onSelect={(media) => onChange({ ...value, media })}
      />
      {showAvailability && (
        <Segment<AvailabilityFilter>
          options={[
            ["all", "All"],
            ["available", "Available"],
            ["coming", "Coming soon"],
          ]}
          value={value.availability}
          onSelect={(availability) => onChange({ ...value, availability })}
        />
      )}
    </div>
  );
}

function Segment<T extends string>({
  options,
  value,
  onSelect,
}: {
  options: [T, string][];
  value: T;
  onSelect: (v: T) => void;
}) {
  return (
    <div className="inline-flex rounded-lg border border-fa-edge bg-fa-glass p-0.5">
      {options.map(([key, label]) => (
        <button
          key={key}
          onClick={() => onSelect(key)}
          aria-pressed={value === key}
          // The segment is a real tap target on a phone and measured 23 px tall — the browse row is
          // the one control on /search that the 44 px sweep (`dca1ed3`) skipped as a foreign
          // component — and "All" was 36 px wide. 44 × 44 on any coarse pointer, with the padding
          // replaced by centring; only a fine pointer from `md` gets the compact segment back.
          className={`inline-flex min-h-11 min-w-11 items-center justify-center rounded-md px-3 md:[@media(pointer:fine)]:min-h-0 md:[@media(pointer:fine)]:min-w-0 md:[@media(pointer:fine)]:py-1 fa-caption transition ${
            value === key ? "bg-fa-frost/20 text-fa-frost-bright" : "text-fa-frost-dim hover:text-fa-frost"
          }`}
          data-testid={`browse-${key}`}
        >
          {label}
        </button>
      ))}
    </div>
  );
}
