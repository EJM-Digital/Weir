// deleted-queued-file, ported from scripts/scenarios/Weir.Scenarios.WeirOnly.ps1: a file put in a watched folder and deleted while Weir waits
// for it to settle is marked no longer there, never reported as a failure. The check of the log that the VM version makes by itself is made for
// every scenario by the runner (run.mjs).
import assert from "node:assert/strict";

import { filesOf, lookAndWait, makeWorkflow, startLook, wait } from "./workflow-support.mjs";

const FILE = "Harbour.Lights.2024.mkv";
const FAILURE_RESULTS = ["failed", "warning", "error"];

export async function deletedQueuedFile(world) {
  // A file under the minimum size that must also wait 60 s to settle: it is found, and held for as long as Weir waits.
  const workflow = await makeWorkflow(world, { name: "Scenario settling", folder: "settling", minimumMegabytes: 50, readyAfterSeconds: 60 });
  const path = `${workflow.watched}/${FILE}`;
  await world.box.writeBytes(path, [1, 2, 3, 4, 5]);

  await startLook(world, workflow);
  const waiting = await wait(world, async () => (await filesOf(world, workflow)).find((file) => file.relative_path === FILE), "Weir to find the file and wait for it to settle", 90_000);
  assert.ok(waiting.status === "on_hold" && !waiting.source_gone, `Weir is waiting for the file to settle (status ${waiting.status}: ${waiting.status_reason})`);

  await world.box.remove(path);
  const gone = await wait(
    world,
    async () => {
      await lookAndWait(world, workflow);
      return (await filesOf(world, workflow)).find((file) => file.relative_path === FILE && file.source_gone);
    },
    "Weir to notice the file is no longer there",
    90_000,
  );
  assert.ok(gone.status !== "processing_failed" && gone.status !== "rejected" && !gone.failure_class, `the vanished file is not a failure (status ${gone.status}, failure class ${gone.failure_class})`);
  const { status_counts: counts } = await world.weir.get(`/processing/files?limit=1&library_id=${workflow.id}`);
  assert.ok(counts.processing_failed === 0 && counts.rejected === 0, "nothing in the workflow needs the person");
  const items = (await world.weir.get("/activity/recent?limit=100")).items;
  const failures = items.filter((item) => item.title.includes("Harbour.Lights") && FAILURE_RESULTS.includes(item.result));
  assert.equal(failures.length, 0, "Activity has no failure or warning about the file");
  return `Weir held the 5-byte file while it settled ("${waiting.status_reason}"), then, once it was deleted, marked it no longer there (status ${gone.status}, source_gone true) with no failure and nothing needing the person.`;
}
