import {
  asNumber,
  asString,
  parseActivityDetail,
} from "../../../../lib/activity/detail";
import { baseName } from "../../../../lib/format/path";
import { ACTIVITY_PATH } from "../../../activity/activity-links";

/** The payload a job was made from, indented to read; text that is not JSON is shown as it is, and an empty one as nothing. */
export function formatPayload(
  payloadJson: string | null | undefined,
): string | null {
  const text = payloadJson?.trim();
  if (!text) return null;
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

/** The period that has every file Activity keeps, so a file last touched long ago is still found. */
const EVERYTHING_KEPT = "all";

/**
 * Where Activity lists the file a job was for, or null when the job is not about one file. Activity finds a file by its
 * name, so the link carries the name and the workflow it is in.
 */
export function activityFileOfJob(job: {
  payload_json?: string | null;
}): string | null {
  const payload = parseActivityDetail(job.payload_json);
  const path = asString(payload?.relative_media_path);
  if (!path) return null;
  const params = new URLSearchParams({
    q: baseName(path),
    within: EVERYTHING_KEPT,
  });
  const workflow = asNumber(payload?.library_id);
  if (workflow !== null) params.set("library", String(workflow));
  return `${ACTIVITY_PATH}?${params}`;
}
