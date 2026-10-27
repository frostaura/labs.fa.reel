import type { AvailabilityFilter, BrowseFilter, MediaFilter } from "../../lib/browseFilter";
import { SegmentedControl, type SegmentedOption } from "../ui/segmented-control";

/**
 * Segmented browse-filter controls: media type + release availability.
 *
 * Both are the design system's `SegmentedControl` (PH-10): a named radio group each ("Media type",
 * "Availability"), one Tab stop per group and the arrow keys to move the choice. They used to be two
 * unnamed rows of toggle buttons, so a keyboard walk met "All, Movies, TV, All, Available, Coming
 * soon" with nothing saying which "All" was which — and both carried the same `browse-all` test id.
 */
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
      <SegmentedControl<MediaFilter>
        label="Media type"
        size="sm"
        options={[
          segment("all", "All", "browse-media-all"),
          segment("movie", "Movies", "browse-movie"),
          segment("show", "TV", "browse-show"),
        ]}
        value={value.media}
        onValueChange={(media) => onChange({ ...value, media })}
      />
      {showAvailability && (
        <SegmentedControl<AvailabilityFilter>
          label="Availability"
          size="sm"
          options={[
            segment("all", "All", "browse-availability-all"),
            segment("available", "Available", "browse-available"),
            segment("coming", "Coming soon", "browse-coming"),
          ]}
          value={value.availability}
          onValueChange={(availability) => onChange({ ...value, availability })}
        />
      )}
    </div>
  );
}

/**
 * One segment's face. The control's own target is 44 px only on a coarse pointer, and this row has
 * to be 44 px on any phone-width screen (the askreel-mobile and phone-target probes measure it at
 * 375 px under a mouse too): so the label holds the 44 px itself below `md`, with a negative margin
 * equal to the `sm` segment's own vertical padding so the segment is 44 px, not 52. From `md` on a
 * fine pointer it is the compact segment. The test id rides the label (the control takes none).
 */
function segment<T extends string>(value: T, text: string, testId: string): SegmentedOption<T> {
  return {
    value,
    label: (
      <span
        className="-my-1 inline-flex min-h-11 min-w-6 items-center justify-center md:[@media(pointer:fine)]:my-0 md:[@media(pointer:fine)]:min-h-0 md:[@media(pointer:fine)]:min-w-0"
        data-testid={testId}
      >
        {text}
      </span>
    ),
  };
}
