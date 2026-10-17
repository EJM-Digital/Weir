import { AuthBrandStack } from "../../components/brand/auth-brand-stack";

/**
 * What someone who cannot change settings sees while the first run is unfinished: the wizard is the
 * administrator's to complete, and a page that offered it would only be refused.
 */
export function SetupNeedsAdmin() {
  return (
    <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
      <div className="mm-auth-frame">
        <AuthBrandStack />
        <div className="mm-auth-card">
          <p className="mm-auth-eyebrow">First run</p>
          <h1 className="mm-auth-title">Weir isn&apos;t set up yet</h1>
          <p className="mm-auth-lead">
            An administrator needs to finish setting Weir up first. Check back
            once they have.
          </p>
        </div>
      </div>
    </main>
  );
}
