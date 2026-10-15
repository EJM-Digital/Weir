import { sendJson, sendJsonUnchecked } from "../api/send-json";
import { apiFetch, readJson, requireOk } from "../api/client";
import type { RequestBody, Schema } from "../api/types";

/** A media manager Weir talks to. The kind selects the payload dialect, nothing more. */
export type MediaManagerKind = Schema<"MediaManagerConnectionOut">["kind"];

export const MEDIA_MANAGER_KIND_LABELS: Record<MediaManagerKind, string> = {
  radarr: "Radarr",
  sonarr: "Sonarr",
  deluno: "Deluno",
  native: "Something else",
};

/**
 * Radarr, Sonarr and Deluno are called by Weir, so they need an address. "Something else" only sends to
 * Weir and exists to be given a webhook secret, so it can be saved without one.
 */
export function connectionNeedsAddress(kind: MediaManagerKind): boolean {
  return kind !== "native";
}

/** What the address field says for a kind that can be saved without one. */
export const OPTIONAL_ADDRESS_HINT =
  "Optional. Leave it blank if it only sends messages to Weir and Weir never calls it.";

/** Kept by hand: the server always sends the last_test_* fields, which the schema marks optional. */
export interface MediaManagerConnection {
  id: number;
  kind: MediaManagerKind;
  name: string;
  /** The short label a person gave it, shown after its name. */
  nickname?: string | null;
  enabled: boolean;
  base_url: string;
  api_key_is_saved: boolean;
  webhook_secret_is_set: boolean;
  webhook_url_path: string;
  downloaded_scan_enabled: boolean;
  unsigned_webhook_warning: string | null;
  last_test_ok: boolean | null;
  last_test_at: string | null;
  last_test_detail: string | null;
  /** How long Weir's last call to it took, in ms. */
  last_answer_ms?: number | null;
  /** When Weir last talked to it or it last called Weir. */
  last_used_at?: string | null;
}

export type MediaManagerConnectionCreate =
  RequestBody<"MediaManagerConnectionCreateIn">;
/** Leave api_key out to keep the saved key; send "" to clear it. */
export type MediaManagerConnectionUpdate =
  RequestBody<"MediaManagerConnectionUpdateIn">;
export type MediaManagerWebhookSecret = Schema<"MediaManagerWebhookSecretOut">;
/** What stands in the way of removing a connection: the workflows that still come from it, hand-offs in progress, reports owed. */
export type ConnectionInUse = Schema<"ConnectionInUseError">;
export type ConnectionWorkflow = Schema<"ConnectionWorkflowOut">;
export type UnlinkedWorkflows = Schema<"MediaManagerWorkflowsUnlinkedOut">;
export type GivenUpReports = Schema<"MediaManagerReportsGivenUpOut">;
export type MediaManagerConnectionTest =
  Schema<"MediaManagerConnectionTestOut">;

export const mediaManagerConnectionsPath = () =>
  "/api/v1/media-managers/connections";

const connectionPath = (id: number) => `${mediaManagerConnectionsPath()}/${id}`;

export async function fetchMediaManagerConnections(): Promise<
  MediaManagerConnection[]
> {
  const path = mediaManagerConnectionsPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load media managers");
  return readJson<MediaManagerConnection[]>(r);
}

export async function createMediaManagerConnection(
  data: MediaManagerConnectionCreate,
): Promise<MediaManagerConnection> {
  const path = mediaManagerConnectionsPath();
  const r = await sendJson(
    path,
    "POST",
    data,
    "Could not add that media manager",
  );
  return readJson<MediaManagerConnection>(r);
}

export async function updateMediaManagerConnection(
  id: number,
  data: MediaManagerConnectionUpdate,
): Promise<MediaManagerConnection> {
  const path = connectionPath(id);
  const r = await sendJson(
    path,
    "PUT",
    data,
    "Could not save that media manager",
  );
  return readJson<MediaManagerConnection>(r);
}

/**
 * Removes a connection, or returns what it is still needed for: the server refuses with a 409 and a code while a workflow
 * comes from the connection, a hand-off from it is in progress, or Weir owes it a report, and removes nothing.
 */
export async function deleteMediaManagerConnection(
  id: number,
): Promise<ConnectionInUse | undefined> {
  const path = connectionPath(id);
  const r = await sendJsonUnchecked(path, "DELETE", {});
  if (r.status === 409) {
    const body = (await r
      .clone()
      .json()
      .catch(() => null)) as ConnectionInUse | null;
    if (body?.code === "connection_in_use") return body;
  }
  await requireOk(path, r, "Could not remove that media manager");
  return undefined;
}

/** Unlinks every workflow from the connection. They stay; one no manager feeds any more is switched off. */
export async function unlinkMediaManagerConnectionWorkflows(
  id: number,
): Promise<UnlinkedWorkflows> {
  const path = `${connectionPath(id)}/unlink-workflows`;
  const r = await sendJson(
    path,
    "POST",
    {},
    "Could not unlink those workflows",
  );
  return readJson<UnlinkedWorkflows>(r);
}

/** Stops owing the connection the reports it could not deliver. Nothing is sent to the manager; Activity records what was dropped. */
export async function giveUpMediaManagerReports(
  id: number,
): Promise<GivenUpReports> {
  const path = `${connectionPath(id)}/give-up-reports`;
  const r = await sendJson(
    path,
    "POST",
    {},
    "Could not give up on those reports",
  );
  return readJson<GivenUpReports>(r);
}

export async function testMediaManagerConnection(
  id: number,
): Promise<MediaManagerConnectionTest> {
  const path = `${connectionPath(id)}/test`;
  const r = await sendJson(
    path,
    "POST",
    {},
    "Could not test that media manager",
  );
  return readJson<MediaManagerConnectionTest>(r);
}

export async function generateMediaManagerWebhookSecret(
  id: number,
): Promise<MediaManagerWebhookSecret> {
  const path = `${connectionPath(id)}/webhook-secret`;
  const r = await sendJson(
    path,
    "POST",
    {},
    "Could not generate a webhook secret",
  );
  return readJson<MediaManagerWebhookSecret>(r);
}
