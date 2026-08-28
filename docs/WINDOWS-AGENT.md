# NutManager Windows agent

The agent is a Windows service that runs on the machine hosting NUT and controls that machine's NUT
service on behalf of authorized NutManager operators.

It exists because of what T34 established: a remote SCM call is authenticated across machines, and a
client that is not recognised by the server is refused before it can ask anything. The agent moves
the SCM call to the machine that owns the service, so the cross-machine authentication that failed
there is no longer on the path to controlling a service.

## What it does

- reports the state, process id and identity of the NUT service on its own machine;
- starts, stops and restarts that one service;
- reports the serial devices Windows already knows about on its own machine, read-only;
- records every control operation in the Windows Event Log.

## What it does not do

- it does not read or write NUT configuration files;
- it does not accept a service name, a path or a command from a caller;
- it does not open a serial port, transmit a byte or run a NUT driver;
- it does not restart the NUT service after a configuration change;
- it does not create its own operators group or its own Event Log source;
- it does not terminate processes.

The service it controls is the one it validated for itself at startup: a Windows service whose binary
lives inside the detected NUT installation. If two services qualify, or none does, the agent reports
that it has no authority and refuses every control operation.

## Requirements

- Windows x64, with NUT installed and registered as a Windows service.
- **Microsoft .NET Runtime 10 x64** and **Microsoft ASP.NET Core Runtime 10 x64**. The Agent is
  framework-dependent so the server's shared runtimes receive normal Microsoft servicing. The Agent
  installer detects each framework independently and installs the pinned official Microsoft package
  when a compatible 10.x runtime is missing. It does not install the Hosting Bundle and does not use
  IIS.
- `NutManager Agent Config`, the elevated local administration utility, is included in the Agent
  installation. It is framework-dependent too and uses `Microsoft.NETCore.App` already supplied by
  the same prerequisite chain; it does not introduce `Microsoft.WindowsDesktop.App`.
- Administrative rights on the server for the installation steps below. The agent itself never
  performs any of them.

Check what is present:

```powershell
dotnet --list-runtimes
```

Both `Microsoft.NETCore.App 10.x` and `Microsoft.AspNetCore.App 10.x` must appear.

## Authentication, and the one thing to check first

The agent's default transport is a named pipe. A named pipe reached from another machine is carried
by SMB and authenticated by Windows, which means the client needs an identity the server recognises —
the same requirement that stopped T34's remote SCM query.

If NutManager runs on a machine that is not joined to the server's domain, and under a local account,
the server has no identity to recognise and the connection is refused. The client reports that as
**access denied**, with the numeric Windows code, and never as a NUT outage.

Two ways to satisfy it:

- run NutManager under an account the server recognises (a domain account, or an account that exists
  on the server with the same name and password); or
- establish a session to the server first, with credentials it recognises. This is an administrative
  step performed by an operator at a prompt, and it is not something NutManager does: the product
  never launches `net`, `sc`, `cmd` or PowerShell on anyone's behalf.

```bash
net use \\GANDALF /user:SBRA\operator
```

The agent does not accept a password over the wire, and it does not fall back to any weaker
authentication if Windows refuses the caller.

On the named pipe, impersonation exists only while the server reads the caller identity and checks
membership of `NutManager Operators`. Any failure in that step is refused before dispatch. The
listener then explicitly restores the Agent process identity before the shared dispatcher runs, so
status, target revalidation and Start/Stop/Restart execute as the LocalSystem service rather than
with the caller's independent SCM rights. The caller name still travels separately for audit.

## Publishing

From the repository root:

```bash
dotnet publish src/NutManager.Agent/NutManager.Agent.csproj --configuration Release --runtime win-x64 --self-contained false --output publish/agent
```

`publish/` is not versioned.

Copy the contents of `publish/agent` to the server, for example to
`C:\Program Files\NutManager Agent`.

The published payload is about 7 MB, because the agent references `NutManager.Infrastructure` as a
whole and takes everything that project carries.

The WMI stack is genuinely used: since T38 the passive serial inspection reads `Win32_SerialPort` and
`Win32_PnPEntity` to enrich the ports it enumerates. The SSH stack is not. No code path in the agent
reaches it, and it is carried only because of that whole-project reference.

Narrowing the payload is a packaging change worth making, and it is recorded as a known limitation
rather than solved by moving platform-specific code into `NutManager.Core`, which is not allowed to
hold it.

