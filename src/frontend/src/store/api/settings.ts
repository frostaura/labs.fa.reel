import { api } from "./base";

export interface FiltersPayload {
  excludeGenres: string[];
  /** "Must have at least one of these" — the positive half of the genre rule. */
  includeGenres: string[];
  excludeKeywords: string[];
  maturityCeiling: string | null;
  minPredictedRating: number | null;
  /** Allowed original languages (ISO-639-1). Defaults to ["en"]; ["*"] = every language. */
  languages: string[];
  /** Minimum community score (0–10). null = off. */
  minCrowdRating: number | null;
  /** Release-year range; either bound may be null for open-ended. */
  minYear: number | null;
  maxYear: number | null;
}

/**
 * What one content rule costs. `marginal` is the eligible count with that ONE rule relaxed minus the
 * eligible count as drafted — "dropping comedy adds ~180". Marginals deliberately do NOT sum: two
 * rules can be holding the same title out, so relaxing both returns less than the two numbers
 * together, which is exactly why the meter reports per rule instead of one grand total.
 */
export interface FilterCostRule {
  /** The server's FilterKind name — "ExcludeGenre", "Language", "MinCrowdRating", "MinYear"… */
  kind: string;
  /** The value the user would tap off: a genre slug, a threshold, or the joined language list. */
  value: string;
  marginal: number;
}

/** A rule worth offering as a one-tap relaxation, because relaxing it actually returns titles. */
export interface FilterCostSuggestion {
  kind: string;
  value: string;
  gain: number;
}

/**
 * The cost of a draft rule set. `total` is the whole catalogue (the denominator in "421 of 5,319");
 * `eligible` is what survives BOTH the rules and the locked zero-footprint rule — the real supply
 * behind tonight's picks, not a filter-only count.
 */
export interface FilterCostReport {
  total: number;
  eligible: number;
  byRule: FilterCostRule[];
  suggestions: FilterCostSuggestion[];
}

/**
 * One genre chip from the catalogue (WS-3, GET /api/search/filter-chips): the films and shows an
 * exclusion of that slug would remove — the same predicate eligibility applies, so the number on a
 * chip is what the chip controls. `offered` is false under the server's bar (`minTitles`); such a
 * chip is listed so a saved rule for it still has a row, but it is not offered for new taps (H-7).
 */
export interface FilterChip {
  slug: string;
  movies: number;
  shows: number;
  /** A combination of facts rather than a tag: anime is animation in Japanese. */
  composite: boolean;
  offered: boolean;
}

export interface FilterChipReport {
  minTitles: number;
  chips: FilterChip[];
}

export interface AccountSettings {
  region: string;
  onboarded: boolean;
  email?: string | null;
  /** Served RESOLVED (never null): how long an evening is, for the Saved shelf's pinned pick. */
  eveningBudgetMinutes?: number;
  /**
   * The IANA zone the account's day is counted in, AS STORED: null when never set. A PUT with an
   * IANA id sets it, an empty string clears it back to UTC, and anything else is a 400
   * `time_zone_invalid` that changes nothing.
   */
  timeZone?: string | null;
  /** The zone actually in force (read-only): the stored one, or UTC when none is set. */
  effectiveTimeZone?: string;
}

