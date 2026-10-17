// Whether a CSRF token Weir issued was signed with a given session secret. The server signs with HMAC-SHA1 under a key derived from the
// secret (apps/server/src/Weir.Core/Security/CsrfTokens.cs, TimestampSigner); a scenario that reads session.secret from disk and finds
// the token verifies against it has shown the running server was started with that file's secret.
import { createHash, createHmac, timingSafeEqual } from "node:crypto";

const SIGNER_SALT = "weir-csrf-v1";

const urlSafe = (bytes) => bytes.toString("base64").replaceAll("=", "").replaceAll("+", "-").replaceAll("/", "_");

/** True when the token's signature is the one `secret` makes: `value.timestamp.signature`, the age is not judged. */
export function csrfSignedWith(token, secret) {
  const cut = token.lastIndexOf(".");
  if (cut < 0 || secret.length === 0) return false;
  const key = createHash("sha1").update(`${SIGNER_SALT}signer${secret}`, "utf8").digest();
  const expected = Buffer.from(urlSafe(createHmac("sha1", key).update(token.slice(0, cut), "utf8").digest()));
  const given = Buffer.from(token.slice(cut + 1));
  return given.length === expected.length && timingSafeEqual(given, expected);
}
