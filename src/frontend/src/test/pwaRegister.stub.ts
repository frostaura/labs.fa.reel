/**
 * Stands in for vite-plugin-pwa's `virtual:pwa-register`, which exists only under the app's Vite
 * build (vitest.config.ts aliases it here). Registers nothing; tests that exercise registration
 * mock the module themselves (lib/pwaUpdate.test.ts).
 */
export function registerSW(): (reload?: boolean) => Promise<void> {
  return () => Promise.resolve();
}
