import { clearHasSession, markHasSession } from "../../lib/sessionFlag";
import { api } from "./base";

/** Pipeline stages as reported by the backend (Account.PipelineStage). */
export type PipelineStage =
  | "Linked"
  | "Ingesting"
  | "Extracting"
  | "Training"
  | "Evaluated"
  | "FeedReady"
  | "Degraded";

export type Tier = "Free" | "Paid" | "Founder";

export interface Session {
  accountId: string;
  displayName: string;
  traktSlug: string;
  avatarUrl: string | null;
  region: string;
  tier: Tier;
  pipelineStage: PipelineStage;
  onboarded: boolean;
  pinConfigured: boolean;
}

export interface TraktAuthStart {
  authorizeUrl: string;
  state: string;
}

export const authApi = api.injectEndpoints({
  // Vite HMR re-runs this module; let the re-injection replace the old definitions.
  overrideExisting: Boolean(import.meta.hot),
  endpoints: (b) => ({
    getSession: b.query<Session, void>({
      query: () => "auth/me",
      providesTags: ["Session"],
      // A successful probe proves this browser holds a session — remember it so the landing
      // page's probe-skip (see Landing.tsx) never applies to a signed-in browser. A failure
      // deliberately does NOT clear the flag: a network blip must not degrade the next visit.
      async onQueryStarted(_arg, { queryFulfilled }) {
        try {
          await queryFulfilled;
          markHasSession();
        } catch {
          // Signed out or offline — leave the flag as-is; only logout clears it.
        }
      },
    }),
    startTraktAuth: b.mutation<TraktAuthStart, void>({
      // POST: generates a fresh signed state server-side on each call.
      query: () => ({ url: "auth/trakt/start", method: "POST" }),
    }),
    exchangeTraktCode: b.mutation<Session, { code: string; state: string }>({
      query: (body) => ({ url: "auth/trakt/callback", method: "POST", body }),
      invalidatesTags: ["Session", "Sync"],
      // The OAuth callback IS account creation — set the flag here so the very first
      // post-sign-in landing visit probes instead of assuming signed-out.
      async onQueryStarted(_arg, { queryFulfilled }) {
        try {
          await queryFulfilled;
          markHasSession();
        } catch {
          // Exchange failed — no session was established, nothing to remember.
        }
      },
    }),
    logout: b.mutation<void, void>({
      query: () => ({ url: "auth/logout", method: "POST" }),
      async onQueryStarted(_arg, { queryFulfilled }) {
        // Clear regardless of outcome: the user asked to sign out, so the landing page should
        // treat this browser as signed-out even if the server call failed.
        clearHasSession();
        await queryFulfilled.catch(() => undefined);
      },
    }),
  }),
});

export const {
  useGetSessionQuery,
  useStartTraktAuthMutation,
  useExchangeTraktCodeMutation,
  useLogoutMutation,
} = authApi;
