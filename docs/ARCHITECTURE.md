# NutManager Architecture

## 1. Architectural goals

- Windows x64 desktop product as the only active and supported target; Linux compatibility is deferred;
- safe monitoring, configuration, and local administration boundaries;
- clear separation of domain, UI, protocol, persistence, and operating-system concerns;
- deterministic tests without a real UPS, NUT server, service, serial port, or elevation;
- minimal dependencies and focused platform adapters.

## 2. Selected stack and solution structure

NutManager uses C#, .NET 10, Avalonia with AXAML, CommunityToolkit.Mvvm, xUnit, and `System.Text.Json` for its own persistence. Package versions are centrally managed through `Directory.Packages.props`.

```text
NutManager/
├── src/
│   ├── NutManager.App/
│   ├── NutManager.Core/
│   └── NutManager.Infrastructure/
├── tests/NutManager.Tests/
└── docs/
```

## 3. Dependency direction

```text
NutManager.App
    ├──> NutManager.Core
    └──> NutManager.Infrastructure

NutManager.Infrastructure
    └──> NutManager.Core

NutManager.Core
    └──> no UI or platform project
```

Core contains deterministic models, contracts, validation, status, and operation results. It must not reference Avalonia, Windows APIs, file-system APIs, sockets, serial ports, or service-control APIs. Infrastructure implements I/O and platform boundaries. App contains Avalonia startup, composition, views, and view models; views and view models do not directly execute NUT commands or operating-system actions.

## 4. Product capability split and managed profiles

```text
NutManager
├── Monitoring
│   └── NUT TCP protocol
└── Management
    ├── Local Windows adapter
    └── Remote configuration transports: SSH/SFTP or SMB
```

The managed-profile model is implemented through `ManagedNutServerProfile`, `NutMonitoringProfile`, `NutManagementProfile`, `ManagedNutServerProfiles`, `ManagedServerCapabilities`, and `ManagedNutServerRuntimeContext`.

Each profile separates:

```text
Profile
├── Monitoring: host, port, preferred UPS
└── Management: Local or Remote, ReadOnly or Manage
```

The active profile is resolved during bootstrap into an immutable runtime context. Changing the active profile persists the selection and requires restart; polling is not silently redirected during a live session.

## 5. Persistence

`settings.json` schema v3 is per-user UTF-8 JSON for polling, timeout, theme, mock-mode, language, and sidebar preferences. It uses temporary-file, atomic persistence and has no secrets. The persistence DTO can read legacy v1/v2 endpoint fields for one-time managed-profile bootstrap, but current serialization and runtime settings no longer mirror an endpoint.

`managed-servers.json` is schema-versioned, per-user metadata for managed profiles and the active profile. Schema v4 retains SSH/SMB metadata and adds non-secret SSH authentication mode and optional private-key path. It uses temporary-file, atomic persistence and never contains passwords, passphrases, or private-key material. Those values are session-only by default and, only after an explicit successful connection, may be saved in app-owned `CRED_TYPE_GENERIC` Windows Credential Manager entries with local-machine persistence.

These stores do not use backup or rollback semantics. Backup, recoverable replacement, and rollback belong to the T14 configuration-file pipeline.

## 6. Monitoring

Monitoring uses the read-only NUT TCP protocol, normally on port `3493`, rather than launching `upsc` for polling. The protocol layer supports UPS discovery, variable snapshots, bounded timeout and cancellation behavior, and controlled protocol errors. Polling permits one active operation per selected UPS, preserves the last successful snapshot on failures, and marks it stale rather than fabricating values.

Mock data is deterministic and visibly simulated. Protocol and polling tests use fakes or an in-process server rather than a real NUT server or UPS.

## 7. Configuration architecture

Configuration management is implemented for `nut.conf`, `ups.conf`, `upsd.conf`, `upsd.users`, and `upsmon.conf`. The syntax-preserving document model retains comments, order, unknown directives, unmanaged sections, quoting, and relevant formatting.

The write pipeline is:

```text
read → parse → requested in-memory change → preview/diff → backup
→ temporary write → validation → safe replacement → verification → rollback on failure
```

