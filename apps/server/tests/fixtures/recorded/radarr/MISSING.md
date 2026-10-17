# What the Radarr recordings do not hold

A case below has no recording because the recorder could not make Radarr produce it. No test invents one: where a test needs it, it says
which recorded reply it started from and which member it changed.

| Case | Why there is no recording | Where the test stands in |
| --- | --- | --- |
| A Radarr that rate limits (429 with `Retry-After`) | A default install never does | `ManagerHttpAndPortTests` build the reply; they are not Radarr's |
| A Radarr that cannot be reached, times out, redirects, or sits behind a proxy that answers HTML | There is no reply to record | `ManagerHttpAndPortTests` |
| Any version but the pinned one (an older Radarr 4 or 5) | The recorder runs the image in `compose.yaml`; the weekly job runs the newest release but checks it against these, it does not keep it | none |
| A second scan asked for while one is running, and the scan of a second file (`downloaded-scan-next-file`, `command-status-finished`) | The session imports one movie, from one file; those states are recorded for Sonarr only | the Sonarr recordings, through the same code |
| A download that has finished in its download client (a queue row that is `completed` or `importPending`, and a `Download` webhook that carries a `downloadId`) | The torrent in the recorder's download client has no peers, so it never finishes | `FailurePoliciesTests` (contract) takes the recorded queue row and changes its state, path and ids (`ArrShapes.QueueRow`) |
| A usenet download client, qBittorrent, or any client but Transmission | The recorder sets up one client | none |
| Webhook events other than `Test`, `Grab`, `MovieAdded`, `Download` and `MovieFileDelete` (rename, upgrade, health, movie delete, application update) | The recorder does not make the app do them | `Every_event_but_the_import_is_not_work_for_Weir` covers the events that were recorded |
| An API key that is blocked (403) or authentication switched on | Not set up | none |
