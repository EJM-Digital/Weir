# What the Deluge recordings do not hold

A case below has no recording because the recorder could not make Deluge produce it. No test invents one: where a test needs it, it says
which recorded reply it started from and which member it changed, or that it builds the reply itself.

| Case | Why there is no recording | Where the test stands in |
| --- | --- | --- |
| A client that cannot be reached, times out or sits behind a proxy that answers something else | There is no reply to record, or it is another program's | `DownloadClientPortsTests` build the failure; they are not the client's |
| A Web UI that is not connected to its daemon | `core.get_config` is never answered: the Web UI waits for a daemon until Weir's 15 second limit, so there is no reply to record | none |
| The version of the daemon through `daemon.info` | The Web UI answers it "Unknown method"; the recorder reads the version from `web.get_host_status` | none |
| Any release but 2.2.0 | The recorder runs the image in `compose.yaml`; the weekly job runs the newest release but checks it against these, it does not keep it | none |
