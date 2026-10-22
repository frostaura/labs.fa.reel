import type { RatingVerdict } from "../../lib/verdictCopy";
import type { LensPayload } from "../feedTypes";
import { titleRemoved, titleRestored } from "../removedTitlesSlice";
import type { PersonPayload, ProvidersPayload, TitleDetailPayload } from "../titleTypes";
import { verdictShown } from "../verdictSlice";
import { api } from "./base";
import { feedApi } from "./feed";

export const titlesApi = api.injectEndpoints({
  endpoints: (b) => ({
    getTitle: b.query<TitleDetailPayload, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => `titles/${mediaType}/${tmdbId}`,
      providesTags: (_r, _e, arg) => [{ type: "Title", id: `${arg.mediaType}:${arg.tmdbId}` }],
    }),
    getProviders: b.query<ProvidersPayload, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => `titles/${mediaType}/${tmdbId}/providers`,
      providesTags: (_r, _e, arg) => [{ type: "Title", id: `providers:${arg.mediaType}:${arg.tmdbId}` }],
    }),
    getPerson: b.query<PersonPayload, string>({
      query: (personId) => `people/${personId}`,
      providesTags: (_r, _e, personId) => [{ type: "Person", id: personId }],
    }),
    ratePerson: b.mutation<{ personId: string; rating: number }, { personId: string; rating: number }>({
      query: ({ personId, rating }) => ({ url: `people/${personId}/rating`, method: "POST", body: { rating } }),
      // Refresh the actor page, the DNA Top-Actors section, and the open title's cast badges.
      invalidatesTags: (_r, _e, arg) => [{ type: "Person", id: arg.personId }, "TasteDna", "Title"],
      async onQueryStarted({ personId, rating }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getPerson", personId, (draft) => {
            draft.userRating = rating;
          })
        );
        try {
          await queryFulfilled;
        } catch {
          patch.undo();
        }
      },
    }),
    unratePerson: b.mutation<{ personId: string }, { personId: string }>({
      query: ({ personId }) => ({ url: `people/${personId}/rating`, method: "DELETE" }),
      invalidatesTags: (_r, _e, arg) => [{ type: "Person", id: arg.personId }, "TasteDna", "Title"],
      async onQueryStarted({ personId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getPerson", personId, (draft) => {
            draft.userRating = null;
          })
        );
        try {
          await queryFulfilled;
        } catch {
          patch.undo();
        }
      },
    }),
    rateTitle: b.mutation<
      { userRating: number; watchedLogged?: boolean; verdict?: RatingVerdict | null },
      { titleId: string; mediaType: string; tmdbId: number; rating: number; markWatched?: boolean }
    >({
      // titleId rides along only to key the local removal set; it must not leak into the POST body.
      query: ({ mediaType, tmdbId, titleId: _titleId, ...body }) => ({ url: `titles/${mediaType}/${tmdbId}/rating`, method: "POST", body }),
      async onQueryStarted({ titleId, mediaType, tmdbId, rating }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.userRating = rating;
            draft.userState.isFullyWatched = true;
          })
        );
        try {
          const { data } = await queryFulfilled;
          // Seen+rated → the title has an interaction footprint now; prune it from the live rows.
          // Deliberately does NOT invalidate Feed — see `markNotInterested`.
          dispatch(titleRemoved({ titleId }));
          // A rating is exactly the answer the loop-closing prompt was asking for, so the strip
          // retires that title on the next read rather than continuing to ask a settled question.
          // Accuracy moves with every rating, so the track record is stale the moment one lands.
          dispatch(api.util.invalidateTags(["TasteDna", "Saved", "RecentlyWatched", "Accuracy"]));
          // The after-rating moment, raised here rather than at the three call sites that can rate:
          // one dispatch means the title modal, the rate picker, the post-watch strip and whatever
          // rating surface ships next all get it, and none of them can forget to.
          if (data?.verdict) {
            dispatch(verdictShown({ verdict: data.verdict, actual: rating }));
          }
        } catch {
          patch.undo();
        }
      },
    }),
    unrateTitle: b.mutation<{ userRating: null }, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/rating`, method: "DELETE" }),
      async onQueryStarted({ mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.userRating = null;
          })
        );
        try {
          await queryFulfilled;
          // Un-rating changes taste and (for a Reel-logged movie) reverses the play — same
          // downstream surfaces as rateTitle, the track record included: a retracted rating is one
          // fewer verdict against the model.
          dispatch(api.util.invalidateTags(["TasteDna", "Saved", "Accuracy"]));
        } catch {
          patch.undo();
        }
      },
    }),
    markNotInterested: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number; reason?: string }>({
      query: ({ mediaType, tmdbId, reason }) => ({
        url: `titles/${mediaType}/${tmdbId}/reactions/not_interested`,
        method: "POST",
        body: { reason },
      }),
      // The dislike history on Taste DNA is a plain list of these rows — safe to invalidate,
      // unlike Feed (see below), because nothing is scrolled through it.
      invalidatesTags: ["Dislikes"],
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.notInterested = true;
          })
        );
        try {
          await queryFulfilled;
          // "Not interested" leaves the feed immediately via the local removal set — and ONLY via
          // that. Invalidating Feed here refetches the snapshot, which mints a new generatedAt,
          // which re-keys every row in Home, which throws away the pages the user has scrolled and
          // scrolls them back to the start. Acting on one card must never reset the list around it;
          // the server rebuild that this reaction triggers is adopted at a deliberate boundary
          // instead (Home's "new picks ready" control, or the next mount).
          dispatch(titleRemoved({ titleId }));
        } catch {
          patch.undo();
        }
      },
    }),
    undoNotInterested: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/not_interested`, method: "DELETE" }),
      invalidatesTags: ["Dislikes"],
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.notInterested = false;
          })
        );
        try {
          await queryFulfilled;
          // Re-eligible — restoring the key un-hides the card that is still in the row's local
          // state, so no refetch is needed (and a refetch would reset the row).
          dispatch(titleRestored({ titleId }));
        } catch {
          patch.undo();
        }
      },
    }),
    // `titleId` rides along only to key the local removal set — the URL is built from
    // mediaType/tmdbId and there is no body, so it never reaches the wire.
    markDropped: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/dropped`, method: "POST" }),
      invalidatesTags: ["ContinueWatching"], // that rail only — not the picks snapshot
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        // Two patches, because the drop button exists on two surfaces. The detail page reads
        // getTitle; the Continue Watching shelf reads getContinueWatching, and it used to have no
        // optimistic patch at all — the card sat there through a POST *and* the invalidation
        // refetch before vanishing. On a fast local stack that is ~50ms and invisible, but there is
        // no spinner and no disabled state, so on a slow or flaky connection the button reads as
        // dead and invites a second click. Removing the entry up front makes the shelf respond to
        // the click itself rather than to the network.
        const patches = [
          dispatch(
            titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
              draft.userState.dropped = true;
            })
          ),
          dispatch(
            feedApi.util.updateQueryData("getContinueWatching", undefined, (draft) =>
              draft.filter((entry) => entry.tmdbId !== tmdbId)
            )
          ),
        ];
        try {
          await queryFulfilled;
          // Dropping is an interaction footprint like any other, so the title must leave the picks
          // rows too — not just the Continue Watching rail. It previously did not, and a dropped
          // show sat in Tonight's picks reading as if the drop had not registered. Via the local
          // removal set and ONLY that: invalidating Feed here would remount every infinite row and
          // discard the pages the user has scrolled — see `markNotInterested`.
          dispatch(titleRemoved({ titleId }));
        } catch {
          patches.forEach((p) => p.undo());
        }
      },
    }),
    undrop: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/dropped`, method: "DELETE" }),
      invalidatesTags: ["ContinueWatching", "Dislikes"],
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.dropped = false;
          })
        );
        try {
          await queryFulfilled;
          // Symmetric with the drop: restore the key so the card comes back to the live rows
          // without a refetch, exactly as `undoNotInterested` does. Without this an undrop leaves
          // the card hidden until a reload.
          dispatch(titleRestored({ titleId }));
        } catch {
          patch.undo();
        }
      },
    }),
    saveForLater: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/save_for_later`, method: "POST" }),
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.savedForLater = true;
          })
        );
        try {
          await queryFulfilled;
          // A saved title drops out of the live rows now. Saved list refreshes; the picks snapshot
          // deliberately does not — see `markNotInterested`.
          dispatch(titleRemoved({ titleId }));
          dispatch(api.util.invalidateTags(["Saved"]));
        } catch {
          patch.undo();
        }
      },
    }),
    unsaveForLater: b.mutation<void, { titleId: string; mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({ url: `titles/${mediaType}/${tmdbId}/reactions/save_for_later`, method: "DELETE" }),
      async onQueryStarted({ titleId, mediaType, tmdbId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(
          titlesApi.util.updateQueryData("getTitle", { mediaType, tmdbId }, (draft) => {
            draft.userState.savedForLater = false;
          })
        );
        try {
          await queryFulfilled;
          // Unsaving makes it eligible again — restore it to the live rows and refresh Saved.
          dispatch(titleRestored({ titleId }));
          dispatch(api.util.invalidateTags(["Saved"]));
        } catch {
          patch.undo();
        }
      },
    }),
    // On-demand lens for one title. Generates when nothing usable is cached, behind a 5s server
    // ceiling, and ALWAYS answers with a `source` — so the modal renders the ladder (fresh, stale,
    // or the templater fallback it already had) rather than a spinner that resolves to nothing.
    getLens: b.query<LensPayload, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => `titles/${mediaType}/${tmdbId}/lens`,
      providesTags: (_r, _e, arg) => [{ type: "Lens" as const, id: `${arg.mediaType}:${arg.tmdbId}` }],
    }),
    // "This spoiled it." Withdraws every note for the title so the surface falls back at once.
    reportLensSpoiler: b.mutation<{ withdrawn: number }, { mediaType: string; tmdbId: number }>({
      query: ({ mediaType, tmdbId }) => ({
        url: `titles/${mediaType}/${tmdbId}/lens/spoiled`,
        method: "POST",
      }),
      invalidatesTags: (_r, _e, arg) => [
        { type: "Lens" as const, id: `${arg.mediaType}:${arg.tmdbId}` },
        "Title",
        "Feed",
      ],
    }),
  }),
});

export const {
  useGetTitleQuery,
  useGetProvidersQuery,
  useGetPersonQuery,
  useRatePersonMutation,
  useUnratePersonMutation,
  useRateTitleMutation,
  useUnrateTitleMutation,
  useMarkNotInterestedMutation,
  useUndoNotInterestedMutation,
  useMarkDroppedMutation,
  useUndropMutation,
  useSaveForLaterMutation,
  useUnsaveForLaterMutation,
  useGetLensQuery,
  useReportLensSpoilerMutation,
} = titlesApi;