## Installation

The normal path is `NutManager-Agent-Setup-x.y.z.exe`. It installs the service and
`NutManager Agent Config`, registers the Event Log source, and registers `NutManagerAgent` as
LocalSystem with Automatic start type. A fresh installation deliberately leaves the service stopped:
the installer neither creates the authorization group nor guesses which transports and identities an
administrator intends to enable.

Open **NutManager Agent Config** from the Start menu after installation. It is the authoritative live
diagnostic and administration surface for the local Agent installation.

### 1. Create the operators group

The card identifies whether `NutManager Operators` exists and offers **Criar grupo** only after an
explicit click. Add authorized accounts with **Adicionar usuário**. Membership is SID-backed; a user
or a nested group is accepted, while an unresolved or non-addable identity is refused.

On a workstation or member server this is a local group. A domain controller has no independent local
SAM, so creation changes the directory and Agent Config requires a separate confirmation. An unknown
machine role is treated with the same caution. The MSI never creates the group.

### 2. Select transports

`SMB (Named Pipe)` and `HTTPS` are independent choices. Named Pipe only, HTTPS only, and both enabled
are valid. Both disabled is invalid; Agent Config blocks disabling the final transport and the service
checks the same shared rule at startup. A legacy `agent.json` without `namedPipeEnabled` keeps Named
Pipe enabled.

### 3. Apply and start explicitly

**Aplicar** configures any explicitly requested HTTPS resources first and commits `agent.json` only
after those operations succeed. It never starts a stopped Agent. If the Agent is running, the utility
offers an explicit restart after saving instead of restarting silently. Use **Iniciar Agent** only
after the group, transports and diagnostics are correct. Nothing in this workflow starts or restarts
NUT.

### Manual deployment from published files

The commands below document the equivalent low-level registration path for a deployment that does not
use the installer. Run them from an **elevated** prompt on the server. The supported installer workflow
above is preferred because it also provisions both runtime prerequisites and Agent Config.

#### Create the operators group

Membership of this local group is the only thing that authorizes control. If the group does not
exist, the agent refuses to start — it never falls back to Administrators.

```bash
net localgroup "NutManager Operators" /add /comment:"May control the NUT service through the NutManager agent."
```

Add the accounts that may control the service. A domain group may be added instead of individual
accounts; membership held through it is recognised.

```bash
net localgroup "NutManager Operators" "SBRA\operator" /add
```

#### On a domain controller

The same command creates the group, but a domain controller has no separate SAM: the group it creates
is held in the directory and appears as `DOMAIN\NutManager Operators` rather than
`SERVER\NutManager Operators`. That is normal and needs no different installation step.

The agent resolves the group against whatever the server itself treats as its local group database —
the SAM on a workstation or member server, the directory on a domain controller — so the same binary
is correct in both roles. It then pins the group's SID and authorizes by SID from that point on.

The distinction still matters for authority. On a member server the local group wins over a domain
group that happens to share the name: existence is proven against the local group database first, and
the translation starts at the local system. A domain account is authorized by being a member of the
group, directly or through a nested group — never by being a domain account.

#### Register the Event Log source

Control is refused whenever the audit sink is unusable, so this step is not optional. It is performed
by an administrator once, and never by the agent.

```powershell
New-EventLog -LogName Application -Source "NutManager Agent"
```

#### Create the service

The agent must run as LocalSystem. It verifies this at startup and refuses to run as any other
account.

```bash
sc.exe create NutManagerAgent binPath= "\"C:\Program Files\NutManager Agent\NutManager.Agent.exe\"" obj= LocalSystem start= auto DisplayName= "NutManager Agent"
```

```bash
sc.exe description NutManagerAgent "Controls the local Network UPS Tools service for authorized NutManager operators."
```

The spaces after `binPath=`, `obj=`, `start=` and `DisplayName=` are required by `sc.exe`.

#### Start it

```bash
sc.exe start NutManagerAgent
```

## Verifying the installation

```bash
sc.exe query NutManagerAgent
```

The service should report `RUNNING`. Then, from NutManager on the client machine, the agent should
answer a handshake and report the NUT service's state and process id.

If the service starts and immediately stops, the reason is in the Application event log under the
source `NutManager Agent`. The startup checks that can stop it are, in order: the account is not
LocalSystem, and the operators group could not be resolved.

