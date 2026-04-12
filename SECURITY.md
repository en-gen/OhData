# Security Policy

## Supported versions

OhData is pre-1.0. Only the latest commit on `develop` is actively maintained.
No back-ports to older commits will be made.

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

This policy covers the `OhData.AspNetCore` and `OhData.Client` NuGet packages and
the code in this repository. It does not cover vulnerabilities in upstream
dependencies (e.g. `Microsoft.AspNetCore.OData`) -- report those to their
respective maintainers.
