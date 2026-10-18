// The scenarios of the windows-files shard, run on a clean GitHub-hosted Windows runner against the Setup this commit built: what the installed
// server does to real files on real NTFS volumes and a real network share, and the setting that opens its port to other devices. All were
// written for CI: the VM suite never cleaned a library file in place, ended the server mid-swap, used a second drive or a share, or flipped
// "other devices".
import { libraryCleanInPlace, librarySwapCrash } from "./files-swap.mjs";
import { networkAccess } from "./files-network.mjs";
import { fullDisk, keepOriginalAcrossVolumes, publishAcrossVolumes } from "./files-publish.mjs";
import { networkShare, networkShareHang } from "./files-share.mjs";

export { watchLogs } from "./files-world.mjs";

/** The scenarios of the windows-files shard, by id. */
export const SCENARIOS = {
  "library-clean-in-place": libraryCleanInPlace,
  "library-swap-crash": librarySwapCrash,
  "publish-across-volumes": publishAcrossVolumes,
  "keep-original-across-volumes": keepOriginalAcrossVolumes,
  "full-disk": fullDisk,
  "network-share": networkShare,
  "network-share-hang": networkShareHang,
  "network-access": networkAccess,
};
