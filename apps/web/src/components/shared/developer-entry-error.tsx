import {
  isHttpErrorFromApi,
  isLikelyNetworkFailure,
  isLikelyViteProxyUpstreamDown,
} from "../../lib/api/error-guards";

/**
 * What a developer running Weir from source sees when the first request fails: how to start the API
 * and Vite, and what a 503 from a live API means. Shown only by `vite dev` (see ApiEntryError).
 */
export function DeveloperEntryError({ error }: { error: unknown }) {
  if (isLikelyNetworkFailure(error) || isLikelyViteProxyUpstreamDown(error)) {
    return (
      <>
        <h1 className="mm-auth-title mm-status-text" data-status="broken">
          Cannot reach the API
        </h1>
        {isLikelyViteProxyUpstreamDown(error) ? (
          <p className="mm-auth-lead">
            With Vite alone, <code className="mm-entry-code">/api</code> is
            proxied to the API port. If nothing is listening there, the dev
            server often returns <strong>HTTP 500</strong> — that means the API
            is not up, not a handler bug inside Weir.
          </p>
        ) : null}
        <p className="mm-auth-lead">
          <strong>Easiest:</strong> from{" "}
          <code className="mm-entry-code">apps/web</code>, run{" "}
          <code className="mm-entry-code mm-entry-code--chip">npm run dev</code>{" "}
          — it starts the API, waits until{" "}
          <code className="mm-entry-code">GET /health</code> succeeds, then
          starts Vite (ports in{" "}
          <code className="mm-entry-code">scripts/dev-ports.json</code>
          ). If you still see this screen, the API never became ready: read the
          same terminal for server errors (.NET SDK, build errors,{" "}
          <code className="mm-entry-code">WEIR_SESSION_SECRET</code>, the
          database path).
        </p>
        <p className="mm-auth-lead">
          <strong>Alternative:</strong> two terminals from the repo root —{" "}
          <code className="mm-entry-code mm-entry-code--chip">
            .\scripts\dev-backend.ps1
          </code>{" "}
          then{" "}
          <code className="mm-entry-code mm-entry-code--chip">
            .\scripts\dev-web.ps1
          </code>
          . If the API is already running, use{" "}
          <code className="mm-entry-code mm-entry-code--chip">
            npm run dev:web
          </code>{" "}
          in <code className="mm-entry-code">apps/web</code> for Vite only.
        </p>
        <p className="mm-auth-lead">
          During <code className="mm-entry-code">vite dev</code>, the app always
          uses relative{" "}
          <code className="mm-entry-code mm-entry-code--chip">/api/v1</code>{" "}
          through the Vite proxy (see{" "}
          <code className="mm-entry-code mm-entry-code--chip">
            apps/web/.env.example
          </code>
          ). Ports:{" "}
          <code className="mm-entry-code mm-entry-code--chip">
            scripts/dev-ports.json
          </code>
          .
        </p>
      </>
    );
  }

  if (isHttpErrorFromApi(error)) {
    const { status } = error;
    if (status === 503) {
      return (
        <>
          <h1 className="mm-auth-title mm-status-text" data-status="attention">
            API is running but not ready
          </h1>
          <p className="mm-auth-lead">
            Auth routes need a migrated SQLite database under{" "}
            <code className="mm-entry-code mm-entry-code--chip">WEIR_HOME</code>{" "}
            (optional{" "}
            <code className="mm-entry-code mm-entry-code--chip">
              WEIR_DB_PATH
            </code>
            ), plus{" "}
            <code className="mm-entry-code mm-entry-code--chip">
              WEIR_SESSION_SECRET
            </code>
            . The server creates or migrates its database when it starts, so
            check its log for the reason, fix it and restart the server. See{" "}
            <code className="mm-entry-code">.env.example</code> and{" "}
            <code className="mm-entry-code">docs/local-development.md</code>.
          </p>
          <p className="mm-auth-lead">
            <code className="mm-entry-code mm-entry-code--chip">
              GET /health
            </code>{" "}
            can still return 200 while{" "}
            <code className="mm-entry-code">/api/v1</code> returns 503 if
            migrations, session secret, or the database path are not ready.
          </p>
        </>
      );
    }
    return (
      <>
        <h1 className="mm-auth-title mm-status-text" data-status="broken">
          Unexpected API error
        </h1>
        <p className="mm-auth-lead">
          The server responded but the request failed (HTTP {status}). Check the
          backend terminal for details.
        </p>
        <p className="mm-auth-lead mm-entry-detail">{error.message}</p>
      </>
    );
  }

  return (
    <>
      <h1 className="mm-auth-title mm-status-text" data-status="broken">
        Cannot load the app
      </h1>
      <p className="mm-auth-lead">
        Something went wrong talking to the API. From{" "}
        <code className="mm-entry-code">apps/web</code> try{" "}
        <code className="mm-entry-code mm-entry-code--chip">npm run dev</code>{" "}
        (API + Vite), or confirm the backend is running, then reload.
      </p>
      {error instanceof Error ? (
        <p className="mm-auth-lead mm-entry-detail">{error.message}</p>
      ) : null}
    </>
  );
}
