---
sidebar_position: 1
title: API Reference
---

# API Reference

Weir exposes a REST API from its .NET server. The API is served at `/api/v1` under the same origin as the web UI.

## OpenAPI specification

The OpenAPI document is a hand-maintained contract committed to the repository. The server embeds it and serves the operations it implements:

- **Running server**: `http://localhost:9347/openapi.json`
- **Source**: [`apps/web/openapi/weir-openapi.json`](https://github.com/jampat000/Weir/blob/main/apps/web/openapi/weir-openapi.json)

When you change an endpoint's request or response shape, update this file in the same change.

## Authentication

Weir uses cookie-based sessions with CSRF protection:

- First-run bootstrap creates the admin user
- Login via `POST /api/v1/auth/login`
- Session cookies are HTTP-only and secure (when behind HTTPS)
- State-changing requests require CSRF tokens

## Errors

Every error answer is JSON with a `detail` sentence. Where a client needs to act on which error it got, the answer also carries a `code`, a stable name that does not change when the wording does. A client reads the status and `code`, never `detail`.

| Status | `code` | Meaning | What to do |
|--------|--------|---------|------------|
| 503 | `database_busy` | Any route that writes: the database stayed locked for the whole time Weir waits for it (30 seconds unless `WEIR_SQLITE_BUSY_TIMEOUT_SECONDS` is set), for example while a backup tool holds it. The write was not made. | Send the same request again after the `Retry-After` header's seconds. That figure is how long the write really waited, from 1 to 60. |
| 409 | `connection_in_use` | `DELETE /api/v1/media-managers/connections/{connection_id}`: a workflow still comes from the connection, a hand-off from it is in progress, or Weir owes it a report. Nothing was removed. The answer also lists `workflows` (each with an `id` and `name`), `handoffs_in_progress` and `reports` (each owed report with its `handoff_id` and `name`). | Unlink the workflows with `POST /api/v1/media-managers/connections/{connection_id}/unlink-workflows`, or remove them with `DELETE /api/v1/processing/libraries/{library_id}`; let the hand-offs finish or cancel them; deliver the reports Weir owes, or give up on them with `POST /api/v1/media-managers/connections/{connection_id}/give-up-reports`; then send the removal again. |
| 409 | `handoffs_in_progress` | `POST /api/v1/media-managers/connections/{connection_id}/unlink-workflows`: a hand-off from the connection is in progress, so its workflows stay linked. Nothing was changed. | Let the hand-off finish or cancel it, then send the same request again. |
| 500 | `internal_error` | Weir hit a failure it did not foresee. The body says nothing of the failure; the details, with the stack trace, are in Weir's log. | The request may be sent again. If it fails the same way, report it with the log. |

The hand-off outcome route (`POST /api/v1/intake/handoffs/{source_key}/{handoff_id}/outcome`) also answers 409 with `outcome_already_recorded`, `handoff_not_finished` or `handoff_ended`; its description in the OpenAPI document says which can be sent again.

## Key endpoints

### Health and readiness

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/health` | GET | No | Process liveness check |
| `/ready` | GET | No | Full readiness (DB, migrations) |
| `/metrics` | GET | Token | Prometheus metrics |

### Processing

The API still says "library" for what the app calls a workflow, so existing integrations keep working.

| Prefix | Description |
|--------|-------------|
| `/api/v1/processing/libraries` | Workflows and their order, rule previews, and the files already in each workflow's library folders: their setup (folders, rules profile, daily clean, originals), scan, clean, leave alone, schedule |
| `/api/v1/processing/files` | Files Weir has picked up: tracks, logs, requeue, move to top, why a file is held |
| `/api/v1/processing/rule-sets` | Audio and subtitle rules |
| `/api/v1/processing/jobs` | Queue and inspect background jobs |
| `/api/v1/processing/maintenance` | Cleanup settings, and running cleanup now |
| `/api/v1/processing/metadata-provider` | Deprecated. Weir gets posters and original languages from Deluno's metadata service by itself, so there is nothing to set; the route still answers so older clients keep working |
| `/api/v1/artwork/posters/` | A poster image Weir already holds, as a file's `poster_url` names it. See [Posters](../guides/posters) |
| `/api/v1/processing/` (other) | Performance and runtime settings, hardware, direct-play devices, media manager setup checks, overview counts |
| `/api/v1/pause` | The one pause switch for processing |

### Media managers

| Prefix | Description |
|--------|-------------|
| `/api/v1/media-managers/` | Connections to Sonarr, Radarr, Deluno and other tools: add, test, searches, webhook secret, unlink their workflows, give up on reports Weir owes them, remove (refused while anything still depends on the connection) |
| `/api/v1/intake/` | Hand-offs from media managers: the webhook, each hand-off's status, and its outcome |

### Platform

| Prefix | Description |
|--------|-------------|
| `/api/v1/auth/` | First-run bootstrap, sign-in and sign-out, CSRF token, password and username changes, sessions |
| `/api/v1/activity/` | Events and per-file history, the live event stream, export |
| `/api/v1/suite/` | App settings, alert channels, backups and the configuration bundle, updates, logs, metrics, security overview |
| `/api/v1/system/` | Runtime directories, media tools, signed-in readiness, reconciliation report and repair |

## TypeScript types

The frontend generates TypeScript types from the committed OpenAPI document:

```bash
cd apps/web
npm run api:types:generate
```

This produces typed API clients in `apps/web/src/lib/api/generated/openapi-types.ts`. CI runs `npm run api:types:check` to make sure the generated types match the document.
