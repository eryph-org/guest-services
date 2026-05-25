# Differences from cloud-init

The agent reuses cloud-init's vocabulary on purpose — same stage names,
module names, frequency labels, semaphore layout, and user-data formats.
If you know cloud-init, most things work the way you expect. This page
lists where the agent genuinely behaves differently.

## Scripts and user-data

- **Script dispatch is filename-led.** A `.ps1` / `.cmd` / `.bat`
  extension on a multipart part (or the staged script name) decides the
  runner; the shebang (`#ps1`, `#!/...`) is only a fallback when there is
  no usable extension. cloud-init keys off the shebang first.
- **Exit code 1003 means reboot-and-continue.** A `runcmd` entry or
  script that exits `1003` reboots the guest and resumes on the next
  boot. cloud-init has no such convention — there you would use
  `power_state`.
- **Jinja2 templating is not supported.** `## template: jinja` is
  detected and ignored; the body is read as-is.
- **`#part-handler` is not supported.** It is logged and ignored.
- **Boothooks are captured but not run.** `#cloud-boothook` parts are
  parsed and stored, never executed.
- **`runcmd` argv entries run verbatim.** A list (argv) entry is passed
  straight to the Windows process API with no shell re-quoting; a string
  entry goes through `cmd.exe`. cloud-init applies POSIX shell quoting.
- **Multipart requires the close delimiter.** A multipart payload must
  end with `--boundary--`; one that omits it is rejected rather than
  guessed at. A leading `From ` mbox line or `From:` header before the
  MIME headers is normal and read either way.
- **Quoted-printable is not decoded.** `base64`, `7bit`, `8bit`, and
  `binary` parts are handled; quoted-printable bodies pass through as raw
  bytes. It is rare in real payloads.

## Modules and keys

- **Random passwords are not supported.** `chpasswd.users[].type: RANDOM`,
  the `chpasswd.list` `R`/`RANDOM` tokens, and password-less
  `chpasswd.users` entries are rejected by `egs-service validate` and
  warn-skipped at runtime. cloud-init writes a generated password to
  `/dev/console`; Windows guests have no console channel reliably
  captured across the clouds eryph targets, so a generated password
  could never be retrieved — setting one would lock you out. Use an
  explicit password.
- **Linux-only keys are accepted and ignored, not run.** `apt`, `snap`,
  `packages`, `bootcmd`, `chef`, and similar parse cleanly and log at
  Info — they do nothing on Windows, where cloud-init would act on them.
  `egs-service validate --target windows` flags them per file.
- **`power_state: halt` falls back to hibernate.** Windows has no clean
  halt analogue.
- **Reporting is log + Hyper-V KVP only.** No webhook or cloud-native
  reporting backend.
- **Vendor-data is parsed but not applied.** Read off the datasource and
  discarded with an Info log; no merge into cloud-config.

## YAML parsing

The agent reads cloud-config exactly as cloud-init does — PyYAML's
`SafeLoader` under YAML 1.1 — so bool tokens (`yes`/`no`/`on`/`off`),
integer forms, `bool | string` union fields, and merge keys all behave
identically. Two deliberate departures, because both cloud-init
behaviours are footguns:

- **Sexagesimal integers stay strings.** cloud-init turns `12:30` into
  base-60 `750`. The agent keeps a colon-bearing scalar as a string, so
  unquoted times-of-day, ratios, and `host:port` values mean what they
  look like.
- **String fields keep their literal text.** cloud-init resolves the
  implicit type first, then stringifies it — `hostname: NO` becomes
  `"False"`, `version: 1.10` becomes `"1.1"`. When the target field is a
  string, the agent keeps the literal text you wrote (`NO` stays `"NO"`,
  `1.10` stays `"1.10"`).

## Same as cloud-init

Behaviour you can rely on being identical:

- Four stages (Local / Network / Config / Final); frequencies
  per-instance / per-boot / per-once; one semaphore file per
  (module, frequency, instance).
- `reset` mirrors `cloud-init clean` (per-instance + per-boot cleared,
  per-once kept unless `--reset-once`); `collect-logs` mirrors
  `cloud-init collect-logs`.
- Network-config v1 and v2 — IPv4 + IPv6, static and DHCP, per-interface
  routes, DNS servers and search suffixes, MTU.
- `chpasswd.expire` (defaults to `true`), `write_files.defer`,
  `prefer_fqdn_over_hostname`, and `users[].gecos` (mapped to the Windows
  full name).
- Full YAML 1.1 scalar parsing — bool tokens, octal/underscore/binary
  integers, `bool | string` unions decided by quoting, merge keys, and
  last-wins duplicate keys.
- Per-stage module selection (`stages.<stage>.enabledModules` /
  `disabledModules`) and datasource ordering (`dataSources.dataSourceList`),
  mirroring cloud-init's `cloud_*_modules` and `datasource_list`.
- Unknown top-level keys log and continue — Info for accepted Linux
  keys, Warning for typos — never fatal.
- Datasource probe loop with `WaitForReady` + backoff; the completion
  hook fires only on full success of the run.
