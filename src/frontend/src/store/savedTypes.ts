/** Provider badge, identical in shape to the feed card's — same server projection, reused a third time. */
export interface SavedProvider {
  name: string;
  logoPath: string | null;
}

/** The three list states a saved card can be in. Anything else from a newer server reads as unknown. */
export type SavedListSync = "pending" | "onList" | "offList";

/**
 * One card in the Saved queue.
 *
 * `predictedRating` is nullable in the TYPE but not in the product's intent: the server backfills
 * missing scores on demand, and `SavedQueueResponse.unscoredRemaining` says how many it could not
 * reach inside this request's budget. A null here means "not scored yet", never "scored zero", and
 * the card says so rather than rendering a blank corner.
 */
export interface SavedQueueItem {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  posterPath: string | null;
  runtimeMinutes: number | null;
  predictedRating: number | null;
  crowdRating: number | null;
  crowdVotes: number | null;
  providers: SavedProvider[];
  availableNow: boolean;
  savedAt: string;
  daysSaved: number;
  fitsEveningBudget: boolean;
  onManagedList: boolean;
  /**
   * Where the card stands against the managed Trakt list, from the server's outbox (WS-4):
   * "pending" only while a ListAdd write-back is genuinely waiting, else "onList" / "offList".
   * Optional because an older server omits it — see `listSyncLabel`.
   */
  listSync?: SavedListSync;
  /** True on at most one item: the server's pinned pick, and the head of the client's candidate list. */
  watchTonight: boolean;
  watchTonightReason: string | null;
}

export interface SavedQueueResponse {
  managedListUrl: string | null;
  sort: string;
  eveningBudgetMinutes: number;
  /** What a displayed prediction is typically off by. Null until the account's closed loop has pairs. */
  expectedAbsoluteError: number | null;
  /** Saves still without a prediction after this request's scoring budget was spent. */
  unscoredRemaining: number;
  items: SavedQueueItem[];
}

/** A title Reel recommended, that was watched, that carries a prediction, and that is still unrated. */
export interface RecentlyWatchedItem {
  titleId: string;
  mediaType: "Movie" | "Show";
  tmdbId: number | null;
  name: string;
  year: number | null;
  posterPath: string | null;
  predictedRating: number;
  watchedAt: string;
}

export interface RecentlyWatchedResponse {
  expectedAbsoluteError: number | null;
  items: RecentlyWatchedItem[];
}
