# Docs site SSG recommendation (issue #208)

Status: **decision-ready proposal.** Nothing here is built, deployed, or published beyond an optional local POC under `docs-site/`. Hosting target is **GitHub Pages** (settled by the maintainer — free for this public repo, `en-gen.github.io/OhData`).

## TL;DR

**Recommended SSG: DocFX.**

DocFX is the only candidate that natively unifies the two things this site needs — hand-written conceptual guides *and* a C#/.NET API reference generated straight from the public surface's XML doc comments — in a single build, with **zero new toolchain** (it is a `dotnet tool`, and this team is already all-in on .NET 10; no Node or Python to install, pin, or keep patched). For a lean OSS project with one maintainer, that toolchain fit and the free API-reference generation are the deciding factors.

**Top tradeoff you give up:** out-of-the-box visual polish and best-in-class search UX. DocFX's modern default template is genuinely decent now (dark mode, client-side Lunr search, responsive), but MkDocs Material still looks better and searches better with no effort, and DocFX's **versioned-docs story is the weakest of the field** (no first-class version dropdown — you script multiple site builds or lean on the modern template's limited support). If C# API-reference auto-gen is ruled **out of scope for v1** (see maintainer decisions), that removes DocFX's biggest advantage and **MkDocs Material becomes the better pick** on polish, search, migration ease, and versioning. That fork is a real maintainer decision, called out below.

## Candidates evaluated

DocFX, Docusaurus, VitePress, MkDocs Material, plus one .NET alternative (Statiq) for completeness.

## Scorecard

Scale: ●●● strong / ●●○ adequate / ●○○ weak, per criterion. "API ref" = auto C# XML-doc → browsable API reference.

| Criterion | DocFX | MkDocs Material | Docusaurus | VitePress | Statiq |
|---|---|---|---|---|---|
| (a) C#/.NET XML-doc → API reference | ●●● native, reads assemblies + XML | ○○○ needs a bridge | ○○○ needs a bridge | ○○○ needs a bridge | ●●○ native (Statiq.Docs) but immature |
| (b) Migrate existing hand-written `docs/*.md` | ●●● markdown + `toc.yml` | ●●● markdown + `nav:` yaml | ●●○ markdown/MDX, some frontmatter churn | ●●● markdown-first | ●●○ markdown |
| (c) Theming / branding flexibility | ●●○ templates, more manual | ●●● excellent via `mkdocs.yml` | ●●● React components, very flexible | ●●● Vue components | ●●○ |
| (d) Built-in search | ●●○ client-side Lunr, ok | ●●● best-in-class, zero config | ●●○ local plugin or Algolia | ●●○ built-in minisearch | ●○○ weak |
| (e) Versioned docs (1.x/2.x) | ●○○ weakest; manual/scripted | ●●● first-class via `mike` | ●●● first-class | ●○○ manual | ●○○ |
| (f) GH Pages via GitHub Actions | ●●● straightforward | ●●● straightforward | ●●● straightforward | ●●● straightforward | ●●○ |
| (g) Maintenance burden / toolchain fit | ●●● **.NET only, dotnet tool** | ●●○ adds Python | ●●○ adds Node + React/MDX weight | ●●○ adds Node | ●●○ .NET but low activity |

### Reading the scorecard

- **DocFX wins (a) and (g) decisively** — the two criteria that matter most for *a .NET library maintained by one person*. It builds conceptual docs and a full API browser from `dotnet build` output in one pass, and adds no language runtime the team doesn't already have.
- **MkDocs Material wins (c), (d), (e)** — it is the polish/search/versioning champion, and migration is trivial (drop the `.md` files in, list them under `nav:`). Its fatal gap for *this* project is (a): no native C# API reference. Bridges exist (e.g. generating markdown from the XML docs, or embedding DocFX's API output), but that reintroduces DocFX anyway or adds a bespoke generation step to maintain.
- **Docusaurus** is the most capable overall (first-class versioning, huge ecosystem) but is the heaviest to run — Node + React + MDX — which is overkill for ~20 markdown files and the worst maintenance fit for a solo .NET maintainer.
- **VitePress** is the nicest lightweight Node option (fast, clean, markdown-first) but has no first-class versioning and no native API ref — it wins nothing DocFX doesn't already cover for this use case, while adding a Node toolchain.
- **Statiq** is the other .NET SSG and can generate API docs, but it is markedly less mature and less actively maintained than DocFX; choosing it trades DocFX's ecosystem for no clear gain.

## Why DocFX, in one paragraph

OhData is a library whose value proposition is a clean public API surface, and the issue explicitly flags auto API docs as a real factor. DocFX is the only tool that delivers that for free, from XML doc comments the code can already carry, in the same build as the guides — and it does so without asking a .NET-only, PM-orchestrator-style maintainer to adopt, pin, and patch a Python or Node stack. The existing `docs/*.md` migrate as-is (markdown + a `toc.yml` nav), and GitHub Pages deployment via Actions is a solved, documented path. You pay for that with less theming polish, a merely-adequate search, and a manual versioning story — all acceptable for v1, and all improvable later without re-platforming the conceptual docs.

## Honest "what you'd give up" summary

- **Search:** DocFX's Lunr search is fine for a site this size but not Material-grade. Acceptable.
- **Theming:** matching the existing brand (social card, icon, palette) is more hand-work in DocFX than in Material/Docusaurus. Budget a day for a branded template; not a blocker.
- **Versioning:** this is the genuine weak spot. If the maintainer wants a slick `1.x / 2.x` dropdown on day one, DocFX will feel primitive next to `mike` (Material) or Docusaurus versioning. Recommendation: defer multi-version hosting; ship `latest` first (see maintainer decisions).
- **If API-ref is out of scope for v1:** re-evaluate — MkDocs Material is then the stronger choice.

## Local preview command (recommended SSG)

DocFX is installed as a .NET global tool and run from the repo root:

```bash
# one-time: install the tool
dotnet tool install -g docfx

# from repo root — build + serve with live reload at http://localhost:8080
docfx docs-site/docfx.json --serve
```

If a thin POC was scaffolded under `docs-site/` (see the POC section / build note at the bottom of this file), that exact command previews it.

## POC status

See the "POC build note" appended to the end of this file for whether the optional `docs-site/` scaffold was built and verified locally in this session. The POC, if present, is intentionally thin and fully discardable: config + nav + a few pages **copied** (never moved) from `docs/`. No deploy step runs; nothing is published.

---

## POC build note (this session)

**Built and verified locally.** DocFX 2.78.5 was installed as a global tool and a thin POC was scaffolded under `docs-site/`:

- `docfx.json` (conceptual-docs-only config; API metadata step intentionally omitted for the POC), `toc.yml` (nav), `index.md` (landing), and three guides **copied** from `docs/` (`architecture.md`, `query-options.md`, `authorization.md`). Originals in `docs/` were left untouched.
- `docfx docs-site/docfx.json` → **Build succeeded, 0 errors, 7 warnings.** All warnings are `InvalidFileLink` for cross-links to the 15 guides not included in this thin POC (e.g. `~/navigation-routing.md`); they resolve once the full doc set is added. Build output (`_site/`) is gitignored and was removed after verification.

**Exact local preview command** (from repo root, DocFX on PATH via `~/.dotnet/tools`):

```bash
dotnet tool install -g docfx        # one-time
docfx docs-site/docfx.json --serve  # http://localhost:8080
```

The POC is fully discardable — deleting `docs-site/` removes it with no effect on the repo.
