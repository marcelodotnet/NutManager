# Packaging and release

How the distributable artifacts are built, what they are, and what has and has not been verified about
them.

For why WiX rather than something else, see [Installer architecture](INSTALLER-ARCHITECTURE.md).

## Prerequisites

```bash
dotnet tool install --global wix --version 5.0.2
```

The version is pinned. WiX v6 and v7 refuse to build without accepting the Open Source Maintenance Fee
EULA, which is a licensing decision rather than a build setting.

The bundle extension is a separate install, and the build script provisions it if missing:

```bash
wix extension add --global WixToolset.BootstrapperApplications.wixext/5.0.2
```

## Building a release

```bash
pwsh ./scripts/build-release.ps1
```

The script restores, builds, tests, publishes both products self-contained, builds both installers,
produces the portable archive and writes the checksums. `-SkipTests` shortens the loop while iterating
on packaging; `-Version 1.2.0` stamps a version without editing `Directory.Build.props`.

It is orchestration, not an installer. Everything that decides what gets installed lives in the `.wxs`
authoring under `installer/`, where the Windows Installer engine owns it.

## The artifacts

```text
artifacts/
  NutManager-Setup-1.0.0.exe          desktop application, ~72 MB
  NutManager-Agent-Setup-1.0.0.exe    agent service, ~45 MB
  NutManager-win-x64.zip              portable desktop copy, ~85 MB
  SHA256SUMS.txt
```

`artifacts/` is not versioned.

The zip survives alongside the installers because it is a genuinely different thing: it needs no
administrator, installs nothing, registers nothing and leaves no trace beyond the folder it was
extracted into. That suits a technician working on a machine they do not own.