Editing works on one file at a time and sends writes exclusively through the pipeline. It does not automatically activate, reload, or restart services after an apply.

### Implemented semantic graphical configuration foundation (T25)

T15 established the editor for existing entries and remains the fallback for content no dedicated form covers. T25 adds a platform-neutral Core schema registry, projection, typed conversion, layered validation, replay-based semantic draft, review model, and explicit structural mutation boundary above the T13 syntax-preserving document. Mutations are validated and replayed from the original text, so failed user operations are atomic and never partially alter the candidate. The implemented flow is graphical form → semantic draft → semantic schema/validation → T13 document → semantic review/generated preview → the existing T14 safe-write pipeline → Local/SFTP/SMB.

Descriptors are immutable Core data: stable semantic IDs, file/scope/entry identity, localized label/help keys, invariant value codecs, field kind, required/sensitive metadata, setting-specific Automatic policy, applicability, insertion order, choices, and known activation metadata. T26 extends the built-in `ups.conf` schema with documented driver-aware production descriptors for `nutdrv_qx`, `usbhid-ups`, and `snmp-ups`; installed/configured drivers without a schema remain editable with limited validation instead of guessed semantics. T27 adds the release-backed `nut.conf` and `upsd.conf` production schemas, and T28 completes the set with `upsd.users` and `upsmon.conf`. Every supported file now has a dedicated graphical form. See [Semantic configuration architecture](SEMANTIC-CONFIGURATION-ARCHITECTURE.md) and [Graphical NUT configuration](GRAPHICAL-NUT-CONFIGURATION.md).

## 7.1 Presentation and localization architecture

T24 is an implemented presentation foundation, not a change to management boundaries. `NutManager.App/Presentation/Themes` contains the Light/Dark color dictionaries, metrics, motion, typography, reusable control styles, shell styles, and PathIcon geometries. `App.axaml` composes those resources and page data templates instead of owning the whole design system. `NutManager.App/Presentation/Controls` currently contains reusable connection-indicator, status-badge, and review-drawer-host controls.

Presentation state remains in App view models. The shell maps Wide/Medium/Compact widths, Expanded/Collapsed/Overlay navigation, and Hidden/Collapsed/Expanded/Overlay review states without adding Core or Infrastructure dependencies. The connection indicator observes the existing `OverviewPageViewModel` state, so shell decoration creates no second NUT client, timer, or polling state machine. The shell itself is not a scroll owner around page content; the selected page owns its scroll surface.

The review-drawer host is a presentation boundary only. T25 connects an optional read-only semantic review presentation containing safe old/new values, validation issues, custom parameters, activation information, and the existing redacted generated preview. T26 supplies the active `ups.conf` draft context and still exposes no drawer write command. Candidate construction calls `INutConfigurationFilePipeline.Prepare`; actual persistence remains exclusively in the existing local/SFTP/SMB pipeline.

### Implemented graphical `ups.conf` boundary (T26)

`UpsConfigurationEditorViewModel` owns a replay-based semantic draft, never the filesystem. Local driver discovery enumerates executable names passively only within the detected installation; it neither launches executables nor probes COM ports. Existing T17 COM metadata can populate choices, while remote profiles receive no fabricated local-device list. The form supports validated section add/rename/remove, documented driver applicability, explicit technical input for installed/configured drivers without schemas, Basic/Advanced fields, custom parameters, and change-only SNMP secrets.

The runtime assistant serializes the documented `nutdrv_qx` `runtimecal = runtime_high,load_high,runtime_low,load_low` setting with invariant formatting. It is not an operational calibration command, does not open hardware, and does not discharge a battery. Applying configuration and any later diagnostic or service action remain separate, explicitly reviewed workflows.

### Implemented graphical server/general boundary (T27)

`NutGeneralConfigurationEditorViewModel` and `UpsdConfigurationEditorViewModel` each own one T25 semantic draft for one loaded snapshot. `nut.conf` insertions use its file-specific `KEY=value` grammar, while existing lexical formatting remains unchanged. `upsd.conf` uses directive grammar and stable draft-lifetime identity for each repeated `LISTEN` node, so occurrence shifts never retarget a later edit.

