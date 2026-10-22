export type SetupAreaId = "workflows" | "connections" | "rules" | "performance";

export type SetupTabId =
  | "workflows"
  | "schedule"
  | "managers"
  | "download-clients"
  | "alerts"
  | "profiles"
  | "devices"
  | "speed"
  | "cleanup"
  | "timers";

export type SetupTab = Readonly<{
  id: SetupTabId;
  /** The name on the tab. */
  label: string;
  /** Where the tab lives. */
  path: string;
}>;

export type SetupArea = Readonly<{
  id: SetupAreaId;
  /** The name in the side menu and the page title. */
  label: string;
  /** The line above the title: what the area is for. */
  eyebrow: string;
  /** Where the area opens, on its first tab. */
  path: string;
  tabs: readonly SetupTab[];
}>;

const SETUP_ROOT = "/setup";

type TabDeclaration = readonly [id: SetupTabId, label: string];

/**
 * Declares an area and its tabs. The first tab lives at the area's own address, so the side menu's link and
 * that tab's link are one address; every other tab adds its id.
 */
function declareArea(
  id: SetupAreaId,
  label: string,
  eyebrow: string,
  tabs: readonly TabDeclaration[],
): SetupArea {
  const path = `${SETUP_ROOT}/${id}`;
  return {
    id,
    label,
    eyebrow,
    path,
    tabs: tabs.map(([tabId, tabLabel], index) => ({
      id: tabId,
      label: tabLabel,
      path: index === 0 ? path : `${path}/${tabId}`,
    })),
  };
}

/** The areas in the order someone sets Weir up: what to watch, what it connects to, what to keep, how hard to work. */
export const SETUP_AREAS: readonly SetupArea[] = [
  declareArea(
    "workflows",
    "Workflows",
    "Where your media is, how each folder is cleaned, and when",
    [
      ["workflows", "File paths"],
      ["schedule", "Schedule"],
    ],
  ),
  declareArea(
    "connections",
    "Connections",
    "The apps Weir hands cleaned files back to, and who it tells",
    [
      ["managers", "Media managers"],
      ["download-clients", "Download clients"],
      ["alerts", "Alerts"],
    ],
  ),
  declareArea(
    "rules",
    "Rules",
    "What to keep and what to take out of every file",
    [
      ["profiles", "Profiles"],
      ["devices", "Playback devices"],
    ],
  ),
  declareArea(
    "performance",
    "Performance",
    "How hard Weir works, and when it tidies up",
    [
      ["speed", "Speed"],
      ["cleanup", "Cleanup"],
      ["timers", "Weir's timers"],
    ],
  ),
];

/** The area that holds `tabId`. */
export function setupAreaOfTab(tabId: SetupTabId): SetupArea {
  const owner = SETUP_AREAS.find((candidate) =>
    candidate.tabs.some((tab) => tab.id === tabId),
  );
  if (!owner) throw new Error(`No setup area holds the tab ${tabId}.`);
  return owner;
}

/** Where a tab lives. */
export function setupTabPath(tabId: SetupTabId): string {
  return setupAreaOfTab(tabId).tabs.find((tab) => tab.id === tabId)!.path;
}

/** The area an address belongs to, or null when it is not one of the setup areas. */
export function setupAreaForPath(pathname: string): SetupArea | null {
  return (
    SETUP_AREAS.find(
      (candidate) =>
        pathname === candidate.path ||
        pathname.startsWith(`${candidate.path}/`),
    ) ?? null
  );
}

/** The tab an address shows within `owner`; its first tab when the address names no other. */
export function setupTabForPath(owner: SetupArea, pathname: string): SetupTab {
  return owner.tabs.find((tab) => tab.path === pathname) ?? owner.tabs[0];
}

/** Opens a workflow's editor, from a link elsewhere. */
export function workflowEditorPath(workflowId: number): string {
  return `${setupTabPath("workflows")}?edit=${workflowId}`;
}

/** Opens the add-a-workflow choice already on a media manager. */
export function workflowFromManagerPath(connectionId: number): string {
  return `${setupTabPath("workflows")}?addFrom=${connectionId}`;
}