Both installers are large because both products publish self-contained — see
[The runtime decision](#the-runtime-decision).

### SHA256SUMS.txt

One line per artifact: the lowercase hash, two spaces, the filename. Hashes are computed after every
step that could still rewrite the files.

Verifying on Windows:

```powershell
Get-FileHash .\NutManager-Setup-1.0.0.exe -Algorithm SHA256
```

Compare the `Hash` value against the line for that filename, case-insensitively.

## Versioning

One source: `NutManagerVersion` in `Directory.Build.props`. Every assembly, both installers and both
artifact names read from it.

An installer whose `ProductVersion` disagrees with the assembly it ships is an upgrade that either
refuses to run or silently does nothing, and from outside those look identical. A test asserts that
neither package carries a literal version.

Windows Installer compares the first three fields of a version and ignores the fourth, so a release
must differ in one of the first three to be recognised as an upgrade.

## The runtime decision

Both products publish **self-contained win-x64**.

The desktop application because an operator should install one thing rather than a thing and a runtime.
The agent because a UPS server previously had to have the ASP.NET Core Runtime 10 installed and kept
current by hand, which is a prerequisite that fails quietly and at the worst moment.

The cost is real and is not hidden here: **a runtime security fix now requires a NutManager release.**
Nothing else on the machine can patch a runtime the product carries privately. The trade was taken
knowingly, and it is softened by the agent's default deployment listening on a named pipe with no TCP
port at all — which is most of the exposure that independent runtime servicing would protect.

If that balance changes — if HTTPS becomes the common case rather than the exception — the decision is
worth revisiting.

## Code signing

**Artifacts are unsigned.** No code-signing certificate is available to this repository, so SmartScreen
warns on first run of either installer. This is stated rather than worked around.

The build script has an explicit signing stage that reports itself as skipped. When a certificate
exists, sign there, in this order: the MSI inside each bundle first, then the bundle. Signing a bundle
whose payload changes afterwards invalidates the signature.

Signing material must come from secret storage. No `.pfx`, private key or certificate password belongs
in this repository, and a test asserts the installer sources carry none.

## Unattended installation

Burn bundles accept:

```text
NutManager-Setup-1.0.0.exe /quiet
NutManager-Setup-1.0.0.exe /passive
NutManager-Setup-1.0.0.exe /uninstall /quiet
NutManager-Setup-1.0.0.exe /log <path>
```

The agent installer takes the same switches.

Exit codes are Windows Installer's: `0` success, `1602` user cancelled, `1603` fatal error, `3010`
success with a restart pending. A deployment tool should treat `3010` as success.

**Not verified.** These are the documented switches of the technology, not observed behaviour — no
unattended installation has been run. See [What has not been verified](#what-has-not-been-verified).

## What each installer does to the machine

### Desktop

Installs to `C:\Program Files\NutManager`, adds a Start Menu shortcut, and registers in Add/Remove
Programs. No service, no scheduled task, no firewall rule.

Upgrade removes the previous version and installs the new one. Repair restores product files and the
shortcut.

Uninstall removes the program files, the shortcut and the registration. It does not touch
`settings.json`, `managed-servers.json` or any Windows Credential Manager entry — not by policy but by
construction: those live outside every component the package declares, and a component is what
uninstall removes.

There is no option to remove user data. Offering one would mean the installer reaching into a
credential store shared with other products, and the semantics of doing that safely could not be
demonstrated. Removing profiles and credentials is done from the application.

### Agent

Installs to `C:\Program Files\NutManager Agent` and registers the `NutManagerAgent` service as
LocalSystem with automatic start. Registers the `NutManager Agent` Event Log source. Creates
`%ProgramData%\NutManager\Agent` and then leaves it alone.

Upgrade stops `NutManagerAgent`, replaces the binaries and starts it again. The NUT service is never
named in the authoring and is never touched. Repair restores the binaries, the service registration and
the Event Log source.

Uninstall stops and removes `NutManagerAgent`, removes the program files and the Event Log source, and
leaves everything else: `agent.json`, certificates, SSL bindings, URL reservations, firewall rules, the
`NutManager Operators` group, Windows credentials, and every part of NUT.

`agent.json` survives because the package never declares it. That is a mechanism rather than a promise
— a file the manifest does not contain is one the engine has no way to schedule for removal.

### What neither installer does

Creates the `NutManager Operators` group. The agent authorizes by membership of it, and an installer
that created it would be deciding who may control a service. On a domain controller it would be
changing the directory as a side effect of running setup. The administrator creates the group; the
agent refuses to authorize anyone until it exists.

Configures HTTPS. No certificate, no SSL binding, no URL reservation, no firewall rule. HTTPS stays an
explicit administrative decision — see [Windows Agent](WINDOWS-AGENT.md).

Touches NUT in any way.

## Compatibility matrix

States mean what they say:

- **Validated** — exercised on that configuration and observed to work.
- **Expected** — nothing suggests it would not work, and nobody has run it.
- **Not validated** — untested, and there is a specific reason to be careful.
- **Unsupported** — deliberately outside scope.

| Configuration | Desktop | Agent |
| --- | --- | --- |
| Windows 11 x64 | Expected | Expected |
| Windows 10 x64 | Expected | Expected |
| Windows Server 2019 | Expected | Expected |
| Windows Server 2022 | Expected | Expected |
| Windows Server 2025 | Expected | Expected |
| Domain member server | Expected | Expected |
| Domain controller | Expected | **Not validated** — see below |
| Workstation, no domain | Expected | Expected |
| Agent over named pipe | n/a | Expected |
| Agent over HTTPS | n/a | Expected, after manual setup |
| Desktop without agent | Expected | n/a |
| Agent without desktop | n/a | Expected |
| Windows on ARM64 | Unsupported | Unsupported |
| Linux, macOS | Unsupported | Unsupported |

Nothing is marked Validated. **No installer has been run on any machine.**

The domain controller row is called out separately because it is where the group question changes
character. On a member server or workstation `NutManager Operators` is a local group. A domain
controller has no local groups, so it must be a domain group, and creating one is a directory change.
The installer creates neither, which is what keeps the two cases behaving the same.

## What has not been verified

Recorded plainly, because an installer that has never been installed is not a tested installer.

**No installation has been performed.** The installers build, their authoring is asserted by fifteen
tests, and their contents were inspected. Installing them requires elevation and modifies the machine
they run on, which is not something to do to a workstation without being asked.

Unverified as a result: the install experience, Start Menu registration, Add/Remove Programs appearance,
upgrade from a previous version, repair, uninstall, the preservation of user data across uninstall in
practice, service registration and startup, Event Log source registration, the unattended switches and
exit codes, and every row of the compatibility matrix.

**No agent acceptance has been run on a server.** The GANDALF acceptance — install, verify the service
and the named pipe, confirm NUT was untouched, upgrade, confirm `agent.json` survived, repair,
uninstall, confirm only the agent was removed — has not been executed.

These are the gap between "builds correctly" and "installs correctly", and closing it is manual work on
a real machine.

## Releasing

No release has been published, no tag created and no GitHub Release made. The infrastructure exists and
is untriggered.

When releasing: set `NutManagerVersion`, build, verify checksums, sign if a certificate exists, and
publish the two installers, the zip and `SHA256SUMS.txt` together. The checksum file is only useful
alongside what it describes.
