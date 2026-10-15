// The linux/arm64 image started the way an operator starts it, on an x86-64 machine that runs it under QEMU (docker/setup-qemu-action).
import assert from "node:assert/strict";

import { createData, expectKept } from "./smoke-data.mjs";

// Emulated, the server takes minutes to answer, not seconds.
export const EMULATED_WAIT = { attempts: 180, intervalSeconds: 5, attemptTimeoutSeconds: 15 };

/**
 * PLAT-13: the arm64 image starts and answers /health, the programs it carries are built for arm64 and run, and the first account can be
 * created and signed in to, which writes and reads the SQLite database through the architecture's own native library.
 */
export async function arm64Starts({ stack, client, candidate }) {
  stack.up(candidate, "never", { platform: "linux/arm64" });
  stack.waitHealthy(EMULATED_WAIT);

  assert.equal(stack.exec("uname -m").trim(), "aarch64", "the container is not running the arm64 image");
  assert.match(stack.exec("ffmpeg -version"), /^ffmpeg version /, "ffmpeg does not run");
  assert.match(stack.exec("mkvmerge --version"), /^mkvmerge v/, "mkvmerge does not run");

  const kept = await createData({ client, stack });
  await expectKept({ client, kept, sessionKept: true });
}
