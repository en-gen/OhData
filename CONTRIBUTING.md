# Contributing to OhData

Thank you for your interest in contributing. This document explains how to get involved.

## Before you start

Open a GitHub Issue before writing code. This gives the maintainer a chance to discuss
the approach, flag any conflicts with the roadmap, and avoid wasted effort. You are
welcome to attach a draft PR to the issue if you want to show your direction early, but
it is not required before discussion.

For small typo or doc fixes you can skip the issue and go straight to a PR.

## Development setup

**Prerequisites:** .NET 8 SDK, Docker Desktop (for k6 integration tests).

```bash
# Clone
git clone https://github.com/en-gen/OhData.git
cd OhData

# Build
dotnet build src/OhData.sln

# Run tests
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj
dotnet test src/OhData.Client.Tests/OhData.Client.Tests.csproj

# Run the interactive test bench (browse to http://localhost:5099/scalar)
dotnet run --project src/OhData.TestBench.AspNetCore
```

## Branching

This repo uses GitFlow. Branch off `develop` for all contributions.

| Type | Prefix | Example |
|------|--------|---------|
| New feature | `feature/` | `feature/batch-requests` |
| Bug fix | `bugfix/` | `bugfix/skiptoken-400` |

Target `develop` in your PR. PRs targeting `main` directly will be declined.

## Code style

Code style is enforced automatically. A pre-commit hook runs `dotnet format` before
every commit. If the hook rejects your commit, run:

```bash
dotnet format src/OhData.sln
```

and re-commit. The CI pipeline also runs a format check and will fail if style is off.

A few conventions to be aware of:

- `ImplicitUsings` is disabled. Add all `using` statements explicitly in every `.cs` file.
- File-scoped namespaces only.
- Allman brace style.
- All public and protected members must have XML documentation comments.
  Reference the relevant OData spec section (e.g., `§11.2.1`) where applicable.

## Pull requests

- Keep each PR focused on a single concern.
- Include or update tests for any behaviour change.
- The PR description should explain *why* the change is needed, not just what it does.
- All CI checks must pass before review.

By submitting a PR you agree that your contribution will be licensed under the
[MIT License](LICENSE) that covers this project.

## Releasing

This section is for maintainers.

### One-time setup: NuGet API key

1. Go to https://www.nuget.org/account/apikeys and create an API key:
   - **Glob pattern:** `OhData.*`
   - **Permission:** Push
   - Set an expiration date and note it for rotation
2. Add the key to this repo:
   **Settings → Secrets and variables → Actions → New repository secret**
   - **Name:** `NUGET_API_KEY`
   - **Value:** the key from step 1

To rotate the key, repeat step 1 and update the secret in step 2.

### Cutting a release

1. Merge all intended changes into `develop` via PRs.
2. Create a `release/x.y.z` branch from `develop`.
3. Update `CHANGELOG.md`: rename `[Unreleased]` to `[x.y.z] - YYYY-MM-DD` and add a fresh `[Unreleased]` section above it. Fix the comparison links at the bottom.
4. Open a PR from `release/x.y.z` → `main`. Merge once CI is green.
5. On GitHub, create a **Release** targeting `main` with tag `vx.y.z` (e.g. `v0.1.0`).
   - The tag **must** match what GitVersion computes from the branch history — the publish workflow validates this and will fail fast if they diverge.
   - Publishing the release (not draft) triggers the publish workflow automatically.
6. Verify both `EnGen.OhData.AspNetCore` and `EnGen.OhData.Client` appear on NuGet.org within a few minutes.
7. Merge `main` back into `develop`.

## Reporting bugs

Open an issue using the Bug Report template. Include a minimal reproduction
if possible. Do not open issues for security vulnerabilities - see
[SECURITY.md](SECURITY.md) if that file exists, or email the maintainer directly.
