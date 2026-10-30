import { QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useEffect } from "react";
import { setUnauthorizedHandler } from "../lib/api/client";
import { authKeys } from "../lib/auth/query-keys";
import { signedOutPath } from "../lib/auth/signed-in-before";
import { queryClient } from "./query-client";

/** The pages a signed-out visitor is meant to be on, so a 401 there does not send them to sign in. */
const SIGNED_OUT_PAGES = new Set(["/login", "/recover", "/setup"]);

export function AppProviders({ children }: { children: ReactNode }) {
  useEffect(() => {
    setUnauthorizedHandler(() => {
      queryClient.setQueryData(authKeys.me, null);
      queryClient.setQueryData(authKeys.session, null);
      void queryClient.cancelQueries({ queryKey: authKeys.me });
      void queryClient.cancelQueries({ queryKey: authKeys.session });
      if (!SIGNED_OUT_PAGES.has(window.location.pathname)) {
        window.history.replaceState(null, "", signedOutPath());
        window.dispatchEvent(new PopStateEvent("popstate"));
      }
    });
    return () => setUnauthorizedHandler(null);
  }, []);

  return (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}
