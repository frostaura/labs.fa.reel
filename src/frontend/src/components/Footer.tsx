import { Link } from "react-router";
import { LEGAL_PAGES } from "../lib/legalPages";

/**
 * Brand + attribution footer, present on every page. The TMDB line is a hard licence
 * requirement — do not remove or reword it. The privacy and terms links appear with their pages.
 */
export default function Footer() {
  return (
    <footer className="mt-auto border-t border-fa-edge/50 py-6 px-6 text-center space-y-1">
      <p className="fa-caption text-fa-frost-dim">
        Reel by <span className="text-fa-frost">FrostAura</span>
      </p>
      <p className="fa-caption text-3">
        This product uses the TMDB API but is not endorsed or certified by TMDB. Watch-provider
        data powered by JustWatch via TMDB.
      </p>
      {LEGAL_PAGES.length > 0 && (
        <nav aria-label="Legal" className="flex justify-center gap-4 pt-1">
          {LEGAL_PAGES.map(({ path, label }) => (
            <Link
              key={path}
              to={path}
              className="fa-caption text-fa-frost-dim underline-offset-4 hover:text-fa-frost-bright hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-fa-frost/50 rounded coarse:inline-flex coarse:min-h-11 coarse:items-center"
            >
              {label}
            </Link>
          ))}
        </nav>
      )}
    </footer>
  );
}
