# Terms of Use — NutManager

<!--
  TRANSLATION. The canonical document is docs/TERMS-OF-USE.md, in pt-BR. This file follows it
  section for section and adds nothing: no broader waiver, no stronger warranty, no restriction the
  Portuguese does not carry. Where the two could be read differently, the pt-BR text governs.

  The RTF shown by the en-US installer is generated from this file by scripts/build-terms-rtf.ps1.

  PENDING FOR v1.0.1: T41 will add an informational GitHub release check. That makes section 10's
  sentence about external links being reached only on explicit user action untrue as written. These
  Terms must be re-synchronised after T41 and before v1.0.1 is tagged. Until then this is the
  current version, not the final one.
-->

**Last updated:** 26 August 2026

## 1. About NutManager

**NutManager** is software developed by **Marcelo Pacheco (@marcelodotnet)** to make it easier to monitor, configure, diagnose and administer **Network UPS Tools (NUT)** installations on Windows systems.

NutManager is an independent tool. It is not part of, affiliated with, or an official representative of the Network UPS Tools project.

## 2. Acceptance

By installing, running or using NutManager, the user acknowledges the characteristics, limitations and risks inherent in administering power systems, UPS units, Windows services and NUT configuration files.

A user who does not agree with these conditions may simply stop using the software.

## 3. Free software and licence

NutManager's source code is made available under the **GNU General Public License version 2.0 — GPL v2.0**.

These Terms of Use **do not replace, restrict or modify the rights granted by GPL v2.0** in respect of:

- use;
- study;
- copying;
- modification;
- redistribution;
- distribution of modified versions.

Where these Terms conflict with the rights granted by GPL v2.0 over the licensed code, the applicable GPL licence terms prevail.

## 4. Purpose

NutManager may be used, among other things, to:

- monitor NUT servers and UPS units;
- inspect state, load, battery and other information published by NUT;
- manage server profiles;
- edit NUT configuration through a graphical interface;
- generate previews before changes are applied;
- back up, validate, safely replace and roll back configuration;
- inspect and administer the Windows service associated with NUT;
- list available serial ports;
- use NutManager Agent for authorised remote administration;
- run diagnostics related to the NUT environment.

The features actually available may vary with version, environment, permissions, hardware, operating system and NUT configuration.

## 5. Administrator responsibility

NutManager is an administrative tool. Some operations can directly affect the availability of the power monitoring system.

The user is responsible for:

- understanding changes before applying them;
- reviewing the preview the software presents;
- keeping adequate backups;
- ensuring they are authorised to administer the computers involved;
- keeping credentials, certificates and access keys protected;
- confirming that configuration is compatible with their hardware and their version of NUT;
- testing changes in a suitable environment when necessary;
- assessing the impact before stopping or restarting services.

**Interrupting the NUT service can interrupt UPS monitoring and the automatic shutdown mechanisms associated with power failures.**

## 6. Configuration changes

NutManager uses mechanisms intended to reduce the risk of file corruption, including:

- validation;
- a preview before writing;
- backup;
- temporary write;
- safe replacement;
- verification afterwards;
- rollback where applicable.

These mechanisms reduce risk, but **do not guarantee that every change will produce the intended behaviour in NUT or in the connected hardware**.

The user remains responsible for the content and the consequences of the configuration applied.

NutManager deliberately **does not automatically restart NUT after a configuration change**. Where that is needed, the administrator must perform it separately.

## 7. Service control

NutManager may allow operations such as:

- start;
- stop;
- restart;

on the Windows service related to NUT.

These operations can cause temporary or permanent loss of monitoring.

The user should assess the impact before performing them.

## 8. NutManager Agent

**NutManager Agent** is an optional component intended for administering remote Windows servers.

The Agent:

- operates under the permissions configured on the server;
- uses Windows authentication mechanisms;
- restricts administrative operations to authorised users;
- does not act as a general-purpose remote execution mechanism;
- must not be treated as a substitute for the organisation's security policies.

It is the administrator's responsibility to correctly configure:

