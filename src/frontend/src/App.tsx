import { lazy, Suspense } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router";
import Layout from "./components/Layout";
import RequireAuth from "./components/RequireAuth";
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
const SearchResults = lazy(() => import("./pages/SearchResults"));
const RatePicker = lazy(() => import("./pages/RatePicker"));

function RouteFallback() {
  return (
    <div className="min-h-screen flex items-center justify-center px-6">
      <div className="text-2xl font-light text-fa-frost animate-pulse">Reel</div>
    </div>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <Suspense fallback={<RouteFallback />}>
        <Routes>
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
            <Route path="/search" element={<SearchResults />} />
            <Route path="/rate" element={<RatePicker />} />
            <Route path="/settings" element={<Settings />} />
            <Route path="/lab" element={<Lab />} />
          </Route>
          <Route path="*" element={<Navigate to="/home" replace />} />
        </Routes>
      </Suspense>
    </BrowserRouter>
  );
}
