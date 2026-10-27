import { useMemo } from "react";
import { filterRegions, regionOptions, type RegionOption } from "./regions";
import SearchPicker from "./SearchPicker";

const keyOf = (o: RegionOption) => o.code;
const render = (o: RegionOption) => ({ text: o.name, note: o.code });

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
  const options = useMemo(() => regionOptions(), []);
  const current = options.find((o) => o.code === value) ?? null;
  return (
    <SearchPicker
      label="Your region"
      placeholder="Search by country or code"
      listLabel="Regions"
      noun="region"
      options={options}
      keyOf={keyOf}
      filter={filterRegions}
      render={render}
      value={value}
      valueText={current ? `${current.name} (${current.code})` : value ?? ""}
      onSelect={onSelect}
      disabled={disabled}
      testId="region-select"
      listTestId="region-options"
      optionTestId="region-option"
    />
  );
}