The schemas come from the official NUT 2.8.5 release manpages. Address validation is pure syntax and performs no DNS or socket operation. TLS fields describe OpenSSL/NSS applicability without runtime backend claims; `CERTIDENT` remains change-only and redacted. Local, SFTP, and SMB all use the existing `INutConfigurationFilePipeline`; Apply never starts, reloads, or restarts NUT.

`pt-BR` is the default culture and `en-US` is an official culture. The shell and Appearance & Language surface resolve semantic keys through `NutManagerLocalizer`; the two culture resource sets are tested for exact key parity and deterministic fallback. The language preference is persisted, with full application after restart rather than a partial live switch. Display values follow UI culture; every NUT parser and serializer remains culture-invariant. NUT filenames, directives, driver names, status tokens, and SFTP stay invariant. Existing pages not yet redesigned are not retroactively described as localized. See [UI design system](UI-DESIGN-SYSTEM.md) and [Localization](LOCALIZATION.md).

### Implemented graphical users/monitoring boundary (T28)

`UpsdUsersConfigurationEditorViewModel` and `UpsmonConfigurationEditorViewModel` each own one T25 semantic draft for one loaded snapshot, following the same publication and cancellation rules as the earlier forms.

`upsd.users` has no global scope: every section of the file is one account, so the editor works one user at a time rather than flattening sections into a single form. It supports validated add/rename/remove, change-only password replacement, `SET` and `FSD` permissions, instant-command modes (none, `ALL`, or an explicit list), and the `upsmon` `primary`/`secondary` role. Permission tokens the release does not manage stay visible and are written back untouched, and the historic `master`/`slave` spellings are preserved rather than rewritten.

`upsmon.conf` is directive-scoped. Repeated `MONITOR` rows carry stable draft-lifetime identity, so an edit still targets the same logical row after earlier rows shift. The form also owns `MINSUPPLIES`, the shutdown group (`SHUTDOWNCMD`, `POWERDOWNFLAG`, `FINALDELAY`, `HOSTSYNC`), the polling group (`POLLFREQ`, `POLLFREQALERT`, `DEADTIME`, `NOCOMMWARNTIME`, `RBWARNTIME`), `NOTIFYCMD`, and a notification matrix over the 29 documented events with `NOTIFYFLAG`/`NOTIFYMSG`. `IGNORE` is exclusive of the other flags in both directions. An unknown event is shown as preserved instead of being dropped.

Warnings are semantic, not operational. `FSD`, `ALL` instant commands, a `primary` role, `SHUTDOWNCMD`, and `EXEC` without a configured `NOTIFYCMD` each surface an explanation. NutManager writes these settings and never executes them: no forced shutdown, no shutdown command, no notification command, and no automatic service restart.

### Embedded configuration secrets

Before T28, a sensitive descriptor marked a whole value as secret — an SNMP community, the `CERTIDENT` password component, a `password` assignment. `upsmon.conf` introduced a second shape:

```text
MONITOR system powervalue username password role
```

Here the credential is one token between arguments the administrator legitimately edits. Changing a power value must not require knowing the password, and must not lose it.

`SecretTokenIndex` on the descriptor marks that position. The projector blanks the token before the codec runs, so the parsed row that reaches Presentation has no password field at all — `NutMonitorEntry` is `System`, `PowerValue`, `Username`, `Role`. What Presentation learns is the same change-only state used everywhere else: not configured, configured, or replacement pending.

`NutEmbeddedSecret` is an internal Core helper that keeps the credential inside Core while the surrounding tokens are rewritten. Three draft mutations use it: `EditRepeatedPreservingSecret` rewrites the visible values and carries the stored credential across, `ReplaceRepeatedSecret` changes only the credential, and `AddRepeatedWithSecret` appends a new row whose credential has to be supplied because there is nothing to preserve. A transport credential is a different domain and is never consulted here.

### Token-list serialization

`ValueIsTokenList` distinguishes a text value that happens to contain spaces from a semantic list of tokens. `actions = SET FSD` is two permissions; quoting it as `actions = "SET FSD"` would make NUT read one unknown permission. The mutator therefore quotes whitespace only for values that are genuinely single text, and descriptors that carry lists opt out.

