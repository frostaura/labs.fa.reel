import { lazy, type ComponentType, type LazyExoticComponent } from "react";

/**
 * The privacy and terms routes (H-14). WS-9 owns the routes and the footer links; the pages are
 * `pages/Privacy.tsx` and `pages/Terms.tsx`, built by the account-pages lane from the parent's
 * legal text. Each is a default-exported component rendering its content only — the public shell
 * around it (brand link, footer) comes from App.tsx.
 *
 * Found with a build-time glob rather than imported by name, so the routes and the footer links
 * appear in the same build as the pages and never point at a page that does not exist yet. There
 * is no stand-in page: until the text exists, there is no link to it.
 */

type PageModule = { default: ComponentType };

const modules = import.meta.glob<PageModule>("../pages/{Privacy,Terms}.tsx");

export interface LegalPage {
  path: string;
  label: string;
  Component: LazyExoticComponent<ComponentType>;
}

const DECLARED = [
  { file: "../pages/Privacy.tsx", path: "/privacy", label: "Privacy" },
  { file: "../pages/Terms.tsx", path: "/terms", label: "Terms" },
] as const;

/** The legal pages present in this build, in footer order. */
export function legalPagesFrom(found: Record<string, () => Promise<PageModule>>): LegalPage[] {
  return DECLARED.flatMap(({ file, path, label }) => {
    const load = found[file];
    return load ? [{ path, label, Component: lazy(load) }] : [];
  });
}

export const LEGAL_PAGES: LegalPage[] = legalPagesFrom(modules);
