// One request to an application of the stack, as the scenarios and the setup make it.

/** One request to Deluno or qBittorrent: `{status, json, text}`, whatever the status. A connection that fails throws and names the request. */
export async function send(origin, { method, path, headers = {}, body, form, what }) {
  const requestHeaders = { Accept: "application/json", ...headers };
  let payload;
  if (form !== undefined) {
    requestHeaders["Content-Type"] = "application/x-www-form-urlencoded";
    payload = new URLSearchParams(form).toString();
  } else if (body !== undefined) {
    requestHeaders["Content-Type"] = "application/json";
    payload = JSON.stringify(body);
  }
  const response = await fetch(origin + path, { method, headers: requestHeaders, body: payload, signal: AbortSignal.timeout(60_000) }).catch((error) => {
    throw new Error(`${what} ${method} ${path} did not get an answer: ${error.cause?.code ?? error.cause?.message ?? error.message}`, { cause: error });
  });
  const text = await response.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }
  return { status: response.status, json, text, headers: response.headers };
}
