# User-data formats

The agent looks at the **first non-empty line** of the user-data bytes
(after stripping a UTF-8 BOM and an `mbox From ` preamble, and after
gunzipping if the bytes are gzip-magic). Whatever it finds maps to one
of the cloud-init content types below.

| Marker / shape | Content-Type | Handler | Notes |
| --- | --- | --- | --- |
| `#cloud-config` | `text/x-cloud-config` | `CloudConfigPartHandler` | YAML cloud-config; multiple fragments are deep-merged. |
| `#include` | `text/x-include-url` | `IncludeUrlHandler` | One URL per line. |
| `#include-once` | `text/x-include-url` (with once semantics) | `IncludeUrlHandler` | Identical to `#include` in v1. |
| `#cloud-boothook` | `text/cloud-boothook` | `BoothookPartHandler` | **Captured, not executed.** See [RFC 0013](../../rfcs/0013-boothook-execution.md). |
| `#ps1`, `#ps1_sysnative` | `text/x-shellscript` | (via `ScriptsUser`) | Single PowerShell script as the whole user-data. |
| `#!` (`#!/...`) | `text/x-shellscript` | (via `ScriptsUser`) | POSIX shebang. Captured but resolved to "Other" on Windows (no POSIX shell); a warning is logged. |
| `Content-Type: multipart/...` or `MIME-Version: 1.0` | `multipart/mixed` | `MultipartMimeHandler` | Hand-rolled RFC 2046 parser. |

Anything else falls back to `text/plain` and is dropped with a
warning. **There is no fallback to `runcmd`.** A user-data file with
no recognised marker is ignored.

## Inside a multipart

Recognised inner `Content-Type` values:

- `text/cloud-config` / `text/x-cloud-config`
- `text/x-shellscript`
- `text/cloud-boothook`
- `text/x-include-url` / `text/x-include-once-url`
- `multipart/...` (recursed)
- `text/plain`, `application/octet-stream`, or missing — body is **sniffed** with the same rules as the root.

`Content-Transfer-Encoding: base64` is decoded. `7bit`, `8bit`, `binary`
are pass-through. Quoted-printable is **not decoded** in v1.

`Content-Disposition: attachment; filename="..."` is preserved and used
for script dispatch (see below).

The closing `--boundary--` delimiter is **optional** — when missing, the
last open part is flushed. cloudbase-init may not tolerate the same
shape.

## Filename-led script dispatch

For shell-script payloads, the agent decides how to run them in this
order (matches cloudbase-init's actual behavior, not cloud-init's
documented behavior):

1. **Filename extension** — `.ps1` → PowerShell, `.cmd` / `.bat` → cmd,
   `.sh` → skipped on Windows with a warning.
2. **Shebang** — `#ps1` / `#ps1_sysnative` → PowerShell, `#!/...` →
   skipped on Windows with a warning.
3. **Content-Type** — `text/x-shellscript` with no other signal → falls
   back to PowerShell with a warning.
4. **No signal** → skipped with a warning (never a silent drop).

The reason filename wins is documented in
[RFC 0007](../../rfcs/0007-scripts-per-frequency-edge-cases.md) and in
the [cbi compatibility constraints memory note](../../../C:/Users/fwagner/.claude/projects/F--source-repos-eryph-guest-services/memory/project_cbi_compat_constraints.md).
Short version: eryph gene fodder was crafted around two cbi bugs
(filename mandatory, shebangs ignored) so we must honor what cbi
honors, not what cloud-init documents.

## Cloud-config merging

If a multipart contains several cloud-config fragments, each is
deserialised and merged. Lists concatenate, scalars from later
fragments override scalars from earlier ones, dicts deep-merge — the
common cloud-init default (`recurse_dict + replace_scalar`).

## Recursion limits

`#include` and nested multiparts recurse through the pipeline. The
limit is `userData.maxRecursionDepth` (default `10`); see
[Settings](settings.md). Already-visited URLs are skipped with a
warning to break cycles.
