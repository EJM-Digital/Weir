# Processing and media-manager coverage

Processing's basic watched-folder remux is standalone. After a file has passed the
wait for the file to stop changing (the workflow's own), settling, access, schedule, and lifecycle checks, it can be processed
without Radarr, Sonarr, Deluno, or another manager.

Weir's Settings call each route a file takes (watched folder, work folder, output folder and
its rules) a **workflow**. There are two kinds, and both can run side by side:

- **Weir only (local folders)**: no manager or download client is involved.
- **Linked to a media manager** (Deluno, Sonarr or Radarr): the workflow is described in the source's own
  words. For Deluno that is its download client **category** and the Deluno **library** the file is
  imported into (its **Library folder**). For Radarr and Sonarr it is the download client
  **category** and the **root folder**.

The API and this document's field names still say "library" for a workflow (for example
`/api/v1/processing/libraries`), so existing integrations keep working. Weir's **Library** menu,
which cleans files already in place, keeps its name.

| Capability | Standalone | Radarr/Sonarr | Deluno | Native hand-off |
| --- | --- | --- | --- | --- |
| Watched-folder remux | Supported; local safety gates apply | Supported | Supported | Supported |
| Upstream import protection | Reduced safety: no upstream import check | Enhanced when the connection answers | Enhanced when the connection answers | Depends on the hand-off signal |
| Workflow discovery/sync | Manual workflows | Optional convenience | Optional convenience | Optional convenience |
| Destructive cleanup requiring manager truth | Safely held until Weir can confirm | Available when the manager answers | Available when the manager answers | Depends on the hand-off contract |
| Callback/hand-off | Not required | Integration-specific | Integration-specific | Supported where configured |

Each workflow reports one of three coverage states (`manager_coverage` on
`GET /api/v1/processing/libraries`):

- `connected`: the linked connection passed its latest test.
- `no_upstream_signal`: no manager is linked, or a linked manager has not yet
  returned a successful signal. This does not mean that its queue is empty.
- `unreachable`: a linked manager failed its latest connection test. Local
  remux remains possible, but manager-truth-dependent cleanup is held.

Setup › Workflows shows which manager each workflow is linked to, and a
warning when that manager did not answer its last check.

Connect or repair a manager from Setup › Connections › Media managers. A manually created
workflow remains valid and is never deleted or disabled merely because it has no
manager link.

## How each manager picks up Weir's output

- **Deluno** hands each finished download to Weir over its API and imports the
  cleaned file itself. A workflow only needs Deluno's folders: the one its
  downloads arrive in as the watched folder (a hand-off must sit inside it), and
  its processed-output folder as the output folder. The workflow editor reads
  both from Deluno's manifest and offers to fill them in. Where Deluno reaches a
  folder by another path (a NAS mount), a path mapping in Deluno (Settings ›
  Media Management › Processing Workflow › Weir › Path mappings) translates it.
  Deluno publishes where each download client saves and its path mappings for
  Weir (`GET /api/integrations/processors/download-destinations`, Deluno
  1.0.0-rc.23 or later), so Weir asks once per check for the workflow's own
  library and translates every folder it names through those mappings (the
  longest matching Deluno folder is replaced by its Weir folder) before comparing
  it with the workflow's folders. Each download client, the downloads folder and
  the processed-output folder then read as fine or needing a fix, and a client
  Deluno could not get an answer from stays not verified. Weir follows a junction
  or symbolic link after mapping, so the same folder reached two ways is not
  reported as a difference. The route needs a Deluno API key with the Imports
  permission (Deluno: System › API Access, the Media automation access). On a
  Deluno older than rc.23 Weir keeps showing the folders as not verified and says
  which release lets it check (a 404 with no body, or none that names a code); when
  Deluno answers 404 with the code library_not_found it has the route but has removed
  the library the workflow is linked to, and Weir says so and what to do, never that
  Deluno is too old; with a key that lacks the permission it says how to fix that. A hand-off is still accepted only when its path, as Deluno sends it,
  is inside the watched folder.

  A hand-off may carry an optional `sourceFiles`: an array of absolute file
  paths, as Weir sees them, each inside the hand-off's `sourcePath`. Deluno
  sends it when a release folder holds files under the workflow's minimum file
  size (`minimum_file_size_bytes` in `GET /api/v1/intake/library-folders`), and
  lists only the files at or above it. When it is present and not empty, the
  hand-off means those files and nothing else in the folder. Each listed file is
  taken the way a file in a folder hand-off is: a sample or a file that is not a
  video is left out, and the workflow's own rules then apply to it as they do to
  any handed-over file (minimum size and so on). The files that are not listed
  are not recorded, shown, reported as skipped or touched, so they stay in the
  download folder for seeding. A listed path that
  is outside `sourcePath` (after `.` and `..` are settled, ignoring case on
  Windows), or a file that is not there, refuses the whole hand-off with
  status 400 and a plain reason naming the file; Weir does not fall back to the
  whole folder. An absent, `null` or empty `sourceFiles` means the whole
  folder, as before.
- **Sonarr and Radarr** are set up by hand, the way FileFlows documents it: the
  download client finishes into Weir's watched folder, and a remote path mapping
  in Sonarr/Radarr (Settings › Download Clients › Remote Path Mappings) maps that
  folder to Weir's output folder, so Completed Download Handling only ever looks
  at cleaned files. Weir writes each output under the same relative path, name
  and extension as the download, and publishes it in one step. With a torrent
  client, turn off the workflow's "New downloads: after cleaning, delete the original download":
  Sonarr/Radarr only import a download the client reports as completed, and a
  torrent whose files Weir removed reports missing files instead. Kept originals
  are recognised by size and modification time and never cleaned twice. The workflow editor
  shows the exact Host, Remote Path and Local Path, and checks them against the
  saved connection with `GET` requests only; Weir never changes a manager's
  settings.
