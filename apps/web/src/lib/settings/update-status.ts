import type { StatusMeaning } from "../ui/status-meaning";

const UPDATE_MEANING: Readonly<Record<string, StatusMeaning>> = {
  checking: "doing",
  up_to_date: "done",
  update_available: "todo",
  downloaded: "todo",
  not_published: "idle",
  rate_limited: "attention",
  off: "idle",
  unavailable: "attention",
};

/**
 * What an update check's status means: a newer version waiting is something to do, not a fault; a check that could not be
 * made, or that GitHub is limiting for now, is for a person to look at.
 */
export function updateMeaning(status: string): StatusMeaning {
  return UPDATE_MEANING[status] ?? "attention";
}
