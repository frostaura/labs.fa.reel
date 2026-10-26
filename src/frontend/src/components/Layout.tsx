import { Suspense, useEffect, useState } from "react";
import { Link, NavLink, Outlet, useLocation, useNavigate } from "react-router";
import { LogOut, Settings as SettingsIcon, FlaskConical, Home, Bookmark, Fingerprint, Users, Target } from "lucide-react";
import { useGetSessionQuery, useLogoutMutation } from "../store/api";
import SyncStatusPill from "./SyncStatusPill";
import SearchBox from "./search/SearchBox";
import MobileTabBar from "./MobileTabBar";
import Footer from "./Footer";
import RealtimeSync from "./RealtimeSync";
import VerdictToast from "./VerdictToast";
import ErrorBoundary from "./ErrorBoundary";
import OfflineBanner from "./OfflineBanner";
import UpdatePrompt from "./UpdatePrompt";
import { markSignOutPending } from "../lib/signOut";
import { LEAVE_CONFIRMED, pendingLeaveQuestion } from "../lib/navigationGuard";
import reelMark from "../assets/brand/reel-mark.svg";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "./ui/dropdown-menu";

const navLinkClass = ({ isActive }: { isActive: boolean }) =>
  `inline-flex items-center justify-center px-2.5 py-2 rounded-md transition ${
    isActive ? "text-fa-frost-bright bg-fa-glass-strong" : "text-fa-frost/80 hover:text-fa-frost-bright hover:bg-fa-glass"
  }`;

/** Desktop header tabs — icon-only, mirroring the mobile bottom bar's icon set. */
const NAV_TABS = [
  { to: "/home", label: "Home", Icon: Home },
  { to: "/saved", label: "Saved", Icon: Bookmark },
  { to: "/dna", label: "Taste DNA", Icon: Fingerprint },
] as const;

function PageFallback() {
  return (
    <div className="flex items-center justify-center py-24" data-testid="page-fallback">
      <div className="text-2xl font-light text-fa-frost animate-pulse">Reel</div>
    </div>
  );
}

