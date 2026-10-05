# OmniSettings security

Document version: 1.1. Updated: 5 October 2026.

Sensitive OmniSettings now use authenticated encryption, private Windows storage, write-only management, and request-audit redaction. This implementation is Windows-only. These changes have been made in source and tested with isolated fixtures; this work has not started the production application, migrated live settings, or rotated live API keys. Migration runs when the updated backend next starts.

The implementation is in [`OmniGlobalSettingsManager.cs`](../Omnipotent/Service%20Manager/OmniGlobalSettingsManager.cs), [`OmniSettingsProtector.cs`](../Omnipotent/Service%20Manager/OmniSettingsProtector.cs), and the KliveAPI and OmniDefence request paths. Deploy the updated backend and management website together.

## Encryption and storage

The settings file remains `SavedData/OmniGlobalSettings/settings.json`, resolved through `OmniPaths`. Its document format is now an object with `FormatVersion: 1` and a `Settings` array. Sensitive records store a versioned `omni-secret:v1:` envelope in `Value`. Non-sensitive values remain readable configuration.

For each sensitive record, AES-256-GCM encrypts a JSON payload containing the value, its dropdown options, and its parent service name. Every encryption obtains a fresh random 12-byte nonce and produces a 16-byte authentication tag. The authenticated associated data binds the envelope version, normalized setting name, parent service ID, type, and sensitivity flag. Copying ciphertext to another setting identity or changing these fields causes authentication to fail. The encrypted payload also authenticates the visible parent service name used for service continuity. Earlier version-1 payloads without this field remain readable and receive the binding on the next atomic startup rewrite. Setting names and service metadata remain visible. See Microsoft's [`AesGcm` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-9.0) and [nonce and associated-data requirements](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-9.0).

The 32-byte master key is generated with the operating system random-number generator and wrapped with Windows DPAPI `CurrentUser`. The wrapped key is stored separately from application data at:

```text
%LOCALAPPDATA%\Omnipotent\Security\OmniSettings\settings.key.dpapi
```

DPAPI binds unwrapping to the account context. Microsoft's [DPAPI guidance](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection) describes the Windows-only API; [CryptProtectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata) also explains the account and usual machine dependency.

The key directories, wrapped key, settings directory, settings file, and staging files use protected ACLs that grant access to the running account and `SYSTEM`. Existing broad permissions are removed. Storage validation rejects reparse points, network paths, and alternate data streams. ACL and encryption failures propagate; they do not trigger a plaintext fallback. Settings commits use private staging files, disk flushes, and atomic replacement before publishing the new in-memory state. Sensitive values and options remain encrypted in the manager's published records. Temporary cryptographic byte buffers are cleared after use.

## Migration and recovery

On the first updated startup, the manager accepts the legacy settings array, validates the records, promotes credential-like names to sensitive, encrypts all sensitive values and dropdown options, and atomically rewrites the versioned document before services can read it. If any same-name record is sensitive, all persisted copies of that name become sensitive. Sensitivity remains sticky during subsequent reads and writes, including calls that omit the flag or pass `false`.

Migration preserves saved values without requesting replacement credentials or applying newer HTTP input limits. The legacy array keeps its previous last-row-wins behavior for normalized duplicate identities, including a final empty or null value, and skips nameless records as earlier releases did. Values resembling `omni-secret:` envelopes in the legacy array are literal values; they become sensitive and are wrapped without alteration. Dropdown values survive migration even when their options have not yet been registered. Typed getter signatures remain compatible, and saved settings take precedence over changed defaults. Trusted services can find the same stable owner after its runtime service ID changes; identical shared copies remain usable when the caller is unknown. Existing empty-string rows can recover a populated sibling, while a persisted empty list stays cleared. Conflicting copies require an explicit identity.

A successful trusted read under a changed service ID establishes an in-memory alias to the saved identity for subsequent service writes. Equivalent historical copies of that owner update atomically, retaining their original identities. If an intervening explicit write makes these copies conflict, the alias write is rejected. HTTP writes always retain explicit ownership, and a direct service setter without a prior alias read retains its requested scope.

HTTP replacements remain limited to 65,536 characters and 384 KiB request bodies. Storage has separate migration headroom: up to 64 MiB per document and 16 MiB of UTF-8 encrypted payload per setting. Trusted service dropdown options and existing identities are not subjected to the smaller new-input limits. Storage failures preserve the original document and leave settings unavailable rather than replacing saved credentials with defaults.

The private key directory also contains a path-specific `settings-<hash>.v1` migration marker. Once migration is recorded for a settings-file path, restoring a legacy plaintext array at that path is rejected. A missing settings file at an already marked path also leaves settings unavailable; startup and later commits refuse to silently recreate it. A genuinely new store creates an empty versioned document and records its marker before serving settings. Unsupported document or envelope versions, damaged ciphertext, missing encryption, duplicate identities in protected documents, unsafe paths, and inaccessible keys leave protected settings unavailable. The old fixed `settings.json.tmp` staging file is removed during successful migration. If it is the only surviving document from an interrupted first save, its contents are validated and migrated before deletion. New non-sensitive writes cannot use the reserved `omni-secret:` envelope prefix.

