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

## Reporting bugs

Open an issue using the Bug Report template. Include a minimal reproduction
if possible. Do not open issues for security vulnerabilities - see
[SECURITY.md](SECURITY.md) if that file exists, or email the maintainer directly.