- the `NutManager Operators` group;
- permissions;
- certificates;
- HTTPS, where used;
- the firewall;
- Windows policies;
- authorised credentials.

## 9. Credentials and security

Certain features may use credentials for:

- SFTP;
- SMB;
- NutManager Agent;
- NUT authentication.

Where supported, NutManager may use the **Windows Credential Manager** to store credentials.

The user is responsible for:

- protecting their Windows account;
- limiting administrative access to the machine;
- protecting private keys;
- using valid certificates;
- controlling who belongs to the authorised groups;
- removing credentials once they are no longer needed.

No credential storage mechanism should be considered absolute protection against the compromise of a machine already controlled by an attacker.

## 10. Privacy and telemetry

The current version of NutManager **has no telemetry system and performs no automatic collection of usage data by the developer**.

The information the application processes is used locally or transmitted directly to the servers the user has configured, according to the feature being used.

That may include connections to:

- a NUT server;
- an SFTP server;
- an SMB share;
- NutManager Agent.

External links, such as GitHub and documentation, are reached only on explicit user action.

## 11. Third-party software and services

NutManager depends on or interacts with third-party technologies, including **Network UPS Tools** and Windows operating system components.

Those projects have their own:

- terms;
- licences;
- policies;
- limitations;
- support requirements.

NutManager's developer does not control changes made by those third parties.

## 12. Compatibility

NutManager's official support may vary between versions.

A given combination of:

- Windows;
- NUT;
- UPS;
- driver;
- USB/serial adapter;
- network configuration;
- Active Directory domain;
- SMB;
- SFTP;
- certificates;

may behave differently from another environment.

A statement of compatibility is not a guarantee of operation with every possible device or configuration.

## 13. No warranty

NutManager is provided **without warranty of uninterrupted operation, freedom from errors, or fitness for a particular purpose**, subject to the terms of the applicable licence.

There is no guarantee that:

- all hardware will be recognised;
- every NUT driver will work;
- every configuration will be valid for a given device;
- remote connections will always be available;
- changes made by the user will not cause unavailability.

## 14. Limitation of liability

To the maximum extent permitted by applicable law, NutManager's developer and contributors will not be liable for damages arising from the use of, or inability to use, the software, including but not limited to:

- data loss;
- loss or corruption of configuration;
- service unavailability;
- monitoring failure;
- unexpected shutdowns;
- system interruption;
- loss of productivity;
- damage related to incorrect configuration;
- problems arising from hardware, drivers or third-party software.

The user must maintain adequate backup, contingency and recovery procedures.

## 15. Inappropriate use

NutManager was developed for the legitimate administration of systems under the user's responsibility or authorisation.

The software must not be used to access, modify or administer systems without the authorisation of the party responsible for them.

This provision describes the product's intended purpose and **does not alter the rights granted by GPL v2.0 over the source code**.

## 16. Bugs and known limitations

Like any software, NutManager may contain defects, incompatibilities or behaviours not yet identified.

Known problems may be documented in:

- the Operator Manual;
- the project's GitHub;
- the release notes.

The user should consult the documentation for the installed version before making significant changes in production environments.

## 17. Updates

NutManager does not guarantee automatic updates.

It is the administrator's responsibility to check periodically for new versions, particularly where there are:

- security fixes;
- bug fixes;
- compatibility changes;
- updates to the runtime included with the software.

## 18. Documentation

The documentation aims to describe the product's actual behaviour, but a temporary difference may exist between a recent version of the software and the published documentation.

Where an operational question arises, check:

1. the installed version;
2. the release notes;
3. the corresponding documentation;
4. the official repository.

## 19. Changes to these Terms

These Terms may be updated to reflect changes to NutManager, its distribution or its documentation.

The current version must state its revision date.

## 20. Project and developer

**NutManager**

Project developed and maintained by **Marcelo Pacheco — @marcelodotnet**.

Official repository:

https://github.com/marcelodotnet/NutManager

Source code licence:

**GNU General Public License v2.0**
