# NutManager Implementation Tasks

## Status legend

- `TODO` — not started;
- `READY` — specified and ready for implementation;
- `IN PROGRESS` — currently assigned;
- `BLOCKED` — waiting on a dependency or decision;
- `DEFERRED` — intentionally postponed for future evaluation;
- `DONE` — implemented and validated.

Only one task should normally be in progress at a time.

## Roadmap

| ID | Status | Task | Primary outcome |
|---|---|---|---|
| T01 | DONE | Create Avalonia solution | Compilable project skeleton |
| T02 | DONE | Build visual shell and navigation | Modern themed application shell |
| T03 | DONE | Define domain models | Stable UPS and connection models |
| T04 | DONE | Implement mock provider | Deterministic simulated scenarios |
| T05 | DONE | Build overview dashboard | Functional UI using mock data |
| T06 | DONE | Implement read-only NUT client | TCP protocol client with tests |
| T07 | DONE | Add UPS discovery and selection | Device listing and details |
| T08 | DONE | Persist local settings | Atomic per-user settings storage |
| T09 | DONE | Add polling and stale-data handling | Robust refresh and reconnect behavior |
| T10 | DONE | Complete MVP diagnostics | Read-only diagnostics page |
| T11 | DONE | Package and validate MVP | Official Windows x64 package and completed live Windows NUT validation |
| T12 | DONE | Detect local NUT installation on Windows | Autodetect installation, executables, version, and configuration directory |
| T13 | DONE | Design syntax-preserving NUT configuration model | Safe model for managed and unmanaged configuration content |
| T14 | DONE | Add configuration backup, write, and rollback pipeline | Previewed, validated, recoverable configuration changes |
| T15 | DONE | Build graphical NUT configuration editor | Windows-first configuration experience |
| T16 | DONE | Add Windows service, UAC, and ACL administration | Explicitly confirmed local administrative actions |
| T17 | DONE | Add Windows COM-port and driver workflows | Local device and driver diagnostics |
| T18 | DONE | Add managed server profiles | Managed profile metadata and strict local/remote management-context separation |
| T19 | DONE | Add remote SSH/SFTP management | Manual remote directory browse, validation, and secure management transport |
| T19B | DONE | Add SMB remote configuration transport | Manual UNC SMB configuration access and verified safe replacement |
| T20 | DONE | Add secure credential storage | Protected SSH and SMB remote-management credentials |
| T21 | DONE | Validate full Windows local and remote administration | End-to-end Windows-first validation; findings recorded and carried into later tasks |
| T22 | DEFERRED | Future Linux compatibility evaluation | Compatibility may be reconsidered in a future task |
| T23 | DONE | Evaluate upstream NUT improvements | Focused issues and PR candidates |
| T24 | DONE | Modern responsive shell, design system and localization foundation | Windows-first responsive presentation and pt-BR/en-US foundation |
| T24A | DONE | Managed server profile UX and typed validation | Reversible profiles, typed validation, deterministic migration, and explicit connection testing |
| T24B | DONE | Current page and administration presentation decomposition | Focused responsive surfaces over existing safe capabilities |
| T25 | DONE | Semantic graphical configuration framework | Core schemas, mutations, and semantic review over T13/T14 |
| T26 | DONE | Graphical ups.conf configuration | Driver-aware UPS administration and runtimecal assistant |
| T27 | DONE | Graphical server and general configuration | Dedicated upsd.conf and nut.conf forms |
| T27A | DONE | Approved visual fidelity, iconography and motion | Windows presentation aligned with the approved visual references |
| T28 | DONE | Graphical users and monitoring configuration | Dedicated upsd.users and upsmon.conf forms |
| T29 | DONE | Graphical configuration UX hardening | Responsive, accessibility, bilingual, and transport regression validation |
| T30 | DONE | Windows-native SMB credential authentication | Native Windows credential UI, simplified SMB profile UX, and protected explicit credentials |
| T31 | DONE | Collapsible NUT file rail | Page-level collapsible file navigation with the current visual language |
| T32 | DONE | Icon library adoption and T31 visual acceptance | Every icon drawn from Material Icons, glass that responds to the pointer, and horizontal administration navigation |
| T33 | DONE | Code health, dead-code cleanup and focused refactoring | Proven-dead code removed, doubtful code preserved, behaviour and visuals unchanged |
| T34 | DONE | Remote Windows NUT service monitoring | Read-only monitoring of the Windows NUT service and its process from a remote NutManager instance, independently from NUT protocol health |
| T35 | DONE | Windows Agent for secure remote NUT service control | Privileged Windows agent with authenticated named-pipe and HTTPS transports, service control restricted to the validated NUT service, Event Log auditing and desktop integration |
| T36 | IN PROGRESS | Windows Agent settings and deployment UX | Expose the agent transport and authentication model in the profile editor, complete the native credential lifecycle, and carry out desktop and server acceptance |

---

## T01 — Create Avalonia solution

**Status:** DONE

### Objective

Create the minimal compilable solution and project references without implementing product behavior.

### Allowed scope

- root solution and shared build files;
- `src/NutManager.App`;
- `src/NutManager.Core`;
- `src/NutManager.Infrastructure`;
- `tests/NutManager.Tests`;
- minimal README build instructions if required.

### Requirements

- create `NutManager.sln`;
- create an Avalonia Desktop application project;
- create Core and Infrastructure class libraries;
- create an xUnit test project;
- reference Core and Infrastructure from App;
- reference Core from Infrastructure;
- reference tested projects from the test project as required;
- add CommunityToolkit.Mvvm;
- create `Directory.Build.props` and `Directory.Packages.props`;
- enable nullable reference types and implicit usings;
- use the Avalonia Fluent theme;
- create a minimal main window showing `NUT Manager`;
- keep build warnings introduced by project code at zero.

### Do not

- implement a NUT client;
- create production domain models;
- add dependency injection unless required by the template;
- implement navigation beyond what the template minimally needs;
- add service control, configuration parsing, serial access, backups, charts, installer, or platform-specific code;
- add the NUT upstream repository to the workspace.

### Validation

```bash
dotnet restore
dotnet build
dotnet test
```

### Completion criteria

- all three validation commands succeed;
- the application project starts and displays the minimal window;
- the agent reports created files and stops.

---

## T02 — Build visual shell and navigation

**Status:** DONE

Create the modern application frame, side navigation, Overview, Devices, Diagnostics, and Settings placeholders, plus persisted theme selection. No NUT data access.

## T03 — Define domain models

**Status:** DONE

Create Core models and status parsing contracts for endpoints, UPS identity, variables, snapshots, connection state, freshness, and diagnostics. Include unit tests. No network access.

## T04 — Implement mock provider

**Status:** DONE

Implement deterministic simulated scenarios defined in the architecture and expose them through the same application-facing abstraction intended for live data.

## T05 — Build overview dashboard

**Status:** DONE

Bind the overview UI to mock data. Include clear simulated-data labeling, accessible state presentation, missing-value rendering, and responsive metric cards.

## T06 — Implement read-only NUT client

**Status:** DONE

Implement the minimum TCP protocol commands needed to list UPS devices and fetch variables. Include cancellation, timeout, partial-read, malformed-reply, and fake-server tests.

## T07 — Add UPS discovery and selection

**Status:** DONE

Connect the Devices page to the NUT client, list exposed UPS devices, retain selection, and provide a raw variable details view.

## T08 — Persist local settings

**Status:** DONE

Persist non-secret endpoint, preferred UPS, polling, timeout, theme, and mock-mode settings per user using versioned atomic JSON storage.

## T09 — Add polling and stale-data handling

**Status:** DONE

Add bounded asynchronous polling, cancellation, reconnect behavior, stale snapshot retention, and timestamps without busy loops.

## T10 — Complete MVP diagnostics

**Status:** DONE

Expose read-only endpoint, connection, discovery, polling, version, and application-error diagnostics. No service or serial operations.

## T11 — Package and validate MVP

**Status:** DONE

Produce and test the official self-contained Windows x64 package. Windows x64 is the only active development, validation, and distribution platform. The real Windows NUT acceptance is complete; this section remains as the package-validation record.

## T12 — Detect local NUT installation on Windows

**Status:** DONE

Discover a local Windows NUT installation, its executables, version, and configuration directory. Allow a user to correct the path manually. Do not change configuration or services.

## T13 — Design syntax-preserving NUT configuration model

**Status:** DONE

Design and test a document model for `nut.conf`, `ups.conf`, `upsd.conf`, `upsd.users`, and `upsmon.conf` that preserves comments, order, unknown directives, unmanaged sections, quoting, and relevant formatting. No real file writes.

## T14 — Add configuration backup, write, and rollback pipeline

