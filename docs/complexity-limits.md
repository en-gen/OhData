# Complexity limits

Five ceilings bound how expensive a single request's query options may be. Each is configurable
globally via `WithDefaults` or per entity set on the profile, with the profile value overriding the
global default, and a request that exceeds one is rejected with `400` before any handler runs. They
apply on all three collection read paths (`GetQueryable`, `GetAll`, Priority-1).

`MaxExpandTop` is the one whose reach is not uniform, so most of this page is about it. How it
interacts with `$expand` itself is on the [`$expand` page](expand.md).

Five ceilings bound how expensive a single request's query options may be. Each is configurable globally via `WithDefaults` or per entity set on the profile (the profile value overrides the global default); a request that exceeds a limit is rejected with `400` before any handler runs. They apply on all three collection read paths (`GetQueryable`, `GetAll`, Priority-1). The table also carries `ExpandPagingEnabled`, which is **not** a ceiling and rejects nothing — it is listed here because it is meaningless apart from `MaxExpandTop` directly above it.

> **`MaxExpandTop` is the one entry whose reach is not uniform, so read its row with this table beside it.** It is not a single check but three mechanisms with three different enforcement points, and only the first is a pre-handler validation:
>
> | Mechanism | Where it is enforced | Which routes reach it |
> |---|---|---|
> | Explicit nested `$top` over the ceiling → `400` | pre-handler validation, walked over the whole `$expand` tree | `GetQueryable`, `GetAll`, Priority-1 **and** `GET /{Set}({key})`. Not the `/$count` route, the delegate-backed navigation routes, or the `$skip` continuation route. |
> | Nested `$count` over the ceiling → `400` | the JSON shaping pass over the pushed-down expand tree | **A navigation the `$expand` pushdown engaged** — i.e. the `GetQueryable` collection route over an EF Core `IQueryable`. |
> | Bare `$expand` over the ceiling → `400`, or the `Nav@odata.nextLink` continuation when `ExpandPagingEnabled` is on | same shaping pass | Same. |
> | Any **raw-served collection** navigation over the ceiling → `400` | a size check over the serialized response, per level, after the expand pipeline | **Everything else, on every read path**: `GetAll`, Priority-1, a non-EF `IQueryable` (which `$search` also produces), a branch the pushdown declined, every level of `GET /{Set}({key})`, and every level below a raw-served parent. Never a link, whatever `ExpandPagingEnabled` says. |
>
> **The split is between how the collection was LOADED, not which route was called.** Rows 2 and 3 apply where the framework *composed* the child query, because that is also where it composed the child-key `ORDER BY` that lets page 1 and a `$skip` continuation agree. Row 4 covers everything the framework did not compose: the rows arrive already materialized inside whatever your handler returned, in that handler's own order, so a continuation link over them would silently skip and duplicate across the page boundary — which is why it is always a `400` and never a link.
>
> **This was not always so, and the gap was the whole of [#463](https://github.com/en-gen/OhData/issues/463) and [#464](https://github.com/en-gen/OhData/issues/464).** Row 4 used to read "`GET /{Set}({key})` only", checked at **depth 1** only, against a set of navigations resolved once at startup from the root profile. So: with a ceiling of 2, `GET /Authors(2)?$expand=Books($expand=Chapters)` served every chapter (the depth axis), and `GET /Authors?$expand=Books` over a `GetAll`, a Priority-1 or a non-EF source served every book with no bound at all (the path axis) — while this document and `MaxExpandTop`'s own XML doc said the value bounded *every* collection `$expand` level. Both are closed; the ceiling now has no gap in depth or in path.
>
> **The consequence worth knowing before you rely on the ceiling as a DoS bound:** row 4 is a **data** ceiling, not a materialization bound. The related rows are loaded by your own handler — typically an EF `Include` inside `GetById`, or a `ToList()` behind `GetAll` — before the framework ever sees the entity, so the `400` is raised *after* that load. It stops the over-sized collection reaching the client, and stops it being served silently truncated, but it cannot stop the query. The mitigation is in the handler: do not eager-load an unbounded child collection (a `GetById` that does not `Include` it serves `[]`), or bound the load yourself. Rows 2 and 3 are the only ones that bound the *fetch*.
>
> **A navigation whose delegate actually RAN is outside all four rows**: those rows are what your `Handler`/`BatchHandler` returned, and the framework neither truncates nor rejects them. Bound them in the delegate.
>
> Read that as written — *ran*, not *is declared with a delegate*. The two differ below a raw-served parent, and the difference is not academic: the expand pipeline does not recurse into a delegate-less navigation's subtree (whatever your handler already materialized there **is** the answer), so a navigation declared with a delegate one level under it is **never invoked** and the rows in the payload came from the *parent's* handler. Those are bounded by row 4 like any other raw rows. Measured, cap 2, `GetAll`, `Author —Books(delegate-less)→ Book —Chapters(delegate)→`: `?$expand=Books($expand=Chapters)` served five chapters with the `Chapters` delegate invoked zero times. What stays exempt is a delegate-backed navigation the framework really did call — which is the depth-1 case, and is also the only place the walk could reach one, since it never descends into a delegate's subtree.
>
> **Row 4 bounds the collection; it does not APPLY the nested window.** On a raw-served expansion a nested `$top`/`$skip` *within* the ceiling is accepted and then ignored, so what comes back is the whole collection (now ceiling-bounded), not the window that was requested. That residue is tracked by [#352](https://github.com/en-gen/OhData/issues/352)/[#464](https://github.com/en-gen/OhData/issues/464).

| Limit | Default | Bounds |
|---|---|---|
| `MaxExpansionDepth` | `3` (hard ceiling **6**) | Nesting depth of `$expand`, and the ceiling `$levels` is resolved and capped to (`$levels=max` becomes exactly this value). **Enforced** — a deeper `$expand`/`$levels` returns `400` rather than a silently-truncated result. Advertised per entity set in `$metadata` as `Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels`. Raise it to allow deeper graph/hierarchy queries, or lower it to harden — but **not above `EntitySetDefaults.MaxExpansionDepthCeiling` (6)**, which throws `ArgumentOutOfRangeException` at startup. See [The depth ceiling](#the-depth-ceiling) below for why the ceiling exists and why it is 6. |
| `MaxExpandTop` | `null` (no ceiling) | Per-navigation ceiling on how many related entities **any** collection `$expand` level may return, and on an explicit **nested** `$top` (`?$expand=Children($top=N)`). **The whole ceiling is opt-in:** the default moved from `1000` to `null` because `1000` was an invented number — the framework cannot know how large a child collection is, so it ships the control point and lets the implementor set it. Until it is set there is no ceiling of any kind: `?$expand=Children($top=999999)` is answered rather than rejected, a nested `$count` materializes the related collection with no bound, and a bare `?$expand=Children` composes no SQL `Take` and gets no size check. Set it (`WithDefaults(d => d.MaxExpandTop = N)`, or per profile) to turn all of that on at once. With a value in force, three mechanisms engage, and **their reach differs — see the callout above the table.** (1) An over-large **explicit nested `$top`** returns `400` (`InvalidQueryOption`) at any depth, whether or not the navigation would have been pushed down, on all three collection read paths and on `GET /{Set}({key})` — the same "what may a client ask for" rule as the root `MaxTop`. (2) A **nested `$count`** whose related collection exceeds the ceiling returns `400` rather than a truncated count (§11.2.4.2). (3) So does the **remaining shape** — a level with **neither** a nested `$count` **nor** an explicit nested `$top`, which includes the plain `$expand=Nav` and anything carrying only `$select`/`$orderby`/`$filter`/`$skip`, and every level of a `$levels=N` recursion. **(2) and (3) are enforced in the pushdown's JSON shaping pass, so they apply to a navigation the `$expand` pushdown actually engaged — the `GetQueryable` collection route over an EF Core `IQueryable`.** (4) Every **other** expanded delegate-less collection navigation is size-checked against the same ceiling, at every level of the `$expand` tree and on every read path, and always as a `400` ([#418](https://github.com/en-gen/OhData/issues/418), widened by [#463](https://github.com/en-gen/OhData/issues/463) and [#464](https://github.com/en-gen/OhData/issues/464)): `GetAll`, Priority-1, a non-EF `IQueryable` (`$search` produces one), `GET /{Set}({key})`, a branch the pushdown declined, and every level below a raw-served parent. That check covers every nested shape rather than only the bare one because a raw-served navigation has **no** nested option applied to it — `$filter`, `$orderby`, `$select`, `$skip`, `$top` and `$count` are all silently ignored there, unlike on the pushed path — so a bare-only ceiling would be bypassable by appending any one of them. A navigation whose delegate the framework actually **invoked** is never capped by any of the four — the framework does not truncate, or reject, a delegate's answer; a navigation merely *declared* with a delegate but reached under a raw-served parent, where the pipeline never recurses and so never calls it, carries the parent handler's own rows and is capped by (4) like any other raw collection. The **root** entity set's resolved value governs at every nesting depth, exactly like `MaxExpansionDepth`. On a profile, `MaxExpandTop = null` means *inherit* the resolved default, not "uncapped" — a profile cannot opt out of a ceiling set in the defaults. Setting a value also composes the nested **key tiebreaker** on shapes that previously had none, so it governs the nested wire *order* as well as the status code (see the nested-paging caveat above). **Cost caveat:** where the ceiling applies it is always *correct* — the request `400`s rather than returning a truncated count or a silently-clipped page — but not always *cheap* to enforce. At a projection **leaf** it is a SQL `Take(MaxExpandTop + 1)`, so a breach is detected without transferring the collection. At a level with its own nested `$expand`, or anywhere inside a `$levels` recursion, it can't be pushed into SQL as a `Take` (the same `APPLY`/`LATERAL` translation problem the nested-`$count` caveat above describes), so the `400` is thrown only **after** the full related collection — for `$levels`, the full recursive hierarchy — is materialized in memory. A hostile `$expand=Children($levels=N)` therefore buys that full materialization before being rejected on breach — a broad but *under*-cap hierarchy just materializes fully and returns `200` like any other under-cap page; the cost only bites once the collection actually exceeds the ceiling. |
| `ExpandPagingEnabled` | `false` | **Not a ceiling — the companion opt-in to `MaxExpandTop`.** Whether a *truly bare* collection `$expand` (one carrying no nested options at all) whose child collection exceeds the resolved `MaxExpandTop` is served as its first `MaxExpandTop` children plus a `Nav@odata.nextLink` continuation, instead of being rejected with `400`. Inert unless `MaxExpandTop` is also set — with no ceiling there is no boundary at which a continuation could begin — and `MaxExpandTop` is also the page size, for the first page and every continuation alike. There is deliberately no second page-size knob. It is a *separate* opt-in from the ceiling because a continuation link is **worse** than a `400` for a client that does not read nested annotations: that client sees a complete-looking collection that has been silently truncated. Only enable it if you know your clients follow `Nav@odata.nextLink`. On a profile it is a `bool?`, so a profile-level `false` genuinely opts **out** of a server-wide `ExpandPagingEnabled = true` — unlike `MaxExpandTop`, whose profile-level `null` means *inherit*. When it is on (with a ceiling set) it registers `GET /{Set}({key})/{Nav}?$skip=N` for each pageable navigation and emits the link; with it off — or on with no ceiling — no route is registered and no annotation is emitted. Turning it on changes **nothing** outside the truly-bare over-ceiling subset: with `MaxExpandTop` unset, and for every non-bare shape with it set, the status, the response body and the emitted SQL are byte-identical either way, and `$metadata` is byte-identical in every configuration. Full rules, the exact pageable set and the deliberate limits: [Nested server-driven paging](expand.md#nested-server-driven-paging-expandpagingenabled). |
| `MaxExpandBreadth` | `50` | Number of navigation expansions in a request's `$expand`, counted across **every level of the tree** (a `$levels=N` expansion counts as `N`). Over the limit is `400` (`InvalidQueryOption`) before any handler runs, on every read path that applies `$expand` — the three collection routes and `GET /{Set}({key})`. Depth-independent and pushdown-independent: it is a statement about what the client may *ask for*. See [The breadth guard](#the-breadth-guard) below. |
| `MaxFilterNodeCount` | `10000` | Number of nodes in a `$filter` expression tree. |
| `MaxOrderByNodeCount` | `1000` | Number of nodes in an `$orderby`. |
| `MaxAnyAllExpressionDepth` | `1000` | Nesting depth of `any()`/`all()` lambdas in a `$filter`. |

```csharp
builder.Services.AddOhData(o => o
    .WithDefaults(d => { d.MaxExpansionDepth = 3; d.MaxFilterNodeCount = 200; })
    .AddEntitySetProfile<OrderProfile>());

public class OrderProfile : EntitySetProfile<int, Order>
{
    public OrderProfile() { MaxExpansionDepth = 5; /* this set allows deeper expands than the default */ }
}
```

The node-count defaults are unchanged from what OhData already applied (they were previously hardcoded), and are now lowerable. Note that a **root** `$top`/`$skip` is governed separately by `MaxTop` (see above), not by these node counts; a **nested** `$top` inside a `$expand` is governed by `MaxExpandTop`.

```csharp
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.MaxExpandTop = 200)   // opt in to the ceiling; the default is null (none)
    .AddEntitySetProfile<OrderProfile>());
```

## The depth ceiling
`MaxExpansionDepth` is capped at **`EntitySetDefaults.MaxExpansionDepthCeiling`, which is 6**.
Configuring a larger value — in `WithDefaults` or on a profile — throws
`ArgumentOutOfRangeException` at startup, not at request time.

**Why a ceiling exists.** Relational query translation for a pushed nested projection is
`Θ(3ⁿ)` in the nesting depth. EF Core re-translates each nested-collection subtree three times with
no memoization, so every extra level triples the CPU spent *building* the query — before a single
row is read. This is not a data-volume problem: it reproduces with no database, no connection and no
rows, purely through `ToQueryString()`. Measured on a 16-node self-referential chain returning a
~6 KB body, one navigation per level:

| depth | translation |
|---:|---:|
| 5 | 0.09 s |
| **6** | **0.24 s** ← the ceiling |
| 8 | 3.8 s |
| 10 | 32 s |
| 12 | 291 s |

291 seconds is 4.9 minutes of single-core CPU for **one unauthenticated request with no body**, and
the growth is a clean ×3.0 per level with no discontinuity — there is no cliff to stay below, only a
curve to stop climbing.

**Why 6 and not 3.** The blow-up is at 10+, not at 5. Depth 5 costs ~90 ms, and this document's own
example above uses `MaxExpansionDepth = 5`, as do two of the framework's own tests. Capping at the
default of 3 would invalidate a documented configuration for a shape that is not expensive. 6 leaves
headroom above 5 while keeping the worst *configurable* depth under a quarter-second on the depth
axis.

**This is a mitigation, not a fix.** Nothing about `$levels=12` over a 16-node chain returning 6 KB
is unreasonable — it is expensive only because of upstream re-translation. The real answer is one
flat query per level instead of one nested projection, tracked in
[#430](https://github.com/en-gen/OhData/issues/430). Until then the ceiling bounds the damage.

**If you need a deeper graph**, fetch it as separate requests, or expand a **delegate-backed**
navigation (`HasMany(x => x.Children, getAll: ...)`) — a delegate-backed navigation is loaded once
per level by the expansion pipeline rather than composed into one nested projection, so it does not
pay the `3ⁿ` translation cost at all.

**Depth is only one axis.** Breadth multiplies on top of it and is bounded separately by
[`MaxExpandBreadth`](#the-breadth-guard).

## The breadth guard
`MaxExpandBreadth` (default **50**) caps how many navigation expansions one request's `$expand` may
contain, **counted across every level of the tree**. Over the limit is `400` (`InvalidQueryOption`)
before any handler runs.

**Why depth alone is not enough.** Translation cost multiplies by ~3 per level *and* by the number
of navigations expanded at each level. Measured at the **default** `MaxExpansionDepth` of 3, on a
model with six collection navigations, before this guard existed:

| navigations per level | wall clock | response |
|---:|---:|---:|
| 1 | 240 ms | 1,440 B |
| 4 | 1,010 ms | 1,696 B |
| 6 | 4,084 ms | 1,952 B |

4.1 seconds of single-core CPU for a 1,952-byte response, at defaults, unauthenticated. And the
compiled-query cache is no defence: each distinct navigation **subset** is a distinct EF cache key,
so a client cycling subsets never warms it and pays full translation cost on every request.

> The table above is the original measurement; the "why 50" figures below were taken later on a
> faster machine (the same shape reproduces at ~1.6 s there). Compare each set internally — the
> ratios hold across both — not across the two.

**Why the count spans the whole tree.** A per-level cap of `B` under a depth ceiling of `D` still
admits `B^D` expansions — 55,986 at `B=6, D=6`. Counting every node bounds both axes at once.
Counting *distinct navigation names* would be weaker still: the most expensive shapes measured reuse
six names over six levels.

**Why 50.** It is far above any realistic request — a three-level chain expanding three navigations
at every level is 39 nodes and is already unusual; typical rich requests are well under 15 — and it
keeps the worst legal request measurable. At the default depth of 3 a 50-node `$expand` measures
~0.4 s (interpolated between 39 nodes = 308 ms and 84 nodes = 699 ms). At the *maximum legal* depth
of 6, a systematic sweep of every branching vector within the budget put the worst legal request at
**1.0–1.4 s** — shape `[1,1,1,1,2,6]`, only 18 nodes, because deep-and-narrow is more expensive per
node than flat-and-wide. Unguarded, the same model reaches 2,850 nodes and **36 s** for a 111-byte
error response; that same request now returns `400` in **56 ms**, essentially all of it URL parsing.

It is a knob precisely because 50 is a judgement call rather than a law:

```csharp
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.MaxExpandBreadth = 20)   // harden every entity set
    .AddEntitySetProfile<OrderProfile>());

public class GraphProfile : EntitySetProfile<int, Node>
{
    public GraphProfile() { MaxExpandBreadth = 200; /* this set genuinely needs a wide graph */ }
}
```

A `$levels=N` expansion counts as `N` — its resolved level count — because that is what it costs:
one nested projection level each, exactly like the equivalent explicit chain. Everything else counts
as one.

## `MaxExpandTop` in detail

### What it bounds

`MaxExpandTop` is **unset by default**, and until you set it there is no ceiling of any kind. Once
set, the rule is:

> **A collection expand level carrying neither a nested `$count` nor an explicit nested `$top` is
> bounded by `MaxExpandTop`, whatever else it carries.**

That is deliberately broader than "bare" suggests — `($select=…)`, `($orderby=…)`, `($filter=…)` and
`($skip=N)` are all in scope, because none of them bounds the collection either. `$levels=N` is
checked at each level independently, so `Nav($levels=1)` behaves exactly like the bare `$expand=Nav`
it restates, and a deeper level that breaches is rejected even when the levels above it are under the
cap.

An **explicit** nested `$top` wins: it is validated against the ceiling up front (`400` if larger
than `MaxExpandTop`) and windows the collection itself, so no default bound is composed alongside it.

**How the bound is applied depends on the shape**, and so does which rows it counts:

| Shape | Bound | Rows measured |
|---|---|---|
| Projection **leaf** (no nested `$expand` of its own) | SQL `Take(MaxExpandTop + 1)` — the over-cap case is detected without transferring the collection | **post-`$skip` remainder**, so `Children($skip=4995)` over 5,000 rows at a ceiling of 1000 succeeds and returns 5 |
| A level with its own nested `$expand`, and every level of a `$levels` recursion | post-materialization check in the JSON pass | the **fully materialized, pre-window** collection, so the same `$skip` request is rejected |
| **Raw-served** — `GetAll`, Priority-1, a non-EF `IQueryable`, every level of `GET /{Set}({key})`, a branch pushdown declined, and every level below any of those | post-materialization check over the serialized response — always a `400`, never a link | the materialized collection |

The middle and bottom rows materialize without a bound before rejecting. Windowing a collection *and*
projecting a further collection out of it in one query needs SQL `APPLY`/`LATERAL`, which not every
provider translates (SQLite among them), so there is nothing to push. The *correctness* of the
ceiling holds on every shape — never a truncated count, never an untranslatable query —
[#299](https://github.com/en-gen/OhData/issues/299) tracks tightening the remaining cost.

### `$count` on a pushed expand

At a **projection leaf**, a `$count` that comes **with** a nested `$top`/`$skip` bounds the SQL fetch
by that window and takes `Nav@odata.count` separately, as a correlated `COUNT(*)` scalar subquery
over the filtered-but-unwindowed collection, in the same single query. That is the split
`Microsoft.AspNetCore.OData` has always made, and a correlated scalar aggregate translates on SQLite
where a windowed collection projected out of a windowed collection does not.

A `$count` with **no** window materializes the filtered collection — bounded by
`Take(MaxExpandTop + 1)` when a ceiling is set — and counts it.

§11.2.4.2 holds throughout: `Nav@odata.count` is the count of the **full filtered** collection, never
the returned page. A ceiling breach is a `400` (`InvalidQueryOption`) rather than a truncated count.
Narrow the collection with a nested `$filter`, or raise or remove `MaxExpandTop`.

### Over the ceiling: `400`, or a page plus a link

An over-cap collection is a `400` **unless** it is a *truly bare* `$expand` on a profile that opted
in with `ExpandPagingEnabled` — then it is a page plus a `Nav@odata.nextLink`. Silently windowing an
expanded collection without a link to continue from would be a worse spec violation than the cost of
rejecting, so the framework never does it: over the ceiling a shape either rejects or links.

The exact pageable set and the continuation's own surface are in
[nested server-driven paging](expand.md#nested-server-driven-paging-expandpagingenabled) below. For every
shape that is not truly bare, and every profile that did not opt in, the remedies are unchanged:
narrow with a nested `$filter`, give the navigation an explicit nested `$top`, or raise or remove
`MaxExpandTop`.

### Setting the ceiling also fixes the nested wire order

When `$top`/`$skip` are pushed to SQL without a nested `$orderby`, the navigation element's single key
is appended as a deterministic tiebreaker, mirroring the root path. A composite-keyed child type is
left to the provider's order.

Because a bare collection expand carries a default bound once `MaxExpandTop` is set, the same
tiebreaker applies to it — so the ceiling governs the nested **wire order**, not only the status
code. With a ceiling in force a nested collection comes back in child-key order; with `MaxExpandTop`
unset, no tiebreaker is composed and the order is whatever the provider yields. You cannot take one
without the other.

### The SQL bound needs window functions

`Take(MaxExpandTop + 1)` inside a collection projection is translated by EF Core as the standard
top-N-per-group form, `ROW_NUMBER() OVER (PARTITION BY <fk> ORDER BY <key>)` — the same shape an
explicit nested `$top` has always produced. Every provider OhData tests against (SQLite, EF Core
InMemory) supports it, as do SQL Server, PostgreSQL, Oracle, MySQL 8.0+ and MariaDB 10.2+. The only
relational providers that cannot translate it are MySQL before 8.0 and MariaDB before 10.2, both long
past end-of-life. On such a provider, leave `MaxExpandTop` unset to keep the plain join.

### Use `null`, not a large number, to mean "no ceiling"

`MaxExpandTop = int.MaxValue` still counts as **set**, so every bound and every key tiebreaker is
composed exactly as for a small value — you pay the `ROW_NUMBER()` window for a rejection that can
never fire. Unset (the default `null`) is the opt-out; a sentinel number is not.

### With no ceiling set, startup warns

`MapOhData()` logs one `Warning` per navigation that is collection-valued, delegate-less **on that
profile's own declaration**, on a profile that has `GetQueryable`, `ExpandEnabled` **and**
`ExpandPushdownEnabled`, when that profile's resolved `MaxExpandTop` is `null` — exactly the
navigations a bare `?$expand=Nav` will materialize without bound. This is what replaced an arbitrary
`1000` default.

A *sibling* profile over the same EDM entity type declaring the navigation with a delegate does
**not** silence it ([#421](https://github.com/en-gen/OhData/issues/421)): that sibling's delegate
governs the sibling's own set, while this one still serves the navigation raw and unbounded.

The warning names the entity set, the navigation, `MaxExpandTop` **and** `ExpandPagingEnabled` — in
that order, because the second is inert without the first — and deliberately prescribes no *number*,
since the framework cannot know how large your child collections are. Leaving the ceiling unset is a
legitimate choice for a collection you know is small; the warning informs that choice rather than
making it. Because `ExpandEnabled` is `false` by default, a registration that never opts into
`$expand` gets no warning at all. Emitted once at startup, never per request.

### `Prefer: odata.maxpagesize` narrows a nested page

§8.2.8.5 scopes the preference to *"each collection within the response"*, so it is not a root-only
header. It applies to a nested collection **only where a `Nav@odata.nextLink` is going out** — a
truly bare `$expand` on a profile that opted into `ExpandPagingEnabled` — because trimming a
collection that gets no link is the silent truncation the framework never does.

It is clamped **down** to `MaxExpandTop` and never up: a client preference cannot lift the server's
ceiling, mirroring the root's clamp to `MaxTop`. It never lowers the ceiling either, so it can never
turn a `200` into a `400`.

The continuation route reads the same header, so a client that keeps sending it pages at its
requested size all the way down; one that stops gets `MaxExpandTop`-sized pages from there on, with
nothing skipped or repeated. `Preference-Applied` stays a single header — §8.2.8.5 makes the echo a
`MAY` and defines its value as *"the maximum page size applied"* for the whole response, so no
per-collection echo is added.

<sub>How the ceiling reached this shape — which shapes it covered at each stage, and the bounds it
used to miss — is recorded in
[design note](https://github.com/en-gen/OhData/blob/develop/docs/design/313-maxexpandtop-ceiling.md).</sub>
