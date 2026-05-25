# Differences from cloud-init

The agent reuses cloud-init's vocabulary on purpose — same stage names,
module names, frequency labels, semaphore layout, and user-data formats.
If you know cloud-init, most things work the way you expect. This page
lists where the agent behaves differently in practice, so you know what
to rely on and what to avoid.

## Scripts and user-data

- **Script dispatch is filename-led.** A `.ps1` / `.cmd` / `.bat`
  extension on a multipart part (or the staged script name) decides the
  runner; the shebang (`#ps1`, `#!/...`) is only a fallback when there is
  no usable extension. cloud-init keys off the shebang first; this agent
  keys off the filename first.
- **Exit code 1003 means reboot-and-continue.** A `runcmd` entry or
  script that exits `1003` reboots the guest and resumes provisioning on
  the next boot. cloud-init has no such convention — there you would use
  `power_state`.
- **Jinja2 templating is not supported.** `## template: jinja` is
  detected and ignored; the body is read as-is. Don't ship templated
  user-data expecting variable substitution.
- **`#part-handler` is not supported.** It is logged and ignored. Custom
  part handlers do not run.
- **Boothooks are captured but not run.** `#cloud-boothook` parts are
  parsed and stored, but never executed.
- **`runcmd` argv entries run verbatim.** A list (argv) entry is passed
  straight to the Windows process API — no shell re-quoting. A string
  entry goes through `cmd.exe`. cloud-init applies POSIX shell quoting;
  on Windows the YAML list is the literal argument vector.
- **Multipart requires the MIME close delimiter.** Every multipart must
  end with `--boundary--`. A payload that omits it is rejected. A leading
  `From ` mbox envelope line or a `From:` header before the MIME headers
  is normal — the agent reads the multipart either way.
- **Quoted-printable transfer encoding is not decoded.** `base64`,
  `7bit`, `8bit`, and `binary` parts are handled; quoted-printable bodies
  pass through as raw UTF-8 bytes. It is rare in real cloud-init payloads.

## Modules

- **Fixed module catalog.** The agent ships a fixed set of modules:
  `Growpart`, `SetHostname`, `ApplyNetworkConfig`, `NtpClient`,
  `Timezone`, `SetLocale`, `UsersGroups`, `SetPasswords`, `SshModule`,
  `WriteFiles`, `WriteFilesDeferred`, `Runcmd`, `Licensing`,
  `ScriptsUser`, `PowerState`. Stage membership and intra-stage order are
  fixed; which modules run per stage is operator-configurable (see below).
  See [Modules](../reference/modules.md).
- **Linux-only and deferred cloud-config keys are accepted, not
  rejected.** Keys like `apt`, `snap`, `packages`, `bootcmd`,
  `phone_home`, and `chef` parse cleanly and are logged at Info — they do
  nothing on Windows, but a cross-cloud cloud-config round-trips without
  errors. `egs-service validate --target windows` surfaces the same per
  file.
- **Unknown top-level keys are logged, not fatal.** Schema-accepted
  Linux keys log at Info; a genuinely unknown key (typo, vendor
  extension) logs at Warning. Neither aborts the run — matching
  cloud-init's log-and-continue contract.
- **Random passwords are not supported.** cloud-init's
  `chpasswd.users[].type: RANDOM`, the `chpasswd.list` `R`/`RANDOM`
  tokens, and password-less `chpasswd.users` entries are all rejected by
  `egs-service validate` and warn-skipped at runtime. cloud-init writes a
  generated password to `/dev/console`; Windows guests have no console
  channel reliably captured across the clouds eryph targets, so a
  generated password could never be retrieved — setting one would
  silently lock you out. Specify an explicit password.
- **`chpasswd.expire` works the same.** Default `true` (every changed
  password must change at next login); `false` suppresses it; omitted
  reads as `true`. Applies to `chpasswd.users`, `chpasswd.list`, and the
  top-level `password` shorthand.
- **`write_files.defer` works.** Entries flagged `defer: true` run in the
  Final stage so they can reference users created earlier in the run.
