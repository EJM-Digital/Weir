# What the qBittorrent recordings do not hold

A case below has no recording because the recorder could not make qBittorrent produce it. No test invents one: where a test needs it, it says
which recorded reply it started from and which member it changed, or that it builds the reply itself.

| Case | Why there is no recording | Where the test stands in |
| --- | --- | --- |
| A client that cannot be reached, times out or sits behind a proxy that answers something else | There is no reply to record, or it is another program's | `DownloadClientPortsTests` build the failure; they are not the client's |
| A login answered 204 with or without a session cookie, or 401 | qBittorrent 4.6.7 and 5.1.4 both answer a good login `200 Ok.` and a bad one `200 Fails.`; the 204 case was reported against another release and is not one the recorder can make | `DownloadClientPortsTests`, `QBittorrentSessionTests` build the reply; they are not qBittorrent's |
| A qBittorrent that has banned the address after repeated wrong passwords (403) | The recorder tries one wrong password | none |
| Any release but 4.6.7 and 5.1.4 | The recorder runs the images in `compose.yaml`; the weekly job runs the newest 5.x release but checks it against these, it does not keep it | none |
