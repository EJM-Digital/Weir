# Deluno's metadata service, as it answered

What the public metadata service (the one Weir asks for posters and a title's original language, `ArtworkGatewayClient`) answered to the
read-only requests Weir makes of it, kept as it sent them. A recording is `{app, version, state, recordedAt, note, exchanges[{request{method, path,
headers}, reply{status, headers, body | bodyText}}]}`, the format of the other recordings; `version` is the day it was recorded, as the service
has no version of its own. Only the content type of each reply's headers is kept.

Recorded on 20 October 2026 with `GET` requests to the service's public address, one for each state. The request path is the one Weir sends
(lower-case title, `movies` or `tv`, the year and the TMDb id only when Weir has them). The titles are public films and series.

| State | What was asked | Replayed by |
| --- | --- | --- |
| `health` | `/health` | `RecordedGatewayTests` |
| `search-film-by-title` | a film by title and year | `RecordedGatewayTests` |
| `search-film-by-tmdb-id`, `search-film-by-tmdb-id-only` | the same film with its TMDb id, and by the id alone | `RecordedGatewayTests` |
| `search-film-original-language` | a film whose original language is not English, with other results behind it | `RecordedGatewayTests` |
| `search-series-by-title` | a series by name | `RecordedGatewayTests` |
| `series-by-tvdb-id` | a series by its TheTVDB id | `RecordedGatewayTests` |
| `search-unknown-title` | a title the service does not know: 200 with no results | `RecordedGatewayTests` |

## What is not recorded

- The busy answer (503 or 429 with `Retry-After`): the service cannot be made busy on demand, so the tests that need it
  (`ArtworkResolverTests`, `ArtworkGatewayClientTests`) script it.
- The poster image: it is a file, not an answer. The tests that store a poster serve a stand-in image.

To record again: send the same requests and replace the files; nothing here is edited by hand.
