/**
 * The words a person sees when a request to Weir fails. Nothing here repeats a status code, a raw
 * response body or a field's code name: the server's own sentences are shown where it wrote them for
 * the operator, and everything else becomes a plain sentence that says what to do (#694, #696).
 */

export const SIGN_IN_ENDED_MESSAGE = "Your sign-in has ended. Sign in again.";
export const TOO_MANY_ATTEMPTS_MESSAGE =
  "Too many attempts. Wait a few minutes, then try again.";
export const NETWORK_UNREACHABLE_MESSAGE =
  "Can't reach Weir. Check it's still running.";
export const TIMED_OUT_MESSAGE = "Weir took too long to answer. Try again.";
export const GENERIC_FAILURE_MESSAGE = "Weir couldn't do that. Try again.";

const UNAUTHORIZED = 401;
const TOO_MANY_REQUESTS = 429;
const BAD_GATEWAY = 502;
const SERVICE_UNAVAILABLE = 503;
const FIRST_SERVER_ERROR = 500;

/** The `code` the server gives when a write gave up on a busy database. */
export const DATABASE_BUSY_CODE = "database_busy";

/**
 * The sign-in form's own path: a 401 there means the name or password was wrong, and the server's
 * reason says so. Anywhere else a 401 means the session has ended.
 */
const SIGN_IN_PATH = "/api/v1/auth/login";

/** Words in field names that are written in capitals in a label. */
const ACRONYMS: ReadonlySet<string> = new Set([
  "api",
  "id",
  "tv",
  "ui",
  "url",
  "utc",
]);

/** "new_password" or "newPassword" becomes "New password"; "api_key" becomes "API key". */
export function fieldLabel(name: string): string {
  const words = name
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .split(/[\s_.-]+/)
    .filter(Boolean)
    .map((word) => word.toLowerCase())
    .map((word) => (ACRONYMS.has(word) ? word.toUpperCase() : word));
  const text = words.join(" ");
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** Ends the text with a full stop unless it already ends a sentence. */
export function asSentence(text: string): string {
  const trimmed = text.trim();
  return /[.!?…]$/.test(trimmed) ? trimmed : `${trimmed}.`;
}

type IssueRule = {
  pattern: RegExp;
  say: (label: string, match: RegExpMatchArray) => string;
};

/** The server's validation wording, rewritten around the field's label. */
const ISSUE_RULES: readonly IssueRule[] = [
  { pattern: /^Field required$/, say: (label) => `${label} is required.` },
  {
    pattern: /^String should have at least 1 character$/,
    say: (label) => `${label} can't be empty.`,
  },
  {
    pattern: /^String should have at least (\d+) (characters?)$/,
    say: (label, m) => `${label} must be at least ${m[1]} ${m[2]}.`,
  },
  {
    pattern: /^String should have at most (\d+) (characters?)$/,
    say: (label, m) => `${label} must be at most ${m[1]} ${m[2]}.`,
  },
  {
    pattern: /^Input should be greater than or equal to (.+)$/,
    say: (label, m) => `${label} must be ${m[1]} or more.`,
  },
  {
    pattern: /^Input should be less than or equal to (.+)$/,
    say: (label, m) => `${label} must be ${m[1]} or less.`,
  },
  {
    pattern: /^Input should be a valid integer/,
    say: (label) => `${label} must be a whole number.`,
  },
  {
    pattern: /^Input should be a (finite|valid) number/,
    say: (label) => `${label} must be a number.`,
  },
  {
    pattern: /^List should have at least 1 item/,
    say: (label) => `${label} needs at least one entry.`,
  },
];

/** The server prefixes a model's own check with this; what follows is already a sentence for the operator. */
const MODEL_CHECK_PREFIX = "Value error, ";

const WHOLE_REQUEST_REFUSED =
  "Weir couldn't accept that. Check the details and try again.";

function issueField(loc: unknown): string | null {
  if (!Array.isArray(loc)) return null;
  const names = loc.filter(
    (part): part is string => typeof part === "string" && part !== "body",
  );
  return names.length > 0 ? names[names.length - 1] : null;
}

function issueSentence(issue: unknown): string {
  if (typeof issue !== "object" || issue === null) return WHOLE_REQUEST_REFUSED;
  const { loc, msg } = issue as { loc?: unknown; msg?: unknown };
  const message = typeof msg === "string" ? msg.trim() : "";
  if (message.startsWith(MODEL_CHECK_PREFIX)) {
    return asSentence(message.slice(MODEL_CHECK_PREFIX.length));
  }
  const field = issueField(loc);
  if (field === null) return WHOLE_REQUEST_REFUSED;
  const label = fieldLabel(field);
  for (const rule of ISSUE_RULES) {
    const match = message.match(rule.pattern);
    if (match) return rule.say(label, match);
  }
  return `${label} isn't valid.`;
}

/** One plain sentence per problem in a validation answer, without repeats. */
export function validationText(issues: readonly unknown[]): string {
  const sentences = [...new Set(issues.map(issueSentence))];
  return sentences.length > 0 ? sentences.join(" ") : WHOLE_REQUEST_REFUSED;
}

/**
 * The `code` the server put on its answer, when it gave one.
 */
export function codeOf(body: unknown): string | undefined {
  if (typeof body !== "object" || body === null || !("code" in body)) {
    return undefined;
  }
  const { code } = body as { code: unknown };
  return typeof code === "string" ? code : undefined;
}

/**
 * Whether the server's `detail` sentence is meant for the operator. Refusals (4xx) are, and so is a
 * media manager's failure relayed as 502, and so is a busy database's 503, which says how long to wait.
 * Weir's own failures (the rest of 5xx) carry text for its log, not for a person.
 */
function detailIsForTheOperator(status: number, body: unknown): boolean {
  return (
    status < FIRST_SERVER_ERROR ||
    status === BAD_GATEWAY ||
    (status === SERVICE_UNAVAILABLE && codeOf(body) === DATABASE_BUSY_CODE)
  );
}

/**
 * What to show for a request that came back with an error. `body` is the parsed JSON answer, when
 * there was one; `fallback` says what failed, for answers with nothing better to say.
 */
export function responseErrorText({
  path,
  status,
  body,
  fallback,
}: {
  path: string;
  status: number;
  body: unknown;
  fallback: string;
}): string {
  if (status === UNAUTHORIZED && path !== SIGN_IN_PATH) {
    return SIGN_IN_ENDED_MESSAGE;
  }
  if (status === TOO_MANY_REQUESTS) return TOO_MANY_ATTEMPTS_MESSAGE;
  const detail =
    typeof body === "object" && body !== null && "detail" in body
      ? (body as { detail: unknown }).detail
      : undefined;
  if (Array.isArray(detail)) return validationText(detail);
  if (
    typeof detail === "string" &&
    detail.trim() &&
    detailIsForTheOperator(status, body)
  ) {
    return asSentence(detail);
  }
  return asSentence(fallback);
}
