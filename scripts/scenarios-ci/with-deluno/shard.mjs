// What the with-deluno shard starts beside Weir, and how it is set up before the scenarios begin.
import { join } from "node:path";

import { imageEnvironment, loadImages } from "../images.mjs";
import { DelunoWorld, newLogins } from "./deluno-world.mjs";

const SERVICES = ["deluno", "qbittorrent", "recorder"];

export const WITH_DELUNO = {
  files: ["scripts/scenarios-ci/with-deluno/compose.yaml"],
  services: SERVICES,

  /** The images to pull (the stack starts with --pull never, for the Weir image CI built), the variables Compose reads, and this run's logins. */
  start() {
    const images = loadImages();
    const logins = newLogins();
    return {
      pull: Object.values(images).map((image) => image.reference),
      environment: {
        ...imageEnvironment(images),
        SCENARIO_QBITTORRENT_USERNAME: logins.qbittorrent.username,
        SCENARIO_QBITTORRENT_PBKDF2: logins.qbittorrent.pbkdf2,
      },
      logins,
    };
  },

  /** Waits for the applications and sets them up; returns what the scenarios reach them by. */
  setUp: (world, { logins }) => DelunoWorld.setUp({ weir: world.weir, logins, clock: world.clock }),

  /** After the scenarios, while the stack is still up: what passed between Weir and Deluno, as the recordings the scenarios asked to keep. Returns the files. */
  collect: (world, folder) => world.deluno.writeRecordings(join(folder, "recordings")),
};
