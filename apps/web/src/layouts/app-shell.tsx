import { Outlet, useLocation } from "react-router-dom";
import { useCallback, useEffect, useState } from "react";
import { MainLandmark } from "../components/shared/page-loading";
import { AppSidebar } from "../components/shell/app-sidebar";
import { SignOutFailedBanner } from "../components/shell/sign-out-failed-banner";
import { LiveConnectionBanner } from "../components/shell/live-connection-banner";
import { ShellHeader } from "../components/shell/shell-header";
import { ShellHeaderProvider } from "../components/shell/shell-header-context";
import { useLiveSync } from "../lib/live/use-live-sync";
import { appTitle } from "../lib/system/app-title";
import { useSystemReadinessQuery } from "../lib/system/readiness-queries";

/**
 * Every signed-in screen: the side menu, the header above the page, and the page itself in the one
 * main landmark. The header owns the title, Pause and the theme switch, so a page only fills what is
 * its own. It also keeps every screen current from the live stream and says when that stream is lost.
 */
export function AppShell() {
  const location = useLocation();
  const readiness = useSystemReadinessQuery();
  useLiveSync();
  const [menuOpen, setMenuOpen] = useState(false);
  const closeMenu = useCallback(() => setMenuOpen(false), []);
  const productTitle = appTitle(readiness.data?.machine_name);

  // The tab says which Weir it is, so two of them side by side can be told apart.
  useEffect(() => {
    document.title = productTitle;
  }, [productTitle]);

  useEffect(() => {
    window.scrollTo(0, 0);
  }, [location.pathname, location.search]);

  return (
    <ShellHeaderProvider>
      <div className="mm-app-layout" data-testid="shell-ready">
        <AppSidebar
          productTitle={productTitle}
          drawerOpen={menuOpen}
          onCloseDrawer={closeMenu}
        />
        {menuOpen ? (
          <button
            type="button"
            className="mm-sidebar-backdrop"
            aria-label="Close navigation"
            onClick={closeMenu}
          />
        ) : null}
        <div className="mm-main-column">
          <ShellHeader
            menuOpen={menuOpen}
            onToggleMenu={() => setMenuOpen((open) => !open)}
          />
          <LiveConnectionBanner />
          <SignOutFailedBanner />
          <main className="mm-main" id="mm-main-content" tabIndex={-1}>
            <div className="mm-main-inner">
              <MainLandmark>
                <Outlet />
              </MainLandmark>
            </div>
          </main>
        </div>
      </div>
    </ShellHeaderProvider>
  );
}
