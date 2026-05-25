# How to use multipart user-data

Cloud-init's multipart MIME format lets one user-data payload carry
several content types — a cloud-config plus one or more scripts plus
optionally an `#include` URL. The agent recognises the same shape.

## Anatomy

```
Content-Type: multipart/mixed; boundary="==BOUNDARY=="
MIME-Version: 1.0

--==BOUNDARY==
Content-Type: text/x-cloud-config; charset="us-ascii"
MIME-Version: 1.0
Content-Disposition: attachment; filename="cloud-config.yaml"

#cloud-config
hostname: my-guest
runcmd:
  - powershell.exe -Command "echo 'first'"

--==BOUNDARY==
Content-Type: text/x-shellscript; charset="us-ascii"
MIME-Version: 1.0
Content-Disposition: attachment; filename="setup.ps1"

Write-Host 'second'

--==BOUNDARY==--
```

## Content-types we recognise

| Content-Type | Maps to |
| --- | --- |
| `text/cloud-config`, `text/x-cloud-config` | Cloud-config fragment (merged into the final config). |
| `text/x-shellscript` | Script payload (PowerShell or cmd; see below). |
| `text/cloud-boothook` | Captured as a boothook but **not executed** in v1. |
| `text/x-include-url`, `text/x-include-once-url` | Fetch URL(s) and recurse on the result. |
| `multipart/mixed`, `multipart/*` | Nested multipart — recursed into. |
| `application/octet-stream`, `text/plain`, missing | Sniffed from the body — same rules as the root user-data. |

## Transfer encoding

`Content-Transfer-Encoding: base64` is decoded. `7bit`, `8bit`, and
`binary` are passed through as-is. Quoted-printable is **not** decoded
in v1.

## Filename is load-bearing for scripts

The agent dispatches shell-script parts by **filename extension**, not
by shebang — see [Run shell scripts](run-shell-scripts.md). Make sure
every script part has a `Content-Disposition` with a `filename=` whose
extension matches what you want to run (`.ps1`, `.cmd`/`.bat`). This is
what eryph fodder genes already emit; the rule exists so we accept the
same shape cloudbase-init does.

## Close delimiter is required

The MIME spec requires the multipart to end with `--boundary--`. The
agent enforces this — a multipart that omits the closing line is
rejected, so make sure your last part is followed by `--boundary--`. The
example above ends with `--==BOUNDARY==--` for exactly this reason.

## Sniffing the root

A leading `From ` mbox envelope line or a `From:` header before the MIME
headers is normal and common — the agent reads the multipart whether or
not such a line precedes the headers. UTF-8 BOM at the start of the file
is stripped too.

## Validating

```powershell
egs-service validate --user-data C:\Temp\multipart.mime
```

reports whether each part was parseable and accepted.
