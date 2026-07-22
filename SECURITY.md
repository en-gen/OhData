# Security Policy

## Supported versions

OhData ships stable 1.x releases to NuGet. **The latest released version is supported**;
security fixes are published as a new release on top of it (patch or minor, per SemVer).
Older releases receive no back-ports — upgrade to the latest release to receive fixes.

| Version | Supported |
|---|---|
| Latest 1.x release | ✅ |
| Older 1.x releases | ❌ (upgrade) |
| Pre-release commits on `develop` | ❌ (not for production) |

## Reporting a vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Email the maintainer directly at
[2085828+engenb@users.noreply.github.com](mailto:2085828+engenb@users.noreply.github.com)
with the subject line `[OhData] Security vulnerability report`.

Please include:

- A description of the vulnerability and its potential impact
- Steps to reproduce or a minimal proof-of-concept
- The version or commit hash you tested against

## What to expect

- **Acknowledgement** within 5 business days
- **Assessment and severity triage** within 10 business days
- **Resolution or mitigation plan** communicated to you before any public disclosure

We follow a coordinated disclosure model. Please allow reasonable time for a fix
to be prepared and released before disclosing publicly.

## Scope

This policy covers every published `EnGen.OhData.*` NuGet package (`AspNetCore`, `Client`, and
the `AspNetCore.Swashbuckle`/`AspNetCore.OpenApi`/`AspNetCore.NSwag` companions) and
the code in this repository. It does not cover vulnerabilities in upstream
dependencies (e.g. `Microsoft.AspNetCore.OData`) -- report those to their
respective maintainers.

## Responding to a bad release

NuGet.org does not allow package deletion. If a published version contains a critical bug
or security vulnerability:

1. **Unlist the version** on NuGet.org (go to the package page → Manage →
   select the affected version → Unlist). Unlisted versions are hidden from search
   but remain installable by direct version reference (`<PackageVersion>x.y.z</PackageVersion>`).

2. **Deprecate the version** on NuGet.org with an appropriate reason
   (`Critical Bugs` or `Other`) and a message pointing users to the fixed version.
   This shows a warning banner in Visual Studio and the NuGet CLI.

3. **Publish a patch release** immediately with the fix following the standard
   release process in `CONTRIBUTING.md`.

4. **File a GitHub Security Advisory** if the issue is a security vulnerability:
   go to the repo **Security** tab → **Advisories** → **New draft security advisory**.
   This generates a CVE and notifies users who have enabled vulnerability alerts.
