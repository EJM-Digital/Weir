/**
 * The few words a card shows for a reason the server wrote as a sentence. The sentence stays where there is room
 * for it (the card's tooltip, Activity, the file's story); a card says what kind of wait or stop it is.
 *
 * The server words each reason in full and does not label its kind, so the kind is read from the wording it
 * uses. A reason none of these recognise reads as the plain state ("On hold"), never as a guess.
 */

const WAITING_TO_SETTLE = "Waiting to settle";
const LOOKING_AGAIN = "Looking again later";
const CANNOT_OPEN = "Can't open it yet";
const CANNOT_WRITE = "Can't write output";
const WAITING_FOR_SPACE = "Waiting for space";
const ON_HOLD = "On hold";
const PAUSED = "Paused";
const OUTSIDE_HOURS = "Outside its hours";

/** Weir could not open the file for reading; it is usually still being written by something else. */
const CANNOT_OPEN_REASON = /could not open this file/i;
const CANNOT_WRITE_REASON = /cannot write to the output folder/i;
const SPACE_REASON = /has less than .* free/i;
const BOOKED_LOOK_REASON = /another look at this file booked/i;
/** The file is new or still changing, so Weir leaves it until it has stopped. */
const SETTLING_REASON =
  /changed too recently|still growing|only just found|confirming that nothing|stopped changing|still being written|no other program is writing/i;

/** A file Weir could not read from start to finish yet, which is usually one still arriving. */
const NOT_READABLE_YET_REASON = /can.t read this file yet/i;

/**
 * Whether a hold is Weir waiting on its own, for a file to settle or for a look it has booked, and ends by itself. Such a
 * hold needs nobody, with or without a clock on it. A hold for a full drive or an output folder it cannot write to does.
 */
export function holdResolvesItself(reason: string): boolean {
  return (
    SETTLING_REASON.test(reason) ||
    BOOKED_LOOK_REASON.test(reason) ||
    NOT_READABLE_YET_REASON.test(reason)
  );
}

/** What an arriving file is waiting for, from the reason its hold gave. */
export function holdWords(reason: string): string {
  if (CANNOT_OPEN_REASON.test(reason)) return CANNOT_OPEN;
  if (CANNOT_WRITE_REASON.test(reason)) return CANNOT_WRITE;
  if (SPACE_REASON.test(reason)) return WAITING_FOR_SPACE;
  if (BOOKED_LOOK_REASON.test(reason)) return LOOKING_AGAIN;
  if (SETTLING_REASON.test(reason)) return WAITING_TO_SETTLE;
  return ON_HOLD;
}

const PAUSED_REASON = /^processing is paused/i;
const OUTSIDE_HOURS_REASON = /scheduled hours/i;

/** Why a file outside its schedule is not being worked on: Weir is paused, or the workflow's hours are closed. */
export function outOfScheduleWords(reason: string): string {
  if (PAUSED_REASON.test(reason)) return PAUSED;
  if (OUTSIDE_HOURS_REASON.test(reason)) return OUTSIDE_HOURS;
  return ON_HOLD;
}
