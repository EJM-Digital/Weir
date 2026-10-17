// workflows-from-deluno, ported from scripts/scenarios/Weir.Scenarios.Deluno.ps1: once Weir and Deluno are connected, Weir sets up its workflows
// from Deluno's libraries and folders: linked to Deluno, with their folders from Deluno and every step of the folder chain ready.
import assert from "node:assert/strict";

import { wait } from "../workflow-support.mjs";

export async function workflowsFromDeluno(world) {
  const connection = await world.deluno.connectWeir();
  // Testing the connection is what makes Weir look at Deluno now, as pressing Test in Weir does, instead of waiting for its schedule.
  const workflows = await wait(
    world,
    async () => {
      await world.weir.write("POST", `/media-managers/connections/${connection.id}/test`, {});
      const linked = (await world.weir.get("/processing/libraries")).filter(
        (workflow) => workflow.folders_synced_from_connection_id === connection.id && workflow.watched_folder && workflow.output_folder,
      );
      return linked.length >= 1 ? linked : undefined;
    },
    "Weir to set up its workflows from Deluno",
    120_000,
    2000,
  );
  for (const workflow of workflows) {
    assert.ok((workflow.manager_connection_ids ?? []).includes(connection.id), `'${workflow.name}' is linked to Deluno`);
    const chain = await world.weir.get(`/processing/libraries/${workflow.id}/folder-chain`);
    assert.equal(chain.ready, true, `the folder chain for '${workflow.name}' is ready`);
  }
  const names = workflows.map((workflow) => workflow.name).join(", ");
  return `Weir ${connection.how}; ${workflows.length} workflow(s) are set up from Deluno (${names}), linked, with their folders from Deluno (${workflows.map((workflow) => `watched ${workflow.watched_folder}, output ${workflow.output_folder}`).join("; ")}) and every step of the folder chain ready.`;
}
