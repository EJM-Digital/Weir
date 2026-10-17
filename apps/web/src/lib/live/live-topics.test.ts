import { describe, expect, it } from "vitest";

import { authKeys } from "../auth/query-keys";
import { downloadClientKeys } from "../download-clients/query-keys";
import { mediaManagerKeys } from "../media-managers/query-keys";
import { pauseKeys } from "../pause/query-keys";
import { processingKeys } from "../processing/query-keys";
import { settingsKeys } from "../settings/query-keys";
import { systemKeys } from "../system/query-keys";
import {
  LIVE_TOPIC_QUERIES,
  parseDataChanged,
  type LiveTopic,
} from "./live-topics";

describe("LIVE_TOPIC_QUERIES", () => {
  it("names every topic the server can send", () => {
    const topics: LiveTopic[] = [
      "backups",
      "connections",
      "direct_play_devices",
      "files_at_once",
      "folder_checks",
      "jobs",
      "kept_files",
      "libraries",
      "library_scan",
      "maintenance",
      "metrics",
      "network_access",
      "pause",
      "readiness",
      "sessions",
      "settings",
      "update",
    ];

    expect(Object.keys(LIVE_TOPIC_QUERIES).sort()).toEqual(topics);
  });

  it("reads the playback devices, and the files that show a Direct Play badge for them, again when the chosen devices change", () => {
    expect(LIVE_TOPIC_QUERIES.direct_play_devices).toEqual([
      processingKeys.directPlayDevices,
      processingKeys.files,
    ]);
  });

  it("reads the list of signed-in browsers again when one signs in or out", () => {
    expect(LIVE_TOPIC_QUERIES.sessions).toEqual([authKeys.sessions]);
  });

  it("refreshes the pause queries, and what files wait for, when the pause changes", () => {
    expect(LIVE_TOPIC_QUERIES.pause).toEqual([
      pauseKeys.state,
      processingKeys.filesAtOnce,
    ]);
  });

  it("reads the Dashboard's workflows, files and read-outs again when the server says they changed", () => {
    expect(LIVE_TOPIC_QUERIES.files_at_once).toEqual([
      processingKeys.filesAtOnce,
    ]);
    expect(LIVE_TOPIC_QUERIES.maintenance).toEqual([
      processingKeys.maintenance,
    ]);
    expect(LIVE_TOPIC_QUERIES.libraries).toContain(processingKeys.libraries);
  });

  it("reads the libraries, files, every library overview and every library file list again when a scan changes", () => {
    expect(LIVE_TOPIC_QUERIES.library_scan).toEqual([
      processingKeys.libraries,
      processingKeys.files,
      processingKeys.allLibraryOverviews,
      processingKeys.allLibraryFiles,
    ]);
  });

  it("reads every workflow's folder check again when one answers differently", () => {
    expect(LIVE_TOPIC_QUERIES.folder_checks).toEqual([
      processingKeys.libraryFolderChains,
    ]);
  });

  it("refreshes the connection lists when a connection changes", () => {
    expect(LIVE_TOPIC_QUERIES.connections).toEqual([
      mediaManagerKeys.connections,
      downloadClientKeys.connections,
    ]);
  });

  it("refreshes the workflows, files at once, their profiles and the libraries setup offers when a workflow changes", () => {
    expect(LIVE_TOPIC_QUERIES.libraries).toEqual([
      processingKeys.libraries,
      processingKeys.filesAtOnce,
      processingKeys.ruleSets,
      processingKeys.allLibrarySuggestions,
    ]);
  });

  it("refreshes the jobs and the System log when a job changes", () => {
    expect(LIVE_TOPIC_QUERIES.jobs).toEqual([
      processingKeys.jobs,
      systemKeys.logEntriesAll,
    ]);
  });

  it("refreshes the server diagnostics when the counters move", () => {
    expect(LIVE_TOPIC_QUERIES.metrics).toEqual([settingsKeys.metrics]);
  });

  it("refreshes the update notice and the tray's network answer when the server says they changed", () => {
    expect(LIVE_TOPIC_QUERIES.update).toEqual([
      settingsKeys.updateStatus,
      settingsKeys.updateSettings,
      settingsKeys.updateState,
    ]);
    expect(LIVE_TOPIC_QUERIES.network_access).toEqual([
      settingsKeys.networkAccess,
    ]);
  });

  it("refreshes the app, alert, operator and runtime settings, and what they decide, when a setting changes", () => {
    expect(LIVE_TOPIC_QUERIES.settings).toEqual([
      settingsKeys.app,
      settingsKeys.notificationChannels,
      processingKeys.operatorSettings,
      processingKeys.runtimeSettings,
      processingKeys.filesAtOnce,
      processingKeys.maintenance,
    ]);
    expect(LIVE_TOPIC_QUERIES.maintenance).toEqual([
      processingKeys.maintenance,
    ]);
    expect(LIVE_TOPIC_QUERIES.files_at_once).toEqual([
      processingKeys.filesAtOnce,
    ]);
  });

  it("refreshes the backups, the kept files and the readiness checks on their own topics", () => {
    expect(LIVE_TOPIC_QUERIES.backups).toEqual([
      settingsKeys.configurationBackups,
    ]);
    expect(LIVE_TOPIC_QUERIES.kept_files).toEqual([processingKeys.keptFiles]);
    expect(LIVE_TOPIC_QUERIES.readiness).toEqual([systemKeys.readiness]);
  });
});

describe("parseDataChanged", () => {
  it("reads the topic of a data.changed frame", () => {
    expect(parseDataChanged('{"topic":"pause"}')).toBe("pause");
    expect(parseDataChanged('{"topic":"files_at_once"}')).toBe("files_at_once");
  });

  it("ignores a topic this app does not know, and anything that is not a frame", () => {
    expect(parseDataChanged('{"topic":"from_a_newer_server"}')).toBeNull();
    expect(parseDataChanged('{"topic":"toString"}')).toBeNull();
    expect(parseDataChanged('{"topic":4}')).toBeNull();
    expect(parseDataChanged("{}")).toBeNull();
    expect(parseDataChanged("not json")).toBeNull();
  });
});
