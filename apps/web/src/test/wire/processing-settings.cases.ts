import {
  fetchProcessingOperatorSettings,
  putProcessingOperatorSettings,
} from "../../lib/processing/operator-settings-api";
import { fetchProcessingOverviewStats } from "../../lib/processing/overview-stats-api";
import { fetchProcessingRuntimeSettings } from "../../lib/processing/runtime-settings-api";
import type { RequestCase } from "./request-case";

const PROCESSING = "/api/v1/processing";

/** The requests behind the Performance and Cleanup settings and the Processing overview's numbers. */
export const processingSettingsRequestCases: RequestCase[] = [
  {
    fn: fetchProcessingOperatorSettings,
    asking: "the processing settings",
    call: () => fetchProcessingOperatorSettings(),
    method: "GET",
    path: `${PROCESSING}/operator-settings`,
    reply: `GET ${PROCESSING}/operator-settings`,
  },
  {
    fn: putProcessingOperatorSettings,
    asking: "saving only the settings that changed",
    call: () =>
      putProcessingOperatorSettings({
        max_concurrent_files: 2,
        keep_failed_work_files: true,
      }),
    method: "PUT",
    path: `${PROCESSING}/operator-settings`,
    body: { max_concurrent_files: 2, keep_failed_work_files: true },
  },
  {
    fn: fetchProcessingOverviewStats,
    asking: "the overview numbers for the default window",
    call: () => fetchProcessingOverviewStats(),
    method: "GET",
    path: `${PROCESSING}/overview-stats`,
  },
  {
    fn: fetchProcessingOverviewStats,
    asking: "the overview numbers for a window of days",
    call: () => fetchProcessingOverviewStats(7),
    method: "GET",
    path: `${PROCESSING}/overview-stats`,
    query: [["window_days", "7"]],
  },
  {
    fn: fetchProcessingOverviewStats,
    asking: "the overview numbers for a window of no days",
    call: () => fetchProcessingOverviewStats(0),
    method: "GET",
    path: `${PROCESSING}/overview-stats`,
    query: [["window_days", "0"]],
  },
  {
    fn: fetchProcessingRuntimeSettings,
    asking: "the runtime settings",
    call: () => fetchProcessingRuntimeSettings(),
    method: "GET",
    path: `${PROCESSING}/runtime-settings`,
  },
];
