# How to use `#include` URLs

`#include` lets one user-data payload reference others by URL. The
agent fetches each URL, sniffs its content type, and feeds the result
back into the pipeline.

## Shape

```
#include
https://example.com/fodder/base.yaml
https://example.com/fodder/scripts.mime
# comment lines are skipped
```

Every non-empty, non-`#` line is treated as a URL. Empty payloads are
skipped with a warning.

## `#include-once`

```
#include-once
https://example.com/fodder/base.yaml
```

Semantically identical: each URL is fetched exactly once anyway. The
header exists for cloud-init compatibility.

## What the agent fetches

The pipeline calls `IUrlHelper.FetchAsync(url)`. The default
implementation does a plain HTTP GET with the timeout and retry policy
from [settings](../reference/settings.md):

- `userData.fetchTimeoutSeconds` (default `30`) — per-attempt timeout
- `userData.fetchMaxAttempts` (default `4`) — total attempts
- `userData.fetchInitialBackoffSeconds` (default `1`) — doubles up to 4s

Authentication is not supported — URLs must be reachable anonymously
(or via a transparent corporate proxy).

## Content sniffing on the fetched payload

The fetched body is sniffed the same way the root user-data is:
gzipped bodies are decompressed; the first line decides the content type
(`#cloud-config`, `#include`, multipart MIME headers, `#ps1`, etc.).
Payloads that don't match any marker are skipped with a warning.

## Cycle protection

Each visited URL is recorded. If a fetched payload references a URL
already visited (directly or transitively), the agent logs a warning
and skips it. Maximum recursion depth is governed by
`userData.maxRecursionDepth` (default `10`).

## Failure tolerance

Cloud-init's `#include` is best-effort — a single URL failure doesn't
fail the whole run. The agent matches that: each failing fetch logs a
warning and the next URL is tried. Use [`validate`](../reference/cli.md)
to catch typos before shipping.
