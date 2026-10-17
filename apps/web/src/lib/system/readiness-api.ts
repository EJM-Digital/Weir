import { apiFetch, readJson, requireOk } from "../api/client";
import type { SystemReadiness } from "../api/types";

const systemReadinessPath = "/api/v1/system/readiness";
const SERVICE_UNAVAILABLE = 503;

/** The readiness body a 503 carries, or null when the 503 says something else (a busy database answers 503 too). */
async function readinessOf503(
  response: Response,
): Promise<SystemReadiness | null> {
  try {
    const body = await readJson<Partial<SystemReadiness> | undefined>(
      response.clone(),
    );
    return typeof body?.ready === "boolean" ? (body as SystemReadiness) : null;
  } catch {
    return null;
  }
}

/**
 * Signed-in readiness: the installed version and whether the workers are running.
 *
 * The server answers 503 while it is starting or when a worker has stopped, with the same
 * body as a 200. That body is exactly what the shell wants to show, so a 503 that carries it is
 * read, not thrown. Any other 503, such as a busy database's, is a failed reading.
 */
export async function fetchSystemReadiness(): Promise<SystemReadiness> {
  const response = await apiFetch(systemReadinessPath);
  if (response.status === SERVICE_UNAVAILABLE) {
    const reading = await readinessOf503(response);
    if (reading) return reading;
  }
  await requireOk(
    systemReadinessPath,
    response,
    "Could not read whether Weir is running",
  );
  return readJson<SystemReadiness>(response);
}