## Using it from NutManager

The agent panel lives on the Administration page of a **remote** profile. It reports four things that
are deliberately kept apart:

- **Agent** — whether the agent answered: connected, unavailable, access denied, host unreachable, no
  answer, incompatible, failed.
- **Transport** — named pipe or HTTPS, as the profile selects.
- **Service** — the NUT service's identity, state, process and pid, as the agent reports them.
- NUT's own protocol health, which is shown elsewhere in the shell and is never touched by any of the
  above.

If the local SCM query itself fails, the status payload preserves a fixed failure category, the
numeric Win32 error when Windows supplied one, and the safe exception type. It never sends the
localized exception message. This distinguishes a missing service or access denial from an ordinary
`Unknown` state without turning the UI into a stack trace.

An agent that cannot be reached on a server whose upsd is answering normally is an administrative
gap. NutManager says so, and does not fall back to any other route: there is no second path to the
service control manager behind the agent.

Start, Stop and Restart appear only when the agent advertises them. If the operators group or the
event source is missing, the agent reports control as unavailable with the reason, and no button is
offered. Stop and Restart ask for confirmation first, naming the host and the service; Restart is a
single request to the agent, which holds both phases under one lock.

### Serial device inspection (T38)

The Devices and drivers section of a remote profile now lists the serial devices the **server**
exposes, read through the agent. It is the same screen a local profile uses; only the source
differs, and the section says which machine was examined so a remote reading is never mistaken for a
local one.

What the agent does for this is enumerate. It reads the port names Windows publishes in the
`SERIALCOMM` device map, and that map alone decides which ports exist — a port disabled in Device
Manager leaves `SERIALCOMM` while remaining a perfectly findable PnP entity, and listing it would
offer an operator a port they cannot use. WMI only fills in blanks on a port that is already there:
`Win32_SerialPort` first, then `Win32_PnPEntity` for whatever is still missing. The second class is
not a nicety — `Win32_SerialPort` commonly returns nothing at all for USB-to-serial adapters, and
without the fallback a working port is listed with no name, no manufacturer, no identifier and no
fault code. The PnP entity is matched to its port by the `(COM3)` suffix Windows appends to the
display name, and a row that names no port enriches nothing rather than being attached to a guess.

The fault code is read as the number Windows stores. `CM_PROB_NONE` is zero and is a real answer —
"no fault reported" — never a missing value, and nothing parses a description that would be localized
on the machine being inspected.

It is the same passive source the local screen has always used, not a second implementation. **No
port is opened, no byte is transmitted, no driver is run, no device setting is changed and no
registry value is written** — a NUT driver already talking to a UPS on the configured port is
unaffected, because nothing in this path touches it. Nothing shells out either: there is no
`Get-PnpDevice`, no PowerShell and no `cmd`, only the WMI and registry reads the application already
performed.

The request carries no port, no speed, no command and no path. There is no field through which a
caller could redirect it, which is the same property the control operations have and for the same
reason.

Each port is shown with a status, and the four states mean different things:

| State | Meaning |
| --- | --- |
| Green | Present, and Windows reported a fault code of zero. |
| Amber | Present, and Windows reported a fault code or a status other than OK. |
| Grey | Present, and nothing further is known — `SERIALCOMM` lists it and WMI has no record. Not a fault. |
| Red | Named but not currently exposed by the operating system. |

A second line names the controller, the USB vendor and product identifiers and the bus, and only
where those are actually established. The identifiers are read out of the PnP device id; the
controller comes from a small fixed table of verified vendor/product pairs. An unrecognised pair
produces no controller name rather than the nearest guess, and a Prolific adapter is reported at
family level as `PL2303` because this build has no verified mapping for the G-series suffixes. The
driver's own friendly name is shown in full on the line above, so nothing is hidden by that
restraint. There is no internet lookup of any kind.

The configured `port` from the server's `ups.conf` is related to what was detected and reported as
**detected on the server** or **not detected on the server**. That comparison is informational. The
agent does not read `ups.conf` — the document is loaded through the profile's own configuration
transport, SFTP or SMB, and only when that session is connected. Nothing here writes configuration;
the graphical editor and the safe-write pipeline remain the only route to a change.

An agent that cannot be reached, or one older than this capability, is reported as such. It is never
presented as a server with no serial ports: "there are no ports" and "nobody could look" are
different findings and the screen keeps them apart.

