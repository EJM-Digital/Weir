import { sendJson } from "../api/send-json";
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

export async function deleteMediaManagerConnection(id: number): Promise<void> {
  const path = connectionPath(id);
  await sendJson(path, "DELETE", {}, "Could not remove that media manager");
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
