# Azure CustomData: encrypted or just base64?

## Verdict

**PLAINTEXT (base64-only).** CustomData in `ovf-env.xml` / `C:\AzureData\CustomData.bin` is not PKCS#7-encrypted. Every reference PA decodes it from base64 and writes the bytes verbatim. The PKCS#7 envelope in ovf-env wraps `AdminPassword` and the `LinuxConfigurationSet` SSH key material, **not** CustomData.

## Evidence

1. **cloud-init helpers (`helpers/azure.py`)** — `_parse_property("CustomData", decode_base64=True, ...)` (~line 1282); `_parse_property` only base64-decodes (`base64.b64decode("".join(value.split()))`, ~line 1240). PKCS#7 CMS decryption (`_decrypt_certs_from_xml`, ~line 705) is invoked **only** on the `certificates_xml` bundle (SSH keys arrive via that bundle, keyed by thumbprint). No code path applies a cert to `CustomData`. ([helpers/azure.py](https://raw.githubusercontent.com/canonical/cloud-init/main/cloudinit/sources/helpers/azure.py))
2. **cloud-init `DataSourceAzure.py`** — `ud = ovf_env.custom_data or ""` is the only touch; written to `userdata_raw` and persisted as cloud-init userdata. AdminPassword is read as **plaintext** from the OVF (hence `_redact_password()` to scrub it from logs); no cert is consulted for CustomData. ([DataSourceAzure.py](https://raw.githubusercontent.com/canonical/cloud-init/main/cloudinit/sources/DataSourceAzure.py))
3. **WALinuxAgent `protocol/ovfenv.py`** — `self.customdata = findtext(conf_set, "CustomData", namespace=wans)` — read as XML text, no `CertificateThumbprint` attribute on the element, no decryption. `pa/provision/default.py` then calls `self.osutil.decode_customdata(customdata)` which is a **base64 decode** (`DefaultOSUtil.decode_customdata` in `osutil/default.py`); the result is written to `<lib_dir>/CustomData`. ([ovfenv.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/common/protocol/ovfenv.py), [pa/provision/default.py](https://raw.githubusercontent.com/Azure/WALinuxAgent/master/azurelinuxagent/pa/provision/default.py))
4. **cloudbase-init `azureservice.py`** — `get_user_data()` reads `CUSTOM_DATA_FILENAME` from the config-set drive with `open(..., 'rb').read()` and explicitly comments `# Don't decode to retain compatibility` in `get_decoded_user_data`. PKCS#7 (`decode_pkcs7_base64_blob`) is used in this file **only** for the certificates fetched from wireserver — never on CustomData. ([azureservice.py](https://raw.githubusercontent.com/cloudbase/cloudbase-init/master/cloudbaseinit/metadata/services/azureservice.py))
5. **MS docs ([custom-data](https://learn.microsoft.com/en-us/azure/virtual-machines/custom-data))** — "you must Base64-encode the contents before passing the data to the API"; "Custom data is placed in *%SYSTEMDRIVE%\AzureData\CustomData.bin* as a binary file"; and the explicit FAQ "Can I place sensitive values in custom data? **We advise not to store sensitive data in custom data.**" That guidance only makes sense if it isn't encrypted.

## Implication for RFC 0014/0015

**RFC 0015 (decrypt CustomData via OSProfile cert) is unnecessary and rests on a false premise.** There is no PKCS#7 envelope to decrypt — `C:\AzureData\CustomData.bin` is already the bytes the user submitted (base64 decoded once by PA). Our agent should read `CustomData.bin` directly. The OSProfile cert / `CertificateThumbprint` machinery exists for `AdminPassword` and SSH key material, both of which PA already applies during OOBE; we have no need to redo that work. RFC 0015 should be either dropped or rescoped to "where do we find CustomData and how do we treat it as cloud-config" — no crypto involved.

## Corrections to azure-wireserver-analysis.md

Several Section 3 + Section 7 + Section 9 claims need to be rewritten:

- **Section 3, step 6**: "The OSProfile-supplied cert that matches `CertificateThumbprint` in `ovf-env.xml` is then used to decrypt the `CustomData` PKCS#7 envelope" — **wrong**. CustomData has no `CertificateThumbprint` and no PKCS#7 envelope. The cert bundle in ovf-env is used to decrypt `AdminPassword` and the keys under `LinuxConfigurationSet/SSH`. Rewrite step 6 to: "The decrypted PEM bundle is indexed by thumbprint and used to decrypt `AdminPassword` and SSH key payloads referenced via `CertificateThumbprint` in `ovf-env.xml`. `CustomData` is **not** encrypted — it is base64-decoded and written verbatim to `C:\AzureData\CustomData.bin`."
- **Section 3, "Key finding for our agent"**: The framing "RFC 0015's plan to decrypt CustomData ... is viable" is moot. Replace with: "CustomData requires no decryption. The local-cert-store path matters only if we ever need to read encrypted AdminPassword/SSH material, which PA already applied during OOBE — so we don't."
- **Section 7, bullet 1 ("CustomData decryption")**: rewrite to "CustomData: nothing to decrypt; it is already plaintext in `C:\AzureData\CustomData.bin` after PA's base64 decode."
- **Section 9 / "Corrections to RFC 0014" point 3**: keep the conclusion ("CustomData does not require wireserver") but fix the rationale — it doesn't need wireserver because **it's not encrypted at all**, not because the cert happens to be cached locally.
