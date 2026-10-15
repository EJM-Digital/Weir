---
sidebar_position: 5
title: Connecting Deluno, Sonarr and Radarr
---

# Connecting Deluno, Sonarr and Radarr

Under **Setup › Connections › Media managers**, Weir can connect to the tools that manage your downloads and
library, so cleaning happens automatically instead of you moving files around by hand.

There are four kinds of connection: **Deluno**, **Sonarr**, **Radarr**, and **Something else** for
anything that can send Weir a message directly.

## Two kinds of workflow

A **workflow** is one route a file takes through Weir: a watched folder, a work folder, an output
folder, and the rules and schedule that apply. You manage them under **Setup › Workflows**.
There are two kinds, and both can run side by side in one Weir:

- **Weir only (local folders).** Weir watches a folder you choose and writes the cleaned file to
  another. No manager or download client is involved.
- **Linked to a media manager (Deluno, Sonarr or Radarr).** The workflow's watched folder is where
  the download client finishes files, and its output folder is where the manager imports from. Weir
  describes the link in the source's own words:
  - **Deluno:** the download client **category** the file comes from, and the Deluno **library**
    (with its **Library folder**) it is imported into.
  - **Radarr and Sonarr:** the download client **category**, and the **root folder** the file is
    imported into.

Whatever the manager, a linked workflow never deletes or moves the original download: it belongs to
your download client and the manager, and the client may still be seeding it. See [How Weir keeps
your files safe](file-lifecycle.md#deleting-the-original).

For example, a linked workflow reads like this: comes from Deluno's download client, category
**movies** → Weir works in its work folder → cleaned into the output folder → Deluno imports it
into its library **Movies**. A local one reads: watches `D:\Kids\Incoming` → works in its work
folder → cleaned into `D:\Kids\Ready`.

Each row in **Setup › Workflows** carries a badge, **Weir only** or **Linked to Deluno** (or Sonarr,
or Radarr). **Add workflow** asks which kind first: **Local folders**, or **From a media manager**,
which asks the manager for its folders and opens the editor filled in and linked. In an existing
workflow's **Media manager** section, **Link to a media manager** and **Unlink** change the kind; the
change applies when you save. A bare download client on its own only suggests a watched folder, so it
never makes a workflow linked. Under **Setup › Connections › Media managers**, each connection lists the
workflows it feeds and offers **Add a workflow from** it.

Weir's **Library** menu is a separate thing. It cleans files that are already in place in your
media library, and it keeps its name; the folders, rules profile and daily clean for those files are
set up there, not on the workflow. A workflow is the path new files take.

The API still says "library" for what the app calls a workflow (for example the `library-folders`
capability below), so existing integrations keep working.

You don't name a connection. Weir names it after the kind and the host in its address: "Deluno on
my-pc", "Radarr on nas", "qBittorrent on 192.0.2.51". Two of one kind on the same host also show their
port, like "Radarr on nas (7879)". Change the address and the name follows.

To tell two connections apart at a glance, give one a **Nickname (optional)** when you add it or
under **Edit**: up to 30 characters, such as `4K`. Weir shows it after the name wherever the
connection appears, including in alerts and connection tests: "Radarr on nas · 4K". A nickname never
replaces the name Weir derives, and clearing it leaves just the name. A backup carries the nickname of
each media manager, and a restore adds it to the connections it creates; it still matches your
existing connections by kind and address, and leaves their nicknames as they are. Download clients
are not part of a backup.

## How the folders fit together

However your setup is arranged, three things own three different folders, and every install works
as long as each one sticks to its own:

- **The download client** (SABnzbd, NZBGet, qBittorrent, Deluge, Transmission…) owns the
  incomplete folder and the completed folder for each category.
- **Weir** owns the watched folder (where it picks files up), the work folder (its private area
  while cleaning), and the output folder (where the cleaned copy goes). The work folder should sit
  on the same drive as the output folder, so a finished file can be moved into place instead of
  copied — it's allowed to be on a different drive, but Weir will tell you when it is.
- **The media manager** (Sonarr, Radarr, Deluno) owns where media ends up (Deluno's library folders,
  Radarr's and Sonarr's root folders) and the download client category each one uses.

A download client's completed folder for a category should be the same folder Weir watches for
that workflow. Weir's output folder is the one the manager reads cleaned files back from — either
directly (Sonarr/Radarr's remote path mapping) or through Deluno's hand-off.

This is what **Setup › Workflows**' compact **Folder chain** section checks for every workflow
(only against the managers that workflow is linked to), and what **Setup › Connections › Media managers** checks per
connection: whether the watched folder exists and
can be listed and read, whether the work folder is set, readable and on the same drive as the output folder,
whether the output folder exists and can be listed, read and written, and — with a manager connected — whether its download-client
or category folders map onto the folder Weir watches. A workflow with no manager connected is shown
as a complete, valid Weir-only setup; there's nothing to warn about.

Weir also publishes each workflow's folders as a read-only API a manager can read instead of you
retyping them by hand (advertised as the `library-folders` capability at `/api/v1/intake/`), and it
can read a connected manager's or download client's own configuration and offer its folders as a
one-click suggestion in the workflow editor. Weir never changes a folder on its own — a suggestion is
only ever applied when you press the button, and a folder you typed yourself always stays.

Each workflow in that list has an `id`, `name`, `media_type`, `watched_folder`, `work_folder` and
`output_folder`. A workflow Weir set up from a media manager also carries that manager's own id for the
library in `manager_library_key`, so the manager can find its entry; it is `null` for a workflow that was
never set up from a manager, or has been unlinked. `folders_from_manager` is `true` while the manager still
owns the workflow's watched and output folders (only Deluno does today), and `false` otherwise.

The same `/api/v1/intake/capabilities` answer carries `product` (always `Weir`) and `machine_name`, the
name of the machine Weir runs on, so a manager can call its connection "Weir on my-pc" without knowing
which processor it talks to.

## Deluno: automatic hand-off

Deluno hands a file to Weir to work on, and waits to be told it's ready. This is the fully
automatic setup: Deluno tells Weir about a new file, Weir cleans it, and Deluno is told when the
cleaned copy is ready to import. You don't move anything by hand.

Weir cleans each downloaded file once. If the same file is handed over again, or you choose "Process
again" on a file Weir has already cleaned, Weir does not process it a second time: Activity shows
"Skipped: already done" (or "Skipped: already imported" once Deluno has collected the cleaned copy),
and Deluno is told the file is ready, with the same cleaned copy as before, never that it failed. A
download that has changed, or a different release, is processed as usual.

### Deluno: Weir sets up its workflows from it

You don't type any folders. Once Deluno is connected, Weir sets up a workflow for each Deluno library that is set to
**Refine before import**, and keeps it in step with Deluno. It does this when the connection is saved or its address or key
changes, after a connection test that passes, and every few minutes after that, so a folder you change in Deluno reaches
Weir without anyone opening Weir.

For each such library Weir makes sure one workflow is linked to it:

- If a workflow is already linked to the library, Weir updates it.
- Otherwise, if there is an unconfigured workflow of the same media type (no watched folder, no output folder, not linked:
  the **Movies** and **TV** workflows a new install starts with), Weir uses that one and gives it the library's name.
- Otherwise Weir makes a new workflow, named after the library.

What Weir fills in is the **watched folder**, the **output folder**, the **media type**, the **name** and the link to
Deluno. A new workflow gets Weir's default rules profile for its media type, as any new workflow does.

- **Watched folder.** Deluno's own default is no downloads folder at all ("use the download client's folder"), so Weir asks
  where each of the library's download clients really saves and uses that. One folder is used as it is. When the clients
  save to several folders, Weir uses the folder that holds them all (never a drive or filesystem root) and otherwise the first
  one; the **Folder chain** then reports the clients that save elsewhere. Only when no client names a folder does Weir use
  the library's downloads folder. If Deluno gives neither, Weir leaves the watched folder empty rather than guess, and says
  so in **Activity** and in the Folder chain: set the downloads folder in Deluno (or the clients' category folders) and Weir
  picks it up on its next look.
- **Output folder.** The processed folder Deluno imports from.
- Both folders are turned into Weir's view through Deluno's path mappings for Weir (see below). With a Deluno older than
  1.0.0-rc.23, which publishes no mappings, the folders are used as Deluno writes them.

Deluno owns those two folders for a workflow linked to it. **Setup › Workflows** shows them but doesn't let you change them
("From Deluno; change it in Deluno."), and Weir rewrites them when Deluno's change, saying so in Activity ("Movies' watched
folder updated from Deluno"). Everything else stays yours: the **work folder**, the rules profile, the schedule and every other
setting. **Unlink** a workflow and its folders are yours again and Weir stops updating it; Weir never deletes a workflow. If a
library stops being set to Refine before import, or disappears from Deluno, its workflow is left as it is and the Folder chain
says why. If the folders Deluno reports would overlap another workflow's folders, Weir leaves the workflow alone and says so in
Activity.

Two things stay manual:

- **Weir's own work folder.** Deluno doesn't know it. Leave it empty and Weir uses a private folder under its own data
  folder; set it in the workflow when you want it on a particular drive.
- **Sonarr, Radarr and Something else.** They can't tell Weir where their folders are, so their workflows are set up by hand,
  as described below.

To stop Weir setting workflows up from Deluno, set `WEIR_MEDIA_MANAGER_WORKFLOW_SYNC_ENABLED=0`.

A hand-off names its file by the path Deluno sees, and Weir accepts it only when that path is inside the watched folder.
Deluno's path mappings for Weir (Settings › Media Management › Processing Workflow › Weir › Path mappings) turn its paths
into Weir's, and the workflows Weir sets up use them.

A workflow linked to Deluno is fed **only** by Deluno's hand-off. Weir does not scan its watched folder:
there is no scheduled scan, no scan when the folder changes, and no **Scan now** (Setup › Workflows ›
Schedule says "Deluno hands this workflow its downloads"). That way Weir can never pick up a download
that is still being fetched or seeded before Deluno asks for it. If a hand-off fails, Weir retries it
on its own, as the workflow's retry settings say. Weir also never deletes the original download of
this workflow, even with **After cleaning, delete the original download** on: the option shows as off
and says why.

### Deluno: checking where downloads land

Deluno 1.0.0-rc.23 and later publish, for each library, where every download client really saves its
finished downloads and how Deluno's own path mappings for Weir translate its folders into Weir's. Weir reads this
whenever it checks a workflow linked to Deluno (once per check, for that workflow's library only) and
translates each folder Deluno names through those mappings, longest matching folder first, before comparing
it with the workflow's folders. A folder that leads to the same place through a junction, symbolic link or mount
counts as the same folder, though a hand-off is still accepted only when the path Deluno sends is inside the watched
folder. The **Folder chain** then says, line by line:

- **Each download client**: a ✓ saying which client and category save inside the watched folder, or **Needs a fix**
  naming the client, the category or label, the folder it saves to, the watched folder and what to change. If Deluno
  itself reports a problem with a client (for instance a remote client no path mapping covers), Weir shows Deluno's
  own message. If Deluno could not get an answer from a client, that line stays **Not verified**.
- **The downloads folder**: a ✓ when it is inside the watched folder once mapped, otherwise **Needs a fix**. The fix
  names Deluno's real path mappings, or says that none covers the folder and which mapping to add.
- **The processed-output folder**: a ✓ when it is the workflow's output folder once mapped, otherwise **Needs a fix**.

The Deluno block and the download client block on the same page therefore agree: when Deluno says a category
saves inside the watched folder, that is what both show.

This needs two things from Deluno. It has to be **1.0.0-rc.23 or later**: an older Deluno has no such route,
so Weir keeps the earlier, not verified lines and adds a short note naming that version. And the API key Weir uses
for Deluno needs the **Imports** permission: in Deluno open **System › API Access** and create a key with
**Media automation** access (it includes Imports), then save that key under **Setup › Connections › Media managers** in Weir.
With a key that lacks it, the line says so and the rest stays not verified. If the Deluno library a workflow was
created from no longer exists, the line says that instead of checking another library.

## Sonarr and Radarr: checking before Weir touches a file

Connecting Sonarr or Radarr lets Weir check what they're still importing before it acts on a file.
This matters because your download client and Sonarr/Radarr are often still working with a file at
the same time Weir notices it in the watched folder — Weir checking their queue first avoids
racing an import that's already in progress.

When Weir cleans a file that is already in your library, it tells the connected manager to look at
that file again.

## Sonarr and Radarr: getting cleaned files imported

Sonarr and Radarr don't hand files to Weir. Instead, your download client finishes into Weir's
**watched folder**, Weir writes the cleaned copy to its **output folder**, and a **remote path
mapping** in Sonarr/Radarr points them at the output folder. They only ever see cleaned files.

### 1. Set up the workflow in Weir

Open **Setup › Workflows** and edit the workflow:

- **Watched folder**: where your download client finishes files, e.g. `/media/downloads/complete/tv`.
- **Output folder**: where Weir puts cleaned files, e.g. `/media/weir/tv`.
- **New downloads: after cleaning, delete the original download**: shows as off, and cannot be changed, once the
  workflow is linked to Sonarr or Radarr (step 2). Weir never deletes the original of a linked workflow:
  the torrent needs its files to keep seeding, and if they disappear, Sonarr stops treating the
  download as finished and never imports it. Your download client or Sonarr removes the original
  later, under your normal seeding rules. Weir remembers what it has already cleaned, so it won't
  clean the same file twice.
- **Existing output**: leave it on anything except **Keep both**. Keep both renames the cleaned file,
  and Sonarr only looks for the original name.

### 2. Connect Sonarr (or Radarr) to Weir

Under **Setup › Connections › Media managers**, add Sonarr with its address and API key. You'll find the key in
Sonarr on its **General** settings page. Then link the workflow to it: in the workflow's editor, choose
Sonarr in the **Media manager** section, press **Link to a media manager** and save. (**Add workflow ›
From a media manager** does this for you.)

### 3. Add the remote path mapping in Sonarr

The workflow's **What your media manager needs** section in Weir shows the exact values, with copy buttons. In Sonarr, go
to **Settings › Download Clients › Remote Path Mappings** and press **+**:

| Field | Value |
| --- | --- |
| Host | Exactly what's in your download client's **Host** field on that same screen, e.g. `qbittorrent` |
| Remote Path | Weir's watched folder, e.g. `/media/downloads/complete/tv/` |
| Local Path | Weir's output folder, e.g. `/media/weir/tv/` |

- The output folder must already exist, or Sonarr won't save the mapping.
- Add one mapping for each download client Host.
- Keep **Completed Download Handling** switched on. This setup relies on it.
- If Sonarr, Weir and your download client see the folders at different paths, use the path **each app
  itself** sees. Giving every container the same folders at the same paths avoids that.

If Sonarr's own download client already has a folder set, the workflow editor offers it as the
watched folder with a **Use this as the watched folder** button, read straight from Sonarr's
`GET /api/v3/downloadclient` — press it instead of typing the path by hand. It only appears when
Sonarr reports one, and it's never applied without you pressing it.

### 4. Check it

In Weir, press **Check again** in the workflow's **Folder chain** section. Weir reads Sonarr's remote path
mappings, download clients and queue — it never changes Sonarr's settings — and shows ✓, or a plain
explanation of what to fix.

A ✓ means Weir read the fact itself. Where it can only take someone's word for it, the line says so and
shows a **?** and **Not verified** instead of a tick. Sonarr and Radarr tell Weir a download client's own
folder only when the client has one set; a client that files downloads by category has no folder Weir can
read from them, so it stays not verified until you connect that client to Weir directly. A download client
you connect to Weir directly is read for itself: each folder it saves to is checked against the watched
folder, and a client saving outside it is a problem naming both folders. Deluno is checked differently,
as described under [Deluno: checking where downloads land](#deluno-checking-where-downloads-land).

### What you'll see

When a download finishes, Sonarr shows **"No files found are eligible for import"** until Weir is done.
That's expected: Sonarr checks again every minute, with no time limit, and imports the cleaned file as
soon as it appears. Weir only ever publishes a finished file, so Sonarr can't import one halfway through.

If Weir can't clean a file, what Sonarr sees depends on the workflow's **When retries run out** setting:

- **Hand the original back unchanged** (the default): Sonarr imports the original, uncleaned.
- **Keep it until someone acts**: Sonarr waits, and Weir shows the file as Failed, with the reason.
- **Reject the release so a different one is found**: Weir removes the download and blocklists it in Sonarr, so it searches again.

Files below the workflow's minimum size (50 MB for a new workflow, under **Intake rules**) are never cleaned,
so Sonarr waits on them indefinitely. Keep the minimum size below your smallest real episode.

### Optional: hand back with Downloaded Scan

Sonarr and Radarr normally notice a cleaned file by scanning the remote path mapping on their own
schedule. If you'd rather Weir tell them the moment a file is ready, edit the connection under
**Setup › Connections › Media managers** and turn on **Scan for downloaded files after cleaning**. Off by
default. When it's on, after Weir writes a cleaned file to the workflow's output folder it asks the
connection to run its `DownloadedMoviesScan`/`DownloadedEpisodesScan` command over it — the same
command tools like Unpackerr use — with the path translated through the remote path mapping so the
manager gets its own view of the file. A manager that doesn't answer never fails the clean; Weir
just keeps relying on Sonarr's own periodic scan instead, and records what happened in Activity.

## Weir on its own, or alongside a bare download client

A Weir-only workflow and a linked one can sit side by side. For example, one workflow can feed
Sonarr while another cleans a folder of home videos that no manager ever sees.

Weir doesn't need a media manager at all. A workflow with a watched, work and output folder and no
manager connected is a complete setup (a **Weir only** workflow) — the folder chain check above only looks at those three
folders and never warns about a manager being absent.

If you'd still like a watched-folder suggestion without connecting Sonarr, Radarr or Deluno, add
your download client itself — SABnzbd, NZBGet, qBittorrent, Deluge or Transmission — under
**Setup › Connections › Download clients**, with its address and whatever
credentials it needs. This connection is outbound only and read only: Weir reads the client's own
completed-download folder (and, where the client organizes downloads by category, each category's
own folder) to suggest a watched folder, and never changes anything on the client. It feeds the same
suggestion list and the same folder chain check as a full media manager, so more than one manager
and a bare download client can all be connected to one install at once, each checked independently.

Weir doesn't store which workflow a bare download client feeds, so a workflow's **Folder chain** lists
a client only when Weir has a reason to think it delivers there: the client saves into the workflow's
watched folder (or a folder below it), or the workflow's linked Sonarr or Radarr uses that client (the
same product at the same address). A Weir-only workflow that watches a folder no connected client saves
into, and a client used only by a manager the workflow isn't linked to, show no client lines and never
cost the workflow its **Ready** badge. Deluno doesn't say which product or address each of its clients
is, so a Deluno-linked workflow relies on Deluno's own per-client lines instead. When a client saves to a
folder Weir can't see from its own computer, for instance a path inside a container or on another
machine, the line reads **Not verified** rather than **Needs a fix**.

## Removing a connection

Weir will not remove a connection while a workflow still comes from it, a hand-off from it is in progress, or Weir
still owes it a report. A workflow that comes from a manager keeps its originals and, for Deluno, is fed only by
Deluno's hand-offs; if the connection simply disappeared, the workflow would become one Weir feeds itself, scan its
folder, and could remove files the download client is still seeding. So Remove asks first and, when something is in the
way, says what, in words like "Movies and TV Shows still come from this Deluno", and changes nothing.

You then choose, as separate steps:

- **Unlink the workflows.** They stay, with all their settings and folders. A workflow that no manager feeds any more is
  switched off, so it does nothing until you decide what it does next (edit it and switch it on, link it to another
  manager, or remove it).
- **Remove the workflows first.** Each is removed on its own, with its folders and files left exactly as they are.
- **Give up on the reports still owed.** A report is owed when Weir finished a hand-off while the manager was not answering. It is
  sent once the manager answers; if it never comes back, give up on the reports and Weir stops waiting. Nothing is sent, and
  Activity records which reports were dropped.

A hand-off in progress has to finish, or be cancelled from the jobs list; then Remove goes through. Weir never touches a folder or
a file when a connection is removed.

## Anything else

A tool that isn't Deluno, Sonarr or Radarr can still hand files to Weir directly, by posting to its
webhook endpoint (`/api/v1/intake/webhook/native` for a connection of kind **Something else**).
See the [API reference](../api) for the full request shape. Weir never calls this kind, so its address is
optional: leave it blank when the tool only sends messages to Weir. The other kinds need an address.