**Status:** DONE

Implement preview/diff, timestamped backup, temporary-file write, validation, safe replacement, activation testing, and rollback using temporary-directory tests.

## T15 — Build graphical NUT configuration editor

**Status:** DONE

Build a Windows-first editor over the syntax-preserving model and recoverable write pipeline. Every administrative change requires explicit confirmation.

## T16 — Add Windows service, UAC, and ACL administration

**Status:** DONE

Implement explicitly confirmed local Windows service, UAC, ACL, process, and Event Log actions behind platform interfaces.

## T17 — Add Windows COM-port and driver workflows

**Status:** DONE

Implement explicitly confirmed local COM-port, driver, and NUT-tool diagnostics behind platform interfaces.

## T18 — Add managed server profiles

**Status:** DONE

Add managed local and remote monitoring and management profiles, manual remote configuration-directory metadata, and strict local/remote management-context separation. T18 does not implement remote browse, remote validation transport, or local-management fallback for a remote profile; those are T19 work.

## T19 — Add remote SSH/SFTP management

**Status:** DONE

Add SSH/SFTP remote management transport, manual remote configuration-directory browse and selection, strict host-key verification, and remote validation. Remote autodiscovery is not permitted. Remote writes remain limited to an explicitly verified Windows/OpenSSH safe-replace path.

## T20 — Add secure credential storage

**Status:** DONE

Store opt-in SSH and SMB remote-management secrets in Windows Credential Manager without exposing them in logs, view-model state, or JSON profile metadata.

## T21 — Validate full Windows local and remote administration

**Status:** DONE

Validate Windows-first local and remote administration, including recovery paths, without unsafe UPS operations. The live Windows validation was executed and its findings are recorded in [LIVE-VALIDATION-FINDINGS.md](LIVE-VALIDATION-FINDINGS.md) as a historical record of that validation run. Findings that required product changes were carried into the later tasks that own them rather than keeping this validation stream open indefinitely. Further validation of surfaces introduced after this run belongs to the task that introduces them, and final graphical hardening remains T29.

## T22 — Evaluate Linux administrative compatibility

**Status:** DEFERRED

Future Linux compatibility evaluation. Linux is deferred and is not an active CI, packaging, distribution, or administration target.

## T23 — Evaluate upstream NUT improvements

**Status:** DONE

Review mature, reproducible limitations discovered by NutManager. Separate client concerns from NUT concerns, then prepare focused issues or branches in `Marcelo-PX/nut` for potential PRs to `networkupstools/nut`.

## T24 — Modern responsive shell, design system and localization foundation

**Status:** DONE

### Objective

Build the Windows-first responsive shell and design tokens, with official `pt-BR` and `en-US` localization infrastructure.

### Allowed scope

- App presentation/resources, shell, settings, localization resources, and focused tests;
- non-secret UI-preference persistence required for theme, language, or sidebar state;
- documentation directly required by the implementation.

### Requirements

- connection indicator with text, accessible status, and restrained status colors;
- sun/moon theme control, with System theme in Appearance & Language;
- Expanded/Collapsed/Overlay sidebar and Hidden/Collapsed/Expanded/Overlay review-drawer shell;
- Wide (>=1200), Medium (860–1199), and Compact (<860) layouts without ordinary horizontal scrolling;
- stable semantic resources for all new user-facing strings in both official cultures;
- culture-invariant NUT serialization and deterministic resource fallback;
- accessible icon controls, focus, and tab order.
- product-owned accent/selection colors, one-scroll-owner rule, localized option presentation, persistent mock/demo indication, and responsive validation-field layout.

### Do not

- implement semantic configuration mutations or graphical file forms;
- add Linux scope, a new writer, or automatic service activation;
- claim that T25–T29 experiences are implemented.

### Validation

- automated resource/fallback/serialization tests;
- manual Windows validation in both cultures and all responsive states;
- standard restore, build, test, vulnerability, format, and diff checks.

### Completion criteria

- both cultures are usable and persisted safely;
- responsive shell and review-drawer foundation are accessible;
- no NUT token is localized or culture-serialized.

## T24A — Managed server profile UX and typed validation

**Status:** DONE

**Dependency:** T24

### Objective

Redesign managed-server profiles and introduce reusable typed validation before semantic configuration work.

### Allowed scope

- Core host/port and reusable validation contracts;
- profile draft/settings/persistence migration required by the new source-of-truth boundary;
- Settings profile UX, localization resources, focused test fakes, and documentation.

### Requirements

- one **New server** flow with reversible Local/Remote, ReadOnly/Manage, and SFTP/SMB choices;
- typed host, TCP/SSH port, UNC, field, and cross-field validation with localized inline errors and Save disabled on Error;
- explicit operational Test Connection, separate from syntax, with no secrets in diagnostics;
- Save/Discard/Continue editing decision for dirty drafts and a first-class restart-required active-profile state;
- future schema migration separating application preferences from managed-profile endpoints/metadata;
- mock/demo target policy: disabled for new normal installs, preserved for existing persisted settings, and visibly indicated when active.

### Do not

- implement semantic `.conf` mutations or configuration writes;
- change T14, T19, T19B, or T20 safety boundaries;
- perform DNS during syntactic validation or persist a resolved IP in place of a hostname.

### Validation

- deterministic host/port/cross-field tests; Local↔Remote and SFTP↔SMB transitions; dirty drafts; settings migration; connection-tester fakes; and pt-BR/en-US validation-resource completeness.

### Completion criteria

- profiles are reversible while drafting, invalid input cannot be saved, and endpoint source-of-truth migration is deterministic without secret migration.

## T24B — Current page and administration presentation decomposition

**Status:** DONE

**Dependency:** T24A

### Objective

Decompose current responsive presentation surfaces before adding T25–T28 graphical forms.

### Allowed scope

- App presentation/view-model decomposition, localized UI resources, focused tests, and documentation;
- existing capability composition only, without new administrative behavior.

### Requirements

- Administration sections for NUT Configuration, Windows Service, Devices & Drivers, and Remote Access;
- neutral selection cards, grouped commands, useful empty states, and responsive Overview/Devices/Diagnostics improvements including copy diagnostics;
- reduce ordinary non-secret code-behind when useful while preserving password/passphrase input at the View boundary;
- bounded read-only NUT-version fallback when file-version metadata is unavailable.

### Do not

- alter safe-write behavior; create remote service control; reduce hardware/admin confirmation; or move passwords/passphrases into ordinary ViewModel state.

### Validation

- responsive/empty-state/command-grouping tests and manual Windows review, with existing local/SFTP/SMB and privileged-boundary regressions.

### Completion criteria

- current pages are focused and responsive while all existing safety boundaries retain their behavior.

## T25 — Semantic graphical configuration framework

**Status:** DONE

### Objective

Create the Core semantic schema, projection, validation, and mutation layer that extends T13 for complete graphical configuration while retaining T14/T19/T19B writes.

### Allowed scope

- Core configuration models/contracts and T13 explicit mutation primitives;
- Infrastructure/App projections and semantic-review support;
- focused deterministic tests and documentation.

### Requirements

- schema registry, file/section/field/driver descriptors, stable resource keys, validation, applicability, insertion order, and activation metadata;
- Explicit, AutomaticByOmission, ExplicitAutoToken, MissingRequired, Unsupported, and CustomUnknown states;
- setting-specific Automatic policies and sensitive change-only model;
- deterministic add/remove/rename/section/repeated-row mutations preserving comments, raw nodes, ordering, quoting, line endings, encoding, duplicates, and unknown content;
- graphical custom parameters with limited-validation warning and read-only generated preview.

### Do not

- write directly from views, reformat whole files, guess defaults, or use runtime internet;
- treat Automatic as universal directive deletion.

### Validation

- deterministic parser/mutation/serialization, culture-invariant, sensitive-redaction, and safe-pipeline integration tests.

### Completion criteria

- semantic mutations project through T13 and exclusively use existing safe transports.

## T26 — Graphical ups.conf configuration

**Status:** DONE

### Objective

Provide a dedicated driver-aware UPS configuration form for `ups.conf`.

### Allowed scope

- T25 semantic framework extensions, local passive driver/COM metadata, App UPS form, and focused tests/documentation.

### Requirements

- add/rename/remove validated UPS sections; identification and `desc`;
- concrete driver selection/detection, driver-aware port/protocol/parameter controls, and documented battery settings;
- local passive COM selector where applicable, never fabricated remote COM enumeration;
- documented `runtimecal` assistant that edits only semantic draft;
- Basic/Advanced/Custom parameters and semantic review.

### Do not

- open serial ports directly, run driver control/shutdown commands, invent driver defaults, or persist UI metadata as NUT directives.