### Approved visual fidelity and runtime hardening (T27A)

T27A aligned the rendered application with the approved visual references without changing behavior. Architecturally it establishes: one shared token set for surface hierarchy, typography, spacing and motion; `NutIcons.axaml` as the only icon source, with vector geometry and no icon package; a supported motion strategy where transitions cover interaction feedback and the Composition API drives the single looping decoration, because a keyframe animation targeting `RenderTransform` has no registered animator in this Avalonia version; and a connection LED that carries text alongside colour.

It also hardened configuration navigation. Selecting a file no longer disables the list during its own load; a superseded selection is cancelled, and asynchronous construction stays inside a local `EditorBuildResult` until generation ownership and the selected target are checked immediately before atomic publication. A stale generation can therefore neither publish an editor nor clear the editor owned by a newer selection. Two Windows adapter defects were corrected in the same pass: COM enumeration reads the `SERIALCOMM` device map as authoritative with WMI used only for enrichment, and NUT service discovery recognises `nut.exe` as the service host inside the trusted installation root.

### Profile validation and presentation boundary (T24A/T24B)

T24A implements pure typed syntactic validation and cross-field materialization in Core, reversible draft/dirty-decision presentation in App, and explicit operational `LIST UPS` testing through Infrastructure. Host/port/UNC validation performs no DNS or I/O. The settings v3 migration makes managed profiles authoritative for endpoint and preferred UPS; compatibility endpoint data exists only while reading legacy settings and only bootstraps when no profile document exists. See [Profile validation architecture](PROFILE-VALIDATION-ARCHITECTURE.md).

T24B decomposes the existing Administration presentation into four focused views—NUT Configuration, Windows Service, Devices and Drivers, and Remote Access—while retaining one `AdministrationPageViewModel`, one remote session, and the existing capability instances. Password and passphrase controls remain in the Remote Access view code-behind, are passed as transient memory to the established session methods, and are cleared after use. Overview, Devices, and Diagnostics use responsive composition over real data only. Diagnostics copy is deterministic and omits raw failure details and configuration/credential content.

Local version resolution remains a read-only Windows capability behind `ILocalNutVersionResolver`: detector file metadata is authoritative; when absent, the adapter may execute only the detected in-installation `upsdrvctl.exe` with the fixed `-V` argument, a three-second timeout, bounded capture, cancellation, and defensive parsing. It never uses a shell, network, elevation, retry, or caller-provided arguments. T24B does not alter safe-write, privilege, driver, remote, credential, or secret-input boundaries. [Live validation findings](LIVE-VALIDATION-FINDINGS.md) preserves the historical observations from the completed T21 acceptance run; follow-up fixes remain documented under their owning tasks.

## 8. Windows local administration

Windows-specific behavior remains in `Infrastructure.Platform.Windows` behind Core contracts:

```text
Normal desktop process
    → explicit review and confirmation
    → limited privileged boundary when needed
    → Windows adapter result
```

The adapter implements local installation detection, service metadata and control, UAC helper handling, conservative ACL assessment and repair, process and Event Log inspection, passive COM metadata, and controlled NUT driver diagnostics. Core remains platform-neutral.

Since T38 the same passive COM source also backs the agent's read-only hardware inspection, so the local screen and the remote one describe a serial device through one enumeration rather than two that could drift apart. The COM naming rule itself moved to Core as `NutComPortName`, because a configured port in another machine's `ups.conf` and a port that machine reported have to be compared by the same rule; the Windows normalizer forwards to it and keeps its public shape.

## 9. Local and remote management boundary

