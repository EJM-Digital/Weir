import { sendJson } from "../api/send-json";
import { readJson } from "../api/client";
import type {
  ProcessingFileRemuxPassManualEnqueueBody,
  ProcessingFileRemuxPassManualEnqueueOut,
} from "./types";

export const processingFileRemuxPassEnqueuePath = () =>
  "/api/v1/processing/jobs/file-remux-pass/enqueue";

export async function postProcessingFileRemuxPassEnqueue(
  body: ProcessingFileRemuxPassManualEnqueueBody,
): Promise<ProcessingFileRemuxPassManualEnqueueOut> {
  const path = processingFileRemuxPassEnqueuePath();
  const r = await sendJson(path, "POST", body, "Could not queue that file");
  return readJson<ProcessingFileRemuxPassManualEnqueueOut>(r);
}
