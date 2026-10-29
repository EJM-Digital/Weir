import { RouterProvider, createBrowserRouter } from "react-router-dom";
import { RouteErrorScreen } from "../components/error-boundary";
import { AppShell } from "../layouts/app-shell";
import { RequireAuth } from "./require-auth";
import { RequireSetupWizard } from "./require-setup-wizard";
import { AppHydrateFallback } from "./hydrate-fallback";
import {
  LegacyHistoryRedirect,
  LegacyProcessingRedirect,
  LegacySettingsRedirect,
} from "./legacy-redirects";
import { boundariesThatSurviveALazyFailure } from "./lazy-route-boundaries";
import { setupRoutes } from "./setup-routes";

const routeErrorElement = <RouteErrorScreen />;

// Five places: the Dashboard (/), Activity, Library, the four setup areas (/setup/...) and System. Addresses
// bookmarked from an installed release that no longer exist redirect to where their page lives now
// (legacy-redirects.tsx); anything older gets the Not found page.
const router = createBrowserRouter(
  boundariesThatSurviveALazyFailure([
    {
      // The two pages before sign-in sit under one route that has the boundary, so a page that cannot be loaded
      // still ends at the error screen.
      errorElement: routeErrorElement,
      HydrateFallback: AppHydrateFallback,
      children: [
        {
          path: "/login",
          lazy: async () => ({
            Component: (await import("../pages/auth/login-page")).LoginPage,
          }),
        },
        {
          path: "/recover",
          lazy: async () => ({
            Component: (await import("../pages/auth/recover-page")).RecoverPage,
          }),
        },
        {
          path: "/setup",
          lazy: async () => ({
            Component: (await import("../pages/setup/setup-page")).SetupPage,
          }),
        },
      ],
    },
    {
      path: "/",
      element: <RequireAuth />,
      errorElement: routeErrorElement,
      HydrateFallback: AppHydrateFallback,
      children: [
        {
          path: "setup-wizard",
          lazy: async () => ({
            Component: (await import("../pages/setup/setup-wizard-page"))
              .SetupWizardPage,
          }),
          errorElement: routeErrorElement,
        },
        {
          element: <RequireSetupWizard />,
          errorElement: routeErrorElement,
          children: [
            {
              element: <AppShell />,
              errorElement: routeErrorElement,
              children: [
                {
                  // The Dashboard: every file Weir is working on, moving as it moves. The landing screen,
                  // because what Weir is doing right now is what an operator opens the app to see.
                  index: true,
                  lazy: async () => ({
                    Component: (
                      await import("../pages/processing/processing-page")
                    ).ProcessingPage,
                  }),
                  errorElement: routeErrorElement,
                },
                {
                  // Every file Weir has touched: what it was, what Weir did and what came out.
                  path: "activity",
                  lazy: async () => ({
                    Component: (await import("../pages/activity/activity-page"))
                      .ActivityPage,
                  }),
                  errorElement: routeErrorElement,
                },
                {
                  // The files already imported, and what Weir would do to each (library mode).
                  path: "library",
                  lazy: async () => ({
                    Component: (await import("../pages/library/library-page"))
                      .LibraryPage,
                  }),
                  errorElement: routeErrorElement,
                },
                {
                  path: "history",
                  element: <LegacyHistoryRedirect />,
                  errorElement: routeErrorElement,
                },
                {
                  path: "processing",
                  element: <LegacyProcessingRedirect />,
                  errorElement: routeErrorElement,
                },
                ...setupRoutes(routeErrorElement),
                {
                  path: "settings",
                  element: <LegacySettingsRedirect />,
                  errorElement: routeErrorElement,
                },
                {
                  // Weir itself: what it is running, what it keeps, and who can sign in.
                  path: "system",
                  lazy: async () => ({
                    Component: (await import("../pages/system/system-page"))
                      .SystemPage,
                  }),
                  errorElement: routeErrorElement,
                },
                {
                  path: "*",
                  lazy: async () => ({
                    Component: (await import("../pages/not-found-page"))
                      .NotFoundPage,
                  }),
                  errorElement: routeErrorElement,
                },
              ],
            },
          ],
        },
      ],
    },
  ]),
);

export function AppRouter() {
  return <RouterProvider router={router} />;
}