Local Windows management is implemented through T17. A Remote profile explicitly selects SSH/SFTP or SMB for configuration files; neither transport accesses a remote NutManager instance. SSH/SFTP uses strict pinned-host-key verification. SMB uses only a manually supplied UNC share and either the current Windows identity or a session-scoped isolated Windows outbound identity created with `LOGON_NEW_CREDENTIALS`; it owns no global WNet connection and never maps a drive, disconnects a redirector connection, or discovers shares. Explicit SMB passwords are converted only for the native logon boundary and the resulting token is disposed when the session ends. A user may opt in after successful authentication to remember SSH or explicit-SMB secrets in Windows Credential Manager; the Core contract exposes only profile ID, fixed credential kind, and disposable secret buffers. A successful connection or browse automatically performs the read-only validation of the configured directory. Writes remain read-only by default and require a separate explicit probe plus the profile's Manage policy: Windows/OpenSSH safe replacement for SSH/SFTP, or verified UNC `File.Replace` semantics for SMB. The transport-neutral remote pipeline preserves T14-style fingerprints, candidate verification, reserved generated backups, post-write verification, rollback, and recovery paths.

### Windows-native SMB credentials (T30)

SMB authentication has two shapes and NutManager owns no password control for either.

**Current Windows identity** uses the session's own token. Nothing is read from the credential
store, no dialog is shown, and no user name is required — an old profile that still carries one, or
even a stored SMB password, is ignored rather than quietly reused.

**Another Windows account** goes through the operating system's credential dialog via
`CredUIPromptForWindowsCredentialsW`, behind the platform-neutral `IWindowsCredentialPrompt`
contract in Core with its Win32 implementation in Infrastructure. The App supplies the owner window
handle so the dialog belongs to NutManager. What comes back is a disposable buffer, never a string;
the account name is ordinary non-secret profile metadata and the password goes to Windows Credential
Manager only if the dialog's own remember box was ticked.

The order is deliberate: prompt, then prove the credential against the share, then persist. A
credential the share refuses is never stored, and replacing a credential keeps the working one until
the new one has succeeded. Cancelling changes nothing at all. Every native and managed buffer is
zeroed and freed on both the success and failure paths.

The share is now the exact configuration location, so the separate directory field is retired. A
profile saved with one is neither dropped nor silently retargeted: the value is preserved and the
form asks for the share to be corrected, because changing where configuration is written without
saying so would be worse than either. Exact-share confinement, the readiness checks, `File.Replace`
semantics, backup, verification, and rollback are all unchanged — T30 changes how a session
authenticates, not how it writes. NutManager still maps no drive, runs no `net use`, and never
disconnects a Windows SMB session it did not create; a credential conflict is reported and refused
rather than "fixed" globally.

### Remote device inspection through the agent (T38)

The Windows agent gained one read-only operation, `GetHardwareSnapshot`, negotiated through the
existing handshake capability list rather than through a version number. It answers identically over
both transports because both listeners call the same dispatcher, and it is available even when the
agent reports control as unavailable: serial devices exist whether or not that machine has a NUT
service the agent could pin.

The operation is passive by construction. Its request has no port, speed, command, executable or
path field, so no caller can widen it into opening a device; the implementation delegates to the
existing `IWindowsComPortSource` and adds no enumeration of its own. It is not audited, because a
read an operator repeats by pressing Refresh would bury the control records the Event Log exists to
preserve; the audit policy for the mutating operations is unchanged.

The agent still does not read or write NUT configuration. Relating a configured `port` to a detected
one loads the server's `ups.conf` through the profile's own SFTP or SMB transport and interprets it
with a platform-neutral reader that reports what it cannot establish from another machine — the
driver executable and its runtime state — as not established rather than guessing. A port list that
could not be read is carried as unknown rather than as an empty list, so an unreachable agent never
makes a configured port read as absent.

## 10. Windows-first CI and packaging

Official CI and package validation run on `windows-latest` only. Windows x64 is the only active, supported package and distribution target. Linux compatibility is deferred; it is not a CI gate and has no active package or administration support.

## 11. Error handling and logging

Infrastructure preserves technical errors; higher layers map them to actionable result categories and concise UI messages. Expected cancellation is not shown as a fault. Logs and diagnostic output must exclude passwords, complete secret-bearing configuration, and unsafe command details.

## 12. Upstream NUT workflow

The upstream NUT repository is not a project dependency or submodule. Approved upstream work reproduces and documents a limitation first, then uses a focused branch in `Marcelo-PX/nut` and follows NUT contribution, test, licensing, and DCO requirements.
