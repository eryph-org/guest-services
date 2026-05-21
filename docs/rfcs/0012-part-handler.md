# RFC 0012 — Part-handler (custom code in user-data)

Status: Draft

## Problem

Cloud-init's `#part-handler` user-data type lets the user ship Python code that handles a custom MIME type. The handler is called with each `text/x-mycustomtype` part the multipart contains. Very powerful, very exotic.

## What cloud-init does

The part-handler script is staged to `/var/lib/cloud/handlers/<name>.py` and dynamically imported. Cloud-init's runtime calls `list_types()` and `handle_part()` on it.

## What cloudbase-init does

Not supported.

## Eryph context

- No eryph use case today.
- Implementing this on Windows requires a script runtime (PowerShell? CSX? C# embedded scripting?). Pick one and we're locked.
- Security: arbitrary code from user-data is a major attack surface in a multi-tenant context. Eryph guests are mostly single-tenant, but still.

## Tentative direction

**Defer indefinitely.** The user-data pipeline detects `#part-handler` content type and logs a Warning ("part-handler not supported; this part will be ignored"). Revisit only if there's a real-world demand.

## Open questions

- None until demand surfaces.
