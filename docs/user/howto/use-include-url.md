# Use `#include` URLs

`#include` lets one user-data payload pull in others by URL. The agent fetches
each URL and processes the result as if it were user-data.

```
#include
https://example.com/fodder/base.yaml
https://example.com/fodder/scripts.mime
# comment lines are skipped
```

Every non-empty line that isn't a comment is a URL. `#include-once` behaves the
same — each URL is fetched once regardless; the variant exists for cloud-init
compatibility.

## Fetching

The agent does a plain anonymous HTTP GET. There's no authentication, so URLs
must be reachable without credentials (a transparent proxy is fine). The timeout,
retries, and maximum response size come from [settings](../reference/settings.md)
(`userData.fetchTimeoutSeconds`, `fetchMaxAttempts`, `fetchInitialBackoffSeconds`,
`fetchMaxBytes`).

Each fetched body is handled by its content, the same as the top-level
user-data: gzip is decompressed, and the first line decides whether it's a
cloud-config, another `#include`, a multipart message, or a script. A body with
no recognised marker is skipped with a warning.

## Loops and limits

Fetched payloads can reference more URLs, up to `userData.maxRecursionDepth`
(default 10). A URL that's already been fetched is skipped, so a cycle can't loop.

A single URL that fails to fetch is logged and skipped — the rest still run, as
in cloud-init. Run [`validate`](../reference/cli.md) to catch typos before you
ship.
