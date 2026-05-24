# Code review — `provisioning-agent` branch

> Review of everything added on this branch since `main`. The branch
> introduces the provisioning agent (cloud-init/cloudbase-init parity on
> Windows) plus the supporting data-source, user-data pipeline, YAML
> serializer, reporting, semaphores and Windows OS adapter.
>
> The cloud-init drift catalog
> [`docs/user/explanation/differences-from-cloud-init.md`](user/explanation/differences-from-cloud-init.md)
> lists deliberate divergences. **Items already documented there are not
> flagged below.** Everything below is either an undocumented drift, a
> shortcut, or an internal inconsistency.

---

## High severity — likely to surprise operators

### 1. `CloudConfigMerge.Merge` silently drops ~15 fields after every merge

**RESOLVED in Phase 1:** `a3fd73d` (source-generated merge derived from
model attributes; every `[CloudInitField]` participates in the merge).

`src/Eryph.GuestServices.Provisioning/UserData/CloudConfigMerge.cs:24-38`
constructs the merged record by hand and only copies these properties:

```
Hostname, Fqdn, PreserveHostname, Users, Groups, Chpasswd,
Password, SshPwauth, SshAuthorizedKeys, WriteFiles, Runcmd
```

Every other field on `CloudConfig` (`Timezone`, `Locale`, `Keyboard`,
`Ntp`, `Growpart`, `License`, `PowerState`, `FinalMessage`,
`PackageUpdate/Upgrade/RebootIfRequired`, `DisableRoot`,
`DisableRootOpts`, `YumRepoDir`, `ManageResolvConf`, plus every
`object?` acknowledged-key) is dropped to its default (`null`) the
moment a second fragment is merged in.

Consequence: any multipart or `#include`-driven cloud-config that
relies on stacking `timezone` / `ntp` / `power_state` / `license` /
`keyboard` / `growpart` from a vendor-data part and a user-data part
will lose the vendor half. The existing tests in
`CloudConfigMergeTests.cs` only cover the 11 fields the implementation
*does* copy, so the gap is invisible from green tests.

This violates the global merge contract documented in the file header
("dicts (records): deep-merged field by field"). Cloud-init's default
merger walks every key.

**Fix shape:** reflect over the record's properties and merge
field-by-field with `right ?? left` semantics (deep-merge nested
records, concat lists, MergeByName the keyed lists). Or at minimum
extend the hand-rolled copy to cover every field and add a unit test
that asserts *every* non-null left field survives a merge with an empty
right.

### 2. `UserConfigYamlTypeConverter` is strict where the rest of the schema is tolerant

**RESOLVED in Phase 2:** `85ffb08` / `8091f2a` (UserConfig expanded to
the full cloud-init Linux schema — `gecos`, `hashed_passwd`,
`ssh_import_id`, `ssh_redirect_user`, `expiredate`, `no_create_home`,
`no_user_group`, `no_log_init`, `selinux_user`, `uid`, `snapuser` all
modeled; converter softened so unmodelled keys flow through tolerantly).

`src/Eryph.GuestServices.CloudConfig.Yaml/Converters/UserConfigYamlTypeConverter.cs:38`
calls
`typeInspector.GetProperty(typeof(UserConfig), null, propertyName.Value, ignoreUnmatched: false, caseInsensitive: true)`.

