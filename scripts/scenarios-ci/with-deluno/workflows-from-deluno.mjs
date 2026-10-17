// workflows-from-deluno, ported from scripts/scenarios/Weir.Scenarios.Deluno.ps1: once Weir and Deluno are connected, Weir sets up its workflows
// from Deluno's libraries and folders: linked to Deluno, with their folders from Deluno and every step of the folder chain ready.
import assert from "node:assert/strict";

import { syncedWorkflows } from "./synced-workflows.mjs";

export async function workflowsFromDeluno(world) {
  const { connection, workflows } = await syncedWorkflows(world);
  for (const workflow of workflows) {
    assert.ok((workflow.manager_connection_ids ?? []).includes(connection.id), `'${workflow.name}' is linked to Deluno`);
    const chain = await world.weir.get(`/processing/libraries/${workflow.id}/folder-chain`);
    assert.equal(chain.ready, true, `the folder chain for '${workflow.name}' is ready`);
  }
  const names = workflows.map((workflow) => workflow.name).join(", ");
  const folders = workflows.map((workflow) => `watched ${workflow.watched_folder}, output ${workflow.output_folder}`).join("; ");
  return `Weir ${connection.how}; ${workflows.length} workflow(s) are set up from Deluno (${names}), linked, with their folders from Deluno (${folders}) and every step of the folder chain ready.`;
}
