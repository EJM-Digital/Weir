import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import istanbul from "vite-plugin-istanbul";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

type DevPortsFile = {
  development: {
    webHost: string;
    webPort: number;
    apiHost: string;
    apiPort: number;
  };
};

const devPortsPath = path.join(
  __dirname,
  "..",
  "..",
  "scripts",
  "dev-ports.json",
);
const devPorts = JSON.parse(
  readFileSync(devPortsPath, "utf-8"),
) as DevPortsFile;
const dev = devPorts.development;

/**
 * The build the browser tests (apps/server/tests/Weir.E2E.Tests) measure: every source file the unit coverage counts
 * is instrumented (the same files as `test.coverage` below), so the page reports what ran in `window.__coverage__`. Opt-in with WEIR_WEB_COVERAGE=1; a normal
 * build carries none of it (docs/coverage/README.md).
 */
function browserCoverage() {
  return istanbul({
    include: "src/**/*",
    exclude: [
      "src/**/*.test.{ts,tsx}",
      "src/test/**",
      "src/**/*.d.ts",
      "src/lib/api/generated/**",
    ],
    extension: [".ts", ".tsx"],
    requireEnv: false,
    forceBuildInstrument: true,
  });
}

/** Local-only unless someone deliberately opts into exposing the dev server on the network. */
const devHost = (process.env.VITE_HOST || "").trim() || "127.0.0.1";

/** ``run-dev-stack.mjs`` may bump the port when the default from ``dev-ports.json`` is busy. */
const devWebPort = (() => {
  const raw = (process.env.WEIR_DEV_WEB_PORT || "").trim();
  if (raw) {
    const n = Number(raw);
    if (Number.isFinite(n) && n >= 1 && n <= 65535) {
      return n;
    }
  }
  return dev.webPort;
})();

function apiProxyTarget(mode: string, envDir: string): string {
  // Takes precedence over .env* (loadEnv) so automation can pin /api to a known backend.
  const forced = (process.env.WEIR_SCREENSHOT_API_PROXY_TARGET || "").trim();
  if (forced) {
    return forced;
  }
  // ``run-dev-stack.mjs`` sets this when the default API port holds an outdated build but a
  // fresh API is started on another port (see the stale-route probes).
  const devStackProxy = (
    process.env.WEIR_DEV_STACK_API_PROXY_TARGET || ""
  ).trim();
  if (devStackProxy) {
    return devStackProxy;
  }
  const fromFiles = loadEnv(mode, envDir, "");
  const fallback = `http://${dev.apiHost}:${dev.apiPort}`;
  return (
    fromFiles.VITE_DEV_API_PROXY_TARGET ||
    process.env.VITE_DEV_API_PROXY_TARGET ||
    fallback
  );
}

export default defineConfig(({ mode }) => {
  const apiTarget = apiProxyTarget(mode, __dirname);

  const apiProxy = {
    "/api": {
      target: apiTarget,
      changeOrigin: true,
    },
    "/ready": {
      target: apiTarget,
      changeOrigin: true,
    },
    "/health": {
      target: apiTarget,
      changeOrigin: true,
    },
  };

  return {
    plugins: [
      react(),
      tailwindcss(),
      ...(process.env.WEIR_WEB_COVERAGE === "1" ? [browserCoverage()] : []),
    ],
    build: {
      // Source maps are opt-in for local diagnostics. Production artifacts do not expose
      // source paths or ship multi-megabyte maps by default.
      sourcemap:
        process.env.WEIR_BUILD_SOURCEMAPS === "true" ? "hidden" : false,
      // Fonts stay files. A script's font is only fetched when text in that script appears (each
      // subset's `unicode-range`), but a font small enough to be inlined was written into every
      // visitor's stylesheet whether or not it was ever used: 2.7 KB of the 200 KiB budget.
      assetsInlineLimit: (file) =>
        file.endsWith(".woff2") ? false : undefined,
    },
    server: {
      // Local-only: binds to 127.0.0.1, not every interface, so the dev server isn't reachable
      // from the rest of the network by default. Set VITE_HOST (env) or pass --host to Vite to
      // expose it on purpose. Default port from ``dev-ports.json`` unless overridden.
      host: devHost,
      port: devWebPort,
      strictPort: true,
      proxy: { ...apiProxy },
    },
    /** Same-origin cookies in dev/preview: browser hits the Vite port; /api is forwarded. */
    preview: {
      host: devHost,
      port: devWebPort,
      strictPort: true,
      proxy: { ...apiProxy },
    },
    test: {
      environment: "jsdom",
      environmentOptions: {
        jsdom: {
          url: "http://localhost/",
        },
      },
      setupFiles: "./src/test/setup.ts",
      // A test that reads a stylesheet's own text (`?raw`) gets it; every other stylesheet stays empty.
      css: { include: /\.css\?raw$/ },
      globals: true,
      // Longer than the hang guard for a wait in src/test/setup.ts, so a wait that hangs is reported as that wait.
      testTimeout: 30_000,
      coverage: {
        provider: "v8",
        // Every source file counts, tested or not: an untested file is a row of zeros in the report.
        include: ["src/**/*.{ts,tsx}"],
        exclude: [
          "src/**/*.test.{ts,tsx}",
          "src/test/**",
          "src/**/*.d.ts",
          "src/lib/api/generated/**",
        ],
        reporter: ["lcov", "json", "json-summary", "text-summary"],
        reportsDirectory: "./coverage",
      },
    },
  };
});