- **`prefer_fqdn_over_hostname` works.** With the flag set, the first
  label of `fqdn` wins over `hostname`. Default precedence
  (hostname-first, fqdn-fallback) holds when the flag is absent or false.
- **`users[].gecos` maps to the Windows full name.** On Windows it sets
  the NTUser `FullName` field (the "Full name" column in `lusrmgr.msc`)
  rather than the `/etc/passwd` comment.
- **`power_state` is implemented.** `mode: halt` has no clean Windows
  analogue and falls back to hibernate.

## Reporting and datasources

- **Reporting is log + Hyper-V KVP only.** There is no webhook or
  cloud-native reporting backend. cloud-init's additional handlers are
  not shipped.
- **Vendor-data is parsed but not applied.** It is read off the
  datasource and discarded with an Info log. There is no merge into
  cloud-config.
- **Per-stage module lists are operator-configurable.** The `stages`
  settings block carries per-stage `enabledModules` / `disabledModules`
  allow/deny lists (case-insensitive, tolerates the `Module` suffix),
  mirroring cloud-init's `cloud_init_modules` / `cloud_config_modules` /
  `cloud_final_modules`. When a stage is absent, all its modules run.
- **The datasource list is operator-configurable.** `dataSources.dataSourceList`
  names the sources to probe, in the listed order (case-insensitive),
  mirroring cloud-init's `datasource_list`. When null/empty, all
  registered sources are probed in priority order; unknown names log at
  Warning and are skipped.
- **Semaphores live under Windows paths.** `%ProgramData%\eryph\provisioning\...`
  instead of `/var/lib/cloud/...`. The layout matches.

## YAML handling

The agent reads cloud-config the same way cloud-init does — PyYAML's
`SafeLoader` under YAML 1.1 — so idiomatic snippets behave identically.

- **YAML 1.1 boolean tokens.** `true`/`false`, `yes`/`no`, `on`/`off`,
  `y`/`n` and their case variants all resolve to a real boolean on
  boolean fields. `package_update: yes` is a bool, not the string
  `"yes"`.
- **`bool | string` union fields.** On `manage_etc_hosts`,
  `resize_rootfs`, and `power_state.condition`, your quoting decides the
  type: plain `condition: true` is a bool (proceed/skip); quoted
  `condition: "true"` is the shell command `/bin/true`.
- **YAML 1.1 integer forms.** Leading-zero octal (`0644` → 420),
  underscore separators (`1_000` → 1000), binary (`0b101`), hex
  (`0x1F`), and the YAML 1.2 `0o`/`0x`/`0b` prefixes all resolve as
  cloud-init resolves them.
- **YAML merge keys (`<<: *anchor`).** Anchored bases merge into mappings;
  local keys override merged-in keys. Lets you factor shared user or
  interface settings into a YAML anchor, same as cloud-init / netplan.
- **Duplicate mapping keys are last-wins**, silently, same as cloud-init.

Two deliberate departures from cloud-init's YAML quirks, because both are
footguns rather than features:

- **Sexagesimal integers stay strings.** cloud-init turns `12:30` into
  base-60 `750`. The agent keeps a colon-bearing scalar as a string, so
  unquoted times-of-day, ratios, and `port:host` values mean what they
  look like.
- **String fields keep their literal text.** cloud-init resolves the
  implicit type first, then stringifies it — `hostname: NO` becomes
  `"False"`, `version: 1.10` becomes `"1.1"`. When the target field is a
  string, the agent keeps the literal text you wrote (`NO` stays `"NO"`,
  `1.10` stays `"1.10"`).

## Things that match deliberately

- Four stages: Local / Network / Config / Final.
- Frequencies: per-instance / per-boot / per-once.
- One semaphore file per (module, frequency, [instance]); the marker file
  body is diagnostic JSON, only its existence gates execution.
- `reset` mirrors `cloud-init clean`: per-instance + per-boot cleared,
  per-once kept unless `--reset-once`.
- `collect-logs` mirrors `cloud-init collect-logs`.
- Datasource probe loop with `WaitForReady` + backoff.
- The completion hook fires only on full success of the run.
- Network-config v1 and v2 are both accepted.
