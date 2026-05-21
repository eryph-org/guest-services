# Multipart MIME samples

Hand-written multipart MIME payloads that exercise the full user-data
pipeline: the multipart handler splits the message into parts, the
content-type sniffer classifies each part, and the per-type handlers
re-introduce them into the pipeline.

## `bootstrap-and-config.mime`

Three parts inside a `multipart/mixed` envelope:

1. **`text/cloud-config`** — a `#cloud-config` document that sets the
   hostname, creates an admin user, writes one file, and supplies one SSH
   key. Routed to the cloud-config part handler, which feeds the YAML into
   the standard modules.
2. **`text/x-shellscript`** — a `#ps1_sysnative` payload routed to the
   shell-script part handler, which adds it to the script queue consumed by
   `ScriptsUserModule` in the Final stage.
3. **`text/cloud-boothook`** — a `#cloud-boothook` cmd script routed to the
   boot-hook handler. Boothooks run before the regular modules; on Windows
   the agent executes them via `cmd.exe`.

## Format reference

The file follows the RFC 2046 shape that
`Eryph.GuestServices.Provisioning.UserData.Handlers.MultipartMimeHandler`
expects:

```
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="<boundary-token>"

--<boundary-token>
<per-part headers, including Content-Type and optional Content-Disposition>

<part body>

--<boundary-token>
<per-part headers>

<part body>

--<boundary-token>--
```

Notes:

- The opening blank line between the top-level headers and the first
  boundary marker is **required** by the parser; without it the body of
  the first part is interpreted as additional top-level headers and the
  message is rejected.
- The closing `--<boundary>--` terminator must appear on its own line.
- Inline base64 transfer-encoded parts are supported by the handler (set
  `Content-Transfer-Encoding: base64` and emit base64 with optional CRLF
  wrapping) but none of the parts in this sample need it.
- Per-part `Content-Disposition: attachment; filename="..."` headers are
  not required, but they let `ScriptsUserModule` write the staged script
  using a recognisable on-disk filename, which makes diagnostics easier.

## How to add another multipart fixture

1. Pick a unique boundary token. The token must not appear anywhere in
   any of the part bodies.
2. Emit the top-level `MIME-Version` and `Content-Type` headers, then a
   blank line.
3. For each part: emit `--<boundary>`, the part's headers, a blank line,
   the part body. End the body with a newline.
4. Finish with `--<boundary>--`.
5. Save with UNIX (LF) line endings. Git's `core.autocrlf` is harmless
   here because the multipart handler normalises CRLF to LF internally.
