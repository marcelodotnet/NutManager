# NutManager Product Specification

## 1. Product vision

NutManager is a Windows-first desktop interface for Network UPS Tools (NUT). It makes NUT monitoring, configuration, diagnostics, and explicitly confirmed administration understandable without replacing NUT drivers, protocols, or configuration formats.

Monitoring and management are distinct concerns. Monitoring uses standard NUT TCP access; management uses either local Windows capabilities or an explicit SSH/SFTP or SMB remote configuration transport.

## 2. Initial monitoring milestone

The initial MVP milestone established a non-administrative, read-only monitoring application. Its requirements remain the baseline for the package acceptance checklist:

- Avalonia desktop shell with Overview, Devices, Diagnostics, and Settings;
- light, dark, and system themes;
- NUT endpoint, timeout, polling, mock-mode, and preferred-UPS settings;
- bounded NUT TCP connection, discovery, telemetry, reconnect, and stale-data handling;
- deterministic mock data for development and automated tests;
- read-only diagnostics and per-user, non-secret settings.

Missing NUT variables remain unavailable rather than estimated. Unknown NUT status tokens and variable names are preserved. The T11 acceptance workflow remains read-only even though later work added separately confirmed administration.

## 3. Current implementation status

The monitoring base from T01–T10 is implemented. T11 is **DONE**; the distributed Windows package completed its manual live-NUT acceptance checklist.

The current product also implements:

- T12 local Windows NUT installation detection;
- T13 syntax-preserving NUT configuration documents;
- T14 previewed, recoverable configuration writes with backup and rollback;
- T15 graphical editing of existing configuration entries;
- T16 Windows service, privilege, ACL, process, and Event Log administration;
- T17 passive COM and controlled NUT-driver diagnostics;
- T18 managed local and remote server profiles;
- T19 SSH/SFTP remote configuration management;
- T19B SMB remote configuration transport;
- T20 opt-in protected SSH and SMB credential storage in Windows Credential Manager;
- T21 completed Windows local and remote administration validation, whose findings were carried into the tasks that own them;
- T23 completed upstream NUT improvement evaluation;
- T24 modern responsive shell, design system, and `pt-BR`/`en-US` localization foundation;
- T24A managed-profile UX with typed validation and explicit connection testing;
- T24B focused administration and page presentation decomposition;
- T25 semantic graphical configuration framework over T13/T14;
- T26 graphical `ups.conf` with driver-aware descriptors and the `runtimecal` assistant;
- T27 graphical `nut.conf` and `upsd.conf`;
- T27A approved visual fidelity, shared iconography, restrained motion, and navigation hardening;
- T28 graphical `upsd.users` and `upsmon.conf` with change-only secrets;
- T29 graphical-configuration UX hardening;
- T30 Windows-native SMB credential authentication with per-profile managed NUT file selection;
- T31 collapsible NUT file rail, T32 icon library adoption, T33 code-health cleanup;
- T34 read-only remote monitoring of the Windows NUT service;
- T35 the Windows Agent for secure remote NUT service control;
- T36 the agent's settings and deployment UX, and its credential lifecycle;
- T37 UI layout, interaction and visual polish, and the official application icon;
- T38 passive remote COM and hardware inspection through the agent.

T39, Windows installers, packaging and complete operator documentation, is the next planned task.
T22, Linux compatibility, remains deferred.

## 4. Platform and quality requirements

### NFR-001 — Windows-first platform strategy

Windows x64 is the only active and officially supported platform for development, CI, current validation, packaging, distribution, and local administration. Linux compatibility is deferred for possible future evaluation; it has no active CI gate, package, or administration-support commitment.

The shared architecture must avoid unnecessary Windows dependencies, and platform APIs must remain behind the Windows adapter boundary.

### Reliability, security, and testability

- External I/O requires cancellation, bounded timeouts, and controlled errors.
- Core behavior must be testable without Avalonia, real hardware, elevation, or network access.
- Secrets must not appear in logs or UI state.
- Platform-specific actions must be explicit and isolated.
- Accessibility must not rely on color alone.

## 5. Administration safety

The normal NutManager process does not require Administrator privileges. Privileged Windows actions are explicitly prepared, reviewed, confirmed, and routed through a limited UAC helper boundary when required.

Configuration changes use a syntax-preserving model and a recoverable write pipeline: review and diff, backup, temporary-file validation, safe replacement, verification, and rollback. Administration is never automatic, and applying configuration does not automatically restart a NUT service. Monitoring remains independent of management actions.

NutManager writes configuration. It does not execute what that configuration describes: it never runs `SHUTDOWNCMD`, `NOTIFYCMD`, or a forced shutdown, and configuring a permission is distinct from exercising it.

### Two secret domains

Transport secrets and NUT configuration secrets are separate and must not be conflated:

- **transport secrets** — SSH passphrases/passwords and explicit SMB passwords. They are session-only by default and may, after an explicit successful connection, be saved in Windows Credential Manager. They authenticate NutManager to a remote host. An explicit SMB account is collected by the Windows credential dialog rather than by a NutManager control, and the current Windows identity needs no stored credential at all.
- **NUT configuration secrets** — passwords stored inside `upsd.users` and `upsmon.conf`. They are change-only: an existing value is never read back into interface state, review, or preview, and the product reports only whether one is configured. There is no lookup from one domain into the other.

