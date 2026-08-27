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

Two extensions are separate installs, pinned to the toolset's own version. The build script and CI
both provision them if missing:

```bash
wix extension add --global WixToolset.BootstrapperApplications.wixext/5.0.2
wix extension add --global WixToolset.Netfx.wixext/5.0.2
```

The Netfx extension is what provides `DotNetCoreSearch`, which the agent bundle uses to detect the
ASP.NET Core runtime.

## Building a release

```bash
pwsh ./scripts/build-release.ps1
```

The script restores, builds, tests, publishes the desktop self-contained and the agent
framework-dependent, builds both installers, produces the portable archive and writes the checksums.
`-SkipTests` shortens the loop while iterating on packaging; `-Version 1.2.0` stamps a version without
editing `Directory.Build.props`; `-Culture en-US` builds the English installer instead of the
Portuguese one.

It also verifies that the installer Terms still match the canonical Markdown, and refuses to build if
they have drifted. Regenerate with `pwsh ./scripts/build-terms-rtf.ps1` and commit the result.

It is orchestration, not an installer. Everything that decides what gets installed lives in the `.wxs`
authoring under `installer/`, where the Windows Installer engine owns it.

## The artifacts

```text
artifacts/
  NutManager-Setup-1.0.1.exe          desktop application, ~77 MB
  NutManager-Agent-Setup-1.0.1.exe    agent service, ~10 MB
  NutManager-win-x64.zip              portable desktop copy, ~87 MB
  SHA256SUMS.txt
```

`artifacts/` is not versioned.

The zip survives alongside the installers because it is a genuinely different thing: it needs no
administrator, installs nothing, registers nothing and leaves no trace beyond the folder it was
extracted into. That suits a technician working on a machine they do not own.

