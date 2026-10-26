/**
 * Loaders for route chunks that more than one place needs: App.tsx hands them to `lazy()`, and the
 * shell prefetches them when idle. The same `import()` both times means one chunk, fetched once.
 */
export const loadTitleDetail = () => import("../pages/TitleDetail");
