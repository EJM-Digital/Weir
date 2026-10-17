// The scenarios of the weir-only shard, by catalogue id: Weir alone in the Docker image, with no manager.
import { deletedQueuedFile } from "./deleted-queued-file.mjs";
import { processAgain, weirOnlyFilm } from "./film-scenarios.mjs";
import { pauseResume } from "./pause-resume.mjs";

export const SCENARIOS = {
  "weir-only-film": weirOnlyFilm,
  "process-again": processAgain,
  "pause-resume": pauseResume,
  "deleted-queued-file": deletedQueuedFile,
};
