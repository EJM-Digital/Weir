# What the NZBGet recordings do not hold

A case below has no recording because the recorder could not make NZBGet produce it. No test invents one: where a test needs it, it says
which recorded reply it started from and which member it changed, or that it builds the reply itself.

| Case | Why there is no recording | Where the test stands in |
| --- | --- | --- |
| A client that cannot be reached, times out or sits behind a proxy that answers something else | There is no reply to record, or it is another program's | `DownloadClientPortsTests` build the failure; they are not the client's |
| A category with no folder of its own, with `AppendCategoryDir=no` | The recording has `AppendCategoryDir=yes` and the default categories (Movies, Series, Music, Software) with no `DestDir`, so Weir suggests `DestDir/<category>` for them, as nzbget.conf documents. A reply with `AppendCategoryDir=no` is not recorded (that needs a second NZBGet setup), so that case changes the recorded value | `NzbgetRulesTests` (the recorded reply with one value changed) |
| Any release but 26.3 | The recorder runs the image in `compose.yaml`; the weekly job runs the newest release but checks it against these, it does not keep it | none |
