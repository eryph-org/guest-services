# User-data formats

The agent reads the first non-empty line of the user-data (after stripping a
UTF-8 BOM and a leading `From` line, and decompressing if it's gzipped) and
treats the payload by its marker:

| Marker / shape | Treated as | Notes |
| --- | --- | --- |
| `#cloud-config` | cloud-config | YAML; multiple fragments merge into one. |
| `#include`, `#include-once` | include URLs | One URL per line; each is fetched and processed. |
| `#cloud-boothook` | boothook | Stored, not run. |
| `#ps1`, `#ps1_sysnative` | PowerShell script | The whole payload is one script. |
| `#!/...` | shell script | Recognised but skipped on Windows — no POSIX shell. |
| `Content-Type: multipart/...` or `MIME-Version: 1.0` | multipart | See [Multipart user-data](../howto/use-multipart-user-data.md). |

Anything without a recognised marker is ignored with a warning. There is no
implicit fallback to `runcmd`.

## Scripts

When a payload (or a multipart part) is a script, the agent chooses the runner
in this order:

1. Filename extension — `.ps1` runs under PowerShell, `.cmd`/`.bat` under cmd,
   `.sh` is skipped on Windows.
2. Shebang — `#ps1`/`#ps1_sysnative` run under PowerShell; `#!/...` is skipped.
3. `Content-Type: text/x-shellscript` with no other signal — runs under
   PowerShell.
4. Nothing recognisable — skipped, with a warning (never a silent drop).

Filename comes first so that scripts carrying a `filename=` (as eryph fodder and
cloudbase-init payloads do) run by their extension regardless of any shebang.

## Cloud-config merging

When several cloud-config fragments arrive — across multipart parts or `#include`
results — they merge into one: lists concatenate, later scalars win, and nested
mappings merge key by key. This is cloud-init's default merge behaviour.

## Includes and recursion

`#include` URLs and nested multiparts are processed recursively, up to
`userData.maxRecursionDepth` (default 10; see [settings](settings.md)). A URL
that's already been fetched is skipped to break cycles.
