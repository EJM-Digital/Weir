# ADR-0014: A Processing library is a row, not one of two fixed scopes

## Status

Accepted — applies to every Processing path, rule, guardrail, schedule and job payload
that partitioned on `media_scope`.

> **Naming.** Weir's Settings now call this row a **workflow**, because in Deluno, Radarr and Sonarr
> a "library" is where media ends up. The API, the database and this record keep the word "library".

## Context

Processing is configured by exactly **two scopes**, `movie` and `tv`, and they are not
data. They are the shape of the schema:

- `processing_path_settings` is one singleton row (`id = 1`) holding
  `processing_watched_folder`, `processing_work_folder`, `processing_output_folder` and then
  the same three again as `processing_tv_*`, plus two poll intervals.
- `processing_remux_rules_settings` is one singleton row holding ten fields, then the
  same ten again `tv_`-prefixed.
- `operator_settings` holds the guardrails — files at once, minimum file age,
  minimum input size, minimum free disk — **globally**, shared by both scopes, plus a
  schedule window per scope.

This is the same shape ADR-0013 found in `arr_library_operator_settings` and removed:
every setting written twice, once per hardcoded product. The cost is the same too. It
is not the typing. It is that **a third path cannot exist without a migration**, so
none ever has.

Concretely, an operator who wants a 4K library processed with different audio rules, or
a kids library with a different schedule, or a staging folder that is checked but never
mutated, cannot have any of it. They get one movie folder and one TV folder.

Three further consequences that only became visible while designing the surrounding
work:

**Admission rules are frozen in source.** `_MEDIA_EXTENSIONS` is five extensions in
`processing_remux_rules.py`; `_TRANSIENT_DOWNLOAD_DIR_MARKERS` is a fixed set of
downloader-staging markers in the scan ops module. Neither is reachable by an operator,
and neither can differ between libraries that genuinely need different answers.

**The media manager is inferred from the scope.** `credentials.py` maps
`{"movie": "radarr", "tv": "sonarr"}` and falls back to a general kind. ADR-0013 already
made a connection a row with a `kind`; this inference is the last place that treats the
mapping as fixed. It breaks the moment a manager serves both scopes — Deluno does — or
two connections could serve the same one.

**The manager already knows the answer.** Deluno publishes
`GET /api/integrations/external/manifest` whose documented purpose is to say which
libraries exist and which roots are configured. Weir asks the operator to retype it,
and can only accept two of them.

## Decision

### 1. A library is a row

`libraries` holds one row per library. `media_scope` becomes a **property of a
library** — it still selects the movie or TV cleanup behaviour — rather than the key the
whole module partitions on.

Each row carries what is today spread across three singletons:

| Group | Fields |
|---|---|
| Identity | name, enabled, media scope, display order |
| Paths | watched, work, output |
| Admission | extensions, include patterns, exclude patterns, min/max size, min age, created and modified windows |
| Timing | scan interval, hold minutes, file-detection interval, schedule |
| Capacity | max concurrent files, priority |
| Manager | connection reference(s) |

### 2. Admission rules are data on the row, not constants in a module

`_MEDIA_EXTENSIONS` and `_TRANSIENT_DOWNLOAD_DIR_MARKERS` become that library's saved
values, seeded from today's constants so nothing changes on upgrade. The hash-stem
staging heuristic stays built in — it is a shape, not a preference — but the marker list
is editable.

### 3. Rules are a named object a library points at

Not inline. Two libraries that want identical audio and subtitle handling should share
one rule set, and changing it once should change both. Inline is simpler for exactly one
library, which is the situation this ADR exists to end.

A library references a rule set; a rule set is not owned by any library. Deleting a rule
set still referenced is refused.

### 4. A library names its manager connection explicitly

The `{"movie": "radarr", "tv": "sonarr"}` inference is retired. A library states which
media manager connection(s) cover it.

**Multiple connections are permitted and are the edge case.** A library may name more
than one — two Radarr instances, or Radarr alongside Deluno during a migration — in
which case Processing asks all of them and treats an in-progress import reported by *any*
as blocking. The UI defaults to one and must not make the common case harder to
configure.

