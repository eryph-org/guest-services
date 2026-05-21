# Cloud-config samples

Realistic fixtures used by the Pester end-to-end harness in
`test/Pester/`. Every file is a payload that the eryph guest provisioning
agent (`egs-provisioning.exe`) should accept and process — these are not
contrived minimal blobs.

The harness invokes the agent in dry-run mode against each sample to verify
that the binary parses, validates, and dispatches to the expected modules
without mutating the host.

## Samples

| File | What it exercises | Expected outcome |
|---|---|---|
| `01-minimal-admin-user.yaml` | `users` + `ssh_authorized_keys` + sudo alias | One Administrators user with one SSH key. |
| `02-write-files-with-encodings.yaml` | `write_files` with plain / base64 / gzip+base64 encodings | Three files under `C:\ProgramData\eryph-samples\02\`. |
| `03-runcmd-mixed-forms.yaml` | `runcmd` shell-string and argv-list forms | Three runcmd entries executed in order. |
| `04-chpasswd-list-and-users.yaml` | Legacy `chpasswd.list` plus per-user `passwd` | Three users created; two get passwords via chpasswd list, one via `users[].passwd`. |
| `05-multipart-mixed.mime` | RFC 2046 multipart MIME with cloud-config + shellscript parts | One user created, one PowerShell script staged and executed. |
| `06-include-url.txt` | `#include` user-data with a `file://` URL | Resolves to sample 01 and applies it. The `{{SAMPLES_DIR}}` placeholder is substituted by the Pester harness. |
| `07-windows-shellscript.ps1` | `#ps1_sysnative` PowerShell payload | One marker file written. |
| `08-locked-user.yaml` | `lock_passwd: true` | One enabled user, one disabled user. |
| `09-plain-text-passwd.yaml` | `plain_text_passwd` alias | One user created with the plaintext password. |
| `10-hostname-only.yaml` | `hostname` + `preserve_hostname: false` | Computer name change attempted; may report reboot-pending. |
| `11-write-files-permissions-formats.yaml` | `write_files[].permissions` literal shapes (`"0644"`, `0644`, `"0o755"`) | Three files written; three "permissions ignored on Windows" warnings. |
| `12-vendor-and-user.yaml` | Combined platform vendor + operator user-data document | hostname + write_files + users + runcmd all applied. |
| `broken/invalid-yaml.yaml` | YAML parse failure | `validate` exits non-zero with a parse error. |
| `broken/duplicate-user.yaml` | Validator catches duplicate user names | `validate` exits non-zero with an aggregation error. |

## Format conventions

- Files use UNIX line endings; the YAML parser is tolerant of CRLF but the
  multipart fixture is sensitive to line endings near the boundary markers
  (RFC 2046 specifies CRLF; we accept both — the in-process parser
  normalises CRLF to LF). When editing on Windows, configure your editor
  to preserve LF line endings.
- The leading `#cloud-config` line is preserved literally; the YAML
  serializer strips it before parsing.
- All SSH keys in these samples are reusable test fixtures and do **not**
  authenticate any real account.
