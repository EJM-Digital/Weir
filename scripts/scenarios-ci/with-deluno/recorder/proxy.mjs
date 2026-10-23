// The recording proxy of the with-deluno shard. Weir is given this as Deluno's address and Deluno as the place Weir's hand-off route is, so every
// call either makes of the other passes through here, is forwarded unchanged and is written down. Nothing here is a product: it is a pipe that
// keeps what went through it.
//
//   :TO_DELUNO_PORT   forwards to Deluno          (Weir calls Deluno: the manifest, health, queue, library folders, download destinations, its reports)
//   :TO_WEIR_PORT     forwards to Weir            (Deluno calls Weir: hand-offs, status asks, cancels, outcomes, the capabilities ask)
//   :CONTROL_PORT     GET /exchanges?after=N      every exchange with a number above N, in order, as { next, exchanges }
//                     GET /health
import http from "node:http";
import { fileURLToPath } from "node:url";

import { exchangeOf } from "./exchange.mjs";

const HOP_BY_HOP = new Set(["connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te", "trailer", "host", "content-length"]);

function readBody(stream) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    stream.on("data", (chunk) => chunks.push(chunk));
    stream.on("end", () => resolve(Buffer.concat(chunks)));
    stream.on("error", reject);
  });
}

/** Forwards to one upstream and writes every exchange to `log`. */
function forwarder({ route, target, log, nextSeq }) {
  const upstream = new URL(target);
  return http.createServer(async (request, response) => {
    const requestBody = await readBody(request);
    const headers = Object.fromEntries(Object.entries(request.headers).filter(([name]) => !HOP_BY_HOP.has(name)));
    headers["accept-encoding"] = "identity";
    if (requestBody.length > 0) headers["content-length"] = String(requestBody.length);
    const seq = nextSeq();
    const at = new Date().toISOString();
    const seen = { seq, route, at, method: request.method, path: request.url, request: request.headers, requestBody };
    const forwarded = http.request({ hostname: upstream.hostname, port: upstream.port, method: request.method, path: request.url, headers, agent: false }, async (reply) => {
      const replyBody = await readBody(reply);
      const replyHeaders = Object.fromEntries(Object.entries(reply.headers).filter(([name]) => !HOP_BY_HOP.has(name)));
      response.writeHead(reply.statusCode, { ...replyHeaders, "content-length": String(replyBody.length) });
      response.end(replyBody);
      log.push(exchangeOf({ ...seen, status: reply.statusCode, reply: reply.headers, replyBody }));
    });
    forwarded.on("error", (error) => {
      const text = Buffer.from(`The recording proxy could not reach ${route === "to-deluno" ? "Deluno" : "Weir"}: ${error.code ?? error.message}`);
      response.writeHead(502, { "content-type": "text/plain", "content-length": String(text.length) });
      response.end(text);
      log.push(exchangeOf({ ...seen, status: 502, reply: { "content-type": "text/plain" }, replyBody: text }));
    });
    forwarded.end(requestBody);
  });
}

/** The control server: what has been recorded, after the number a caller last saw. */
function control(log) {
  return http.createServer((request, response) => {
    const url = new URL(request.url, "http://recorder");
    if (url.pathname === "/health") {
      response.writeHead(200, { "content-type": "text/plain" });
      return response.end("ok");
    }
    if (url.pathname === "/exchanges") {
      const after = Number(url.searchParams.get("after") ?? 0);
      const exchanges = log.filter((exchange) => exchange.seq > after).sort((a, b) => a.seq - b.seq);
      response.writeHead(200, { "content-type": "application/json" });
      return response.end(JSON.stringify({ next: exchanges.at(-1)?.seq ?? after, exchanges }));
    }
    response.writeHead(404, { "content-type": "text/plain" });
    response.end("not here");
  });
}

/**
 * Starts the three servers. An exchange is logged once its reply is complete, so the log is in the order replies finished; each carries the
 * number it was given when its request arrived.
 */
export async function startRecorder({ deluno, weir, ports, host }) {
  const log = [];
  let counter = 0;
  const nextSeq = () => ++counter;
  const servers = [
    [forwarder({ route: "to-deluno", target: deluno, log, nextSeq }), ports.toDeluno],
    [forwarder({ route: "to-weir", target: weir, log, nextSeq }), ports.toWeir],
    [control(log), ports.control],
  ];
  await Promise.all(servers.map(([server, port]) => new Promise((resolve) => server.listen(port, host, resolve))));
  return { servers: servers.map(([server]) => server), log };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const ports = { toDeluno: Number(process.env.RECORDER_TO_DELUNO_PORT ?? 8081), toWeir: Number(process.env.RECORDER_TO_WEIR_PORT ?? 8082), control: Number(process.env.RECORDER_CONTROL_PORT ?? 8083) };
  await startRecorder({ deluno: process.env.RECORDER_DELUNO ?? "http://deluno:8080", weir: process.env.RECORDER_WEIR ?? "http://weir:9347", ports });
  console.log(`recording proxy: Deluno at :${ports.toDeluno}, Weir at :${ports.toWeir}, control at :${ports.control}`);
}
