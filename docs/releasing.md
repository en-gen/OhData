# Releasing OhData to NuGet

The publish pipeline is [`.github/workflows/publish.yml`](../.github/workflows/publish.yml): it fires when a
GitHub Release is **published**, validates the release tag against the GitVersion-computed version, builds,
runs all three test suites, packs `EnGen.OhData.AspNetCore` and `EnGen.OhData.Client` (with `.snupkg`
symbols), runs a package-quality gate, attests build provenance, and pushes to nuget.org with
`--skip-duplicate`.

## Deterministic builds

`src/Directory.Build.props` sets `ContinuousIntegrationBuild=true` whenever `$(GITHUB_ACTIONS)` is `true`,
so every CI build (not just the publish workflow) is deterministic — the same source and compiler version
produce byte-identical assemblies. This is what lets SourceLink map a shipped PDB back to the exact commit.
Local builds stay non-deterministic (faster incremental builds); nothing changes for day-to-day development.

## Package quality gate

After `Pack`, the workflow runs [Meziantou.Framework.NuGetPackageValidation.Tool](https://github.com/meziantou/Meziantou.Framework)
against both `.nupkg` files:

```bash
dotnet tool install --global Meziantou.Framework.NuGetPackageValidation.Tool
meziantou.validate-nuget-package ./nupkg/*.nupkg --excluded-rules IconMustBeSet
```

It checks description/author/license/repository/tags/readme are set, XML docs and symbols are present, and
assemblies are optimized (Release, not Debug) — `IconMustBeSet` is excluded because a package icon is a
deliberately deferred decision (see memory: 1.0.0 scope decisions), not an oversight. A non-zero exit fails
the job.

Also gated on `EnablePackageValidation=true` (set on both packable csprojs): MSBuild's own API/ABI compat
checks, currently limited to cross-TFM (net8.0/net10.0) compatibility since there's no published baseline
yet. **After 1.0.0 ships**, set `PackageValidationBaselineVersion=1.0.0` in both csprojs (see the comment
already left next to `EnablePackageValidation` in each) so every later pack is diffed against the public
1.0.0 API surface and breaking changes fail the build.

## One-time setup

Publishing uses **nuget.org Trusted Publishing** (OIDC) — no API key, no repository secret to store
or rotate. The workflow exchanges a GitHub-signed OIDC token for a 1-hour API key at run time.

1. **Trusted Publishing policy** — on nuget.org: username > Trusted Publishing > Create:
   - Policy Name: anything (e.g. `OhData publish via GitHub Actions`)
   - Package Owner: `engenb`
   - Repository Owner: `en-gen`
   - Repository: `OhData`
   - Workflow File: `publish.yml` (file name only, no `.github/workflows/` path)
   - Environment: leave blank (the workflow does not use GitHub environments)
2. **Watch the policy status**: a new policy may show as *temporarily active* for 7 days. A successful
   publish inside the window locks it permanently; if it lapses, restart the window from the UI.
3. **Recommended:** reserve the `EnGen.` package ID prefix on nuget.org (Account > ID prefix reservation)
   for the verified-prefix checkmark and squatting protection.

The workflow side is already wired: `permissions: id-token: write` on the publish job and a
`NuGet/login@v1` step (user `engenb`) that runs immediately before the push, feeding
`steps.nuget-login.outputs.NUGET_API_KEY` to `dotnet nuget push`.

## Release procedure (GitFlow + GitVersion)

GitVersion ([`GitVersion.yml`](../GitVersion.yml), `GitFlow/v1`) computes versions from branch topology.
`develop` computes `X.Y.Z-alpha.N`; the **release version is carried by a `release/X.Y.Z` branch name**.
A direct `develop -> main` merge computes the wrong version and the workflow's tag-validation step will
reject the release.

1. Update `CHANGELOG.md`: retitle the pending section to `## [X.Y.Z] - <today>` and leave a fresh empty
   `## [Unreleased]` above it. Merge via PR to `develop` as usual.
2. Create the release branch: `git checkout -b release/X.Y.Z origin/develop && git push -u origin release/X.Y.Z`.
3. Open a PR from `release/X.Y.Z` to **`main`** and merge it once green.
4. Create a GitHub Release targeting `main` with tag `vX.Y.Z` (Releases > Draft a new release > publish).
   Paste the CHANGELOG section as the release notes.
5. The `Publish to NuGet` workflow runs automatically. If the tag-validation step fails, the tag does not
   match what GitVersion computed on `main` — do not force it; fix the branch topology.
6. Verify: package pages render (README, license, version) at
   `https://www.nuget.org/packages/EnGen.OhData.AspNetCore` and `.../EnGen.OhData.Client`;
   `dotnet add package EnGen.OhData.AspNetCore` resolves the new version; symbols step through from
   the NuGet symbol server; and confirm build provenance with `gh attestation verify` (see below).
7. Back-merge `main` into `develop` (GitFlow) so the tag is reachable and develop's computed version
   advances past the release.

## Rehearsal mode (no push, no key required)

The workflow can be run manually without a release: **Actions > Publish to NuGet > Run workflow**. Leave
`dry_run` at its default (`true`) to run everything through build, test, pack, the package-quality gate,
and provenance attestation, then stop — no login, no push. Download the packed `.nupkg`/`.snupkg` from the
run's **Artifacts** section to inspect them. There's no release tag on a manual run, so the tag-validation
step is skipped automatically. Setting `dry_run` to `false` on a manual run pushes to nuget.org exactly
like a real release — use this only if you intend to actually publish outside the normal Release flow.

## Verifying build provenance

Every pack (real release or rehearsal) is attested with `actions/attest-build-provenance`. Verify a
downloaded package against the repo:

```bash
gh attestation verify EnGen.OhData.AspNetCore.X.Y.Z.nupkg --repo en-gen/OhData
```

A successful verification confirms the package bytes were produced by this workflow from a specific commit
SHA on GitHub's infrastructure, not assembled or modified elsewhere.

## Local dry-run (no key required)

```bash
dotnet pack src/OhData.AspNetCore/OhData.AspNetCore.csproj -c Release -o ./nupkg-dry
dotnet pack src/OhData.Client/OhData.Client.csproj -c Release -o ./nupkg-dry
```

GitVersion.MsBuild stamps the computed version (an explicit `-p:Version` is overridden at build time —
expected). Inspect the `.nupkg` as a zip: both TFMs under `lib/`, XML docs beside each assembly,
`README.md` at the package root. To also exercise the quality gate locally:

```bash
dotnet tool install --global Meziantou.Framework.NuGetPackageValidation.Tool
meziantou.validate-nuget-package ./nupkg-dry/*.nupkg --excluded-rules IconMustBeSet
```
