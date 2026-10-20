# What the Transmission recordings do not hold

A case below has no recording because the recorder could not make Transmission produce it. No test invents one: where a test needs it, it says
which recorded reply it started from and which member it changed, or that it builds the reply itself.

| Case | Why there is no recording | Where the test stands in |
| --- | --- | --- |
| A client that cannot be reached, times out or sits behind a proxy that answers something else | There is no reply to record, or it is another program's | `DownloadClientPortsTests` build the failure; they are not the client's |
| A second 409 after the retry | A real Transmission accepts the session id it just named | `DownloadClientPortsTests` build it; it is not Transmission's |
| Any release but 4.0.6 and 4.1.3 | The recorder runs the image in the Sonarr and Radarr recorder's `compose.yaml`; the weekly job runs the newest release but checks it against these, it does not keep it | none |
