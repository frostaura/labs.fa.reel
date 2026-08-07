import { useState } from "react";
import { Link, NavLink, Outlet, useLocation, useNavigate } from "react-router";
import { LogOut, Settings as SettingsIcon, FlaskConical, Home, Bookmark, Fingerprint } from "lucide-react";
import { useGetSessionQuery, useLogoutMutation, api } from "../store/api";
import { useDispatch } from "react-redux";
import type { AppDispatch } from "../store";
import SyncStatusPill from "./SyncStatusPill";
import SearchBox from "./search/SearchBox";
import MobileTabBar from "./MobileTabBar";
import Footer from "./Footer";
import RealtimeSync from "./RealtimeSync";
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

export default function Layout() {
  const { data: session } = useGetSessionQuery();
  const [logout] = useLogoutMutation();
  const navigate = useNavigate();
  const location = useLocation();
  const dispatch = useDispatch<AppDispatch>();
  const [mobileSearchOpen, setMobileSearchOpen] = useState(false);

  // A navigation (tapping a typeahead result or Ask Reel) closes the mobile search panel.
  // Adjusted during render rather than in an effect: an effect would paint the panel open on
  // the new route first and then close it, a visible flash plus a cascading render.
  //
  // This replaces an effect that carried a `react-hooks/set-state-in-effect` suppression. That
  // suppression's stated objection was that deriving the panel's state from the route would make
  // it un-reopenable — true of a pure derivation, but this is not one. The `lastNavKey` sentinel
  // closes the panel on a route *change* only; opening it again on the same route leaves
  // `navKey === lastNavKey`, so nothing reopens or re-closes it. Same behaviour, no suppression.
  const navKey = `${location.pathname}${location.search}`;
  const [lastNavKey, setLastNavKey] = useState(navKey);
  if (navKey !== lastNavKey) {
    setLastNavKey(navKey);
    setMobileSearchOpen(false);
  }

  const handleSignOut = async () => {
    await logout().unwrap().catch(() => undefined);
    dispatch(api.util.resetApiState());
    navigate("/");
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
      <main className="flex-1 mx-auto w-full max-w-7xl px-4 sm:px-6 py-6 pb-24 md:pb-6">
        <Outlet />
      </main>
      <Footer />
      <MobileTabBar searchOpen={mobileSearchOpen} onToggleSearch={() => setMobileSearchOpen((v) => !v)} />
    </div>
  );
}
