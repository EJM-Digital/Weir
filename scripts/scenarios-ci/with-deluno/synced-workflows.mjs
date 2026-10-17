// Weir connected to Deluno and its workflows set up from Deluno's libraries: what the scenarios of the with-deluno shard that follow a film start from.
// Each scenario asks for it itself, so none depends on another having run; the connection is made once and later calls find it.
import { wait } from "../workflow-support.mjs";

/**
 * Connects Weir and Deluno and waits for Weir to set up its workflows from Deluno's libraries: each linked to Deluno, with its folders from Deluno.
 * Testing the connection is what makes Weir look at Deluno now, as pressing Test in Weir does, instead of waiting for its schedule.
 * @returns {Promise<{connection: {id: number, how: string}, workflows: object[]}>}
 */
export async function syncedWorkflows(world) {
  const connection = await world.deluno.connectWeir();
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
  return { connection, workflows };
}