The desktop installer is large because the desktop publishes self-contained; the agent installer is
roughly a tenth of the size because it does not — see [The runtime decision](#the-runtime-decision).

### SHA256SUMS.txt

One line per artifact: the lowercase hash, two spaces, the filename. Hashes are computed after every
step that could still rewrite the files.

Verifying on Windows:

```powershell
Get-FileHash .\NutManager-Setup-1.0.1.exe -Algorithm SHA256
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

The two products deploy differently, on purpose.

| | Desktop | Agent |
| --- | --- | --- |
| Deployment | self-contained win-x64 | framework-dependent win-x64 |
| .NET prerequisite | none | Microsoft ASP.NET Core Runtime 10 x64 |
| Runtime servicing | needs a NutManager release | independent, by the administrator |
| Installer size | ~77 MB | ~10 MB |

**Desktop stays self-contained.** An operator should download one thing and run it. A runtime prompt on
a workstation buys nothing the payload has not already paid for, and the cost — a runtime security fix
requiring a NutManager release, because nothing else on the machine can patch a runtime the product
carries privately — is acceptable for an application someone launches, uses and closes.

**The Agent is framework-dependent.** That balance is different, and it changed. The agent is a
long-lived Windows service on a server, and a private runtime inside `C:\Program Files\NutManager Agent`
means that server stays unpatched until NutManager ships. Sharing the machine's runtime puts servicing
back where the administrator already manages it, and drops the download from roughly 70 MB to 10.

The cost moved rather than disappeared: the agent now has a real prerequisite, which is why its
installer detects the runtime and offers to install the official Microsoft package. What it must never
do is install an agent that cannot run — see [Agent](#agent-1) below.

`Publish-Product` in the build script takes `-SelfContained` as a **mandatory** parameter with no
default. That is deliberate: a default is exactly how the two products would drift back into being the
same, and a test asserts both call sites. The build also checks the published agent directory for
`hostpolicy.dll`, `coreclr.dll` and their neighbours, so "framework-dependent" is proven from the
payload rather than inferred from its size.

## The Agent's runtime prerequisite

**Detection.** `netfx:DotNetCoreSearch` with `RuntimeType="aspnet"`, `Platform="x64"`,
`MajorVersion="10"`. It asks the supported question — can this machine run the agent — rather than
looking for `dotnet.exe` on disk. The agent takes a `FrameworkReference` on `Microsoft.AspNetCore.App`
for HTTP.sys, so the base .NET runtime alone would not be enough.

**Roll-forward semantics.** Detection is compatibility-oriented: `AspNetCoreRuntimeVersion >= v10.0.0`.
Any serviced 10.x satisfies it, so a server already on 10.0.7 downloads nothing. Pinning detection to
one patch would reinstall a runtime that already works.

**The package, when one is needed.** Exactly one, pinned in `installer/Common/Product.wxi`:

```text
aspnetcore-runtime-10.0.11-win-x64.exe
https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.11/aspnetcore-runtime-10.0.11-win-x64.exe
SHA512  a03584dd…d66d319f
Size    11 262 944 bytes
```

Fixed at compile time, HTTPS, Microsoft's own build service. No user-supplied URL, no mirror list, no
redirect through anything this project controls, no address arriving from an API response. Burn
verifies the hash and size before running it with elevation, so a substituted or corrupted download
fails the install instead of executing.

The normal ASP.NET Core Runtime, **not** the Hosting Bundle: the agent does not use IIS, and installing
an IIS hosting bundle to obtain a runtime would change the server's configuration well beyond what was
asked for.

**Ownership.** `Permanent="yes"`. The runtime is a machine-shared Microsoft component and removing
NutManager Agent must not take it from whatever else on the server depends on it.

**Behaviour:**

| Situation | What happens |
| --- | --- |
| Compatible runtime present | Screen shows *Instalado*. Nothing is downloaded, nothing is offered. Works offline. |
| Runtime missing, interactive | Screen shows *Necessário* with the install option ticked by default. |
| Runtime missing, operator unticks | **Install Agent is disabled**, with a message saying why. |
| Runtime missing, `/quiet` | The official package is installed by default. |
| Runtime missing, `/quiet InstallAspNetRuntime=0` | Fails **before** the service is registered. |
| Download or install fails | Bundle fails, named as a download failure. No retry, no fallback mirror, no partial agent. |

The refusal is the point. A `NutManagerAgent` that registers and then cannot start is worse than one
that refused, because it leaves something broken behind on a server. The disabled button covers the
interactive path; `/quiet` has no buttons, so the chain repeats the precondition as an
`InstallCondition` on the MSI itself.

## Terms of Use in the installer

Both installers show the **NutManager Terms of Use** and require acceptance before Install enables.

The text is **embedded in the bundle as RTF**, never fetched. An operator on an isolated server has to
be able to read what they are accepting, and a link to a published page would mean accepting a document
they cannot open.

```text
docs/TERMS-OF-USE.md            canonical, pt-BR
docs/TERMS-OF-USE.en-US.md      translation
  |  scripts/build-terms-rtf.ps1   deterministic, no external converter
  v
installer/Common/Terms/Terms.pt-BR.rtf   committed
installer/Common/Terms/Terms.en-US.rtf   committed
```

The RTF is committed rather than generated at build time, so an offline build ships the reviewed text.
`build-release.ps1` runs the generator in `-Check` mode and refuses to build if the two have drifted.

**The Terms are not the licence.** The source code remains under **GNU GPL v2.0**, and the Terms say in
section 3 that they do not replace, restrict or modify GPL rights. The installer states the two
separately: the acceptance checkbox names only the Terms, and a line beneath it names the GPL. One
checkbox covering both would imply the GPL is a condition of installing, which inverts what the GPL is.

> **v1.0.1 — first public release.** No automatic update check is included. External resources
> continue to open only after explicit user action, so the current canonical Terms remain accurate
> and do not require regeneration.

## Installer appearance

A custom WixStdBA theme, not a custom bootstrapper application. Everything the branded screen needs —
artwork, a product description, an offline Terms pane, an acceptance checkbox that gates Install —
already exists in WixStdBA. A managed BA would add an executable to the elevated install path in
exchange for layout control.

```text
installer/Common/Theme/DesktopTheme.xml      layout
installer/Common/Theme/AgentTheme.xml        layout, plus the dependency block
installer/Common/Theme/{Desktop,Agent}.{pt-BR,en-US}.wxl    every visible string
```

All visible text lives in the `.wxl` files, which is what makes parity checkable: a test asserts the
two cultures declare exactly the same set of string Ids. Technical identifiers are not translated —
`NutManager`, `NutManagerAgent`, `Microsoft ASP.NET Core Runtime 10 x64`, paths, versions,
`GNU GPL v2.0`.

A bundle's strings are baked in at build time, so one build produces one language. `-Culture` selects;
both are authored and either can be produced from the same tree.

The artwork is `NutManager.png`, the high-resolution product image. The `.ico` keeps the jobs an icon is
for — the bundle executable and the Add/Remove Programs entry — because scaling a 256px icon frame up
to fill the header is what made the previous installers look pixellated.

The desktop and agent screens are deliberately distinguishable: different taglines, different product
kind (*Aplicativo de administração* against *Componente de servidor*), different primary button
(*Instalar* against *Instalar Agent*). Nobody should install the server service believing it is the
graphical application.

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
NutManager-Setup-1.0.1.exe /quiet
NutManager-Setup-1.0.1.exe /passive
NutManager-Setup-1.0.1.exe /uninstall /quiet
NutManager-Setup-1.0.1.exe /log <path>
```

The agent installer takes the same switches, plus one Burn variable of its own:

```text
NutManager-Agent-Setup-1.0.1.exe /quiet InstallAspNetRuntime=0
```

`InstallAspNetRuntime` defaults to `1`, so an unattended install on a bare server installs the official
Microsoft runtime without an extra switch. Setting it to `0` declines that deliberately; if the runtime
is then absent, the install fails before the service is registered rather than leaving a
`NutManagerAgent` that cannot start.

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