export const settingsApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
  endpoints: (b) => ({
    // What the account has stored — read so the email box shows (and can clear) what is on file
    // instead of always starting empty. Tagged "Session" because every PUT below invalidates that.
    getSettings: b.query<AccountSettings, void>({
      query: () => "settings",
      providesTags: ["Session"],
    }),
    // Returns the stored settings AFTER the write. Callers compare it with what they sent: the
    // endpoint answers 200 and keeps the old value for an email it does not accept.
    // The stored settings it returns are written straight into `getSettings`: waiting for the
    // invalidation's refetch left the email box showing the OLD address in between — and for good
    // if that refetch failed.
    updateSettings: b.mutation<AccountSettings, Partial<AccountSettings>>({
      query: (body) => ({ url: "settings", method: "PUT", body }),
      invalidatesTags: ["Session"],
      async onQueryStarted(_body, { dispatch, queryFulfilled }) {
        try {
          const { data } = await queryFulfilled;
          dispatch(
            settingsApi.util.updateQueryData("getSettings", undefined, (draft) => {
              Object.assign(draft, data);
            })
          );
        } catch {
          // A failed save changes nothing stored; the caller reports it.
        }
      },
    }),
    getFilters: b.query<FiltersPayload, void>({
      query: () => "settings/filters",
      providesTags: ["Prefs"],
    }),
    // Saves ride the short-lived unlock token from /pin/verify (X-Settings-Unlock) — the raw PIN
    // is never cached client-side, so it is never replayed per call.
    updateFilters: b.mutation<FiltersPayload, { filters: FiltersPayload; unlockToken?: string }>({
      query: ({ filters, unlockToken }) => ({
        url: "settings/filters",
        method: "PUT",
        body: filters,
        headers: unlockToken ? { "X-Settings-Unlock": unlockToken } : undefined,
      }),
      invalidatesTags: ["Prefs", "Feed"],
    }),
    // What the DRAFT in the editor costs — a POST because it carries the unsaved rule set, but a
    // query and not a mutation because it changes nothing: it needs the arg-keyed cache, since the
    // editor re-costs on every debounced chip tap and slider drag. `keepUnusedDataFor` matches the
    // server's own 60s window, so a user toggling a chip back and forth pays for it once.
    // Catalogue-wide and slow-moving (the server caches it 30 minutes), so it is kept as long.
    getFilterChips: b.query<FilterChipReport, void>({
      query: () => "search/filter-chips",
      keepUnusedDataFor: 1800,
    }),
    filterCost: b.query<FilterCostReport, FiltersPayload>({
      query: (filters) => ({ url: "settings/filters/cost", method: "POST", body: filters }),
      keepUnusedDataFor: 60,
    }),
    setPin: b.mutation<{ pinConfigured: boolean }, { pin: string; currentPin?: string }>({
      query: (body) => ({ url: "settings/pin", method: "POST", body }),
      invalidatesTags: ["Session"],
    }),
    verifyPin: b.mutation<{ valid: boolean; unlockToken: string; expiresAt: string }, { pin: string }>({
      query: (body) => ({ url: "settings/pin/verify", method: "POST", body }),
    }),
    // PIN removal is destructive, so it deliberately takes the freshly re-entered raw PIN — the
    // backend refuses unlock tokens on this endpoint and callers must prompt, not read a cache.
    removePin: b.mutation<{ pinConfigured: boolean }, { pin: string }>({
      query: ({ pin }) => ({ url: "settings/pin", method: "DELETE", headers: { "X-Settings-Pin": pin } }),
      invalidatesTags: ["Session"],
    }),
    getPreferenceTags: b.query<{ id: string; text: string }[], void>({
      query: () => "preferences/tags",
      providesTags: ["PrefTags"],
    }),
    addPreferenceTag: b.mutation<{ id: string; text: string }, string>({
      query: (text) => ({ url: "preferences/tags", method: "POST", body: { text } }),
      invalidatesTags: ["PrefTags", "Feed"],
    }),
    removePreferenceTag: b.mutation<void, string>({
      query: (id) => ({ url: `preferences/tags/${id}`, method: "DELETE" }),
      invalidatesTags: ["PrefTags", "Feed"],
    }),
    getBillingStatus: b.query<{ tier: string; configured: boolean }, void>({
      query: () => "billing/status",
      providesTags: ["Session"],
    }),
    createCheckout: b.mutation<{ url?: string; error?: string }, void>({
      query: () => ({ url: "billing/checkout", method: "POST" }),
    }),
    // Web push. The VAPID key is fetched lazily at opt-in time, not on page load.
    getVapidPublicKey: b.query<{ key: string }, void>({
      query: () => "push/vapid-public-key",
    }),
    subscribePush: b.mutation<{ ok: boolean }, { endpoint: string; keys: { p256dh: string; auth: string } }>({
      query: (body) => ({ url: "push/subscribe", method: "POST", body }),
    }),
    unsubscribePush: b.mutation<{ ok: boolean }, { endpoint: string }>({
      query: (body) => ({ url: "push/subscribe", method: "DELETE", body }),
    }),
  }),
});

export const {
  useGetSettingsQuery,
  useUpdateSettingsMutation,
  useGetFiltersQuery,
  useUpdateFiltersMutation,
  useGetPreferenceTagsQuery,
  useAddPreferenceTagMutation,
  useRemovePreferenceTagMutation,
  useGetBillingStatusQuery,
  useCreateCheckoutMutation,
  useSetPinMutation,
  useFilterCostQuery,
  useLazyFilterCostQuery,
  useGetFilterChipsQuery,
  useVerifyPinMutation,
  useRemovePinMutation,
  useLazyGetVapidPublicKeyQuery,
  useSubscribePushMutation,
  useUnsubscribePushMutation,
} = settingsApi;
