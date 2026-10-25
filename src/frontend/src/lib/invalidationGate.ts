/**
 * Holds cache invalidations back while the tab is hidden and applies them once, together, when it
 * is shown again (H-16, I-12).
 *
 * Stopping the status poll in a hidden tab was not enough on its own: the live stream stays open,
 * and every `job-completed`, `failed` and reconnect frame invalidated tags whose queries then
 * refetched at once — a hidden tab still asked for `/api/sync/status` each time a background job
 * finished. Nobody is looking at a hidden tab, so the refetch can wait until somebody is; what it
 * must not do is get lost, which is why the tags are remembered rather than dropped.
 */
export interface InvalidationGate<T> {
  /** Applies the tags now, or remembers them while the tab is hidden. */
  invalidate(tags: readonly T[]): void;
  /** Applies everything remembered, once, if the tab is visible. Safe to call repeatedly. */
  flush(): void;
  /** Whether anything is being held back. */
  pending(): boolean;
}

export function createInvalidationGate<T>(isHidden: () => boolean, apply: (tags: T[]) => void): InvalidationGate<T> {
  const held = new Set<T>();
  return {
    invalidate(tags) {
      if (isHidden()) {
        tags.forEach((tag) => held.add(tag));
        return;
      }
      apply([...tags]);
    },
    flush() {
      if (held.size === 0 || isHidden()) return;
      const tags = [...held];
      held.clear();
      apply(tags);
    },
    pending: () => held.size > 0,
  };
}
