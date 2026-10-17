// What the tray says, word for word, as apps/tray/Weir.Tray/TrayBalloons.cs and LanAccess/LanAccessNotice.cs write it, and how tray-host.log keeps a
// balloon (TrayBalloons.LogLine). scenarios-in-ci-tray.test.mjs fails when these drift from the C# source.

export const address = (port) => `http://localhost:${port}`;
const tries = (attempts) => (attempts === 1 ? "1 try" : `${attempts} tries`);

/** The line tray-host.log keeps of a balloon. */
export const balloonLine = (icon, title, text) => `Balloon (${icon}) ${title}: ${text}`;

export const balloons = {
  running: (port) => `Weir is running at ${address(port)}. Click to open it.`,
  alreadyRunning: (port) => `Weir is already running at ${address(port)}. Click to open it.`,
  portMoved: (port) => `Weir is now at ${address(port)}. Click to open it.`,
  portMoveFailed: (port, current) => `Weir could not start on port ${port}, so it is still at port ${current}. See tray-host.log in the data folder.`,
  stoppedPortInUse: (port, attempts) =>
    `Weir stopped and didn't start again after ${tries(attempts)} because another program is using port ${port}. Close that program, or use Change port, then click to try again.`,
  stoppedAfterTries: (attempts) => `Weir stopped and didn't start again after ${tries(attempts)}. The details are in the tray log. Click to try again.`,
  couldNotStart: "Weir couldn't start. Click to try again.",
  notRunning: "Weir isn't running. Click to restart it.",
  lanOpened: "Other devices on your network can now reach Weir. Weir restarted to apply this.",
  lanClosed: "Only this PC can reach Weir now. Weir restarted to apply this.",
  updateAvailable: (version) => `Version ${version} is available. Open System › About in Weir to update.`,
};

export const menu = {
  openWeir: "Open Weir",
  pause: "Pause processing",
  resume: "Resume processing",
  restart: "Restart Weir",
  copyAddress: "Copy address",
  allowOtherDevices: "Allow other devices on your network...",
  onlyThisPc: "Only allow this PC",
  changePort: (port) => `Change port (${port})...`,
  openDataFolder: "Open data folder",
  openLogsFolder: "Open logs folder",
  startWithWindows: "Start with Windows",
  checkForUpdates: "Check for updates",
  reportProblem: "Report a problem...",
  quit: "Quit Weir",
};

/** The status line of the menu, which is the icon's hover text, for a running Weir and for one the tray gave up on. */
export const status = {
  running: (port) => `Weir - Running at ${address(port)}`,
  paused: "Weir - Paused",
  stoppedPortInUse: (port) => `Weir - Stopped - Port ${port} is in use - choose Restart Weir`,
};
