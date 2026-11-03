# Weir Sonarr and Radarr recorder

Records a real Sonarr and a real Radarr talking to Weir's own manager client code (#1013, #1014). Every request Weir sends and every
reply the app gives is kept, byte for byte, in the same recording format as Deluno's (`{app, version, state, recordedAt, note,
exchanges[{request{method, path, headers, body}, reply{status, headers, body | bodyText}}]}`). The replay tests read them from
`apps/server/tests/fixtures/recorded/sonarr/` and `radarr/`; no test builds a Sonarr or Radarr reply by hand.

## How it records

1. `compose.yaml` starts Sonarr, Radarr and a Transmission download client from the published Docker images, pinned by digest.
2. The recorder reads the API key the app generated from its `config.xml`, waits for the app to answer, and sets it up through the app's
   own API: a root folder, a title (Breaking Bad, Big Buck Bunny), a download client, and a webhook that points back at the recorder.
   None of those calls is recorded.
3. It then drives Weir's code (`HttpMediaManagerPort`, `ArrDownloadedScan`, `ArrManagerRedownload`, `ArrRedownloadRiskGateway` and
   `MediaManagerHttpClient`) over Weir's own HTTP transport with a recorder in front, one recorded state per operation.
4. The apps' webhooks (`Test`, `Grab`, `Download`, the file-delete event) are kept as the app posted them. The `200` in those recordings
   is the recorder's own listener's, not Weir's.

A secret (the API key, the webhook secret) is written as `<scrubbed>`, and so is a title's `overview`, which is free text from a
metadata site and plays no part in anything Weir reads.

## States

| State | What Weir does |
| --- | --- |
| `connection-test-wrong-key`, `connection-test-no-root-folder`, `connection-test` | The connection test with an unknown key, against a fresh app, and against an app with a root folder |
| `queue-empty`, `queue-with-download`, `queue-after-remove` | Read the queue |
| `queue-remove-unknown-item`, `queue-remove-and-blocklist` | Remove a queue item from the download client and blocklist the release |
| `downloaded-scan`, `downloaded-scan-with-mapping` | Ask the app to scan the folder a finished download is in, without and with a remote path mapping |
| `setup-check` | Read the path mappings, the download clients and their settings, and the queue |
| `library-listing` | List the files the app keeps, matched to titles, and ask which files it still keeps |
| `redownload-risk` | Read a file and its quality profile |
| `rescan` | Tell the app a file in its library changed |
| `redownload-unknown-title`, `redownload`, `redownload-file-gone` | Download again: delete the file, ask for a search |
| `webhook-*` | What the app posts to Weir's intake webhook |

## Running it

On a CI runner: `.github/workflows/record-arr.yml` (it runs when `compose.yaml` changes, or run it by hand). It uploads the recordings as an artifact.
It also compares against the checked-in replies, and by hand (`images: latest`, `compare: true`) against the newest release of each app, and fails when the replies differ from the checked-in ones
(`scripts/compare-arr-recordings.mjs`).

By hand, with Docker, FFmpeg and the .NET SDK:

```bash
export RECORDER_WORK=/tmp/arr PUID=$(id -u) PGID=$(id -g)
mkdir -p $RECORDER_WORK/{sonarr,radarr}/{config,library,downloads} $RECORDER_WORK/transmission/{config,downloads}
docker compose -f apps/server/tools/Weir.ArrRecorder/compose.yaml up -d
RECORDER_APP=sonarr RECORDER_CONFIG_DIR=$RECORDER_WORK/sonarr/config RECORDER_WORK_DIR=$RECORDER_WORK/sonarr/downloads \
  RECORDER_OUT_DIR=/tmp/arr-recorded dotnet run --project apps/server/tools/Weir.ArrRecorder -c Release
```

| Variable | Meaning |
| --- | --- |
| `RECORDER_APP` | `sonarr` or `radarr` (required). |
| `RECORDER_CONFIG_DIR` | The app's config folder on this machine, where `config.xml` appears (required). |
| `RECORDER_WORK_DIR` | The folder on this machine that the app sees as `/downloads` (required). |
| `RECORDER_OUT_DIR` | Where the recordings are written, as `<app>/<version>/<state>.json` (required). |
| `RECORDER_URL` | Where the app listens. Default `http://localhost:8989` or `:7878`. |
| `RECORDER_WEBHOOK_PORT` / `RECORDER_WEBHOOK_HOST` | The listener for the app's webhook and the name the app's container reaches it by. Default 18080 or 18081 and `host.docker.internal`. |
| `RECORDER_TRANSMISSION_URL` | The download client, as the recorder reaches it. Default `http://localhost:9091`. |

The listener binds every interface (`http://+:port/`), which on Windows needs an administrator.

Exit code 0 means every step recorded, 1 that a step failed (the others are still written), 2 that the recorder was not set up.
