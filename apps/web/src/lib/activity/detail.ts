/**
 * An Activity entry's `detail` is free text, or a JSON object when the event carries structured facts.
 * These read it without trusting its shape.
 */
export type ActivityDetail = Record<string, unknown>;

/** The detail as an object, or null when it is plain text or not valid JSON. */
export function parseActivityDetail(
  detail: string | null | undefined,
): ActivityDetail | null {
  if (!detail?.trim().startsWith("{")) return null;
  try {
    return JSON.parse(detail) as ActivityDetail;
  } catch {
    return null;
  }
}

/** A non-blank string, trimmed. */
export function asString(value: unknown): string | null {
  if (value == null) return null;
  const text = String(value).trim();
  return text ? text : null;
}

/** A finite number, including one written as a string. */
export function asNumber(value: unknown): number | null {
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (
    typeof value === "string" &&
    value.trim() !== "" &&
    Number.isFinite(Number(value))
  )
    return Number(value);
  return null;
}

/** A boolean, including "true" and "false" written as strings. */
export function asBoolean(value: unknown): boolean | null {
  if (typeof value === "boolean") return value;
  if (value === "true") return true;
  if (value === "false") return false;
  return null;
}