The top-level deserializer uses `.IgnoreUnmatchedProperties()` (and
`CloudConfigYamlSerializer.cs:26` documents that as the deliberate
"warn-and-continue" mirror of cloud-init's runtime tolerance). The
custom converter for `users[]` overrides that and *throws* on any
property `UserConfig` doesn't model.

`UserConfig` only models 12 cloud-init user keys. The very common
Linux-only keys cloud-init accepts — `gecos`, `hashed_passwd`,
`ssh_import_id`, `ssh_redirect_user`, `expiredate`, `no_create_home`,
`no_user_group`, `no_log_init`, `selinux_user`, `uid`, `snapuser` — will
each cause a `YamlException` instead of being ignored.

A single `gecos: "Full Name"` line in a cross-cloud cloud-config makes
the whole user record (and therefore the whole `users:` block) fail to
parse. This is the opposite of the documented top-level behaviour.

**Fix shape:** pass `ignoreUnmatched: true` and log Info (mirroring
`CloudConfigSerializer`'s acknowledged-key tier) for each unmodelled
user property.

### 3. Several user-config properties are typed too narrowly vs. cloud-init

**RESOLVED in Phase 2:** `85ffb08` (Sudo → `IReadOnlyList<string>?`,
Inactive → `string?`, plus the missing keys listed under finding 2).

`src/Eryph.GuestServices.CloudConfig/Configs/UserConfig.cs`:

- `Sudo` is `string?`. Cloud-init accepts a string **or a list of
  strings** (sudoers rules). A list-valued `sudo:` in a real cloud-init
  YAML will fail to deserialize.
- `Inactive` is `bool?`. Cloud-init accepts bool **or string** ("number
  of days"). Numeric or string forms will fail.
- `gecos`, `ssh_import_id`, `hashed_passwd`, `ssh_redirect_user` are
  not present at all — by (2) above this currently means parse error,
  by (2) fixed, this would mean silent loss of the value.

Even where we don't intend to act on these fields, they should at least
parse without breaking the user block. `gecos` in particular has a
clean Windows mapping (`LocalUserSpec.FullName`, which `WindowsOs.UpdateLocalUserAsync`
already plumbs through — see (4)).

### 4. `gecos` → `FullName` plumbing exists but is never wired up

**RESOLVED in Phase 3.4:** `UsersGroupsModule.ProcessUsersAsync` now
populates `LocalUserSpec.FullName` and `Comment` from `user.Gecos` so
the existing `WindowsOs.UpdateLocalUserAsync` / `NetUserHelpers.SetFullName`
path is reachable.

`src/Eryph.GuestServices.Provisioning/Windows/WindowsOs.cs:89`:

```csharp
if (spec.FullName is not null && !string.Equals(current.usri2_full_name, spec.FullName, StringComparison.Ordinal))
    NetUserHelpers.SetFullName(spec.Name, spec.FullName);
```

`LocalUserSpec.FullName` exists, `NetUserHelpers.SetFullName` exists,
the `UpdateLocalUserAsync` branch is implemented. But
`UsersGroupsModule` builds the spec with `Comment = null` and never
sets `FullName` because `UserConfig.Gecos` does not exist. Result: an
ad-hoc plumbing layer is built and tested but cannot be reached by any
cloud-config input today.

### 5. `MultipartMimeHandler` UTF-8 round-trips binary `8bit`/`binary` parts

`src/Eryph.GuestServices.Provisioning/UserData/Handlers/MultipartMimeHandler.cs:83-110`:

```csharp
var text = UserDataEncoding.DecodeUtf8(part.Body);
…
return Encoding.UTF8.GetBytes(body);  // 7bit / 8bit / binary fallback
```

The whole multipart blob is decoded as UTF-8 into a string, and parts
without `Content-Transfer-Encoding: base64` are re-encoded back to
UTF-8 bytes. Any byte 0x80+ in an `8bit` or `binary` transfer-encoded
part is now mangled to U+FFFD or a multi-byte UTF-8 sequence by the
round-trip.

This contradicts the binary-contracts pattern the rest of the codebase
follows (`NoCloudDataSource` and `AzureDataSource` both deliberately
read user-data as `byte[]` and the file header for `NoCloudDataSource`
documents *why*). The handler should hold the multipart body as bytes
and slice boundary regions out without going through `Encoding.UTF8`.

There is no regression test that puts a 0x80+ byte through a
`Content-Transfer-Encoding: 8bit` part; per
`feedback_binary_contracts.md`'s rule that should exist.

### 6. `SetUserSshAuthorizedKeysAsync` overwrites authorized_keys; cloud-init merges

`src/Eryph.GuestServices.Provisioning/Windows/WindowsOs.cs:268-269`:

```csharp
var bytes = new UTF8Encoding(false).GetBytes(content);
File.WriteAllBytes(keyFile, bytes);
```

`SshAuthorizedKeysModule` is `[Stage(Config), Order=2,
Frequency=PerInstance]`, so the same module will run once per instance
— but if an operator subsequently rotates keys via a second cloud-init
include or extends them via vendor-data, cloud-init's `ssh_util.py`
merges + de-duplicates against the existing `authorized_keys`. This
implementation blows away whatever the user (or a previous run) put
there.

Likewise, two cloud-config fragments that each declare
`ssh_authorized_keys` will be concatenated in `CloudConfigMerge.Concat`
(good) but on disk the second module run would still overwrite — not
relevant for the per-instance semaphore case, but relevant for `reset
--rerun` workflows where the operator expects to *add* keys rather
than swap.

`differences-from-cloud-init.md` does not mention this. It should
either be documented as deliberate (and the cloud-init contract
explicitly waived) or fixed to merge.

### 7. IPv6 / routes / DNS search are completely absent from `ApplyNetworkConfigModule`

`src/Eryph.GuestServices.Provisioning/Modules/ApplyNetworkConfigModule.cs`:

- `NetworkEthernetConfig` carries `Dhcp6`, `Gateway6`, `Routes`,
  `Nameservers.Search`.
- The module reads `Dhcp4`, `Addresses`, `Gateway4`, `Nameservers.Addresses`,
  `Mtu`. The other fields are silently discarded.

The serializer parses them (so they parse cleanly), the model carries
them, but no `IWindowsOs` method consumes them. Operators who write
RFC-2002-style v1 `routes:` entries (cloud-init does honor them) or
configure search domains (cloud-init does honor them) will not see
their config applied. This isn't platform-driven: `netsh
interface ipv4 add route` and `Set-DnsClient -ConnectionSpecificSuffix`
both exist; nobody has wired them up.

RFC 0002 should call this out under "not implemented yet"; today it
reads as if v1/v2 are fully applied.

### 8. `WriteFileConfig.Defer` is parsed but ignored

**RESOLVED in Phase 3.1:** Config-stage `WriteFilesModule` now skips
`defer: true` entries; new `WriteFilesDeferredModule` at
`[Stage(Final), Order=-1]` processes them after users / groups /
passwords have been applied. Per-entry logic is shared via
`WriteFilesProcessor`.

`src/Eryph.GuestServices.CloudConfig/Configs/WriteFileConfig.cs:17`
defines `bool? Defer`. The cloud-init contract: `defer: true` postpones
the write until the Final stage (e.g. so an earlier module can prepare
the path or so the file ends up under a user that the `users` module
created).

`WriteFilesModule` is `[Stage(Stage.Config), Order=3]` and walks every
entry unconditionally. `entry.Defer` is never read. A cloud-init author
writing `defer: true` will see the file appear at Config time, before
Final-stage modules and scripts run, which can break expectations
(e.g. a file with `owner: my-new-user` written before `users` ran).

### 9. `chpasswd.expire` accepted but ignored

**RESOLVED in Phase 3.2:** `SetPasswordsModule` now reads
`config.Chpasswd?.Expire ?? true` once at the top and threads the
flag through `ProcessChpasswdUsersAsync`, `ProcessChpasswdListAsync`,
and `ProcessPasswordShorthandAsync` so the
`IWindowsOs.SetLocalUserPasswordAsync(..., mustChangeAtNextLogon, ...)`
call honours it. Default mirrors cloud-init: `true`.

`SetPasswordsModule.SetPasswordAsync` (line 125) hardcodes
`mustChangeAtNextLogon: false`. `ChpasswdConfig.Expire` is in the
model and survives the merge, but nothing reads it. Cloud-init's
contract: `expire: true` (the default!) flags every changed password
for "must change at next login".

This is silently the opposite default from cloud-init: cloud-init
expires by default, this agent never expires. Either implement and
honor `chpasswd.expire`, or document it under
`differences-from-cloud-init.md` so operators don't expect cloud-init
default behaviour.

### 10. `chpasswd.list` does not honor `R` / `RANDOM` after the colon

**RESOLVED in Phase 3.3:** `ProcessChpasswdListAsync` now recognises
the exact-case `R` / `RANDOM` tokens after the colon and routes
through the existing `GenerateRandomPassword()` helper. Regression
test locks the split-on-first-colon behaviour
(`bob:literal:colon:RANDOM` → literal password).

`SetPasswordsModule.ProcessChpasswdListAsync` (line 82-102) splits each
line on the first colon and uses the right-hand side verbatim as the
password. Cloud-init's `cc_set_passwords.py` accepts `user:RANDOM` and
`user:R` as "generate a random password for this user", logging the
result in the same way as `chpasswd.users[].type: RANDOM`. Here
`bob:RANDOM` would set bob's password to the literal string `"RANDOM"`.

The `users:[].type: RANDOM` form is implemented (line 52-62), so the
shortfall is just in the list-form parser. Easy fix; missing today.

---

## Medium severity — internal inconsistencies / drift not platform-driven

### 11. Random passwords have no out-of-band channel

`SetPasswordsModule.cs:58-60`:

```csharp
// TODO(C-fix): once IReportingDispatcher exposes a secret-reporting
// channel (e.g. a GeneratedCredential event), pipe `password`
// through it here so the orchestrator can retrieve it.
```

Today `chpasswd[].type: RANDOM` generates a password, sets it, and
logs only `"Generated random password for '{User}'."` — the bytes are
gone. Cloud-init writes the password to
`/var/log/cloud-init-output.log` (configurable). Here the operator has
no way to learn the value. The TODO is honest; this needs landing
before the feature is usable.

### 12. `SetHostnameModule` drops `fqdn` when `hostname` is also present

**RESOLVED in Phase 3.6:** `prefer_fqdn_over_hostname` modeled on
`CloudConfig` (Phase 2A) and now honored in `SetHostnameModule.PickName`:
with the flag set, the FQDN's first label wins over `hostname`.
Default precedence (hostname-first, fqdn-fallback) is unchanged when
the flag is absent or false. Setting the FQDN as the primary DNS
suffix is still on the as-yet-unscheduled list.

`SetHostnameModule.PickName` (line 47-58) takes `hostname` if non-empty
and otherwise the first label of `fqdn`. Cloud-init applies BOTH:
`hostname` as the kernel/local hostname and `fqdn` to populate
`/etc/hosts` (so reverse DNS sees the FQDN). On Windows the closest
analogue is `Set-DnsClient -ConnectionSpecificSuffix` + the primary
DNS suffix in System Properties → Computer Name → More. Neither is
applied. Also `prefer_fqdn_over_hostname` (the cloud-init flag that
swaps the preference) is not modeled at all.

If "we only track NetBIOS-style on Windows" is deliberate, document it.

### 13. `WriteFilesModule` skips files with unknown encoding; cloud-init falls back to plain

`WriteFilesModule.DecodeContent` (line 121) raises
`NotSupportedException` on unrecognised encodings, and the caller
catches and **skips the entry**:

```csharp
catch (Exception ex)
{
    logger.LogError(...);
    continue;
}
```

Cloud-init's `decode_perms` / content decode treats unknown encodings
as "fall back to UTF-8 plaintext, log warning". So a typo like
`encoding: gz-b64` (note the dash vs `+`) drops the file silently
instead of writing the raw text.

### 14. `Sudo` is a string-only field; cloud-init `sudo:` accepts list

**RESOLVED in Phase 2B / 3.5:** `UserConfig.Sudo` widened to
`IReadOnlyList<string>?`. `UsersGroupsModule.IsSudoEnabled` and
`SshAuthorizedKeysModule.PickAdminUser` collapse the list to the
binary "is the user an Administrator" decision: any non-`"false"`
entry promotes. Mixed-list policy (`["NOPASSWD:ALL", "false"]` →
promote) documented and locked by a regression test.

`UsersGroupsModule.IsSudoEnabled` (line 129) handles the
disable-on-`"false"` rule correctly, but `UserConfig.Sudo` is typed as
`string?`. A YAML list (`sudo: ["ALL=(ALL) NOPASSWD:ALL"]`) raises a
YAML exception today. Even if we don't have a per-rule Windows mapping,
"any non-disabling value enables sudo / Administrators" should accept
the list form so cross-cloud YAML doesn't break.

### 15. `PrimaryGroup` survives the merge but `UsersGroupsModule` never reads it

**RESOLVED (partial) in Phase 2B:** `PrimaryGroup` is now an explicitly
cross-platform property on `UserConfig` (Linux semantics:
useradd --gid). On Windows we do not have a workable mapping ("primary
group" is not a first-class user attribute), so the field is parsed +
preserved through the merge but applied as a no-op. Status moves from
"dead config" to "documented divergence" — see modules.md.

`CloudConfigMerge.MergeUser` (line 51) carries `PrimaryGroup` through.
`UsersGroupsModule.ProcessUsersAsync` ignores it. On Windows the
concept is awkward but mappable to "set this user's default group to
X". Today it's just dead config — either implement or drop the field.

### 16. `YamlSchemaTypeResolver` is labelled "PyYAML-equivalent YAML 1.2"; PyYAML defaults to 1.1

`src/Eryph.GuestServices.CloudConfig.Yaml/Converters/YamlSchemaTypeResolver.cs`
class-doc and `ResolvePlainScalar` implement the YAML 1.2 core schema:
only `true`/`True`/`TRUE` and `false`/`False`/`FALSE` are bools.

PyYAML's `SafeLoader` (the loader `cloud-init` uses via `yaml.safe_load`)
still applies the YAML 1.1 bool regex by default, which additionally
treats `yes`/`Yes`/`YES`/`no`/`No`/`NO`/`on`/`On`/`ON`/`off`/`Off`/`OFF`/
`y`/`Y`/`n`/`N` as bools.

So `manage_etc_hosts: yes` (a real, idiomatic cloud-init snippet — see
e.g. Ubuntu's cloud-images defaults) deserializes to bool `true` in
cloud-init and to the string `"yes"` on this agent. The
acknowledged-key inventory in `CloudConfigSerializer` checks
`c.ManageEtcHosts is not null` so it still surfaces the value, but the
runtime type is observably different from what cloud-init's modules
see.

Either (a) extend the resolver to YAML 1.1 bool tokens to match PyYAML,
or (b) update the class-doc and `differences-from-cloud-init.md` to
say the agent uses YAML 1.2 deliberately and operators must quote
`yes`/`no`/etc. to keep them as strings.

### 17. NoCloud `seedfrom` is not implemented

`NoCloudDataSource.ReadAsync` reads `meta-data`, `user-data`,
`vendor-data`, `network-config` from the volume root. Cloud-init's
NoCloud also reads a `seedfrom:` URL out of the meta-data and refetches
the seed from there (used for net-boot scenarios). The line-scalar
meta-data parser would even successfully find a `seedfrom:` line — but
nothing in the code branches on it. Cross-cloud fodder that ships with
`seedfrom: http://...` will be silently incomplete.

### 18. NoCloud meta-data parser cannot read YAML maps

`NoCloudDataSource.ParseYamlScalars` (line 136-158) is a flat
`key: value` reader that drops everything after the first colon and
strips leading/trailing quotes. Real-world cloud-init NoCloud
meta-data legitimately uses mappings:

```yaml
public-keys:
  default-key: ssh-rsa AAAA...
network-interfaces: |
  iface eth0 inet static
  ...
```

These will be silently empty in `MetaData`. The instance-id-only check
gates discovery and ignores everything else, so this is currently
hidden. The agent should at minimum parse the structure with the
existing YamlDotNet plumbing rather than a hand-rolled scalar
splitter.

### 19. `ConfigDriveDataSource` only probes `openstack/latest/`

`ConfigDriveDataSource.ReadAsync` (line 66) hardcodes
`openstack/latest/`. Cloud-init walks `openstack/2018-08-27/`,
`2017-02-22/`, ... down to `latest/` and uses the first present
version. OpenStack-derived ConfigDrive ISOs that don't write the
`latest` symlink will not be picked up.

### 20. ConfigDrive `public_keys` nested object flattens to raw JSON string

`ConfigDriveDataSource.FlattenJson` (line 153-181) stores nested object
values via `GetRawText()` — so an OpenStack meta_data.json carrying
`"public_keys": {"key1": "ssh-rsa ..."}` lands in `MetaData` as the
literal string `{"key1": "ssh-rsa ..."}`. Cloud-init exposes those keys
as a proper dict and the OpenStack datasource layer auto-merges them
into the default user's `authorized_keys`. Nothing reads
`MetaData["public_keys"]` today, so this is silent today, but it is a
real divergence from cloud-init's ConfigDrive contract.

### 21. `Process.OutputDataReceived` uses default console code page

`WindowsOs.RunAsync` collects stdout/stderr via
`OutputDataReceived` / `ErrorDataReceived`. Without setting
`StandardOutputEncoding` / `StandardErrorEncoding`, .NET decodes with
the console code page (CP437 / CP1252 / etc.). Any UTF-8 stdout from a
PowerShell script will be mangled — and that stdout is what
`ScriptsUserModule.WriteLogAsync` writes verbatim to the per-script
log file.

This isn't a cloud-init drift per se but it intersects with the
binary-contracts pattern the rest of the codebase follows. The
recommended pattern on Windows is to set
`StandardOutputEncoding = Encoding.UTF8` and have the script start
with `[Console]::OutputEncoding = [Text.UTF8Encoding]::new()` or pass
`-OutputFormat XML` for structured capture.

### 22. `BootSessionDetector` returns "new boot" when the boot id is unreadable

**RESOLVED in Phase 3.9:** when the boot-id source fails AND a marker
exists, the detector now fails closed (returns `false` → "same boot"),
suppressing per-boot module re-runs on systems with a chronically
broken `Win32BootClock`. First-run-with-no-marker still returns
`true`. Behaviour documented on the method's XML doc.

`BootSessionDetector.IsNewBootAsync` (line 36-47): an unreadable boot
id → log warning + return `true`. That causes `ClearPerBootAsync` to
run, which means per-boot modules re-execute on every run instead of
once per boot. The comment defends the choice as "safer than
suppressing" but the practical effect on a system where `Win32BootClock`
throws every run (e.g. a misconfigured WMI) is that `GrowpartModule`
(the only `PerBoot` module today) runs on every `egs-service` cycle —
not catastrophic, but worth knowing about. Failing closed (treat as
*same* boot when the marker exists and the read fails) might be a
better default.

### 23. The `IsAzureDataSource` check leaks a magic string into `LicensingModule`

**RESOLVED in Phase 3.8:** `LicensingModule.IsAzureDataSource` now
checks `context.DataSource.PlatformMetadata?.CloudName == "azure"` and
the `AzureSourceName` constant is gone. Regression test asserts the
detection follows `CloudName` and not `SourceName`.

`src/Eryph.GuestServices.Provisioning/Modules/LicensingModule.cs:29`:

```csharp
private const string AzureSourceName = "Azure";
```

with a comment "kept in sync with AzureDataSource.cs". This is the only
data source the licensing module special-cases. The reason
(`set_avma`/`set_kms` skipped on Azure because Azure handles activation
natively) is documented — but coupling by string match is brittle. An
`IDataSource.Capabilities` flag (e.g. `PlatformOwnsActivation`) or a
property on `DataSourceResult.PlatformMetadata.CloudName == "azure"`
(already present!) would close the gap. The cloud name is already
populated; switching to it would also remove the constant.

### 24. `CloudConfigSerializer`'s acknowledged-key inventory fires once per multipart fragment

**RESOLVED in Phase 1:** the acknowledged-key surface is now driven by
the source-generated `CloudConfigPlatformInventory.Fields` instead of a
hand-curated parallel list, so adding a new Linux-only key updates the
log surface automatically. The duplicate-emission concern from
multipart fragments is unchanged at runtime, but Phase 3.10 exposes
the same inventory as `egs-tool validate --target windows`, giving
authors a one-shot static-analysis surface that runs against the
already-merged config and emits each warning exactly once.

`CloudConfigSerializer.Deserialize` (line 76-82) walks
`AcknowledgedKeys` for every call. The pipeline parses each
`cloud-config` part separately and merges the results, so a four-part
multipart with `apt:` in two parts will log `"cloud-config: 'apt' is
acknowledged but not applied on Windows"` twice. That's mild noise
rather than a bug, but worth folding into the merge step so the
inventory is emitted once on the final composed `CloudConfig`.

---

## Low severity — design oddities worth noting

### 25. `UserDataPipeline` keeps `ICloudConfigSerializer` + `IUrlHelper` as fields it never reads

`UserData/UserDataPipeline.cs:38-40` exposes both via public
properties; the constructor comment admits

> required by the task spec on the pipeline's constructor signature
> even though the handlers consume them directly via DI

i.e. the fields exist solely to satisfy a constructor signature
contract. If no caller consumes them, drop the parameters (and remove
the public surface).

### 26. `Boothook` is collected with a full payload but never executed

**ACK in Phase 4:** still deferred to RFC 0013 by design. The
unused-collection concern persists but is the v1 contract — work to
drop or wire it lands with the RFC implementation, not this review
cycle. Recording here so the finding stays visible.

`UserData/Handlers/BoothookPartHandler.cs` captures the byte body and
filename into `ctx.AddBoothook(...)`, and nothing reads
`ResolvedUserData.Boothooks` today. This is explicitly deferred in
`differences-from-cloud-init.md`, so the *intent* is documented — but
the collection of bytes is unused work and a small memory footprint
that survives across the resolution graph. Either drop until RFC 0013
lands, or wire a `BoothookModule` that at least logs the count.

### 27. `IModuleContext.DataSource` doc claims "unused in v1"

**RESOLVED in Phase 3.7:** doc rewritten to list the three call sites
(`LicensingModule`, `ApplyNetworkConfigModule`, `ScriptsUserModule`).

`Modules/IModuleContext.cs:11-12`:

```csharp
// Exposed for modules that need raw metadata beyond the parsed CloudConfig
// (e.g. instance id for state correlation, host-name fallback). Unused in v1.
DataSource { get; }
```

`LicensingModule.IsAzureDataSource`, `ApplyNetworkConfigModule`
(network-config off the datasource), and `ScriptsUserModule`
(`context.DataSource.InstanceId` for the checkpoint store) all read it.
The comment is stale — update or drop it.

### 28. `UrlHelper.FetchAsync` has no response-size cap

`UserData/UrlHelper.cs:39-105` `ReadAsByteArrayAsync`'s a remote
response in full with no size limit. The HTTP client has a per-attempt
timeout (configurable) but a slow attacker dribbling bytes within the
timeout could exhaust memory. The trust boundary is
operator-supplied user-data, so this is low-risk today, but worth a
cap on `Content-Length` / a streaming limit for the `#include` URL
flow.

### 29. `RuncmdModule` continues past failures; documents say "exit code propagates"

`RuncmdModule.ApplyAsync` continues on non-zero exit codes (line 52-55).
The user reference `docs/user/reference/modules.md` should make this
loud — cloud-init does the same (it logs, doesn't abort the module),
but operators coming from "shell scripts stop on first failure" need
the heads-up. Currently the contract is implicit in the code.

### 30. `MaxRebootsPerModule = 3` and `MaxRebootsPerScript = 2` are independent

`StageRunner.MaxRebootsPerModule = 3` and
`ScriptsUserModule.MaxRebootsPerScript = 2` are both guards against
1003-loop scenarios. Currently they live in different files with
different numeric caps and no shared knob in
`ProvisioningSettings`. If `ScriptsUserModule` itself reboots three
times across two different scripts, both caps trip independently. The
two caps should either be unified or the relationship documented in
RFC 0010 / docs/bugs/0001.

---

## Notes that are *not* findings (verified deliberate)

These looked suspicious during the read but are intentional and
covered elsewhere:

- **Filename-led script dispatch** — RFC 0007 + memory
  `cbi_compat_constraints`; cbi ignores shebangs and we have to mirror
  it.
- **`reboot-and-continue` exit-code 1003** — cloudbase-init convention,
  documented; both `RuncmdModule` and `ScriptsUserModule` honor it.
- **Multipart close-delimiter optional** — documented in
  `differences-from-cloud-init.md`; eryph-zero's configdrive emits
  without it.
- **`AzureDataSource` declines to touch the wireserver** — RFC 0008 /
  0014 + `azure-wireserver-analysis.md`; coexistence with PA/WinGA.
- **`ConfigDrive` / `NoCloud` decline to claim a volume when Azure is
  detected** — defensive opt-out, memory
  `project_eryph_cross_cloud_scope` covers it.
- **`PosixPermissions` always grants SYSTEM + Administrators FullControl
  regardless of POSIX bits** — required so Defender / backup can read
  the file; documented in `cloudbase-init` parity comment.
- **`VendorData` parsed but discarded** — documented divergence,
  RFC 0001.
- **`power_state.halt` falls back to hibernate** — documented
  divergence; `shutdown /h`.
- **`OnCompletedAsync` only runs on full success** — documented;
  matches cloudbase-init.
- **`growpart` mode-only "auto" / "off"** — Linux modes (`growpart`,
  `gpart`) cannot map to Windows; documented in the module.
- **Trim-safety / source-generated JSON contexts** — required by NAOT
  publish path on the guest binary.

---

## Suggested next steps in priority order

1. Rewrite `CloudConfigMerge.Merge` so every field is merged; add a
   reflection-based unit test that fails if a new `CloudConfig`
   property is added without merge coverage. (Finding 1)
2. Soften `UserConfigYamlTypeConverter` to `ignoreUnmatched: true`
   and add `gecos` / `ssh_import_id` to `UserConfig`. Wire `gecos`
   through to `LocalUserSpec.FullName`. (Findings 2, 3, 4)
3. Hold the multipart body as `byte[]` end-to-end and add an `8bit`
   binary-part regression test with 0x80+ bytes. (Finding 5)
4. Decide whether `ssh_authorized_keys` overwrites or merges, document
   in `differences-from-cloud-init.md` either way, fix or note.
   (Finding 6)
5. Either implement v1/v2 IPv6 + routes + DNS search, or call them out
   in RFC 0002 as "v1 covers IPv4 only". (Finding 7)
6. Wire `chpasswd.expire` to `SetLocalUserPasswordAsync`'s
   `mustChangeAtNextLogon` and handle `RANDOM`/`R` in
   `chpasswd.list`. (Findings 9, 10)
7. Implement `WriteFileConfig.Defer` (move deferred files to a
   Final-stage pass) and fall back to plaintext on unknown encoding
   like cloud-init does. (Findings 8, 13)
8. Reconcile the YAML 1.2 / 1.1 bool resolution mismatch: extend the
   resolver or document the requirement to quote
   `yes`/`no`/`on`/`off`. (Finding 16)
9. Land a secret-reporting channel for `chpasswd[].type: RANDOM` (the
   in-code TODO already names it). (Finding 11)
