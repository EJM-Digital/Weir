# ADR-0013: A media manager is a kind, not a product name

## Status

Accepted — applies to every inbound event, outbound report, and stored connection
for a product that manages a media library.

> **Update (2026-08-28): Subber moved to Deluno.** This ADR is left as it was written — an ADR records the decision, not the current file list — but wherever it names Subber, read it as an example rather than as a lane that still exists. The ``subber_jobs`` table is dropped by migration ``0010_drop_subber_tables``, and ``subber.`` is now an abandoned prefix refused on every remaining lane, alongside ``trimmer.``.

> **Update (2026-10-15): search lanes retired.** A connection no longer has search lanes: nothing ever ran a search from one, and searching for missing files and upgrades belongs to the media manager. Migration ``0046_retire_unused_settings`` drops ``media_manager_search_lanes``, and the lane route only answers an older client. Read the paragraph on search lanes below as the decision as it was written.

## Context

Weir grew up alongside Radarr and Sonarr, so "the media manager" and "Radarr or
Sonarr" were the same statement. That assumption then hardened into the schema, the
routes, and the module layout:

- `arr_library_operator_settings` carried every connection and search-lane setting
  **twice**, once `sonarr_`-prefixed and once `radarr_` — roughly forty columns per
  product. A third manager meant a migration.
- `/arr-library/arr-connection/radarr` and `.../sonarr` were two routes, two schemas
  and two service functions doing one job.
- `/subber/webhook/radarr` and `.../sonarr` were two handlers whose only real
  difference was which key the file path hid under.
- `radarr_queue_adapter.py` and `sonarr_queue_adapter.py` were the same function
  twice. A comment claimed the duplication let each side "evolve alone"; in practice
  they never diverged.
- `resolve_radarr_http_credentials` / `resolve_sonarr_http_credentials` were the same
  duplication one layer down.

The cost was not the typing. It was that **a manager could not be added without a
schema change**, so none ever was — and a manager that behaves differently in kind
(one that hands work over and waits, rather than reporting work already finished)
had nowhere to exist at all.

## Decision

### 1. A connection is a row, and its `kind` is a field

`media_manager_connections` holds one row per configured manager: `kind`, name,
address, encrypted API key, its own inbound webhook secret, and its last test result.
Search lanes are rows in `media_manager_search_lanes`, so "missing" and "upgrade"
stop being column-name prefixes. Adding a manager is a POST.

### 2. One inbound endpoint, many payload dialects

`POST /api/v1/intake/webhook/{source}`. The source selects a **dialect** — how that
product phrases an event — and nothing else. A dialect is a function from a raw body
to a `MediaManagerImportEvent`. Adding a manager is a dialect, not a route, a job
kind, or a module.

### 3. What happens next is decided by the event, not the sender

Two event kinds, and they mean genuinely different things about who is waiting:

- **`imported`** — the manager has finished and the file is in the library. Subber's
  cue to look for subtitles.
- **`handoff`** — the manager has *not* finished. It wants the file cleaned first and
  is holding its import open until it hears back. Processing's cue.

A manager that does both gets both from the same endpoint. This is the distinction
that the vendor-named routes could not express.

### 4. A hand-off is a loan, so it must be reported back

Processing posts the outcome to the callback path the manager supplied, authenticated
with that connection's stored key. The origin rides on the job payload, so the report
survives a restart without a second table.

Two rules that follow, and are easy to get wrong:

- **`ok` alone does not mean "ready to import".** A guardrail skip is `ok` and produced
  no file. Only an outcome that actually wrote or verified one reports `completed`.
- **Reporting never raises and never fails the job.** A manager being down must not
  undo a remux that already succeeded on disk. Failures are logged and returned as a
  status string.

### 5. Ask the question you mean

Callers never wanted "Radarr" — they wanted *the manager that knows about movies*.
`resolve_manager_credentials(session, settings, media_scope=…)` asks that, so a
manager serving both scopes answers both without new code.

### 6. Secrets are per connection

One instance-wide webhook secret meant revoking one manager's access locked out all
of them. Each connection mints its own, stored encrypted and shown once.

## Consequences

- Adding a media manager is a dialect plus a settings entry. No migration.
- The vendor-named routes are **removed, not deprecated**: no instance had a manager
  connected to them, so there was nothing to keep working.
- Kinds still carry product knowledge where the wire format genuinely differs — a
  Radarr body really does nest under `movie`. The rule is that this knowledge lives
  in a dialect, never in a route, a column name, or a module boundary.
- Queue dialects are keyed by **media scope**, not vendor: a movie row nests under
  `movie` and identifies with `movieId` whoever sent it. Neutral keys (`media`,
  `entityId`) sit alongside the vendor ones.

## Out of scope

**Subber keeps its own connection storage** (`sonarr_base_url` and friends), so a
Sonarr address can currently be saved in two places. That is a known duplication,
deliberately left for a later change rather than folded in here.

## Related

- ADR-0007 — module-owned worker lanes
- ADR-0008 — settings aggregate runtime config
- Migration `0009_media_manager_connections`
