import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "@fontsource-variable/inter/wght.css";
import "@fontsource-variable/jetbrains-mono/wght.css";
import { AppRouter } from "./app/router";
import { AppProviders } from "./app/providers";
import { preloadAboveTheFoldFonts } from "./app/preload-fonts";
import { prefetchOnBoot } from "./app/prefetch-on-boot";
import { StartupGate } from "./app/startup-gate";
import { AppErrorScreen, ErrorBoundary } from "./components/error-boundary";
import {
  applyAppThemeToDocument,
  currentAppTheme,
  followSystemAppTheme,
} from "./lib/ui/app-theme";
import "./index.css";
import "./styles/weir.css";

applyAppThemeToDocument(currentAppTheme());
followSystemAppTheme();
preloadAboveTheFoldFonts();
// Alongside StartupGate's own /ready polling, not after it: whoever is signed in and the app's
// settings are wanted by the very first screen a person sees once Weir is ready (#719).
prefetchOnBoot();

const el = document.getElementById("root") as HTMLElement;

createRoot(el).render(
  <StrictMode>
    <AppProviders>
      <ErrorBoundary fallback={(error) => <AppErrorScreen error={error} />}>
        <StartupGate>
          <AppRouter />
        </StartupGate>
      </ErrorBoundary>
    </AppProviders>
  </StrictMode>,
);
