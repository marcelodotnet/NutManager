# Installer architecture

How NutManager is packaged for Windows, which technology builds it, and why that one.

Two products ship separately: the **NutManager Desktop** application and the **NutManager Agent**
service. They are installed independently — either alone, or both — and neither installer contains
the other.

## The decision

**WiX Toolset v5**, pinned, producing one MSI per product wrapped in a Burn bundle so the operator
downloads and runs a single `.exe`.

```text
installer/
  Common/      shared version, branding and platform authoring
  Desktop/     NutManager.Package.wixproj       -> NutManager.msi
               NutManager.Bundle.wixproj        -> NutManager-Setup-x.y.z.exe
  Agent/       NutManager.Agent.Package.wixproj -> NutManager.Agent.msi
               NutManager.Agent.Bundle.wixproj  -> NutManager-Agent-Setup-x.y.z.exe
```

## Why not the alternatives

### Inno Setup

Produces a `.exe` directly and is the least work to start with, which is why it was considered first
and rejected on the two requirements that matter most here.

It has no transactional rollback. A Windows Installer package that fails partway is rolled back by
the operating system; an Inno Setup script that fails partway leaves whatever it had already written.
For a package that registers a Windows service, that is the difference between a failed install and a
half-registered service.

It has no native repair, and installing a service means either shelling out to `sc.exe` or calling
the service APIs from Pascal script. This product has a standing rule against `sc.exe` driven by the
product itself, and satisfying it through custom script code moves service registration into
hand-written imperative code rather than a manifest the installer engine owns.

### MSIX

Ruled out on a technical constraint rather than a preference. MSIX cannot register a classic Windows
service running as LocalSystem with the named-pipe ACL model the agent depends on. Packaged services
exist but are constrained in ways this agent's security design does not fit, and the T35 authorization
boundary is not something to redesign to suit a packaging format.

### A custom installer

Not considered beyond stating it. Writing one means reimplementing rollback, upgrade detection, repair
and Add/Remove Programs registration, each of which is a place to get privilege and file replacement
wrong.

## Why WiX

| Requirement | How WiX answers it |
| --- | --- |
| Windows x64, per-machine | `Package Scope="perMachine"`, `ProgramFiles64Folder` |
| A single `.exe` for the operator | Burn bundle chaining the MSI |
| Windows service registration | `ServiceInstall` / `ServiceControl`, declared — no `sc.exe`, no custom code |
| Upgrade | `MajorUpgrade`, with the engine detecting and removing the older version |
| Repair | Native to Windows Installer; no authoring required |
| Transactional rollback | Native to Windows Installer |
| Preserving user data | Data lives outside the installed component set, so it is never a removal candidate |
| Unattended install | `msiexec` and Burn both define documented quiet switches and exit codes |
| CI | Installs as a .NET global tool; no separate installer product on the build agent |
| Versioning | `.wixproj` is MSBuild, so it reads `NutManagerVersion` from `Directory.Build.props` |
| Authenticode | `signtool` applies to both the MSI and the bundle |
| Two separate products | Two independent project pairs, each with its own `UpgradeCode` |

The decisive one is service registration. WiX declares the service, its account and its start type in
the manifest, and the Windows Installer engine performs the registration, the upgrade replacement and
the rollback. Nothing in the product shells out.

## Why v5 specifically, and not the newest

**WiX v6 and v7 require accepting the Open Source Maintenance Fee (OSMF) EULA.** A build with v7
installed refuses to run at all:

```text
error WIX7015: You must accept the Open Source Maintenance Fee (OSMF) EULA to use WiX Toolset v7.
```

That is a licensing commitment rather than a technical setting, and not one to accept on the project's
behalf as a side effect of a packaging task. WiX v5 is the last release under the MS-RL, is free for
any use, and carries every capability in the table above.

The version is pinned rather than floating. A toolchain that silently moved to v6 would fail the build
on a legal prompt, which is a confusing way to discover a licensing change.

Revisiting this belongs to the project owner. If the OSMF is accepted later, moving to v6 or v7 is a
version bump and a small amount of schema migration, not a redesign.

## Known limitations

**Unsigned by default.** No code-signing certificate is available to this repository, so local and CI
builds produce unsigned artifacts and SmartScreen warns on first run. The signing step exists in the
build script as an explicit, skipped stage rather than as something to bolt on later. See
[Packaging and release](PACKAGING-AND-RELEASE.md).

**No auto-update.** Deliberately out of scope. Upgrading means downloading and running the newer
installer.

**The bundle chrome is not localized.** The Burn user interface is the WiX standard bootstrapper
application in English. The application itself remains `pt-BR` and `en-US`; the installer chrome is not
part of that parity requirement, and asserting otherwise in the localization tests would be false.

## What the installers deliberately do not do

Recorded here because the absence is the design rather than an omission:

- neither installer installs, removes, modifies, starts, stops or restarts **NUT**;
- the agent installer never touches `nut.conf`, `ups.conf`, `upsd.conf`, `upsd.users` or `upsmon.conf`;
- neither installer opens a serial port or runs a NUT driver;
- neither installer creates a certificate, an SSL binding, a URL reservation or a firewall rule;
- neither installer creates a domain group;
- uninstall removes only what the installer itself created.

One thing the agent installer now does, which is worth stating precisely because it is the single
exception to the list above: it may install **Microsoft ASP.NET Core Runtime 10 x64**, from one fixed
Microsoft address, verified by hash before it runs. That is a consequence of the agent publishing
framework-dependent, and it is bounded deliberately — one package, no user-supplied URL, no mirror
list, no generic prerequisite mechanism, and never removed when the agent is removed.

The rule that governs it: an agent whose runtime is absent and whose prerequisite was declined is not
installed at all. A registered service that cannot start is worse than a refusal, because the refusal
is visible and the broken service is not.

## The bootstrapper application

WixStdBA with a custom theme, not a bespoke managed bootstrapper application.

This was reconsidered when the branded screens were specified. WixStdBA turned out to support
everything the design needed — `ThemeFile` for layout, `LocalizationFile` for strings, `LicenseFile`
for the embedded RTF Terms, `LogoFile` for artwork, and named controls bound to Burn variables for the
runtime checkbox — so the alternative would have meant writing and shipping an executable that runs
inside an elevated install, in exchange for layout control.

The constraint that comes with it: control and page names are contractual. WixStdBA finds
`EulaRichedit`, `EulaAcceptCheckbox`, `InstallButton` and the rest by name, and all eight pages must
exist even for a product that never reaches Modify. Renaming a control does not move it; it removes it.

See [Windows Agent](WINDOWS-AGENT.md) for what the agent may and may not do at runtime, and
[Packaging and release](PACKAGING-AND-RELEASE.md) for how the artifacts are built and verified.
