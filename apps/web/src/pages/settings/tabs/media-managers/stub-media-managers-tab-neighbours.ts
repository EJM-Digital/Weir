import { vi } from "vitest";

import { signedInAs } from "../../../../test/signed-in-as";
import * as librariesApi from "../../../../lib/processing/libraries-api";
import * as folderChainApi from "../../../../lib/processing/library-folder-chain-api";
import * as settingsQueries from "../../../../lib/settings/queries";

/**
 * Answers everything the Media managers tab loads besides the connection list, so a test of that
 * list makes no real request. The linked libraries, each connection's folder-chain check and the
 * app timezone all load on their own and have their own tests.
 */
export function stubMediaManagersTabNeighbours(): void {
  signedInAs("admin");
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([]);
  vi.spyOn(folderChainApi, "fetchConnectionFolderChain").mockResolvedValue([]);
  vi.spyOn(settingsQueries, "useAppSettingsQuery").mockReturnValue({
    data: undefined,
  } as ReturnType<typeof settingsQueries.useAppSettingsQuery>);
}
