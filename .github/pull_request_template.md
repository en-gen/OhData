<!--
Branch targets `develop` (release PRs to `main` excepted). See CONTRIBUTING.md.
-->

## Summary

<!-- One or two sentences on what this PR does and why. The diff shows the mechanics. -->

## Related issue

<!-- Fixes #N / Refs #N. -->

## Changes

-

## Testing

<!-- Which tests prove it, and did they fail before the change? Counts per test project. -->

-

## Performance

<!-- For changes on the server request path: k6 and BenchmarkDotNet results with the
commit they were taken at. Delete for anything else. -->

## Checklist

- [ ] PR targets `develop`
- [ ] `dotnet build src/OhData.sln` and `dotnet test src/OhData.sln` pass locally
- [ ] New behaviour is covered by a test that fails without the change (or the PR explains why not)
- [ ] Matching `docs/internals/` file and public `docs/` guide updated
- [ ] `CHANGELOG.md` updated under `[Unreleased]`; breaking changes marked BREAKING with the remedy
- [ ] Public API, wire-visible or breaking change: `needs-owner` label added, auto-merge left off
