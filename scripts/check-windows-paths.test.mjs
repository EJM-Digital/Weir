// node --test scripts/check-windows-paths.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { isWindowsOnly, unmatchedWindowsFiles, windowsFilter } from "./check-windows-paths.mjs";

const ci = `            windows:
              # a comment between patterns
              - 'apps/tray/**'
              - 'apps/server/src/**/*.Windows.cs'
              - 'apps/server/src/Weir.Infrastructure/Runtime/**'
            media_tools:
              - 'scripts/other.mjs'
`;

test("the patterns are those under windows:, comments skipped, and stop at the next filter", () => {
  assert.deepEqual(windowsFilter(ci), ["apps/tray/**", "apps/server/src/**/*.Windows.cs", "apps/server/src/Weir.Infrastructure/Runtime/**"]);
});

test("a ci.yml with no windows filter, or an empty one, is an error", () => {
  assert.throws(() => windowsFilter("            tray:\n              - 'apps/tray/**'\n"), /no `windows:` path filter/);
  assert.throws(() => windowsFilter("            windows:\n            media_tools:\n"), /lists nothing/);
});

test("a file is Windows-only by its name or by what it calls", () => {
  assert.equal(isWindowsOnly("apps/server/src/A/Thing.Windows.cs", ""), true);
  assert.equal(isWindowsOnly("apps/server/src/A/ComServerFirewall.cs", ""), true);
  assert.equal(isWindowsOnly("apps/server/src/A/Native.cs", '[DllImport("kernel32")]'), true);
  assert.equal(isWindowsOnly("apps/server/src/A/Keys.cs", "using Microsoft.Win32;"), true);
  assert.equal(isWindowsOnly("apps/server/src/A/Check.cs", 'if (OperatingSystem.IsWindows()) { }'), false);
});

test("a Windows-only server file the filter misses is named, and one it matches is not", () => {
  const patterns = windowsFilter(ci);
  const files = [
    "apps/server/src/Weir.Infrastructure/Runtime/Firewall.cs",
    "apps/server/src/Weir.Infrastructure/Swap/Swap.Windows.cs",
    "apps/server/src/Weir.Infrastructure/Other/Native.cs",
    "apps/server/src/Weir.Infrastructure/Other/Plain.cs",
    "apps/server/tests/Weir.Infrastructure.Tests/Native.cs",
  ];
  const content = { "apps/server/src/Weir.Infrastructure/Other/Native.cs": "[LibraryImport(\"user32\")]" };
  assert.deepEqual(unmatchedWindowsFiles(files, (file) => content[file] ?? "", patterns), ["apps/server/src/Weir.Infrastructure/Other/Native.cs"]);
});
