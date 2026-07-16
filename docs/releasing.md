# Releasing OhData to NuGet

The publish pipeline is [`.github/workflows/publish.yml`](../.github/workflows/publish.yml): it fires when a
GitHub Release is **published**, validates the release tag against the GitVersion-computed version, builds,
runs all three test suites, packs `EnGen.OhData.AspNetCore` and `EnGen.OhData.Client` (with `.snupkg`
symbols), and pushes to nuget.org with `--skip-duplicate`.

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
   the NuGet symbol server.
7. Back-merge `main` into `develop` (GitFlow) so the tag is reachable and develop's computed version
   advances past the release.

## Dry-run (no key required)

```bash
dotnet pack src/OhData.AspNetCore/OhData.AspNetCore.csproj -c Release -o ./nupkg-dry
dotnet pack src/OhData.Client/OhData.Client.csproj -c Release -o ./nupkg-dry
```

GitVersion.MsBuild stamps the computed version (an explicit `-p:Version` is overridden at build time —
expected). Inspect the `.nupkg` as a zip: both TFMs under `lib/`, XML docs beside each assembly,
`README.md` at the package root.
