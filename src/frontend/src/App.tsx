import { BrowserRouter, Navigate, Route, Routes, matchPath, useLocation, type Location } from "react-router-dom";
import Layout from "./components/Layout";
import RequireAuth from "./components/RequireAuth";
import RequireFounder from "./components/RequireFounder";
import Landing from "./pages/Landing";
import TraktCallback from "./pages/TraktCallback";
import Onboarding from "./pages/Onboarding";
import Home from "./pages/Home";
import Settings from "./pages/Settings";
import Lab from "./pages/Lab";
import TitleDetail from "./pages/TitleDetail";
import Person from "./pages/Person";
import Saved from "./pages/Saved";
import TasteDna from "./pages/TasteDna";
import TrackRecord from "./pages/TrackRecord";
import SearchResults from "./pages/SearchResults";
import RatePicker from "./pages/RatePicker";
import Blend from "./pages/Blend";

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

      {/* Title overlay rendered on top of the kept-mounted background page. */}
      {isTitleOverlay && (
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
