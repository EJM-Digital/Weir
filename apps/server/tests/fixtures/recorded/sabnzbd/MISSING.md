# What the SABnzbd recordings do not hold

A case below has no recording because the recorder could not make SABnzbd produce it. No test invents one: where a test needs it, it says
which recorded reply it started from and which member it changed, or that it builds the reply itself.

| Case | Why there is no recording | Where the test stands in |
| --- | --- | --- |
| A client that cannot be reached, times out or sits behind a proxy that answers something else | There is no reply to record, or it is another program's | `DownloadClientPortsTests` build the failure; they are not the client's |
| A category with no folder of its own, the default category `*`, and a folder that climbs out with `..` | The default categories are in the recording, with `dir` empty; whether SABnzbd then saves under the category's name is not something a reply shows, so Weir lists only categories that name a folder | `SabnzbdRulesTests` (hand-written shapes) |
| Any release but 5.1.3 | The recorder runs the image in `compose.yaml`; the weekly job runs the newest release but checks it against these, it does not keep it | none |
