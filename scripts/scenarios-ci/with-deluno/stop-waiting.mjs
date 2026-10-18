// deluno-stop-waiting (#1017, INT-DEL-03): Deluno hands Weir a release while Weir is paused, so the hand-off waits its turn, and then the person stops
// waiting for it in Deluno ("Stop waiting"). Deluno asks Weir to drop it; Weir does, says so, and never works it once it resumes.
//
// The conversation is kept as a recording (real-stop-waiting): the hand-off, Deluno's questions while it waited, its cancel and Weir's answer.
import assert from "node:assert/strict";

import { wait } from "../workflow-support.mjs";
import { ANSWER_CEILING_MS, reportingWhereItStopped, waitForDownload, waitForHandoff } from "./handoffs.mjs";
import { syncedWorkflows } from "./synced-workflows.mjs";

const pause = (world, paused) => world.weir.write("PUT", "/pause", { paused, scan_while_paused: true });
const DROPPED = 204;

export async function delunoStopWaiting(world) {
  await syncedWorkflows(world);
  const film = (world.failingFilm = await world.deluno.makeFilm({ box: world.box, title: "ScnStop", year: 2020 }));
  const paused = await pause(world, true);
  assert.equal(paused.status, 200, `Weir paused (answered ${paused.status}: ${paused.text})`);
  try {
    return await reportingWhereItStopped(world, film, async () => {
      await world.deluno.sendFilm(film);
      await waitForDownload(world, film);
      const held = await waitForHandoff(world, film, (found) => Boolean(found), `Deluno to hand ${film.release} to Weir`);
      await wait(
        world,
        async () => (await world.deluno.exchanges.where("to-weir", (exchange) => exchange.request.method === "POST" && exchange.request.body?.handoffId === held.id))[0],
        `Weir to take in Deluno's hand-off of ${film.release}`,
        ANSWER_CEILING_MS,
        2000,
      );

      const stopped = await world.deluno.deluno.call("POST", `/api/integrations/processors/handoffs/${held.id}/stop-waiting`);
      assert.equal(stopped.status, 200, `Deluno stopped waiting (answered ${stopped.status}: ${stopped.json?.message ?? stopped.text.slice(0, 300)})`);
      const cancel = await wait(
        world,
        async () => (await world.deluno.exchanges.where("to-weir", (exchange) => exchange.request.method === "DELETE" && exchange.request.path.includes(held.id)))[0],
        `Deluno to ask Weir to drop ${film.release}`,
        ANSWER_CEILING_MS,
        2000,
      );
      assert.equal(cancel.reply.status, DROPPED, `Weir dropped the hand-off (it answered ${cancel.reply.status}: ${JSON.stringify(cancel.reply.body)})`);
      const ended = await waitForHandoff(world, film, (found) => found && found.status !== held.status, `Deluno to show the hand-off of ${film.release} as ended`, { failureExpected: true });

      const resumed = await pause(world, false);
      assert.equal(resumed.status, 200, `Weir resumed (answered ${resumed.status}: ${resumed.text})`);
      const said = await wait(
        world,
        async () => (await world.weir.get("/activity/recent?limit=100")).items.find((item) => String(item.title).includes(`cancelled its hand-off of ${film.release}`)),
        `Weir's Activity to say Deluno cancelled its hand-off of ${film.release}`,
        ANSWER_CEILING_MS,
        2000,
      );
      assert.deepEqual(await world.box.findNamed("/weir", `${film.release}.mkv`), [], "Weir never worked the dropped hand-off: nothing was written to any output folder");

      world.deluno.keepConversation(ended, {
        state: "real-stop-waiting",
        note: "A release handed to Weir by a real Deluno while Weir was paused, and then dropped by a person pressing Stop waiting in Deluno: Deluno's questions while it waited, its request that Weir drop the hand-off and Weir's answer, as the proxy between them saw it.",
      });
      return `Deluno's hand-off of ${film.release} waited while Weir was paused; after Stop waiting in Deluno it asked Weir to drop it and Weir did (${cancel.reply.status}), Deluno shows it ${ended.status}, Weir's Activity says "${said.title}" and nothing was written to the output.`;
    });
  } finally {
    await pause(world, false);
  }
}
