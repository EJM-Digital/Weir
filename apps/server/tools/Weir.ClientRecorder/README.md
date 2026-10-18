# Weir download client recorder

Records the five download clients Weir can read folders from (qBittorrent, Transmission, Deluge, SABnzbd and NZBGet) talking to Weir's
own download-client code (#1015). Every request Weir sends and every reply the client gives is kept, byte for byte, in the same
recording format as Deluno's, Sonarr's and Radarr's (`{app, version, state, recordedAt, note, exchanges[{request{method, path, headers,
body | bodyText}, reply{status, headers, body | bodyText}}]}`). The replay tests read them from
`apps/server/tests/fixtures/recorded/<client>/<version>/`; no test builds a download client's reply by hand.

## How it records

1. `compose.yaml` starts the client from its published Docker image, pinned by digest. Transmission comes from the Sonarr and Radarr
   recorder's `compose.yaml`, which pins it once.
2. The recorder waits for the client to answer and sets it up through the client's own API: a password of its own, a completed folder,
   categories or labels with folders. None of those calls is recorded.
3. It then drives Weir's own `IDownloadClientPort` for that client (`QBittorrentPort`, `TransmissionPort`, `DelugePort`, `SabnzbdPort`,
   `NzbgetPort`) over Weir's own HTTP transport with a recorder in front, one recorded state per operation. The recorder's pieces
   (`RecordingTransport`, `RecordingWriter`, `ArrApi`) are the Sonarr and Radarr recorder's (`../Weir.ArrRecorder`), linked, not copied.

A secret is written as `<scrubbed>`: the passwords and API keys the session used, and the values of the session cookies, session ids and
`Authorization` tokens the client and Weir exchanged. The password a recording uses for a refused sign-in (`not-the-password`) is not a secret.

## States

| State | What Weir does |
| --- | --- |
| `test`, `test-wrong-password` (`test-wrong-key` for SABnzbd) | The connection test with the right credentials and with ones the client does not know |
| `test-open`, `test-nothing-saved` (Transmission) | The connection test against a Transmission that asks for no sign-in, and with nothing saved against one that wants it |
| `folders` | Read the folders: the default save path and the categories or labels that have a folder of their own |
| `folders-wrong-password`, `folders-wrong-key` (NZBGet, SABnzbd) | Read the folders with credentials the client does not know |
| `folders-stock`, `folders-move-completed-off` (Deluge) | Read the folders of a Deluge as it starts, and of one whose folder for completed downloads is switched off |

## Running it

On a CI runner: `.github/workflows/record-clients.yml` (run it by hand, or wait for Monday). It runs one job per client and uploads the
recordings as an artifact. Weekly it also records against the newest release of each client and fails when the replies differ from the
checked-in ones (`scripts/compare-arr-recordings.mjs --apps <client>`).

By hand, with Docker and the .NET SDK (qBittorrent prints its temporary password in the container's log; read it from there):

```bash
export RECORDER_WORK=/tmp/clients PUID=$(id -u) PGID=$(id -g)
mkdir -p $RECORDER_WORK/sabnzbd/{config,downloads}
docker compose -f apps/server/tools/Weir.ClientRecorder/compose.yaml up -d sabnzbd
RECORDER_CLIENT=sabnzbd RECORDER_CONFIG_DIR=$RECORDER_WORK/sabnzbd/config RECORDER_OUT_DIR=/tmp/client-recorded \
  dotnet run --project apps/server/tools/Weir.ClientRecorder -c Release
```

| Variable | Meaning |
| --- | --- |
| `RECORDER_CLIENT` | `qbittorrent`, `transmission`, `deluge`, `sabnzbd` or `nzbget` (required). |
| `RECORDER_OUT_DIR` | Where the recordings are written, as `<client>/<version>/<state>.json` (required). |
| `RECORDER_URL` | Where the client listens. Default `http://localhost:8080` (qBittorrent), `:9091`, `:8112`, `:8085`, `:6789`. |
| `RECORDER_AUTH_URL` | Transmission only: where the second copy that asks for a sign-in listens (`transmission-auth` in `compose.yaml`, `http://localhost:9092`). |
| `RECORDER_CONFIG_DIR` | SABnzbd only: its config folder on this machine, where `sabnzbd.ini` appears and holds its keys. |
| `RECORDER_SETUP_PASSWORD` | qBittorrent only: the temporary password it printed. |
| `RECORDER_VERSION` | The version to file the recordings under, for a client that does not say its own. |

Exit code 0 means every step recorded, 1 that a step failed (the others are still written), 2 that the recorder was not set up.
