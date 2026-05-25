# RFC 0011 — Jinja2 templating in user-data

Status: Draft

## Problem

Cloud-init supports `#jinja2` user-data — a templating preamble that renders the rest of the user-data with `instance-data.json` variables substituted in. Useful when one cloud-config blob needs to reference per-instance values (region, IP, etc.).

## What cloud-init does

- User-data starts with `## template: jinja` marker.
- Renderer expands variables from the `instance-data` JSON (datasource-provided platform metadata).
- Output of rendering goes through the rest of the user-data pipeline (cloud-config / shellscript / include / etc.).

## What cloudbase-init does

No jinja2. Some operators use external templating (Ansible, PowerShell template strings) before user-data reaches the VM.

## Eryph context

- Eryph-zero already substitutes `{{ var }}` placeholders at the host before user-data lands in the ConfigDrive. So a USER's user-data doesn't need jinja2.
- BUT: vendor-data or cloud-provided user-data on Azure / EC2 could carry jinja2 markers. Cloud-init compatibility implies we should handle them.

## Tentative direction

**Defer to v2.** No eryph use case today; eryph-zero templating happens before guest. Adding jinja2 means a .NET jinja2 implementation (or porting Scriban / Liquid syntax to approximate) — non-trivial.

If we later need it: use **Scriban** (a .NET templating library compatible with Liquid; not jinja2 but close enough for cloud-init's typical use). Or implement a minimal jinja2 subset covering `{{ var }}` and `{% if %}` / `{% for %}` only.

## Open questions

- Is jinja2 actually present in real-world Azure / AWS vendor-data, or is it a cloud-init theoretical feature? Survey before implementing.
- The jinja2 marker (`## template: jinja`) is a comment line — easy to detect. The marker check goes in `UserDataContentTypeSniffer`. v1 detects + warns + ignores; v2 implements.