Active driver diagnostics — `upsdrvctl`, driver help, version, variable listing and data capture —
stay local. Every one of them opens the configured device through a NUT driver, which is exactly
what remote inspection is built not to do, and the remote screen says so instead of offering buttons
that would have to be refused.

The reading is refreshed by the Refresh button on that section. There is no new poll and no timer:
serial hardware does not change between two ticks of a monitor, and a read an operator can repeat
deliberately does not need one. Consistent with that, the read is not written to the Event Log —
the audit exists to preserve control records, and burying them under enumeration noise would defeat
it. The audit policy for Start, Stop and Restart is unchanged.

### Older agents

The capability is negotiated in the handshake, not inferred from a version number. An agent built
before T38 simply does not list the operation among its capabilities, and NutManager reads that and
reports device inspection as unavailable for that server rather than sending a request the agent
would refuse. The protocol version is unchanged at 1 for exactly this reason: raising it would make
an older agent reject every request from a current client, including the handshake that carries the
answer. In the other direction, a current agent answers an older client unchanged — the hardware
payload is an optional field an older build ignores.

The agent must be redeployed on the server for the capability to appear.

### Transport selection

The profile stores the agent transport, and the named pipe is the default for new and existing
profiles. HTTPS is selected per profile and requires the server-side setup below.

Since T36 the profile editor owns these options. The transport, the HTTPS endpoint, the
authentication mode and the account name are set in the profile form; the profile document remains
their storage, and the application reads, validates and migrates it exactly as before.

The agent credential has its own lifecycle there, and it is deliberately not the SMB or SFTP one. An
alternate Windows account is captured, saved to the Windows Credential Manager under the agent's own
target, and removed from it, without touching the configuration-transport secret beside it. A profile
may perfectly well read configuration over SMB as one account and control the service as another.

There is no fallback in either direction. A profile that selects HTTPS never quietly uses the named
pipe when the endpoint is wrong, and a profile on the named pipe never tries HTTPS: an operator who
cannot tell which transport answered cannot diagnose either.

## The optional HTTPS transport

HTTPS exists for the case the named pipe cannot serve. A pipe reached from another machine rides SMB
and needs a Windows session the client may not have; Negotiate over HTTPS can be given an explicit
credential instead, so a client outside the server's domain can authenticate without anyone
establishing a session first.

It is **disabled by default**. Installing the agent opens no TCP port. Everything below is a
deliberate act by an administrator.

### Configure with NutManager Agent Config

Enable **HTTPS** and enter the explicit host/FQDN and port. The certificate summary is read-only;
**Import...** is the explicit file-selection action and accepts `.pfx`, `.p12`, `.cer` and `.crt`
files into `LocalMachine\My`. A PFX/P12 password is used only for that import attempt and is never
persisted. The utility shows subject, issuer, expiration and thumbprint and refuses a certificate
that lacks a private key, is outside its validity window, lacks server-authentication usage, or does
not cover the host in its SAN (with Common Name used only for a legacy certificate that has no SAN).
Wildcard matching is limited to one DNS label. A CER/CRT without a private key remains inspectable
but cannot be used by the HTTPS listener.

Applying HTTPS performs only the requested local administrative actions through documented Windows
APIs: HTTP Server API for the SSL binding and URL reservation, and Windows Firewall APIs for the
inbound rule. It does not run `netsh`, PowerShell, `cmd` or `sc.exe`.

Every created resource carries a NutManager ownership marker. Cleanup is offered only after explicit
confirmation and removes only resources whose ownership can be proven. A foreign or ambiguous
binding, reservation or firewall rule is left untouched and reported. **Reset HTTPS** clears the
NutManager-owned HTTPS resources and the saved HTTPS selection, but never removes a certificate.
Certificates are never exported automatically, and private-key material is never displayed.

The resulting `%ProgramData%\NutManager\Agent\agent.json` contains no secret:

```json
{
  "namedPipeEnabled": true,
  "httpsEnabled": true,
  "httpsPrefix": "https://nut-server.example.local:5199/",
  "certificateThumbprint": "0123456789ABCDEF0123456789ABCDEF01234567"
}
```

The write validates, writes and flushes a temporary file beside the target, applies an ACL limited to
SYSTEM and Administrators, reads and validates it again, and atomically replaces the previous file.
If system-resource configuration fails, the file is not committed. Applying never starts a stopped
Agent; a running Agent receives an explicit restart offer.

