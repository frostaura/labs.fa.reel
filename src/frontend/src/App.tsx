import { lazy, Suspense } from "react";
import {
  BrowserRouter,
  Navigate,
  Route,
  Routes,
  matchPath,
  useLocation,
  type Location,
} from "react-router";
import Layout from "./components/Layout";
import RequireAuth from "./components/RequireAuth";
import RequireFounder from "./components/RequireFounder";
import Landing from "./pages/Landing";

// Landing stays eager — it is the signed-out entry point and must paint without a second
// round-trip. Every other route is split out. This is not merely to quiet the bundle-size
// warning: TasteDna alone pulls in recharts, which a signed-out visitor was downloading
// before they had anything to chart.
const TraktCallback = lazy(() => import("./pages/TraktCallback"));
const Onboarding = lazy(() => import("./pages/Onboarding"));
const Home = lazy(() => import("./pages/Home"));
const Settings = lazy(() => import("./pages/Settings"));
const Lab = lazy(() => import("./pages/Lab"));
const TitleDetail = lazy(() => import("./pages/TitleDetail"));
const Person = lazy(() => import("./pages/Person"));
const Saved = lazy(() => import("./pages/Saved"));
const TasteDna = lazy(() => import("./pages/TasteDna"));
const TrackRecord = lazy(() => import("./pages/TrackRecord"));
const SearchResults = lazy(() => import("./pages/SearchResults"));
const RatePicker = lazy(() => import("./pages/RatePicker"));
const Blend = lazy(() => import("./pages/Blend"));

function RouteFallback() {
  return (
    <div className="min-h-screen flex items-center justify-center px-6">
      <div className="text-2xl font-light text-fa-frost animate-pulse">Reel</div>
    </div>
  );
}

/**
 * Title detail is a fixed-overlay modal that cards open with `state.backgroundLocation`. When that
 * marker is present we pin the main <Routes> to the background location so the page underneath
 * (Home and its carousels, Saved, search results…) stays MOUNTED while the modal is on top —
 * closing it then reveals the same DOM with scroll positions intact, instead of remounting the
 * page and snapping every carousel back to the start. Scoped to /title only: Person is a full-page
 * view, so it (and any other route) always navigates normally even if a link supplied a background.
 */
function AppRoutes() {
  const location = useLocation();
  const background = (location.state as { backgroundLocation?: Location } | null)?.backgroundLocation;
  const isTitleOverlay = !!background && matchPath("/title/:mediaType/:tmdbId", location.pathname) != null;
  const mainLocation = isTitleOverlay ? background! : location;

  return (
    <>
      {/* Each <Routes> gets its own boundary. A single shared one would defeat the overlay:
          the modal's chunk suspending would swap the whole tree for the fallback and unmount
          the very background page this arrangement exists to keep alive. */}
      <Suspense fallback={<RouteFallback />}>
        <Routes location={mainLocation}>
          <Route path="/" element={<Landing />} />
          <Route path="/auth/trakt/callback" element={<TraktCallback />} />
          <Route
            path="/onboarding"
            element={
              <RequireAuth>
                <Onboarding />
              </RequireAuth>
            }
          />
          <Route
            element={
              <RequireAuth>
                <Layout />
              </RequireAuth>
            }
          >
            <Route path="/home" element={<Home />} />
            <Route path="/title/:mediaType/:tmdbId" element={<TitleDetail />} />
            <Route path="/person/:personId" element={<Person />} />
            <Route path="/saved" element={<Saved />} />
            <Route path="/dna" element={<TasteDna />} />
            <Route path="/track-record" element={<TrackRecord />} />
            <Route path="/search" element={<SearchResults />} />
            <Route path="/rate" element={<RatePicker />} />
            <Route path="/blend" element={<Blend />} />
            <Route path="/settings" element={<Settings />} />
            <Route
              path="/lab"
              element={
                <RequireFounder>
                  <Lab />
                </RequireFounder>
              }
            />
          </Route>
          <Route path="*" element={<Navigate to="/home" replace />} />
        </Routes>
      </Suspense>

      {/* Title overlay rendered on top of the kept-mounted background page. */}
      {isTitleOverlay && (
        <Suspense fallback={null}>
          <Routes>
            <Route
              path="/title/:mediaType/:tmdbId"
              element={
                <RequireAuth>
                  <TitleDetail />
                </RequireAuth>
              }
            />
          </Routes>
        </Suspense>
      )}
    </>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <AppRoutes />
    </BrowserRouter>
  );
}
