// The scenarios of the windows-tray shard, run on a clean GitHub-hosted Windows runner against the Setup this commit built: the real installed
// tray, driven through Windows UI Automation, over the real server. update-requests is ported from the VM's catalogue; the rest were written for
// CI, because the VM suite never pressed a menu item, ended a server or started the tray twice.
import { installHooks } from "./install-hooks.mjs";
import { trayChangePort, trayLanAccess } from "./tray-network.mjs";
import { networkAccessFromWeir } from "./tray-network-access.mjs";
import { trayPortable } from "./tray-portable.mjs";
import { traySecondLaunch, trayStart } from "./tray-launch.mjs";
import { trayMenu } from "./tray-menu.mjs";
import { trayGiveUp, traySupervision } from "./tray-supervision.mjs";
import { updateRequests } from "./tray-updates.mjs";

export { watchLogs } from "./windows-install.mjs";

/** The scenarios of the windows-tray shard, by id. */
export const SCENARIOS = {
  "update-requests": updateRequests,
  "tray-menu": trayMenu,
  "tray-supervision": traySupervision,
  "tray-give-up": trayGiveUp,
  "tray-change-port": trayChangePort,
  "tray-lan-access": trayLanAccess,
  "tray-second-launch": traySecondLaunch,
  "tray-start": trayStart,
  "install-hooks": installHooks,
  "tray-portable": trayPortable,
  "network-access-from-weir": networkAccessFromWeir,
};