## 6. Managed profiles and remote boundary

Managed profiles separate monitoring from management metadata:

- monitoring stores the NUT TCP host, port, and optional preferred UPS;
- management is Local or Remote and has an explicit access mode.

A remote profile can monitor through NUT TCP and manage configuration only through an explicit SSH/SFTP or SMB file session; it never falls back to local management. The user supplies the directory without server/share autodiscovery; NutManager performs its read-only validation automatically after a successful connection or browse. SSH host keys use explicit SHA-256 fingerprint pinning; an unknown key requires review and a mismatch is rejected. SMB uses an explicit UNC share and current-identity or session-only explicit authentication. A successful explicit connection may opt in to save only its secret in Windows Credential Manager; the profile JSON stores no secret.

Remote ReadOnly profiles can inspect configuration. Remote Manage profiles can write only after an explicit safe-write capability probe: Windows/OpenSSH for SSH/SFTP or verified `File.Replace` behavior for SMB.

Remote Windows administration is a fourth path and not part of the configuration transport. It reaches the NutManager Agent on the managed server, over an authenticated named pipe or over HTTPS, and covers NUT service monitoring and control and passive serial-hardware inspection. The agent never reads or writes a NUT configuration file. Remote ACL administration and active remote driver diagnostics remain unavailable: the latter open the device, and opening a device stays local.

## 7. Post-MVP capability status

### Implemented

- managed server profiles with typed validation and explicit connection testing;
- syntax-preserving configuration parsing plus dedicated graphical forms for all five supported files;
- preview, backup, safe write, recovery, and rollback;
- Windows local service, UAC, ACL, process, Event Log, COM, and driver diagnostics;
- SSH/SFTP and SMB remote configuration management with automatic read-only directory validation; SSH uses pinned host keys and SMB uses a share-root boundary;
- Windows-native SMB credential authentication and per-profile selection of the NUT files a profile manages;
- responsive shell, shared design system, `pt-BR`/`en-US` localization, and the official application icon;
- remote Windows administration through the NutManager Agent: read-only NUT service monitoring, explicitly confirmed service control, and passive COM and hardware inspection, over an authenticated named pipe or HTTPS with no fallback between them.

### Next

- T39 Windows installers, packaging and complete operator documentation: official installers for the desktop application and for the agent, install/upgrade/repair/uninstall, release artifacts and versioning, safe preservation of settings, profiles and credentials, validation on Windows client and Windows Server, and operator documentation written against the build actually distributed.

### Later

- T22 deferred Linux compatibility evaluation;
- multi-server simultaneous runtime, history, notifications, and other future product capabilities as separately scoped.

## 8. Graphical-first administration (T24–T29 implemented)

Normal administrators configure supported NUT settings graphically, without manually editing the supported `.conf` files. Dedicated experiences exist for `nut.conf`, `ups.conf`, `upsd.conf`, `upsd.users`, and `upsmon.conf`. Generated text is a read-only preview or advanced inspection, never the primary editor.

The implemented flow is:

```text
Graphical Form
    → Semantic Draft
    → Schema / Validation
    → T13 syntax-preserving document
    → Semantic Review
    → Generated Preview
    → T14 safe-write
    → Local / SFTP / SMB
```

Applying a configuration change never silently restarts a service.

The implemented UX includes driver-aware UPS controls, setting-specific Automatic semantics, the `runtimecal` assistant, graphical custom parameters that preserve unknown content, redacted change-only secrets, semantic review, a responsive review drawer, and explicit confirmation. Schema sources are the primary official NUT manpages and driver documentation; runtime internet access and guessed defaults are excluded.

T24 also established `pt-BR` (default) and `en-US` UI localization. User-facing presentation is localized through stable semantic resource keys, while NUT filenames, directives, drivers, status/configuration tokens, and serialization stay invariant. Display formatting follows culture; NUT serialization is culture-invariant. Both cultures were validated across the responsive and accessibility states in T29.

## 9. Profile validation and focused administration UX (T24A/T24B)

Structured profile fields validate before Save: monitoring and management host input means only an IP address or hostname, ports remain in range, and remote/local transport requirements are checked across fields. Local/Remote and SFTP/SMB choices are reversible during a draft. Operational Test Connection is separate from syntactic validity and does not disclose secrets.

New installations target real data rather than silently enabling simulated mode, and an existing persisted preference is preserved by the settings migration. Active simulation is visibly identified. Administration is decomposed into focused, responsive surfaces that retain every existing reviewed configuration, service, driver, remote, credential, and privilege boundary.

## 10. MVP package acceptance

The MVP package was accepted after the Windows x64 archive was manually validated against a real NUT server using the read-only checklist in [MVP-VALIDATION.md](MVP-VALIDATION.md). T11 is done; the checklist remains as a regression record.

## 11. Upstream strategy

NutManager documents and reproduces limitations before proposing upstream NUT work. Approved upstream work uses the official `networkupstools/nut` repository and focused branches in `Marcelo-PX/nut`; the upstream source tree is not embedded in the normal NutManager workspace.
