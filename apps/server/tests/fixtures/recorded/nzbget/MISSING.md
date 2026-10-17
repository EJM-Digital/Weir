# What the NZBGet recordings do not hold

A case below has no recording because the recorder could not make NZBGet produce it. No test invents one: where a test needs it, it says
which recorded reply it started from and which member it changed, or that it builds the reply itself.

| Case | Why there is no recording | Where the test stands in |
| --- | --- | --- |
| A client that cannot be reached, times out or sits behind a proxy that answers something else | There is no reply to record, or it is another program's | `DownloadClientPortsTests` build the failure; they are not the client's |
| A category with no folder of its own | The default categories (Movies, Series, Music, Software) are in the recording with no `DestDir`; `AppendCategoryDir=yes` is in the config, but whether a reply shows where such a category saves is not something the recorder can check, so Weir lists only categories that name a folder | `NzbgetRulesTests` (hand-written shapes) |
| Any release but 26.3 | The recorder runs the image in `compose.yaml`; the weekly job runs the newest release but checks it against these, it does not keep it | none |