### Authentication over HTTPS

The transport is hosted by ASP.NET Core on the HTTP.sys server, configured through `UseHttpSys`. TLS
stays with HTTP.sys and the certificate an administrator bound to the port: nothing inside the agent
loads a certificate or terminates TLS.

The listener requires **Negotiate** and does not accept anonymous requests — HTTP.sys authenticates
before the request reaches the agent, so an unauthenticated caller never gets as far as the code that
could refuse it. Membership of `NutManager Operators` is then required, exactly as on the named pipe.
There is no bearer token, no Basic authentication and no password anywhere in the agent protocol.

In NutManager, an HTTPS profile chooses between:

- **Current Windows identity** — the account NutManager runs as. Nothing is stored.
- **Alternate Windows account** — a different account, supplied to Negotiate as an explicit
  credential. Its password is kept in the Windows Credential Manager under the agent's own target,
  separate from the SMB and SSH secrets: those authorize reading configuration files, this authorizes
  controlling a service, and one stored secret must not silently grant both.

### Troubleshooting HTTPS

**The service starts but HTTPS does not.** Application event log, source `NutManager Agent`, event
1001. It names which precondition failed: a missing or plain-text prefix, a wildcard host, a
thumbprint that is not hexadecimal, a certificate that is absent or has no private key, or a bind
that failed because no SSL certificate is attached to the port.

**NutManager reports an incompatible agent after enabling HTTPS.** That state also covers TLS
failures — an untrusted certificate or one whose name does not match the host. Certificate validation
is the platform default and is never bypassed, so fix the certificate rather than the client.

**NutManager reports access denied over HTTPS.** Negotiate failed, or the account is not a member of
`NutManager Operators` on the server. A 401 and a 403 both arrive here.

**Rolling HTTPS back.** Disable HTTPS to stop using the transport while choosing whether to retain or
remove its proven NutManager-owned resources. Use **Reset HTTPS** to clear the saved HTTPS selection
and remove every proven NutManager-owned firewall, SSL-binding and URL-reservation resource. Foreign
and unknown resources remain untouched, and the certificate is never part of either cleanup. Ensure
Named Pipe is enabled first if HTTPS is the last active transport.

## Event log

All entries are written to the Application log under the source `NutManager Agent`. The event ids are
part of the agent's contract and are stable:

| Id | Meaning |
|----|---------|
| 1001 | A security precondition failed at startup |
| 1002 | A caller was refused for not belonging to the operators group |
| 1003 | The NUT service stopped matching what was validated at startup |
| 1010 | A control operation was requested |
| 1011 | A control operation succeeded |
| 1012 | A control operation failed |

Each entry records the caller, the transport, the service, the state before and after, the result and
the operation id. No entry can contain a credential: the audit record has no field one could travel
in.

## Upgrading

```bash
sc.exe stop NutManagerAgent
```

Replace the files, then:

```bash
sc.exe start NutManagerAgent
```

The operators group and the Event Log source survive an upgrade and do not need to be recreated.

## Uninstalling

```bash
sc.exe stop NutManagerAgent
```

```bash
sc.exe delete NutManagerAgent
```

Remove the files. The group and the event source are left alone unless you remove them deliberately,
because both may predate this installation:

```powershell
Remove-EventLog -Source "NutManager Agent"
```

```bash
net localgroup "NutManager Operators" /delete
```

## Troubleshooting

**The service will not start.** Read the Application log for source `NutManager Agent`, event 1001.
The two causes it reports are an account that is not LocalSystem and an operators group that could
not be resolved.

**NutManager reports that no agent is available.** Nothing accepted a connection: the service is not
running on that host, or it is not installed there. This says nothing about NUT — a server whose upsd
is answering normally can be a server with no agent on it.

**NutManager reports access denied.** Windows refused the caller. Either the account is not a member
of `NutManager Operators` on the server, or the server does not recognise the client's identity at
all — see the authentication section above.

**The agent reports that it has no NUT service.** No Windows service on the server runs a binary
inside the detected NUT installation, or more than one does. The agent refuses to guess: a wrong
guess here would attach service control rights to the wrong service.

**Control is unavailable although the agent is running.** The handshake reports why. The causes are a
missing operators group, an unusable Event Log source, and an unresolved NUT service.
