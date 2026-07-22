# Content inventory + information architecture (issue #208)

## 1. Existing content inventory

### `docs/*.md` (18 guides)

| File | H1 / topic | Site section (proposed) |
|---|---|---|
| `architecture.md` | Architecture — the core flow, design decisions | Concepts |
| `authorization.md` | Authorization — `RequireAuthorization`/roles/`RequireResource` | Guides · Security |
| `bound-operations.md` | Bound Functions and Actions | Guides · Modeling |
| `client.md` | OhData.Client — typed LINQ client | Client |
| `deep-insert.md` | Deep Insert (nested related entities in POST) | Guides · Writing data |
| `deployment.md` | Deployment (Dockerfile, Render) | Operations |
| `etags.md` | ETags and Optimistic Concurrency | Guides · Writing data |
| `ignoring-properties.md` | Ignoring Properties | Guides · Modeling |
| `migrating-from-microsoft-odata.md` | Migrating from Microsoft.AspNetCore.OData | Getting Started · Migration |
| `navigation-routing.md` | Navigation Property Routing, `$ref`, POST-to-nav | Guides · Modeling |
| `nswag.md` | NSwag Integration | Guides · API documentation |
| `observability.md` | Observability (#200) | Operations |
| `openapi.md` | OpenAPI (`Microsoft.AspNetCore.OpenApi`) | Guides · API documentation |
| `property-access.md` | Individual Property Access, `/$value` | Guides · Modeling |
| `query-options.md` | Query Options (`$filter`/`$orderby`/`$select`/`$expand`/`$count`/`$search`) | Guides · Querying |
| `releasing.md` | Releasing OhData to NuGet | Contributing (or hide from public nav) |
| `spec-compliance.md` | OData 4.0 Spec Compliance | Reference |
| `versioning.md` | API Versioning | Guides · Advanced |

### `docs/design/*` (internal design notes — NOT for the public site)

`199-per-operation-authorization.md`, `206-select-projection-pushdown.md`, `226-ignore-property-exclusion.md`. These are per-issue design records. Keep them in the repo; **exclude from the published site** (or surface under a "Design notes / ADR" area behind Contributing if desired).

### `docs/superpowers/{plans,specs}/`

Internal agent workflow artifacts. **Exclude from the site.**

### README.md sections (source material for landing + getting started)

| README section | Reuse as |
|---|---|
| Intro + "Try it live" (Scalar/Swagger demo, v2 service doc) | Home / landing hero |
| Getting Started (install server + client packages) | Getting Started · Installation |
| Packages table | Getting Started · Packages, and Home |
| Server quick start (Product profile) | Getting Started · Quick start (server) |
| Client quick start | Getting Started · Quick start (client) |
| OpenAPI/Swagger documentation | Guides · API documentation (links to openapi/nswag/versioning) |
| Feature/reference table (bottom) | becomes the site nav itself |
| Benchmarks / server-comparison link | Reference · Performance |

### Brand assets already present (`assets/`)

`icon.svg`, `icon.png`, `icon-64/512.png`, `social-card.svg`, `social-card.png`. Ready to wire into the site logo, favicon, and social/OpenGraph card. The issue explicitly notes these exist and are currently unused for a site.

## 2. Proposed information architecture (site nav)

```
Home (landing)                         ← hero from README intro + social card + demo links + quick install
│
├─ Getting Started
│   ├─ Installation                    ← README Getting Started + Packages
│   ├─ Quick start (server)            ← README server quick start
│   ├─ Quick start (client)            ← README client quick start
│   ├─ EF Core + SQLite tutorial       ← ★ NEW (see gaps) — samples/OhData.Sample.EfCoreSqlite as narrative
│   └─ Migrating from Microsoft.AspNetCore.OData   ← migrating-from-microsoft-odata.md
│
├─ Concepts
│   └─ Architecture                    ← architecture.md
│
├─ Guides
│   ├─ Querying
│   │   └─ Query options               ← query-options.md
│   ├─ Modeling
│   │   ├─ Navigation & $ref routing   ← navigation-routing.md
│   │   ├─ Individual property access  ← property-access.md
│   │   ├─ Bound functions & actions   ← bound-operations.md
│   │   └─ Ignoring properties         ← ignoring-properties.md
│   ├─ Writing data
│   │   ├─ Deep insert                 ← deep-insert.md
│   │   └─ ETags & concurrency         ← etags.md
│   ├─ Security
│   │   └─ Authorization               ← authorization.md
│   ├─ API documentation
│   │   ├─ OpenAPI (AddOpenApi)        ← openapi.md
│   │   └─ NSwag                       ← nswag.md
│   └─ Advanced
│       └─ API versioning              ← versioning.md
│
├─ Client
│   └─ OhData.Client guide             ← client.md
│
├─ Operations
│   ├─ Deployment                      ← deployment.md
│   └─ Observability                   ← observability.md
│
├─ Reference
│   ├─ OData 4.0 spec compliance       ← spec-compliance.md
│   ├─ Performance / benchmarks        ← link to server-comparison-report.md
│   └─ API reference (auto-generated)  ← ★ DocFX from XML docs — IN/OUT of v1 is a maintainer decision
│
└─ Contributing (optional, can be repo-only)
    ├─ Releasing to NuGet              ← releasing.md
    └─ Design notes / ADRs             ← docs/design/* (optional, if surfaced at all)
```

## 3. Gaps — docs that need writing (issue #208 calls out the first two)

1. **`docs/index.md` landing page** — currently the only "home" is the table at the bottom of README. Needed as the site front door (hero, value prop, demo links, install, next steps). Brand social card fronts it.
2. **Getting-started walkthrough** — README has quick-start snippets but no continuous "zero to running API" narrative. Promote to a first-class Getting Started page.
3. **EF Core + real-provider tutorial** — issue asks for an "EF-Core-with-a-real-provider guide." Raw material exists (`samples/OhData.Sample.EfCoreSqlite/`, referenced from README) but there is no prose guide walking through it. Write one.
4. **(Decision-dependent) API reference** — no generated API browser today. DocFX can produce it from XML doc comments; whether it ships in v1 is a maintainer decision (see PUBLISH.md).

## 4. Notes on placement decisions

- **`releasing.md`** is contributor-facing, not user-facing. Recommend it live under a "Contributing" area or stay repo-only; don't put it in the main user nav.
- **`docs/design/**` and `docs/superpowers/**`** are internal — exclude from the published site by config (DocFX `content`/`exclude` globs), do not delete.
- The README's bottom reference table effectively *is* today's IA; this proposal formalizes it into sections and adds the three missing pages.
