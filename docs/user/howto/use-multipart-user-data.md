# Multipart user-data

A multipart MIME message carries several parts in one user-data payload — a
cloud-config plus one or more scripts, or an `#include` URL. It's the same
format cloud-init uses; `cloud-init devel make-mime` and the eryph fodder
compiler produce it.

```
Content-Type: multipart/mixed; boundary="==BOUNDARY=="
MIME-Version: 1.0

--==BOUNDARY==
Content-Type: text/x-cloud-config
Content-Disposition: attachment; filename="cloud-config.yaml"

#cloud-config
hostname: my-guest

--==BOUNDARY==
Content-Type: text/x-shellscript
Content-Disposition: attachment; filename="setup.ps1"

Write-Host 'hello from the script'

--==BOUNDARY==--
```

Each part's `Content-Type` decides how the agent handles it:

| Content-Type | Handling |
| --- | --- |
| `text/cloud-config`, `text/x-cloud-config` | Parsed as a cloud-config fragment; multiple fragments merge into one. |
| `text/x-shellscript` | A script (PowerShell or cmd — see below). |
| `text/x-include-url`, `text/x-include-once-url` | URLs to fetch; each result is processed as user-data. |
| `text/cloud-boothook` | Stored, not run. |
| `multipart/*` | Nested multipart, processed in turn. |
| `text/plain`, `application/octet-stream`, none | Read and handled by content (`#cloud-config` header, `#ps1` line, …). |

## Scripts

A script part runs by its filename extension: `.ps1` under PowerShell,
`.cmd`/`.bat` under cmd. Set it with `Content-Disposition: attachment;
filename="setup.ps1"`. No extension, or `.sh`, won't run on Windows.

## Binary content

Base64-encode non-text parts and set `Content-Transfer-Encoding: base64`.
Text parts go as-is.

## Validate

```powershell
egs-service validate --user-data C:\Temp\multipart.mime
```