### Validation

- schema, section, driver/port/protocol applicability, runtimecal, redaction, local/SFTP/SMB safe-pipeline regression, and Windows UI validation.

### Completion criteria

- supported UPS settings are graphical and all writes remain reviewed/recoverable.

### Implemented

- immutable production catalog for documented `nutdrv_qx`, `usbhid-ups`, and `snmp-ups` options, plus passively detected/configured drivers with limited validation;
- graphical section, driver, port, protocol, battery, polling, SNMP change-only secret, Basic/Advanced, and custom-parameter editing;
- documented `runtimecal` four-value assistant that changes only the semantic draft and never performs a battery-discharge operation;
- semantic validation, read-only review, and generated preview converging exclusively on the existing local/SFTP/SMB pipeline.

## T27 — Graphical server and general configuration

**Status:** DONE

### Objective

Provide dedicated graphical `upsd.conf` and `nut.conf` forms.

### Allowed scope

- T25 semantic schemas/forms, focused tests, and documentation for server/general configuration.

### Requirements

- repeated `LISTEN` address/port rows, server behavior, timeouts, TLS/certificate metadata, and custom parameters;
- documented NUT MODE and advanced `nut.conf` options respecting that file's own grammar;
- review, redaction where applicable, and existing safe pipeline.

### Do not

- assume all NUT files use `key = value`, create an unrestricted raw editor, or restart services automatically.

### Validation

- parser/serializer grammar, repeated-row, validation, preservation, and local/SFTP/SMB regression tests.

### Completion criteria

- supported server/general settings are graphical without altering unmanaged syntax.

### Implemented

- dedicated `nut.conf` General editor with required `MODE`, release-backed Advanced fields, `KEY=value` insertion grammar, and limited-validation custom assignments;
- dedicated `upsd.conf` Server editor with stable repeated `LISTEN` rows, syntax-only IPv4/IPv6/hostname/wildcard validation, server/timeouts, TLS metadata, and protected change-only `CERTIDENT`;
- semantic review, read-only redacted preview, external-change protection, and explicit Apply through the existing Local/SFTP/SMB pipelines only;
- responsive graphical composition with ReadOnly/Manage capability enforcement and no DNS, socket, certificate, process, or service side effects.

## T27A — Approved visual fidelity, iconography and motion

**Status:** DONE

### Objective

Align the existing Windows presentation with the approved visual references, including shared iconography, restrained motion, dashboard hierarchy and configuration-review fidelity without changing administrative behavior.

### Allowed scope

- `NutManager.App` presentation: shared themes, shared controls, views, and the presentation-only view-model projections required to surface state that already exists.

### Requirements

- shared surface hierarchy, typography, colour and motion tokens instead of page-local palettes;
- vector iconography from one shared resource dictionary, with no emoji, pictographic text, or raster UI icons;
- integrated window chrome so the product identity and window controls read as one bar;
- Overview composed as a UPS dashboard with battery, load gauge, runtime, input/output, state and connection;
- configuration review presented as pending-change cards, redacted generated preview, and an explicit action bar;
- restrained motion on navigation, selection, metric values, drawer and theme toggle only.

### Do not

- fabricate readings, tests, logs, service state, or capabilities that the product does not implement;
- change domain logic, writers, transports, credential handling, or semantic configuration behavior;
- add charting, icon, or animation dependencies;
- begin T28 or T29.

### Validation

- existing suites stay green; focused tests pin that absent readings remain absent in the dashboard projections;
- Release build with zero warnings, format, vulnerability and whitespace gates;
- application launched on Windows to confirm the shell, dashboard and configuration surfaces initialise.

### Completion criteria

- the rendered application matches the approved references closely enough for human visual acceptance, with no functional or safety regression. Final acceptance requires that human comparison and is not implied by passing gates.

### Implemented

- one surface hierarchy, typography, spacing and motion token set in `Presentation/Themes`, replacing page-local palettes; restyled cards, buttons, inputs, lists, tabs, badges, title bar, navigation and profile card;
- `NutIcons.axaml` as the only icon source: `StreamGeometry` on a 24×24 grid, no icon font, icon package or raster UI image; semantic icon colour always redundant with text;
- integrated window chrome through `WindowDecorations="BorderOnly"`, using standard Avalonia window operations with no platform interop;
- Overview composed as a UPS dashboard with battery, semicircular load gauge, runtime, input/output, state and connection, each projected from the current snapshot and pinned by tests that keep an absent NUT variable absent;
- restrained motion within roughly 140–320 ms for interaction feedback, plus the decorative connection LED as the only looping animation; loops use the Avalonia Composition API because a keyframe animation targeting `RenderTransform` has no registered animator in this version;
- configuration navigation hardening: the file list stays enabled during a load, a superseded selection is cancelled, and only the newest selection may publish an editor;
- two runtime defects fixed while validating the above: COM enumeration now reads the `SERIALCOMM` device map with WMI used only for enrichment, and Windows NUT service discovery recognises `nut.exe` inside the trusted installation root.

Human visual acceptance was given for the rendered application. Merged to `main` through PR #33 as merge commit `fae5b4d1`.

## T28 — Graphical users and monitoring configuration

**Status:** DONE

### Objective

Provide dedicated graphical `upsd.users` and `upsmon.conf` forms with protected secret handling.

### Allowed scope

- T25 schemas/forms, focused tests, and documentation for users and monitoring.

### Requirements

- user add/rename/remove, roles/actions/instcmd permissions, password state, and change-only replacement;
- repeated graphical `MONITOR` rows plus MINSUPPLIES, timing, shutdown, notification, advanced, and custom controls;
- explicit warning for dangerous permissions such as FSD; secret redaction in UI, review, and logs.

### Do not

- reveal existing secrets, execute FSD/shutdown, or conflate permission configuration with command execution.

### Validation

- secret non-exposure, repeated row, user mutation, validation, and transport safe-pipeline regression tests.

### Completion criteria

- users and monitoring are manageable graphically with change-only secrets.

### Implemented

- dedicated `upsd.users` editor working one user at a time, since every section of the file is one account: add/rename/remove with section-name validation, change-only password, SET and FSD permissions with an explicit FSD warning, instant-command modes (none/`ALL`/specific) with an `ALL` warning, and the `upsmon` `primary`/`secondary` role with a primary warning;
- dedicated `upsmon.conf` editor with repeated `MONITOR` rows, `MINSUPPLIES`, shutdown settings, polling and timing directives, `NOTIFYCMD`, and a notification matrix over the 29 documented events with `IGNORE` exclusivity and per-event custom messages;
- embedded-secret handling for `MONITOR`, whose credential sits between ordinary editable arguments: `SecretTokenIndex` marks the position, the projector blanks the token before any codec or view model sees it, and neighbouring values are edited without revealing the stored password;
- token-list serialization so `actions = SET FSD` stays a list of tokens instead of being quoted into one;
- unmanaged permissions, roles, directives and notification events preserved and shown as preserved rather than dropped;
- semantic review with redacted preview, and Apply exclusively through the existing T13/T14 pipeline over Local, SFTP and SMB.

### Validation record

Gates on the final round: restore PASS; Release build PASS with 0 warnings and 0 errors; 1102/1102 tests PASS, up from a 1051 baseline; vulnerability gate PASS; `dotnet format --verify-no-changes` PASS; `git diff --check` PASS.

Windows runtime smoke against a real installation: ten rapid selections across the five configuration files kept the UI responsive with a maximum observed latency of 19.4 ms, no freeze, and the newest selection always winning. No stored password appeared in the UI, review redacted the sensitive line, and a comparison against the real credential found zero leaks. `C:\NUT\etc\upsd.users` and `C:\NUT\etc\upsmon.conf` kept their original modification times; no Apply was performed during the smoke.

## T29 — Graphical configuration UX hardening

**Status:** DONE

T29 was completed and merged into `main` through PR #35.

### Delivered

Sidebar motion, profile quick navigation, footer authorship, and consistent Administration and
Settings icon semantics.

### Objective

Validate and harden the complete graphical configuration experience.

### Allowed scope

- focused App/Core/UI tests, manual Windows validation artifacts, and defect fixes required by that validation.

### Requirements

- Wide/Medium/Compact, sidebar/drawer, keyboard, focus, automation, clipping/overflow, and semantic-error validation;
- accessible names on every actionable control;
- `pt-BR` and `en-US` validation; preference persistence;
- 100/125/150% Windows scaling, invalid-field states, and non-blue Windows system-accent regression;
- local, SFTP, and SMB regression of reviewed safe writes and recovery;
- final graphical configuration Windows validation.

### Do not

