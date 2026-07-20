# Publish workflow outline (issue #208)

Target: **GitHub Pages** (settled — free for this public repo, native `en-gen.github.io/OhData` URL, no extra infra). SSG: **DocFX** (see RECOMMENDATION.md). This is an outline/example, **not wired live** — no workflow is committed to `.github/workflows/` by this proposal, and no deploy runs.

## How it fits the repo's existing CI conventions

Matches `.github/workflows/ci.yml`: `ubuntu-latest`, `actions/checkout@v7`, `actions/setup-dotnet@v6` with `dotnet-version: 10.x`. Because DocFX generates the **API reference from build output + XML docs**, the site build restores/builds the solution first (only needed if API-ref is in scope for v1 — otherwise the build step is just DocFX over markdown).

## Example workflow (illustrative — do not assume it is committed)

`.github/workflows/docs.yml`:

```yaml
name: Docs site

on:
  push:
    branches: [main]        # publish from the release branch; adjust to taste
  workflow_dispatch:

permissions:
  contents: read
  pages: write              # required for GitHub Pages deploy
  id-token: write           # required for the deploy-pages OIDC handshake

concurrency:
  group: pages
  cancel-in-progress: true

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with:
          fetch-depth: 0

      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.x

      # Only needed if the API reference is in scope for v1 (DocFX reads build output).
      # Drop these two steps for a conceptual-docs-only v1.
      - name: Restore
        run: dotnet restore src/OhData.sln
      - name: Build (for XML-doc API metadata)
        run: dotnet build src/OhData.sln --no-restore -c Release

      - name: Install DocFX
        run: dotnet tool install -g docfx

      - name: Build docs site
        run: docfx docs-site/docfx.json     # emits static HTML to docs-site/_site

      - name: Upload Pages artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: docs-site/_site

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - id: deployment
        uses: actions/deploy-pages@v4
```

### One-time repo setup the maintainer performs (not automatable here)

- Settings → Pages → **Source: GitHub Actions** (not "deploy from branch").
- Confirm the DocFX `docfx.json` sets a base URL / `_appTitle` consistent with the `en-gen.github.io/OhData` path (project sites are served under a `/OhData/` subpath — links/baseUrl must account for it).

## Decisions that are the maintainer's to make (NOT decided here)

Hosting is **already decided (GitHub Pages)** and is deliberately absent from this list.

1. **SSG choice** — this proposal recommends **DocFX**; the maintainer signs off (or picks MkDocs Material if API-ref is dropped from v1 — see #4). Everything downstream depends on this.
2. **Theme / branding** — how far to invest in a branded DocFX template (logo, favicon, palette, social/OpenGraph card from `assets/social-card.*`). Ranges from "default modern template + logo" to a fully custom template. Not decided.
3. **Docs-versioning scheme** — whether/when to host multiple versions (`1.x` / `2.x`) with a version switcher, or ship only `latest` for v1. DocFX's versioning is manual/scripted, so this has real cost; recommendation is *defer, ship `latest` first*, but it's the maintainer's call.
4. **API-reference auto-gen in scope for v1?** — the pivotal one. **In scope:** keep DocFX, keep the restore/build steps above, get a full API browser from XML docs. **Out of scope for v1:** the case for DocFX weakens and **MkDocs Material** becomes the stronger SSG (drop the build steps, swap the build command). This choice also gates whether the public types need an XML-doc pass.
5. **Publish trigger / branch** — the example publishes on push to `main`; the maintainer may prefer tag-triggered, `develop`, or manual-only. Not decided.

## Optional nicety (not a required decision)

- **Custom domain** — GitHub Pages supports one (CNAME + DNS). Requires the maintainer to *own a domain*; it is purely cosmetic over the free `en-gen.github.io/OhData` URL. Mention-only; no action needed for launch.

## What this proposal deliberately does NOT do

No workflow committed, no Pages enabled, no deploy executed, no domain registered/configured, no existing `docs/` content moved or deleted. The optional POC under `docs-site/` (if built) has **no runnable deploy step**.
