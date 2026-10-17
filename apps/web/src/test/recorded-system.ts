import recorded from "./recorded/system.json";
import type { RecordedReply } from "./recorded-weir";

/**
 * Replies a running Weir server gave to what System › About, Backups and Logs ask of it, kept word for word (status and
 * body text) in `recorded/system.json`. Each was recorded from a Weir server started on an empty data folder, signed in as
 * its administrator, with the media tools, the database or the job it names set up as the name says.
 */
export const systemReplies: Record<keyof typeof recorded, RecordedReply> =
  recorded;