- broaden to Linux, remote service control, raw editors, or unreviewed writes.

### Validation

- automated responsive/resource/accessibility tests plus documented manual Windows validation.

### Known follow-up

The configuration action bar's buttons wrap their content in a panel, so UI Automation announces the
panel type rather than the button label. This is understood and deliberately deferred rather than
outstanding work on this task: it needs `AutomationProperties.Name` on those buttons and is worth
picking up with the next presentation change that touches that bar.

### Completion criteria

- graphical forms remain accessible, bilingual, responsive, and safe across local and supported remote transports.

## T30 — Windows-native SMB credential authentication

**Status:** DONE

### Current scope

Windows-native SMB credentials, the simplified SMB profile form, per-profile managed NUT file
selection, and detection of the supported files.

### Objective

Let an SMB profile authenticate the way Windows already does it: the current session's identity
when that is enough, and the operating system's own credential dialog when another account is
needed. Remove the redundant SMB fields that the new model makes meaningless.

### Allowed scope

- SMB profile model, validation, presentation, and the remote session's credential flow;
- a Windows credential-prompt boundary behind a testable interface;
- the connection LED's size and healthy colour.

### Requirements

- current Windows identity connects with no user name, no password and no stored credential;
- another Windows account uses `CredUIPromptForWindowsCredentialsW`, never a NutManager password control;
- an explicit credential is validated against the share before it is persisted, and a failed attempt leaves the previous one intact;
- the share is the exact configuration location, so the separate directory field is retired without discarding legacy values;
- Windows Credential Manager remains the only persistent secret store.

### Do not

- weaken exact-share confinement, map a drive, run `net use`/`cmdkey`, or disconnect global SMB sessions;
- change SSH authentication, the safe-write pipeline, or any writer boundary;
- let a password reach ordinary view-model state, profile JSON, logs, or the automation tree.

### Validation

- prompt state, credential-lifecycle, simplified-surface, and Manage/ReadOnly wording tests, plus Windows runtime validation of both authentication modes.

### Completion criteria

- both SMB authentication modes work on Windows with no redundant fields and no NutManager-owned password input.

### Implemented

- current Windows identity connects with the session's own token: no user name, no dialog, and no
  credential read from the store, ignoring one left over from an older profile rather than reusing it;
- another Windows account is collected by `CredUIPromptForWindowsCredentialsW` behind the
  platform-neutral `IWindowsCredentialPrompt` contract, with the owner window handle supplied by the
  App so the dialog belongs to NutManager; NutManager owns no password control for SMB;
- an explicit credential is proven against the share before it is persisted, and only when the
  dialog's own remember box was ticked; a refused credential is never stored and a failed
  replacement leaves the working one in place; cancelling changes nothing;
- the share became the exact configuration location, retiring the separate directory field; a legacy
  value is preserved and surfaced for correction instead of being dropped or silently retargeted;
- Windows Credential Manager remains the only persistent secret store, with the account name kept as
  ordinary non-secret profile metadata;
- the connection LED core was reduced and given its own brighter green while its glow, pulse, period
  and lifecycle stayed as they were;
- a connection failure no longer claims read-only access on a management profile, and an unprobed
  management session is no longer described as read-only.

### Per-profile managed NUT files

A profile now records which of the five supported files it exposes, defaulting to all of them so a
profile saved before the setting existed behaves exactly as it did. Disabling a file only removes it
from the Administration list for that profile: nothing on disk is created, renamed, or deleted, and a
file that is enabled but currently absent still appears and reports its missing state when opened.
Enabled-by-profile and currently-present are deliberately kept as separate facts.

Zero files is allowed rather than blocked. A remote profile used only for monitoring is a legitimate
product state, and the Administration surface already has an empty-file-list path, so forbidding it
would invent a rule the architecture does not need. The form says plainly what an empty selection
means.

Detection is a separate, explicit step. `INutManagedFileDetector` reports which supported files are
actually present and hands back a proposal; nothing is applied without the administrator asking.
The local detector reads the presence flags the installation detector already produces, and the
remote one reads what directory validation already established over the existing session — the same
pinned host key for SFTP, the same exact-share confinement and resolved credential for SMB. Neither
adds I/O, opens a session, or looks at a name outside the closed set, so the `.sample` files NUT
ships and directives like `upssched.conf` are never offered.

Administration takes its file list from the profile's selection, and refuses a file outside it, so
the selection can never point at something the profile does not expose. Because the runtime profile
context is captured at bootstrap, a change to the selection applies on restart, exactly like every
other profile edit.

### Validation record

Gates: Release build with 0 warnings and 0 errors; 1150/1150 tests, up from a 1114 baseline;
vulnerability gate clean; `dotnet format --verify-no-changes` clean; `git diff --check` clean.

Windows runtime, current-identity profile against a real SMB share: no password control present on
either surface, the management profile is described as such rather than as read-only, and the status
light renders with its reduced core, glow and pulse intact. No configuration was applied.

Not exercised on the development machine: the native dialog was not opened against a second Windows
account, because no alternate credential with access to the share was available there. That path —
prompt, cancel, successful sign-in, and both remember variants — is covered by automated tests
against a faked native seam rather than by manual validation, and is worth confirming on a machine
where a test account exists.

## T31 — Collapsible NUT file rail

**Status:** DONE

### Objective

Turn the fixed file list on Administration → NUT Configuration into a collapsible page rail, so the
editing form gets the space back, and bring that surface up to the current visual language.

### Allowed scope

- `NutManager.App` presentation for the configuration page, the shared rail styles it needs, and the
  settings preference that remembers its state.

### Requirements

- expanded and collapsed states, with the collapsed one showing icons and keeping every row named;
- only the files the profile manages, and a dignified empty state when it manages none;
- the state persists and is restored on the next launch;
- folding never changes the selected file, rebuilds an editor, or touches a draft;
- switching files keeps using the existing guard.

### Do not

- alter T13/T14, the transports, credential handling, or the T30 file-selection logic;
- add a continuous animation; the connection light stays the only one.

### Narrow layouts

Below 860 px the page cannot afford both a labelled rail and a usable form beside it, so the rail
folds. It folds without touching the stored preference: the administrator asked for it expanded, and
widening the window again gives back exactly that rather than a state the layout imposed. There is
no second UX for small windows — the same rail, just folded.

### Implemented

- a rail whose column changes width rather than a panel that is shown and hidden, so collapsing gives
  the editing form the space back;
- folding is presentation only: the selected file, its draft and its editor come through untouched,
  and rows are buttons rather than list items so the existing dirty-draft guard stays in charge of
  whether a switch happens at all;
- only the files the profile manages, with a dignified empty state when it manages none;
- the expanded state persists through the settings store at schema 4, and a document written before
  the preference existed opens expanded;
- an acrylic pane behind the shell, two deliberately separated tones, and glass surfaces in Apple's
  language — frosted and cool, a thin white hairline for an edge, larger continuous radii — with
  foreground colours untouched so text keeps the contrast it had;
- narrow layouts fold the rail without altering the stored preference.

### Validation

Gates: Release build with 0 warnings and 0 errors; 1201/1201 tests; vulnerability gate clean;
format and whitespace clean.

Windows runtime: the shell, the acrylic backdrop and both themes were confirmed on screen, and the
rail was driven expanded and collapsed with its rows named in both states.

### Known follow-ups

Two items were accepted rather than completed, and neither blocks the surface working:

- the rail was last seen on screen before the two-tone glass landed, so the current appearance of
  that specific panel is confirmed by the theme captures rather than by a picture of the rail itself.
  Seeing it needs a local profile or a reachable configuration share;
- external icon fonts were authorised but not adopted. Doing so means adding a package, recording it
  in the third-party notices, and reversing the "no icon font, no icon package" decision recorded in
  the design system and the agent rules. It belongs in a round that changes those documents together
  rather than leaving the repository asserting one thing and practising another.

## T32 — Icon library adoption and T31 visual acceptance

**Status:** DONE

### Objective

Settle the icon system with an explicit, investigated decision, and see the T31 rail on screen with
the glass it actually ships with.

### Allowed scope

- the icon catalog and whatever dependency the decision requires, the three documents that record the
  icon policy, and focused tests;
- manual Windows validation of the configuration rail.

### Requirements

- a real comparison of maintained options, recorded with licences and reasons;
- the semantic catalog stays authoritative — views must not reference an icon library directly;
- no icon fetched over the network at runtime;
- the rail observed in dark and light, expanded and collapsed, plus the narrow-layout fold.

### Do not

- start operational functionality, reimplement the rail, or change any transport, writer or
  credential boundary.

### Icon decision

