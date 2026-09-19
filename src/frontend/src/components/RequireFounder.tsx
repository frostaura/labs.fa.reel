import type { ReactNode } from "react";
import { Navigate } from "react-router";
import { useGetSessionQuery } from "../store/api";

/**
 * The model lab is a founder instrument. The header only offers the link to the founder, but a
 * route is a URL, and a Free account that typed /lab was shown the whole readout with a few of its
 * panels quietly answering 403. Sits inside RequireAuth, so the session is already resolved here.
 */
export default function RequireFounder({ children }: { children: ReactNode }) {
  const { data } = useGetSessionQuery();
  if (data && data.tier !== "Founder") return <Navigate to="/home" replace />;
  return <>{children}</>;
}