This is also what makes library discovery coherent: a library imported from a manager
arrives already knowing which manager it came from.

### 5. Job payloads gain a library reference as an additive field

`processing.file.remux_pass.v1` and the scan dispatch family keep their job kind. A
`library_id` is added alongside the existing `media_scope`, and a payload without one
resolves to the seeded library for its scope.

A new job-kind version is rejected: a version bump would strand every row already queued
at upgrade time, and the queue is exactly where a half-finished remux lives. The
additive field costs one fallback branch and strands nothing.

### 6. Upgrade is a behavioural no-op

The migration seeds two libraries, "Movies" and "TV", from the existing singleton rows,
carrying every current value including the hardcoded extension and marker lists as saved
defaults. It links each to whichever manager connection the current inference would have
chosen.

**No operator reconfiguration on upgrade.** This is the hard constraint the design is
answerable to, not a nice-to-have.

## Consequences

- Adding a library is a POST. No migration.
- Every guardrail currently global becomes per-library. `operator_settings`
  keeps only what is genuinely suite-wide.
- The singleton path and rules tables are dropped once nothing reads them, in a later
  migration than the one that seeds from them, so a rollback has somewhere to land.
  Done in `0025`. Its downgrade recreates them empty and says so: the values live on the
  libraries by then, and re-seeding stale paths into a table nothing reads would be worse
  than an empty one, because a later re-upgrade would carry them back across.
- Deleting a library must be guarded: it may not orphan queued or leased jobs.
- Discovery ([#351]) and the media manager port ([#350]) both depend on this shape, and
  neither is in scope here.

## Relationship to existing ADRs

**ADR-0012 (Processing preflight parity boundary) is untouched.** That ADR bounds parity
work to probe depth and observability, and requires a follow-up ADR for anything beyond.
Nothing here is preflight depth — this is configuration ownership and storage — so its
boundary neither constrains this decision nor is loosened by it. `probe_size_mb` and
`analyze_duration_seconds` stay on `WeirSettings` per ADR-0008.

**ADR-0013 (a media manager is a kind) is completed, not changed.** That ADR made a
connection a row with a kind and stated the principle that callers want *the manager that
knows about movies*, not Radarr. Processing never adopted it. Decision 4 above is that
adoption. What a connection *is* does not change; who consults it, and how it is chosen,
does.

**ADR-0009 (suite-wide timing isolation) is respected, and a library schedule is not a
second timing authority.** ADR-0009 governs *when a job family ticks*. A library schedule
governs *whether a file in that library is eligible right now*. A family still owns its
own interval and its own persisted timing state; the library schedule is an eligibility
predicate the family consults, in the same way it already consults `movie_schedule_*`
today. Where a family ticks for several libraries, it keeps one timing state per family
per scope as ADR-0009 requires, and evaluates eligibility per library inside the tick.

**ADR-0007 (module-owned worker lanes) is unchanged.** Libraries do not add a lane, a
prefix, or a queue. Everything stays on `jobs` under `processing.*`.

## Out of scope

- The media manager port itself ([#350]) — the dialects, the fan-out, and the
  no-queue-signal outcome. This ADR only settles that a library names its connections.
- Library discovery from a manager ([#351]).
- Whether Pruner adopts the same library shape. It had its own instance model and its
  own constraints; folding the two was a separate decision that should not be smuggled
  in here. (Pruner has since moved to Deluno, #473, and its constraints document went
  with it.)

## Related

- [ADR-0007](ADR-0007-module-owned-worker-lanes.md) — module-owned worker lanes
- [ADR-0008](ADR-0008-weir-settings-aggregate-runtime-config.md) — settings aggregate
- [ADR-0009](ADR-0009-suite-wide-timing-isolation.md) — suite-wide timing isolation
- [ADR-0012](ADR-0012-processing-preflight-parity-boundary.md) — preflight parity boundary
- [ADR-0013](ADR-0013-media-managers-are-kinds-not-products.md) — a media manager is a kind
- Execution plan (archived): [`docs/archive/processing-library-model.md`](../archive/processing-library-model.md)

[#350]: https://github.com/EJM-Digital/Weir/issues/350
[#351]: https://github.com/EJM-Digital/Weir/issues/351