Investigated and recorded in the design system. `FluentIcons.Avalonia` (MIT, Avalonia 12) renders
through a font and exposes no geometry, so it cannot fill a catalog without putting a library
reference in every view. `Material.Icons.Avalonia` (MIT) is vector and exposes path data through
`MaterialIconDataProvider.GetData`, so one adapter can fill the catalog while the views go on asking
for semantic names. **It was adopted, and it now supplies all 62 icons in the product.**

Twenty-one glyphs had been assembled from several shapes each so their parts could animate
separately — LEDs blinking out of phase, a gear turning around a stationary hub, a dot sweeping a
trace, a sun's rays turning around a fixed disc. Those parts were removed. The trade was made
explicitly: one drawing system across the whole product outranks segmented animation, because a
single icon animating more richly is not worth having one icon that is not from the library. The
motion moved to the whole glyph — breathe, pulse, pop, beat, turn — and the amplitudes came down with
it. `NutIcons.axaml` is now a fallback catalog: it defines the valid names and a drawing for each, in
case a future version of the library drops a kind.

The dependency rule in the agent instructions permits an icon library on the same terms as any other
dependency, so the repository no longer asserts a rule it had replaced.

### Liquid glass hover

The glass panes react to the pointer: the surface lightens and its edge comes up over 180 ms. Scoped
to the surfaces that are actually glass — the cards and the configuration file strip — plus a separate
rung for the rows that sit on them, so a hovered row does not vanish into a hovered pane. Containers
(the sidebar, the shell chrome) stay inert. Nothing moves or resizes, and pressed, selected and
disabled all still win over hover.

### Visual acceptance

Observed on Windows with a temporary local profile, restored afterwards and verified by hash: the
navigation icons and their block motion, the glass hover measured in pixels on both themes, the
horizontal section tabs and file strip, the segmented file tiles, the acrylic backdrop against the
desktop, and the power source on the status badge.

The rail's fold was observed while it existed and then removed outright later in the task, so the
narrow-layout fold that this section originally recorded as inconclusive no longer has anything to
confirm.

Not confirmed on screen, and carried into T33's follow-ups: the file strip loaded from
`\\Gandalf\etc` rather than a local profile, and the battery variant of the status badge, which
needs the UPS actually running on battery.

### Validation

- icon catalog and policy tests, plus documented manual Windows observation.

## T33 — Code health, dead-code cleanup and focused refactoring

**Status:** DONE

### Objective

Remove what T27A–T32 left behind, and nothing else. This is internal quality only: no redesign, no
feature, no architectural rewrite.

### Allowed scope

- code, resources, localization keys and documentation proven to have no consumer;
- duplication with genuinely identical semantics;
- concrete async, lifetime and disposal defects;
- names that became misleading after T32;
- focused tests defending a real boundary.

### Requirements

- prove a problem before changing anything, then change the minimum;
- anything doubtful is kept, and the reason recorded;
- absence of a textual reference does not prove a public member is dead — Avalonia resolves views,
  bindings and resources by name, template and convention;
- behaviour and approved visuals stay exactly as they are;
- every boundary listed under "Do not" stays intact.

### Do not

- simplify T13, T14, T16, T17, T19, T19B, T20, T25, T27A navigation ownership, T28 embedded secrets,
  T30 managed files, or the T32 icon boundary;
- remove serialized members that older settings files still depend on;
- treat missing coverage as proof that code is dead;
- optimise speculatively.

### Validation

- the full gate set including a warnings-as-errors build, plus a runtime smoke over every page.

### Outcome

The audit ran before any edit, and most of what it flagged was kept. Three searches produced almost
all of the false positives, and each one is worth remembering because a less careful pass would have
deleted working code:

- **Source generators.** A first scan called 131 C# declarations dead. Nearly all were
  `[ObservableProperty]` backing fields and `[RelayCommand]` methods, whose public members are
  generated rather than written. Excluding them left 23 real candidates.
- **Keys composed at run time.** 244 localization keys had no textual consumer, but 209 of them are
  built by interpolation — `Config.Field.{id}.Label`, `{id}.Help`, `Semantic.Operation.{operation}`
  and seven more families. Only 35 were genuinely unreferenced, and those were still kept: an
  unused string costs nothing, and a wrong removal is a blank label in a rarely-visited screen.
- **Word boundaries.** Substring matching hid `NutSpacing2` inside `NutSpacing20` and `NutIconSize`
  inside `NutIconSizeLarge`, so the first resource audit under-reported.

What was removed is what a removed *concept* left behind: fourteen view-model members with no
binding and no caller, including two byte-identical duplicates of the properties the administration
view actually binds; the two `NutFileRail*` metrics belonging to the rail T32 replaced; and the six
localization keys whose only consumers were the members deleted here. The design-token palettes in
`NutMetrics.axaml` and `NutMotion.axaml` stayed even where unreferenced, because the design system
documents them as its source; so did the Core `*Repeated*` mutator overloads, `NutEmbeddedSecret`
and the catalog vocabularies, which sit on the T13 and T28 boundaries.

One name was corrected rather than removed: the administration view styles its buttons
`nut-file-tile` and its own class comment describes a strip, while its handlers still said rail, so
`ConfigurationFileRailItem_OnClick` and `ConfigurationFileRailIcon_OnAttached` became
`ConfigurationFileTile_OnClick` and `ConfigurationFileTileIcon_OnAttached`. The leftover
`nut-file-rail-icon` class went in the follow-up below, once it turned out not to need renaming at
all: no selector anywhere referenced it, so the attribute styled nothing and could simply go.

No subscription leak was found: in all ten cases with unbalanced `+=`, the subscriber owns the
publisher or is the control itself. No sync-over-async was found either — every `.Result` hit was a
property on a result record, not a blocked task.

### Visual follow-up requested after T33 closed

T33 itself changed no visuals. What follows was asked for afterwards, on the same branch, and is
recorded here so the section above is not read as covering it.

**Neon hover on the metric cards.** Each card now lights up in the colour of what it measures, the
way its glyph already did: green for charge, blue for load, purple for runtime, amber for input. The
glow is two shadows rather than one — a short ring that reads as the lit tube and a wide halo that
reads as its spill. Two Avalonia constraints shaped it. `BoxShadowsTransition` interpolates shadow by
shadow, so the resting style carries two fully transparent shadows at zero radius; without them the
neon would appear in a single frame instead of growing. And `BoxShadow` is a parsed value rather than
a brush, so it cannot take a `DynamicResource` — the colours are literals that mirror
`NutHealthyBrush`, `NutCyanBrush`, `NutPurpleBrush` and `NutWarningBrush` and have to change with
them. Nothing loops; the connected halo is still the only looping animation in the product.

**The cards became matte at every moment.** `NutSurfaceHoverBrush` carries a 70% alpha, so a card
that was opaque at rest turned translucent under the pointer and let the wallpaper through — the one
place the cards contradicted their own material. The fix was to drop the hover fill entirely rather
than swap the brush: the card keeps its sheen throughout, and the lift, the lit edge and the glow are
the whole response. That also removed a fade that never ran, since the resting fill is a gradient and
the hovered one was solid, and `BrushTransition` only interpolates between solid brushes.

**Transparency is now bound to the palette.** Acrylic only reads as glass under the dark theme, so
under the light one the switch is disabled and a question mark beside it explains why, appearing only
while the control is unusable. The user's choice is held apart from what the window draws:
`MainWindowViewModel` keeps the preference and computes the backdrop as preference AND dark, so a
trip through light theme suppresses transparency without resetting it. Three tests pin that.

The switch reports the backdrop in use rather than the stored choice, so under the light palette a
disabled control reads "off" instead of claiming a transparency the window is not drawing. The
preference survives underneath, and the setter is inert while the control is disabled so no stray
binding write can erase a value the user cannot see.

