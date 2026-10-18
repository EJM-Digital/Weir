// Reading tray-host.log, the tray's one record of what it did. Every line is `[yyyy-MM-dd HH:mm:ss] message`; the scenarios take a mark (the
// length of the log) before they act and read only what was written after it.

/** The lines of the log after the mark, without their time stamps. */
export function linesAfter(text, mark = 0) {
  return text
    .slice(mark)
    .split(/\r?\n/)
    .filter((line) => line.trim())
    .map((line) => line.replace(/^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] /, ""));
}

/** The first line that holds the text, or matches the pattern, or undefined. */
export function lineWith(lines, wanted) {
  return lines.find((line) => (wanted instanceof RegExp ? wanted.test(line) : line.includes(wanted)));
}

/** Every line that holds the text or matches the pattern. */
export function linesWith(lines, wanted) {
  return lines.filter((line) => (wanted instanceof RegExp ? wanted.test(line) : line.includes(wanted)));
}

/** The process ids the tray started a server as, in order. */
export function serverProcessIds(lines) {
  return lines.flatMap((line) => {
    const found = /^Bundled server host pid=(\d+)$/.exec(line);
    return found ? [Number(found[1])] : [];
  });
}

/**
 * The failed starts the supervision logged: the attempt, why it failed, and the wait before the next one (null when it gave up or the server
 * said why it cannot start).
 * @returns {{attempt: number, reason: string, waitSeconds: number|null, failures: number|null, gaveUp: boolean}[]}
 */
export function failedStarts(lines) {
  return lines.flatMap((line) => {
    const found = /^Start attempt (\d+) failed: (.*?)\. (Trying again in ([\d.]+) s \((\d+) of \d+ failures allowed within [\d.]+ min\)\.|Giving up \((\d+) of \d+ failures)/.exec(line);
    if (!found) return [];
    const waiting = found[4] !== undefined;
    return [
      {
        attempt: Number(found[1]),
        reason: found[2],
        waitSeconds: waiting ? Number(found[4]) : null,
        failures: Number(waiting ? found[5] : found[6]),
        gaveUp: !waiting,
      },
    ];
  });
}

/**
 * The wait before attempt n+1 is "equal jitter" on a ceiling that doubles from 2 s: half the ceiling and a random share of the other half,
 * so it lies between ceiling/2 and ceiling, to the tenth of a second the log prints. Null when it does.
 */
export function backoffProblem(failureNumber, waitSeconds, { initialSeconds = 2, multiplier = 2, maxSeconds = 60 } = {}) {
  const ceiling = Math.min(maxSeconds, initialSeconds * multiplier ** (failureNumber - 1));
  const low = ceiling / 2 - 0.05;
  const high = ceiling + 0.05;
  return waitSeconds >= low && waitSeconds <= high ? null : `wait ${waitSeconds} s after failure ${failureNumber} is outside ${ceiling / 2}-${ceiling} s`;
}