Restart Omnipotent under the same Windows account with its original profile available. Changing the service account, rebuilding the profile, or moving hosts requires a planned recovery procedure. An encrypted settings backup alone is insufficient: preserve the original wrapped key, the private migration markers, and the original account/profile DPAPI recovery material. A different account with the same display name does not supply the original identity. Preserve machine recovery material as appropriate for DPAPI; copying the wrapped key to a new host alone does not guarantee recovery.

When a protected value cannot be decrypted or a marked settings file is missing, the application refuses to reset the store. It does not generate a replacement key for an unavailable encrypted value. Restore the original settings document, protected key material, and account context. If recovery is impossible, revoke and replace the affected credentials through an explicit recovery plan; deleting the key or settings file is not a repair procedure. There is no automatic key-reset or key-rotation feature.

## API, browser, and notifications

All `/OmniGlobalSettings` routes require Klives clearance and an HTTPS connection observed by the backend. Plain HTTP requests are rejected before authentication or body dispatch. `X-Forwarded-Proto` is not trusted, and OmniSettings routes cannot run through `/batch`. A proxy must preserve an encrypted connection to the backend. Responses use `no-store` and are excluded from shared response caching, ETags, and compression.

`List` and `Get` return metadata only for sensitive values: configured secrets use `Value: "********"`; unset secrets use an empty value and `HasValue: false`. Sensitive dropdown options are always empty. `HasValue` intentionally reveals whether a value is configured. `revealSensitive` no longer reveals values.

The website uses a blank password input for every sensitive replacement, including list and dropdown settings. It never initializes an editor from the returned mask and does not submit an untouched mask. Successful or failed replacements clear the draft. Successful refreshes and leaving the page clear stored drafts. Existing list entries are not rendered. A sensitive list replacement is entered as a JSON array of strings. Sensitive dropdown replacements must match an allowed option known to the operator; the API does not reveal those options.

`Set` accepts a string `value` and optional boolean `sensitive: true`; existing settings retain their type. Boolean, integer, dropdown, and list replacements are validated. `Set` and `Delete` have bounded request bodies and parsing depth, reject duplicate JSON properties and incorrect field types, and return fixed error messages without reflecting secret input.

Change events carry redacted setting snapshots and previous values. Subscribers use `ValueChanged` to detect a replacement because successive secrets have the same mask; trusted consumers needing the actual value must call a typed getter. Sensitive fulfillment sends only a notice directing the owner to the HTTPS settings page. Credentials are not collected through Discord text prompts.

## Audit history and security limits

OmniSettings request audits omit request bodies, queries, body hashes, payload lengths, arbitrary headers, user agents, and client-page URLs. Access outcomes and necessary identity metadata remain available. OmniDefence startup also scrubs legacy settings audits and settings-related batch payloads, enables SQLite secure deletion, checkpoints the scrubbed database, truncates its WAL, and removes legacy request/IP snapshot files.

This cleanup covers the current audit database and generated snapshots. It cannot scrub prior backups, exported logs, external monitoring, filesystem snapshots, or data previously disclosed by the old reveal feature. Restrict or retire those historical copies and rotate API keys and other credentials stored before this upgrade.

Trusted host code can still read secrets through typed getters. Clear strings must temporarily exist when a service uses a credential, and a service may retain one for an authenticated client. DPAPI and ACLs do not protect against malicious code running as the same account, a compromised application process, or an administrator taking control of the host. Encryption also does not authenticate the entire settings document or prevent replay of an older valid encrypted record. Protect the host, service identity, website, and backups accordingly.

## Validation environment

The application and backend test projects target .NET 9. This test host has .NET 8 and .NET 10 runtimes, but no .NET 9 runtime. Backend tests can run with an explicit process-local major roll-forward override; this does not migrate live application data:

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major'
dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj --filter 'FullyQualifiedName~OmniSettings|FullyQualifiedName~SensitiveAuditMigration'
```

The override belongs to the test session. Production runtime and account/profile compatibility must be validated as part of deployment. Microsoft's [runtime troubleshooting documentation](https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch) explains compatible runtimes and explicit roll-forward settings.

Security regressions cover authenticated encryption, nonce generation, ciphertext and identity tampering, DPAPI key reuse and missing-key behavior, private ACLs, migration, API transport and caching, audit cleanup, and write-only browser editors. Compatibility regressions additionally cover fresh DPAPI restarts, all five legacy types, duplicate rows, empty values, changed service IDs and defaults, literal envelope prefixes, large values and options, missing optional fields, and failed-write recovery. Use isolated fixture directories for tests rather than production settings paths.
