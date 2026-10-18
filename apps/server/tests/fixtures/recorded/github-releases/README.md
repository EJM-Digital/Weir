# GitHub's release feed, as GitHub sent it

`releases.atom` is the public release feed of the Weir repository (`https://github.com/<owner>/Weir/releases.atom`), saved byte for byte as GitHub
answered a plain `GET` on 17 October 2026: the ten newest releases, newest first, as Atom. Nothing in it was edited, so a change GitHub makes to the
feed shows here the next time it is recorded again.

Weir reads the newest release from this feed before it asks GitHub's API (`ReleaseFeed`, `GitHubReleaseCatalogClient`). The browser tests
(`apps/server/tests/Weir.E2E.Tests`) serve it from a local address (`FakeReleaseFeed`) and point a test server at it with the test-only
`WEIR_RELEASES_FEED_URL`, so the Updates card of System › About reads the real shape of the feed instead of the internet.

To record it again:

```
curl -sS -o apps/server/tests/fixtures/recorded/github-releases/releases.atom https://github.com/<owner>/Weir/releases.atom
```

The newest release in the file is `v1.0.0-rc.18`; the tests that need "a newer release than the one running" run a server as an older version
(`WEIR_VERSION`), and the one that needs "up to date" runs it as that release, so recording a newer feed changes nothing for them until the
release they name has gone from the first ten.