Two dead things went with it. `Border.nut-card-interactive` and its hover rule had no consumer in the
application at all — seventeen lines whose comment described a policy ("only for surfaces that
actually navigate or activate something") that nothing followed. And the `nut-file-rail-icon` class
on the file tile's icon panel was matched by no selector in any styles file, so it applied nothing;
removing the attribute is behaviour-neutral by construction rather than by inspection, which is what
made it safe to do without being able to render the strip.

Both were missed by the T33 audit for the same reason: that pass covered resources declared with
`x:Key` and never enumerated **style class selectors**, so a class defined but never used, or used
but never defined, fell outside every search it ran.

## T34 — Remote Windows NUT service monitoring

**Status:** DONE

### Objective

Read-only monitoring of the Windows NUT service and its process from a remote NutManager instance,
independently from NUT protocol health.

### The separation this task is built around

NUT protocol health, Windows service health and Windows process presence are three different facts,
and the whole point of T34 is that the product stops implying they are one:

```
Remote managed profile
    ├── NUT protocol probe ──→ Gandalf.sbra.local:3493
    └── Windows service probe ──→ SCM ──→ service ──→ process id
```

A refused SCM query says nothing about whether upsd is answering, so it must never turn into "server
offline". The monitor holds no connection, endpoint or protocol state at all — there is nothing it
could mark offline, and a test asserts that absence rather than trusting the intention.

### What was built

`IRemoteWindowsNutServiceProbe` in Core has exactly one verb, and it observes. A mutation would need
a new member on the interface, which is a reviewable change rather than a silent one.

`WindowsRemoteNutServiceProbe` uses `ServiceController` for enumeration, identity and state, because
that is managed code and needs no interop. It does not expose a process id or a binary path, so those
come from two query-only Win32 calls isolated in `WindowsServiceControlManagerInterop`: the whole
declared rights list is `SC_MANAGER_CONNECT`, `SERVICE_QUERY_STATUS` and `SERVICE_QUERY_CONFIG`, and a
test refuses the file if any mutating right or API name appears in it.

Identification reuses `WindowsNutServiceAssociation`, the same rules the local detector applies. What
cannot follow is the containment check: locally the binary is verified to sit inside the detected
installation root, and there is no trusted root for a host whose filesystem this task may not touch.
So remote identification rests on service identity, the binary path is reported rather than used as
proof, and more than one plausible candidate returns `AmbiguousService` instead of a guess.

`NutServiceState` gained `PausePending` and `ContinuePending`. Windows reports four transitional
states and the probe passes all four through; collapsing them would lose the difference between a
service settling and a service in trouble. The local mapping is untouched.

### Authentication

The current Windows identity, and nothing else. No credential is collected, prompted for or read from
any store, and the SMB and SSH credentials of T19/T19B/T20 are deliberately not reused: they belong to
different boundaries. A refused query shows an informative state and never opens a prompt.

### Lifetime

One probe in flight per monitor, ever. A blocked RPC can outlive its interval, and starting another
call each tick is how a monitor becomes a thread leak against a host that is already not answering, so
a second refresh joins the running one instead. Stopping or disposing invalidates a generation counter
first and unconditionally, because cancellation cannot recall a call already inside Win32 — the guard
is what stops a late answer writing into a view model nobody is watching. Polling follows the
section's visibility rather than its attachment, since administration sections are switched by
`IsVisible` and an attached-but-hidden panel would otherwise poll forever.

### What T34 does not do

No start, stop or restart of a remote service, and not as a disabled button either: the view model
publishes one command and it refreshes. No firewall change, no service ACL change, no impersonation,
no remote process enumeration, no WMI, no remote registry, no WinRM, no `Process.Start`. No NUT
configuration file is read or written.

### Acceptance as observed

The query reached GANDALF and GANDALF refused it: `ERROR_ACCESS_DENIED` (5), which is authorization
failing rather than the network — a blocked RPC returns 1722. NutManager runs as `PT90N\Marcelo`, a
local account on a machine that is not joined to `SBRA.LOCAL`, so the remote SCM has no identity to
recognise. That is a property of the machine, not of any particular session.

What the run did prove is the thing this task exists for. Throughout the refused query the shell kept
reporting the UPS as connected on `Gandalf.sbra.local:3493`: an administrative failure did not become
an outage. The panel showed "Acesso negado" with the numeric code beside a NUT that was plainly fine,
which is case D of the state matrix, observed live rather than argued.

What stayed unproven is the positive path — a successful SCM authentication showing Running with a
process id — and it stayed unproven for environmental reasons this task is forbidden to change. T35
supersedes the approach rather than fixing it: an agent on the server queries its own SCM locally, so
the cross-machine authentication that failed here stops being on the path at all.

### Validation

- the full gate set including a warnings-as-errors build, plus a runtime smoke over every page;
- acceptance against the real GANDALF profile, recorded exactly as observed.

## T35 — Windows agent for secure remote NUT service control

**Status:** DONE

### Objective

An agent on the NUT server that monitors and controls the one NUT service it has validated for
itself, over a transport Windows authenticates.

### Why the approach changed rather than the code

T34 reached GANDALF and GANDALF refused it: `ERROR_ACCESS_DENIED`, a cross-machine authentication
that a non-domain client with a local account cannot satisfy. Moving the SCM call to the machine that
owns the service takes that authentication off the path to controlling a service. It does not remove
authentication from the product — the agent authenticates its callers — it removes the hop that had
no identity to offer.

### What the agent will not accept

Nothing in the protocol names a service, a path or a command. The request is three scalars, so a
caller cannot redirect the agent at a target it did not choose. That is the confused-deputy defence,
and it is structural: implementing a redirect would require adding a field, which is a reviewable
change rather than a silent one.

### The containment T34 could not perform

The agent runs on the server, so it can require what a remote observer cannot: the service binary
must live inside the detected NUT installation. Name-based association is not sufficient here, and a
service that borrows the name "Network UPS Tools" while pointing elsewhere is refused. Revalidation
before every mutation re-runs the whole selection and compares the binary path as well as the name,
which closes the window between "we checked" and "we acted".

### Fail-closed, in the specific places it matters

- No operators group means no control, for the lifetime of the process. There is no fallback to
  Administrators; the group is resolved machine-qualified so a domain group of the same name cannot
  become the authority.
- No usable audit sink means no mutation. A privileged action nobody can account for is worse than no
  action.
- No unambiguous NUT service means no authority to act.
- Not running as LocalSystem means the service refuses to start.
- The group, the event source and the service registration are deployment acts. An agent that can
  create them is an agent that can be made to.

### Transport

A named pipe, versioned in its name. The ACL grants LocalSystem and the operators group and names
nobody else, because a pipe grants nothing it was not told to grant. There is deliberately no deny
entry for Everyone: a deny outranks every allow and every operator is also a member of Everyone.

Framing is length-prefixed, bounded, and checks the declared length before allocating. Reads are
exact. A peer closing on a frame boundary is not an error; stopping half-way through one is.

Connections are handled concurrently so a status poll keeps being answered while a restart holds the
mutation gate.

### What the client keeps from T34

No transport failure is ever reported as a NUT outage, and a test refuses the status enum if a name
like `Offline` or `ServerDown` appears in it. An agent that is missing from a server whose upsd is
answering is an administrative gap, and the product says so.

### Desktop integration

`RemoteWindowsServiceViewModel` was evolved rather than replaced: it reads through
`INutManagerAgentClient` now, and everything T34 built into it survives — the ten-second interval,
one probe in flight, the generation guard that stops a late answer writing into a view nobody is
watching, and the stale reading that stays legible while being labelled. It still exposes exactly one
command, and that command refreshes.

Control lives in `RemoteWindowsServiceControlViewModel`, a separate object. The separation is what
keeps T34's assertion true, and a test now reflects over the monitor for mutation members rather than
trusting the intent. The control object does not poll: it reads the monitor's observation and asks it
to refresh once an operation finishes, so there is still only ever one probe in flight.

Stop and Restart require an explicit confirmation that names the host, the action and the service.
Restart is one request; the desktop never composes a stop followed by a start, because the atomicity
belongs to the agent's mutation gate. Each action carries one operation id, generated once, and
nothing is retried automatically.

Buttons follow the handshake rather than the service state. An agent whose audit sink is unusable
reports a perfectly healthy service and still offers nothing.

### No fallback

When the agent cannot be reached the panel says so. There is no second attempt over the remote SCM,
and a test refuses the application composition if the probe reappears in it. A silent second path
would leave an operator unable to tell which route answered, or why control is unavailable on a
server whose monitoring appears to work.

### Profile

`NutAgentTransportKind` is separate from `RemoteConfigurationTransportKind`: editing configuration
over SMB while controlling the service over a named pipe is an ordinary combination, and one setting
must not decide the other. Schema 6 is additive — a version 5 document loads unchanged and takes the
named pipe, and an unreadable or unusable transport falls back to it rather than making a profile
unopenable.

### T34 supersession audit

`IRemoteWindowsNutServiceProbe` has no production consumer left, and a test asserts that rather than
claiming it. The type was not deleted: `WindowsRemoteNutServiceProbe` still supplies the host
normalisation and executable-name helpers the agent uses, and
`WindowsServiceControlManagerInterop` still answers the agent's local process-id query. Removing the
remote probe path means relocating those helpers, which is a separate change with its own risk, so
the finding is recorded and the removal left as a follow-up.

### HTTPS

Implemented on HTTP.sys through ASP.NET Core's `UseHttpSys`. It was first written against
`System.Net.HttpListener`, which is also HTTP.sys and needed no framework reference; that was
replaced because Microsoft marks `HttpListener` as not recommended for new development and gives it
limited servicing, and a privileged agent is the wrong place to depend on a type in that state. A
test now refuses the whole `src` tree if `HttpListener` reappears.

The cost is explicit and operational: the published agent requires `Microsoft.AspNetCore.App`
alongside `Microsoft.NETCore.App`, so the server needs the ASP.NET Core runtime even when HTTPS stays
disabled. The deployment guide states it rather than leaving an administrator to discover it from a
service that will not start. Kestrel was not considered: HTTP.sys keeps TLS and the certificate
binding with Windows, where an administrator put them, instead of inside the process.

It is off unless a deployment turned it on, so installing the agent opens no port.
`AuthenticationSchemes.Negotiate` means HTTP.sys authenticates before the request reaches the agent:
an anonymous caller never arrives at the code that would refuse it. Membership of the operators group
is then required, through the same check the pipe uses, and the request goes to the same dispatcher —
neither transport has its own opinion about what an operation means.

One route, `POST /v1/agent`, and nothing else. There is no endpoint that names an operation and no
unauthenticated health probe. The body is bounded before it is deserialized, with the same ceilings
the pipe uses.

Every way the configuration can be wrong stops the listener rather than degrading it: a plain-text
prefix, a wildcard host, a thumbprint that is not hexadecimal, a certificate that is missing or has no
private key. The named pipe keeps working in every one of those cases, so a mistake in the HTTPS
configuration cannot take away the transport that was already secure. The certificate is named by
thumbprint and lives in `LocalMachine\My`; the agent never creates, installs or trusts one, and the
TLS binding and any URL reservation are `netsh` steps the administrator performs.

### The alternate Windows account

The client authenticates as the current Windows identity, or as an explicit account handed to
Negotiate through the handler — no `LogonUser`, no process-wide impersonation, and nothing that
changes the identity of anything else the application is doing. That is what makes a client outside
the server's domain usable without establishing a session first, which is the environmental problem
T34 ran into.

The password is stored under the agent's own Credential Manager target and kind. Same server and same
user name do not make the secrets equivalent: the SMB one authorizes reading configuration files and
this one authorizes controlling a service, so a test asserts that the agent path never reads the SMB
credential. Certificate validation is the platform default; a test refuses either transport file if a
validation callback ever appears in it.

### Where this task was closed, and why there

T35 delivers the agent and the two authenticated ways of reaching it: the server-side authority, the
named pipe, HTTPS on ASP.NET Core HTTP.sys with Negotiate, the validated NUT-only service control,
the Event Log record, and the desktop monitoring and control that consume them. The profile carries
the transport, the endpoint, the authentication mode and the account name, and the application builds
the right client from them.

What was deliberately left out is the **graphical** editing of those options and the operational
acceptance that follows it. That is a scope decision rather than an unfinished implementation: the
model, the persistence, the migration, the credential boundary and both clients exist and are tested,
and what T36 adds is the surface an operator uses to set them without editing a profile document.

Deferred to T36:

- the agent section of the profile editor — transport selector, HTTPS endpoint, authentication
  selector;
- the interactive credential lifecycle — authenticate, change, remember, forget, and the status an
  operator reads;
- the desktop runtime smoke;
- installation on the real server, and acceptance against it.

### What was not validated

Stated plainly, because closing a task does not turn an unrun check into a passed one:

- the desktop runtime smoke was **not run**;
- the agent was **never installed** on GANDALF, and nothing on that machine was touched;
- no real Start, Stop or Restart was performed against the live NUT service.

These are deferred acceptance activities belonging to T36, not defects in what T35 built. Destructive
operations against the real service continue to require the user's explicit authorization at the
moment they are run.

### Known limitations

The published agent carries the SSH and WMI libraries because it references
`NutManager.Infrastructure` as a whole. No agent code path reaches them. Narrowing the payload is a
packaging change; the platform-specific code cannot move into `NutManager.Core`.

## T36 — Windows agent settings and deployment UX

**Status:** IN PROGRESS

### Objective

Expose the agent transport and authentication model T35 built through the graphical profile editor,
complete the native credential lifecycle, and carry out the desktop and server acceptance that T35
deferred.

### Scope

- an agent section in the remote profile editor: transport (named pipe or HTTPS), the HTTPS
  endpoint, and the authentication mode (current Windows identity or an alternate account);
- the credential lifecycle through the existing native prompt: authenticate, change, remember,
  forget, and a status an operator can read without seeing a secret;
- pt-BR and en-US parity, and accessible names for every new control;
- profile save and reload, including a draft that is never persisted while invalid;
- the desktop runtime smoke;
- installation on the real server and acceptance against it.

### What is implemented

The profile editor now carries the agent section for a remote profile: the transport, and — for
HTTPS — the endpoint, the authentication mode and the account the profile is configured for. The
section is absent for a local profile, because there is no remote agent to reach.

The named pipe offers no endpoint and no account, and that is the point rather than an omission:
over the pipe the caller is whoever Windows already authenticated, so an account field there would
be a promise the transport cannot keep. The model normalises the same way, so a draft switched back
to the pipe cannot carry a stale endpoint or account into the saved document.

The endpoint is validated by the same rule the model uses — absolute, https, a named host, no
embedded credentials — reported inline as the operator types and again by the profile validator, so
an invalid draft cannot be saved. The agent fields take part in dirty tracking, Cancel and
save/reload like every other field, and the agent transport stays independent of the configuration
transport: SMB with a named pipe is an ordinary combination and a test asserts it.

There is nowhere in the editor for a password. The draft has no property that could hold one, by
name or by type, and the section contains no password box — the secret is collected by the Windows
credential dialog and kept in the Credential Manager under the agent’s own entry.

### The credential lifecycle

Signing in uses the Windows credential dialog T30 already owns, and the rule the whole flow is built
around is that the dialog returning OK is not authentication. It collected a credential; whether that
credential has any rights on that server is a question only the agent can answer, so a handshake is
performed against the configured endpoint before anything is remembered, stored or shown as valid. A
password typed correctly for an account with no rights is a password that must not be saved.

Where the secret lives follows from what the operator asked for. Declining to remember it does not
mean the connection they just authenticated should stop working, so it is held in memory for the
session and nowhere else — the session store has no path that writes to a file, a registry key or the
Credential Manager. Choosing to remember it does not write it at authentication time either: a
credential stored for a profile the operator then cancels is an orphan nobody will think to remove,
so persistence waits until the profile has actually been saved.

The profile store and the Credential Manager are separate stores with no transaction across them, so
the order is chosen instead: the profile first, then the secret, and a failure to write the secret is
reported rather than papered over — the profile would otherwise point at an account whose password
only exists in memory.

Failure never destroys what worked. A rejected or cancelled replacement leaves the previous
credential exactly as it was, and the account shown always comes from the dialog rather than from
what was typed, so a profile cannot claim one account while the secret belongs to another. A late
handshake cannot publish into a draft the operator has since changed: the same generation guard the
monitor uses applies here.

Forget clears the agent's persistent, session and pending credentials for that profile and touches
neither the SMB nor the SSH secret — they authorize different things.

A stored credential is reported as stored, never as valid. It may have been changed on the server or
expired since, and only a handshake settles that.

### The operator group on a domain controller

Installing on the real server found a defect before the service was ever created. The agent resolved
its operator group machine-qualified, as `MachineName\NutManager Operators`, which is correct on a
workstation or member server and resolves to nothing on a domain controller — there the group is held
in the directory and exists as `DOMAIN\NutManager Operators`. On GANDALF the group was plainly
present, with its member, and the agent would have started and refused every operation.

Resolution now follows the server's own local group database rather than a name the code assembles:
the local group API is asked whether the name is a group it holds, and only then is the name
translated, with the search starting at the local system. A workstation or member server answers from
its SAM, a domain controller from the directory it uses as its local database, and neither the domain
nor the machine is named anywhere in the code.

Asking in that order is what preserves the original guarantee. A member server that also has a domain
group of the same name still pins its own: the existence proof runs against the local database first,
so a name that is only a domain account never reaches the translation. The resolved account must also
be a group — a user or a computer answering to the name is refused rather than pinned as the
authority over service control.

The rest is unchanged. The SID is still pinned at startup, authorization still compares SIDs rather
than names, the pipe ACL is still built from the pinned SID, indirect membership is still expanded,
and every failure — missing group, failed lookup, wrong account type, SID mismatch — still fails
closed.

### What is still open

The desktop runtime smoke of this flow beyond a cancelled dialog, and the acceptance on the real
server, both remain. No real credential was entered against GANDALF, and the agent is not yet
installed there: the domain-controller fix landed before the service was created, so the corrected
build is what will be deployed.

### It consumes T35 rather than repeating it

The profile model, the schema, the credential kind, the agent protocol, both clients and the
service-control path already exist. T36 must use them. It must not introduce a second agent protocol,
a second credential store, another HTTPS or named-pipe client, or another route to service control —
a parallel implementation is how two security boundaries end up disagreeing, and only one of them
gets reviewed.

The managed profile schema stays at version 6. Nothing in this scope needs a new field.

### Acceptance on the real server

Requires, on the server: the ASP.NET Core runtime 10, the agent installed and running as LocalSystem,
the `NutManager Operators` group, the Event Log source, an authorized operator account, and — for
HTTPS — a certificate in `LocalMachine\My` with its HTTP.sys binding and any URL reservation.

Then verify: the handshake answers, `GetStatus` reports the right service identity and process id,
NUT's own protocol health stays independent of all of it, and an unauthorized caller is refused.

Start, Stop and Restart against the live NUT service require the user's explicit authorization at the
moment they are run. That rule does not relax because the agent is installed.

## T38 — Remote COM and hardware inspection through the Windows agent

**Status:** IMPLEMENTED — pending acceptance on the real server

### Objective

Let a remote profile see the serial hardware of the machine it manages, through the agent, on the
same Devices and drivers screen a local profile already uses — and keep that inspection completely
passive on the server.

### Scope

- one read-only agent operation that enumerates the server's serial devices;
- capability negotiation, so an older agent is detected rather than asked;
- equal behaviour over the named pipe and over HTTPS, with no fallback either way;
- vendor and product identifiers parsed from the Windows PnP device id;
- a small, verified controller catalogue with no network lookup;
- a status for each port that never turns absent metadata into a fault;
- the configured `port` from the server's `ups.conf` related to what was detected;
- the existing screen reused rather than a second one built;
- pt-BR and en-US parity for every new string.

### What is passive means here

The agent enumerates and does nothing else. It does not open a serial port, transmit a byte, send
`Q1`, run `nutdrv_qx` or any other driver, change a device setting, touch the registry beyond the
read the existing COM source already performs, or write configuration. A NUT driver already talking
to a UPS on the configured port is unaffected by an inspection running, and that is a property of the
contract rather than of a check: the request has no port, speed, command, executable or path field,
so there is nothing in it through which a caller could ask for any of that.


### Enumeration sources and who decides existence

Acceptance on the real server corrected the enrichment model. `Win32_SerialPort` returned nothing at
all for the Prolific USB-to-serial adapter on `COM3`, so the port was listed bare — no name, no
manufacturer, no identifier and no fault code — and read as grey on a device that was working
perfectly. `Win32_PnPEntity` knew all of it.

The rule is now explicit and enforced by `WindowsComPortEnumeration.Merge`:

- `SERIALCOMM` is the only source of existence. A port disabled in Device Manager leaves the device
  map and stays a findable PnP entity, so a `COM2` in that state must not be listed.
- `Win32_SerialPort` enriches first, being the more specific class.
- `Win32_PnPEntity` fills only what is still missing, matched to its port by the `(COM3)` suffix
  Windows appends to the display name. A row that resolves to no port enriches nothing.
- Neither WMI class may introduce a port. The merge looks the resolved name up in the map built from
  `SERIALCOMM` and drops the row when it is absent.

`ConfigManagerErrorCode` is read as the number Windows stores. `CM_PROB_NONE` is zero and is a real
answer, so it is filled in only when absent rather than when falsy, and a clean port cannot be
overwritten by a later, less specific row. No localized description is parsed anywhere.

### The capability, not a version

`GetHardwareSnapshot` is announced in the handshake's capability list. An agent built before this
task does not announce it, and NutManager reads that and reports device inspection as unavailable for
that server instead of sending a request that would be refused.

The protocol version stays at 1 deliberately. Raising it would make an older agent reject every
request from a current client — including the handshake that would have carried the answer — turning
a clean "this server cannot be inspected" into a total failure. In the other direction the hardware
payload is an optional response field, so a current agent answers an older client unchanged.

The capability is announced independently of control availability. An agent whose NUT service could
not be pinned, or whose audit sink is unusable, reports control as unavailable and still enumerates
serial devices perfectly well; gating the read behind control would report a readable machine as
having no ports.

### What is claimed about a device, and what is not

The identifiers come out of the PnP device id and nothing else. `USB\VID_067B&PID_23A3\…` and FTDI's
`FTDIBUS\VID_0403+PID_6001+…` are both read, in either case; a PCI serial port is recognised as PCI
but its `VEN_`/`DEV_` pair is not reported as a VID/PID, because that is a different identifier space
and presenting one as the other would be a small, confident lie.

The controller name comes from a fixed local table of verified vendor/product pairs. An unrecognised
pair produces no controller name rather than the nearest neighbour. Prolific is entered at family
level — `PL2303` for both `067B:2303` and `067B:23A3` — because no verified offline mapping for the
G-series suffixes was available to this build; a specific variant is never reported on the strength
of a guess. Nothing is lost by that restraint: the driver's own friendly name is shown in full on the
line above, and it is the driver that names the variant. There is no internet lookup of any kind.

A catalogued vendor name is used only where the device itself reported no manufacturer.

### Port status

| State | Meaning |
| --- | --- |
| Healthy | Present, and Windows reported a fault code of zero. |
| Warning | Present, and Windows reported a fault code or a status other than OK. |
| Unknown | Present, and nothing further is known. `SERIALCOMM` lists it and WMI has no record. |
| Critical | Named but not currently exposed by the operating system. |

`SERIALCOMM` is authoritative for presence and WMI is enrichment, so a port WMI knows nothing about
is a present port with unknown metadata — never a fault, and never absent.

These states describe a Windows device. They are not a statement about NUT. A COM port that exists is
not a UPS that answers, and the screen keeps "COM present", "driver configured", "driver running",
"UPS answering", "agent connected" and "NUT service running" as six separate facts.

### Relating the configuration to the hardware

The agent does not read `ups.conf` and no second writer was introduced. The server's document is
loaded through the profile's own configuration transport — SFTP or SMB — and interpreted by a
platform-neutral reader that reports what cannot be established from another machine as not
established: the driver executable is `NotApplicable` and its runtime state is `Unknown`.

The comparison is informational and changes nothing. The detected ports are offered to the graphical
`ups.conf` editor as choices for the `port` field, exactly as local ports already were; writing still
goes through the semantic draft, the schema, the T13 document, the review, the generated preview and
the T14 safe-write pipeline.

### One screen, three states

Devices and drivers is now applicable on a remote profile and is described the way the Windows
service section has been since T35: available through the agent. The cards are the same cards; a
source line names the machine that was examined, so a remote reading is never presented as a local
diagnostic.

The screen distinguishes local inspection, remote inspection through the agent, and no inspection at
all. The third is a statement about the source, never about the server: an unreachable agent, or one
without the capability, leaves the port list unknown rather than empty, and every claim about a
configured port being present or absent is gated on the list being an answer.

Active driver diagnostics stay local. `upsdrvctl`, driver help, version, variable listing, dry run
and data capture all open the configured device through a NUT driver, which is what this task exists
not to do remotely. The remote screen says so instead of offering buttons that would be refused, and
no remote process execution of any kind was added.

### Refresh and audit

The existing Refresh button serves both profiles and routes by profile with no fallback. No new timer
and no polling were added: serial hardware does not change between two ticks of a monitor.

The read is not audited. The Event Log exists to preserve control records, and a read an operator can
repeat at will would bury them. The audit policy for Start, Stop and Restart is unchanged.

### Acceptance on the real server

The agent must be rebuilt and redeployed on the server before the capability can appear there; an
installed agent that predates this task will correctly report device inspection as unavailable.

Then verify, on a remote profile: the handshake lists `GetHardwareSnapshot`; the Devices and drivers
section lists the server's COM ports with their identity line; the configured `port` from the
server's `ups.conf` is related to the detected list once the configuration session is connected; the
active diagnostics remain absent; and the NUT service keeps running throughout, with its driver's
conversation with the UPS uninterrupted.

## Task execution template

Use this structure when assigning a task to a coding agent:

```text
Objective:
Implement only [task ID and title].

Read:
- AGENTS.md
- relevant section of docs/TASKS.md
- only the required sections of docs/SPEC.md and docs/ARCHITECTURE.md

Allowed files:
- [explicit paths]

Requirements:
- [testable requirements]

Do not:
- change unrelated files
- add unrequested dependencies
- begin another task

Validation:
- [exact commands]

Completion:
- list changed files
- report command results
- report task-specific limitations
- stop
```