export default function Layout() {
  const { data: session } = useGetSessionQuery();
  const [logout] = useLogoutMutation();
  const navigate = useNavigate();
  const location = useLocation();
  const [mobileSearchOpen, setMobileSearchOpen] = useState(false);

  // A navigation (tapping a typeahead result or Ask Reel) closes the mobile search panel.
  //
  // Kept as an effect deliberately. The router location is an external system this panel follows,
  // which is exactly what an effect is for; the panel's open state is not derivable from the route
  // (the user opens it on any route, and reopening it on the same route must work). The linter's
  // preferred shapes both cost more than the warning: deriving from the route makes the panel
  // un-reopenable, and threading a close callback down through SearchBox and every result link
  // spreads one concern across files that cannot see this panel. The cascading render it warns
  // about is one extra pass during a route change that is already re-rendering the whole tree.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional sync to the router; see above
    setMobileSearchOpen(false);
  }, [location.pathname, location.search]);

  const handleSignOut = async () => {
    // Ask about unsaved drafts BEFORE the session ends: asking after logout, a Cancel would leave
    // the page signed out underneath its own draft (IREV3S-04).
    const question = pendingLeaveQuestion();
    if (question !== null && !window.confirm(question)) return;
    await logout().unwrap().catch(() => undefined);
    // The cleanup runs on the landing page, once this shell and its pages have unmounted
    // (components/SignOutCleanup.tsx): clearing here would reset the cache under live subscribers,
    // and every one of them would refetch. Everything this browser holds for the account goes, not
    // just the RTK cache: the slices, the reel.* keys and the worker's offline API cache (I-11).
    markSignOutPending();
    navigate("/", { state: LEAVE_CONFIRMED });
  };

  return (
    <div className="min-h-screen flex flex-col">
      <RealtimeSync />
      <header className="sticky top-0 z-40 border-b border-fa-edge/50 bg-fa-ink/80 backdrop-blur-md">
        <div className="mx-auto max-w-7xl px-4 sm:px-6 h-14 flex items-center gap-4">
          <Link to="/home" aria-label="Reel home" className="flex items-center gap-2 shrink-0 hover:opacity-80 transition">
            <img src={reelMark} alt="" aria-hidden className="h-7 w-7 shrink-0" />
            <span className="text-lg font-light tracking-wide text-fa-frost-bright">Reel</span>
          </Link>
          {/* Inline nav lives on desktop; mobile uses the bottom tab bar. Icon-only — the label
              rides aria-label + native title tooltip so it stays accessible. */}
          <nav className="hidden md:flex items-center gap-1">
            {NAV_TABS.map(({ to, label, Icon }) => (
              <NavLink key={to} to={to} className={navLinkClass} aria-label={label} title={label}>
                <Icon className="h-5 w-5" />
              </NavLink>
            ))}
          </nav>
          <div className="ml-auto flex items-center gap-3 flex-1 justify-end">
            <div className="hidden md:block flex-1 max-w-md">
              <SearchBox />
            </div>
            <SyncStatusPill />
            <DropdownMenu>
              <DropdownMenuTrigger className="flex items-center gap-2 rounded-full border border-fa-edge bg-fa-glass px-2 py-1 hover:border-fa-frost/40 transition">
                {session?.avatarUrl ? (
                  <img src={session.avatarUrl} alt="" className="h-6 w-6 rounded-full object-cover" />
                ) : (
                  <span className="h-6 w-6 rounded-full bg-fa-ink-3 flex items-center justify-center fa-caption text-fa-frost">
                    {session?.displayName?.[0]?.toUpperCase() ?? "?"}
                  </span>
                )}
                <span className="fa-caption text-fa-frost hidden sm:inline">{session?.displayName}</span>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem onClick={() => navigate("/blend")}>
                  <Users className="h-4 w-4 mr-2" /> Blend
                </DropdownMenuItem>
                {/* Free for every tier, deliberately: a paid accuracy report reads as hiding the
                    scorecard. Sits above Settings because it is a thing to read, not a thing to
                    configure. */}
                <DropdownMenuItem onClick={() => navigate("/track-record")}>
                  <Target className="h-4 w-4 mr-2" /> Track record
                </DropdownMenuItem>
                <DropdownMenuItem onClick={() => navigate("/settings")}>
                  <SettingsIcon className="h-4 w-4 mr-2" /> Settings
                </DropdownMenuItem>
                {session?.tier === "Founder" && (
                  <DropdownMenuItem onClick={() => navigate("/lab")}>
                    <FlaskConical className="h-4 w-4 mr-2" /> Model lab
                  </DropdownMenuItem>
                )}
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={handleSignOut}>
                  <LogOut className="h-4 w-4 mr-2" /> Sign out
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        </div>
        {/* Mobile search panel — slides under the header so typeahead + Ask Reel work on a phone. */}
        {mobileSearchOpen && (
          <div className="md:hidden border-t border-fa-edge/50 bg-fa-ink/95 backdrop-blur-md px-4 py-3" data-testid="mobile-search-panel">
            <SearchBox />
          </div>
        )}
      </header>
      <OfflineBanner />
      {/* At least a viewport tall (less the 3.5rem header), so the footer never sits in the first
          screen while a page is still loading and then jumps away (G-14, I-5). */}
      <main className="flex-1 mx-auto w-full max-w-7xl px-4 sm:px-6 py-6 pb-24 md:pb-6 min-h-[calc(100dvh-3.5rem)]">
        {/* The boundary and the suspense both sit INSIDE the shell: a page that throws, or whose
            chunk is still loading, replaces only itself — the header, nav and pill stay (I-4).
            Navigating elsewhere clears a caught error. */}
        <ErrorBoundary scope="route" resetKey={location.pathname}>
          <Suspense fallback={<PageFallback />}>
            <Outlet />
          </Suspense>
        </ErrorBoundary>
      </main>
      <Footer />
      {/* Mounted once, driven by the store: every rating path raises the after-rating moment and
          none of them has to know this component exists. */}
      <VerdictToast />
      <UpdatePrompt />
      <MobileTabBar searchOpen={mobileSearchOpen} onToggleSearch={() => setMobileSearchOpen((v) => !v)} />
    </div>
  );
}
