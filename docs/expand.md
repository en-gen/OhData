# `$expand` and nested paging

`$expand` embeds related entities inline in the parent response. This page covers the option itself,
the automatic SQL pushdown that serves it, nested options on an expanded collection, server-driven
paging of a nested page, and the five ceilings that bound how expensive one request may be.

The other query options are in [query options](query-options.md); how a navigation is declared in the
first place is in [navigation routing](navigation-routing.md).

Enabled via `ExpandEnabled = true`. Embeds related entities inline in the parent response:

```
GET /odata/Orders?$expand=Lines
GET /odata/Orders?$expand=Lines($select=ProductName,Quantity)
GET /odata/Orders?$expand=Lines,Customer
GET /odata/Orders(3f2a...)?$expand=Lines        ← single-entity route too
```

For a navigation **declared with a delegate**, `$expand` does **not** use EF Core's `Include()` or push the join into SQL. Instead the framework invokes that navigation's registered handler. This is a generic mechanism with no EF Core dependency, and it behaves identically on the `GetQueryable`, `GetAll`, and Priority-1 (`IODataEntitySetEndpointSource`) paths. See [navigation-routing.md](navigation-routing.md) for details. A navigation **declared without a delegate** takes a different path — SQL-JOIN pushdown — described in [Delegate-less navigations JOIN automatically](#expand-pushdown-delegate-less-navigations-join-automatically) below.

There are two ways to register the handler, and they have very different `$expand` performance:

- **Per-entity (`getAll`/`get`)** - invoked once per parent entity per expanded property. For a
  page of *N* items with *P* expanded properties, that's *N×P* sequential awaited calls (an N+1
  query pattern when the handler hits a database). Simple to write; fine for small pages or
  handlers with no per-call cost.
- **Batch (`batchGetAll`/`batchGet`)** - invoked **once per expanded property per page**,
  receiving every parent key on the page at once. *N×P* collapses to *P*. This is the
  recommended form for EF Core-backed handlers.

Navigation properties must be declared in the profile:

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        ExpandEnabled = true;

        // Batch form: ONE query loads every order's lines for the whole page.
        HasMany(x => x.Lines, batchGetAll: async (orderIds, ct) =>
        {
            var lines = await db.OrderLines.Where(l => orderIds.Contains(l.OrderId)).ToListAsync(ct);
            return lines.ToLookup(l => l.OrderId);
        });

        // Per-entity form: one query PER order (N+1 under $expand).
        HasOptional(x => x.Customer,
            get: async (orderId, ct) => await db.Customers.FindAsync([orderId], ct));

        GetQueryable = () => db.Orders;
    }
}
```

`HasMany`'s batch overload returns an `ILookup<TKey, TNavigation>` (e.g. via `.ToLookup(...)`); `HasOptional`/`HasRequired`'s batch overloads return an `IReadOnlyDictionary<TKey, TNavigation?>`/`IReadOnlyDictionary<TKey, TNavigation>`. A parent key missing from the result is treated as "no children" (`[]`) for a collection nav, or "no related entity" (`null`) for a single-valued nav.

Registering only the batch overload is enough - the framework auto-derives a single-key handler from it, so the standalone `GET /Orders(id)/Lines` route, nav `$count`, and `$ref` endpoints all keep working without writing a second handler. You may still register both explicitly (e.g. if the single-key path warrants a different query shape), in which case the per-entity handler you supply is used for those standalone routes and the batch handler is used only for `$expand`.

Restrict which navigation properties may be expanded:

```csharp
ExpandProperties(x => x.Lines, x => x.Customer);
```

Expanding a navigation property outside the allowlist returns `400 Bad Request` (`InvalidQueryOption`).

<a id="expand-pushdown-delegate-less-navigations-join-automatically-206"></a>
## `$expand` pushdown: delegate-less navigations JOIN automatically

> **The one rule to remember:** writing an expand delegate opts a navigation **out** of pushdown; a bare declaration opts it **in**.
>
> **Mental model:** write a delegate only when expansion needs real logic (filtering, ordering, authorization, a custom query shape). A plain relationship gets SQL-JOIN expansion for free.

A navigation declared **without** any expand delegate — a bare `HasMany(x => x.Lines)` /
`HasOptional(x => x.Ref)` / `HasRequired(x => x.Ref)` with no `getAll`/`get`/`batchGetAll`/`batchGet`
— is **SQL-JOIN-expandable automatically**. On the EF Core-backed `GetQueryable` path, `$expand`'ing
such a navigation folds it into the collection query's projection, so **one JOIN'd query** loads the
page and all its related rows — no delegate to write, no N+1.

The behavior is decided **purely by whether a delegate exists**. There is no global flag to flip and
no per-navigation opt-in:

| Declaration | `$expand` path | Why |
|---|---|---|
| `HasMany(x => x.Lines)` — **no delegate** | **SQL-JOIN pushdown** (one query) | There is no delegate to bypass; the `Include`/JOIN *is* the definition of the expansion. |
| `HasMany(x => x.Lines, getAll: …)` / `batchGetAll: …` — **has a delegate** | **Delegate** (never pushed down) | The delegate may filter/order/authorize; pushing it down would change results or leak rows, so it is always honored. |

A delegate-backed navigation is **never** affected by anything below — it always expands through its
delegate. Set `ExpandPushdownEnabled = false` (per profile or in `WithDefaults`) to keep every
delegate-less navigation unexpandable.

### When pushdown engages

Pushdown is **on by default** (`EntitySetDefaults.ExpandPushdownEnabled`, per-profile
`ExpandPushdownEnabled` override). It engages only on the EF Core-backed `GetQueryable` path, and
only for a related type that is either free of a back-reference cycle or itself
member-init-projectable.

A type is projectable when it has a public parameterless constructor and a public setter on every
EDM structural property. Projectable elements are materialized through a fresh POCO rather than the
EF-tracked entity, which forecloses a serialization cycle structurally — so an ordinary
bidirectional relationship (`Author.Books` / `Book.Author`) pushes down and JOINs like any other
navigation. Only a type that is **both** cyclic **and** not projectable keeps the conservative defer.

### When it does not engage, it falls back silently

Pushdown is structurally ineligible for a request when the provider is not EF Core, when the
navigation is cyclic *and* non-projectable, or when a nested option is deferred (see the table
below). In every case the delegate-less navigation simply stays EDM-only for that request and the
reason is `Debug`-logged. The fallback does not surface a `500` — the response serializes through
the same clause-bounded walker as every other path, so a reference cycle among tracked entities is
structurally unreachable however far pushdown deferred.

**"Stays EDM-only" means the framework does not itself load the navigation. It does not mean the
navigation comes back empty.** Whatever your `GetQueryable`/`GetAll` query already produced for it —
a non-EF `IQueryable`'s own eager load, an `Include` you wrote yourself, a hand-built object graph —
passes through and serializes as-is whenever the framework composes no projection of its own.
`$expand`'d navigations are never stripped regardless of how they got their data.

Two `GetQueryable`-path shapes do force it empty anyway, because a member-init projection
structurally omits any navigation it does not bind:

1. **Another navigation in the same `$expand` pushed down.** Only the structural properties and the
   navigations that engaged pushdown are bound, so a sibling that stayed EDM-only comes back empty
   even if your handler had populated it.
2. **`$select` pushdown is eligible for the request** (`$select` + `$expand`, `SelectPushdownEnabled`,
   on by default). It is not gated on an EF Core provider, so `?$select=Name&$expand=Children`
   against e.g. a `List.AsQueryable()` source still composes a member-init of only the selected and
   key properties, omitting `Children`.

Outside those two shapes, only a handler that left the navigation genuinely unpopulated reports it
empty.

### What a pushed expand changes on the wire

Every pushed-down expand — a leaf included — is materialized through a member-init projection. A
public CLR property on the related type that is **not** an EDM structural property (a `[NotMapped]`
field, a get-only computed property not derived from bound scalars, a member excluded from the model)
is therefore not materialized, and comes back as its type's default value. A computed get-only
property whose getter derives purely from bound scalars still serializes correctly, because the
scalar inputs are bound and the getter runs against the projected POCO.

One shape keeps the older behaviour entirely: a related type with a get-only EDM **structural**
property is not projectable at all, so it falls back to the bare, untransformed leaf. Nothing is
projected there, so the get-only property still serializes — but that type also does not get the
cycle fix, and a back-reference on it still defers the branch off pushdown.

### A root model that cannot be projected uses `Include` instead

If the **root** model has no public parameterless constructor, an unknowable ETag selector, or a
complex or unsettable structural member, it cannot support a member-init projection. Rather than
dropping to EDM-only, the engaged delegate-less navigations are served through EF Core's own
`Include` (resolved by reflection — this package carries no compile-time EF Core dependency),
bounded by `MaxExpandTop` exactly like the projection path, with nested `$count`/`$select`/`$top`/`$skip`
shaped afterward the same way.

A nested `$filter`/`$orderby` under this fallback is `400`, and so is a nested `$expand`/`$levels`:
a plain `Include` cannot carry them. Both messages point at making the root model projection-eligible
or writing an expand delegate for that navigation. A leaf expand whose related type has a
back-reference — to the root, a sibling leaf, or itself — is served rather than rejected.

### A translation failure fails loud

If a nested `$filter`/`$orderby` cannot be bound, or the composed query cannot be translated by the
provider even though everything looked eligible, the request returns `400` (`InvalidQueryOption`)
rather than silently degrading to EDM-only under a `200`. Simplify the nested option combination, or
write an expand **delegate** for that navigation to take full control of its query shape.

One residual gap: a cycle closed by an entity-typed CLR property that is **not** an EDM navigation
(excluded from the model entirely) is outside what the serialization walker bounds, and still
surfaces as a `500`. By that point the query itself already succeeded, so this is the one case where
failing loud means an actual server error rather than a `400`.

### Composing with `$select` pushdown

`$expand` pushdown composes with `$select` pushdown: `?$select=name&$expand=Lines` prunes the
parent's column list *and* JOINs the lines in the same single query. The two are **independent** —
disabling `SelectPushdownEnabled` does not disable `$expand` pushdown, and an `$expand` push never
column-prunes the parent on its own.

<sub>Why the eligibility rules are what they are, and the cycle classes that shaped them, is recorded
in [design note](https://github.com/en-gen/OhData/blob/develop/docs/design/323-expand-pushdown-eligibility.md).</sub>

### Nested options bypass the parent's property allowlists

`FilterProperties`/`OrderByProperties`/`SelectProperties` restrict the **root** entity set only. A
navigation-target type has no allowlist surface of its own and is treated as fully queryable — the
same decision that makes nav-path `$filter` work — so `$expand=Children($filter=…)`, `($orderby=…)`
and `($select=…)` may reference any column of the child type regardless of what the parent restricted.

Model your navigation targets accordingly: do not expose a sensitive column on a type reachable
through a delegate-less navigation you allow `$expand` on, or write an expand **delegate** for that
navigation, which opts it out of pushdown and lets you enforce your own shaping.

### Nested options on a pushed `$expand`

A pushed (delegate-less) `$expand` honors the nested options of the expanded collection. `$filter`, `$orderby`, and `$top`/`$skip` are pushed down to SQL as a **filtered / ordered / paged `Include`** (translated by Microsoft's own OData `FilterBinder`/`OrderByBinder`, so the semantics match a top-level `$filter`/`$orderby`), producing a single JOIN'd query — no per-parent N+1. `$count` and `$select` are then applied to the serialized result (in whatever naming policy is configured — PascalCase by default).

| Nested option (on a delegate-less pushed nav) | Supported | How |
|---|---|---|
| `$select` — `Children($select=name)` | ✅ | JSON projection of the expanded elements (configured naming policy preserved) |
| `$filter` — `Children($filter=active eq true)` | ✅ | filtered `Include` (SQL `WHERE` in the JOIN) |
| `$orderby` — `Children($orderby=name desc)` | ✅ | ordered `Include` (SQL `ORDER BY` in the JOIN) |
| `$top` / `$skip` — `Children($orderby=name;$top=5)` | ✅ | paged `Include` (SQL `ROW_NUMBER` window); `$top` is capped by [`MaxExpandTop`](#complexity-limits) when that is set (it defaults to no ceiling) |
| `$count` — `Children($count=true)` | ✅ | inline `Children@odata.count` = full filtered count (paging is applied after counting, per §11.2.5.5); bounded by [`MaxExpandTop`](#complexity-limits) when that is set (it defaults to no ceiling) |
| **nested `$expand`** — `Children($expand=Grandkids)` | ✅ | multi-level pushdown: folded into the same query as an `Include`→`ThenInclude` JOIN when every level is delegate-less (see [Multi-level `$expand`](#multi-level-expand-and-levels) below) |
| `$levels` — `Children($levels=2)` / `Children($levels=max)` | ✅ | recursive self-referential expand, bounded by `MaxExpansionDepth`; may carry `$filter`/`$orderby`/`$skip`/`$top`/`$count`/`$select`, applied at **every** level (see below) |
| `$search` / `$compute` / `$apply` | ❌ (deferred) | not implemented on the pushdown path |

A deferred nested option is not an error: the request still returns `200`, but the delegate-less navigation that carried it stays EDM-only for that request — the framework doesn't load it via pushdown, though whatever the handler's own query already populated (or didn't) is what serializes (see the caveat above). Nested options on a **delegate-backed** navigation follow the delegate path and are subject to that path's own support (see [navigation-routing.md](navigation-routing.md)); they never engage pushdown. <a id="nested-options-on-a-delegate-backed-navigation"></a>
#### Nested options on a delegate-backed navigation

**Nothing is silently dropped there.** Every nested option a delegate-backed navigation cannot honour is rejected with `400` (`InvalidQueryOption`), naming the option and the remedy:

| Nested option (on a **delegate-backed** nav) | Answer |
|---|---|
| `$filter` / `$orderby` | ✅ applied ([#650](https://github.com/en-gen/OhData/issues/650)) — bound by the same `FilterBinder`/`OrderByBinder` as the pushdown path, executed in memory over the children the delegate returned |
| `$top` / `$skip` | ✅ applied — windowed after the count, per §11.2.5.5 |
| `$count` | ✅ applied — counted after `$filter` and **before** `$top`/`$skip` (§11.2.5.5) |
| nested `$expand` **beneath a raw-served parent** | `400` — that branch does not recurse, so the navigation's handler never runs and there is nothing to shape |
| `$select` | ✅ applied — JSON projection of the materialized children |
| nested `$expand` | resolved through the delegate path, level by level |
| multi-level `$levels` | `400` |

`$filter`/`$orderby`/`$count` are **applied**, not merely permitted. Through 1.7.0 they were silently dropped — `Children($filter=…)` returned the *unfiltered* collection, which a client cannot distinguish from a filter that matched everything. They are bound with Microsoft's own `FilterBinder`/`OrderByBinder`, the same call the pushdown path makes, so a clause means the same thing whichever way the navigation is declared.

**One difference is worth knowing.** The pushdown path executes the clause as SQL; this path executes it in memory over what the delegate returned, so string comparison, null ordering and culture follow the CLR rather than the database's collation. That is the same divergence `Microsoft.AspNetCore.OData` has when `[EnableQuery]` runs over an in-memory source, and it is why this path binds with `HandleNullPropagation` **on** where the SQL path has it off — LINQ-to-Objects would otherwise dereference and throw where SQL evaluates `NULL` to "no match".

`$top`/`$skip` are applied too, and **after** the count — §11.2.5.5 makes `Nav@odata.count` the count of the collection after `$filter` and *before* the window, so counting a windowed array would report the page size as the total. `MaxExpandTop` is still **not** imposed on this path: bounding a delegate's answer behind its back remains out of scope, and only the window the *client* asked for is applied.

Two shapes still answer `400`, and both are cases where there is genuinely nothing to shape: a clause the binders cannot bind at all, and a navigation expanded **beneath a raw-served parent** — that branch does not recurse, so the rows come out of the parent's own materialized graph and this navigation's handler never runs. Loud either way, never dropped. The status is `400` and not `501` because a profile setting does make such a request succeed (declare the navigation delegate-less); `Microsoft.AspNetCore.OData` answers an unhonourable nested option the same way (`SelectExpandQueryValidator` throws, `EnableQueryAttribute` converts to a bad request). This applies to any delegate-backed navigation under `$expand`, self-referential or not (see `SelfReferentialNavMaxTopTests.cs`, `BatchExpandTests.cs` and `Issue650NestedOptionsOnDelegateNavTests.cs`).

<a id="multi-level-expand-and-levels-206"></a>

### Multi-level `$expand` and `$levels`

A nested `$expand` is pushed **recursively**: `?$expand=Books($expand=Chapters($expand=Pages))` folds all three levels into one JOIN'd query (EF Core `Include`→`ThenInclude`), applying each level's own nested `$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$select`. A branch is pushed only when it is **delegate-less at every level**; the moment a level's navigation carries a delegate (or is cyclic / a non-projectable type), that whole branch is deferred off pushdown and resolves through the existing path — a **delegate-backed navigation is never EF-included at any depth**, so the delegate is never bypassed. A delegate-backed navigation reached directly from the root (or under delegate-backed ancestors) still expands through its delegate exactly as before; a delegate navigation nested *beneath* a delegate-less parent is **never JOIN-loaded and its delegate is never invoked** — but, exactly as for any deferral, that is not a guarantee of emptiness: if the parent handler's own query populated it (an `Include`/`ThenInclude` it wrote, or a hand-built graph), it serializes as-is.

`$levels=N` recursively expands a **self-referential** navigation (a tree/hierarchy) `N` levels deep — `?$expand=Children($levels=2)` — as a bounded, cycle-free projection (each level is a fresh POCO; the deepest loaded level terminates the recursion). `$levels=max` resolves to the configured `MaxExpansionDepth`. Both are capped at `MaxExpansionDepth`: a `$levels` (or a nested `$expand`) that resolves deeper is rejected with `400` before any handler runs (see [Complexity limits](#complexity-limits)).

**`$levels` off the pushdown path.** The recursion above is the EF-pushed one. On a
**delegate-less** navigation served from the handler's own graph — `GetAll`, Priority-1, a non-EF
`IQueryable`, `GET /{Set}({key})`, or a branch the pushdown declined — `$levels=N` now serves the
same `N` levels the explicit nested spelling does, read straight off that graph: `?$expand=Children($levels=2)`
and `?$expand=Children($expand=Children)` return byte-identical responses. On a **delegate-backed** navigation a `$levels` resolving to more
than one level is rejected with `400` (`InvalidQueryOption`) instead: the delegate loads a single level
and there is no settled rule for which delegate governs level 2 on that substrate (the pushed path
deliberately stays on the URL-named set all the way down, while Model B resolves depth ≥ 2
from the exact-EDM-type union — both frozen by design), so the framework says so rather than truncating. Spell
the depth out with nested `$expand`, or declare the navigation delegate-less. `$levels=1` is unaffected
anywhere: it restates a bare `$expand` and is served as one.

A `$levels` expand may **also carry `$filter`, `$orderby`, `$skip`, `$top`, `$count`, and `$select`**. Those options apply **at every level of the recursion**, not just the first — the semantics Microsoft's own OData stack implements (`$levels=N` is rewritten into `N` nested expands each carrying the same options) and the reading the spec's equivalence example implies. So `?$expand=Children($levels=2;$filter=active eq true)` prunes inactive nodes at both levels (an inactive node's whole subtree disappears with it), `($levels=2;$count=true)` emits `Children@odata.count` on every level, and `($levels=2;$select=name)` keeps the self-navigation itself at every level while pruning the other properties.

**A nested `$top`/`$skip` on a `$levels` expand.** What happens depends on whether the navigation is
delegate-backed:

- On a **delegate-less** navigation the window is applied in the JSON pass rather than pushed to SQL
  — the same `APPLY`/`LATERAL` translation problem the ceiling caveats below describe — so
  `?$expand=Children($levels=2;$top=1)` windows to one child **at every level** of the recursion.
- On a **delegate-backed** navigation a nested `$top`/`$skip` is rejected with `400`
  (`InvalidQueryOption`), because the delegate returns its full per-parent answer and nothing
  downstream re-windows it.

That rejection is resolved from the navigation's **Model B treatment** — serve-raw, run-the-delegate,
or blank, decided from the declarations of every entity set exposing the type at that level
([#293](https://github.com/en-gen/OhData/issues/293)) — not from which navigation the expansion walker
happens to reach, so it does not depend on how the navigation was arrived at:

- It fires on a **blanked** navigation too — one whose candidate entity sets disagree about whether it
  is delegate-backed — with a message naming that disagreement instead of a delegate. A blanked
  navigation is emptied outright, so no window could be applied to it either.
- It fires on a navigation reached **only through a delegate-less parent's already-materialized
  graph**, at any depth.
- It does **not** fire on a serve-raw navigation, which is exactly the case where the window *is*
  applied. A branch is SQL-pushdown-windowed only when every level of it is serve-raw.

One residual, deliberately unchanged: a serve-raw navigation whose branch was never pushed down at all
— an in-memory `GetAll` source, a non-EF `IQueryable`, or a branch deferred off pushdown for a
structural reason — still ignores its nested `$top`/`$skip` silently. Rejecting it would make the
answer depend on whether pushdown happened to engage, which is an internal optimisation decision
invisible to the client, and would turn requests that are honoured today into `400`s.

**A plain (non-`$levels`) `$expand=Children($top=…)`** on a delegate-less self-referential navigation
windows to one child as asked: a self-referential related type is projectable, so it engages pushdown
without needing `$levels`. The projected elements are leaves — the self-navigation on each is not
itself bound, consistent with the leaf-projection wire change above — so the result stays finite
without `$levels`' explicit termination. `?$expand=Children($levels=2;$orderby=name desc;$skip=1)`
windows deterministically at every level, and the other options are unaffected.


The one combination still **deferred** off pushdown is a `$levels` expand carrying its **own nested `$expand`** (`Children($levels=2;$expand=Tags)`): depth accounting between the `$levels` budget and the nested branch's own remaining depth is ambiguous against `MaxExpansionDepth`. As with any deferral the request still returns `200`; the navigation just stays EDM-only for that request — not guaranteed empty, per the caveat above.

The ceiling is advertised in `$metadata` as the `Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels` annotation on each entity set, so a client can discover it before issuing a request.

## Nested server-driven paging (`ExpandPagingEnabled`)
A bare `?$expand=Nav` whose related collection exceeds `MaxExpandTop` can be served as its first
`MaxExpandTop` children plus a `Nav@odata.nextLink` continuation, instead of being rejected with
`400`. This is **off by default** and needs **two** settings, both of them yours to make.

### The two knobs, and how they interact

| `MaxExpandTop` | `ExpandPagingEnabled` | What an over-large bare `$expand` does |
|---|---|---|
| unset (`null`, the default) | `false` (the default) | Returns the **whole** related collection. No bound, no `400`, no link. A startup `Warning` names each navigation in this state. |
| unset (`null`) | `true` | Identical to the row above — the flag is **inert without a ceiling**: no route is registered, no link is emitted, and there is no boundary at which a continuation could begin. |
| set to `N` | `false` | `400 InvalidQueryOption` — *"the related collection exceeds the maximum of N entities. Narrow it with a nested `$filter`."* |
| set to `N` | `true` | `200` with the first `N` children and a `Nav@odata.nextLink` — **but only for a truly bare `$expand`**. Every other over-ceiling shape keeps the `400` from the row above. |

`MaxExpandTop` is **also the page size**, for the first page and every continuation alike. There is
deliberately no second page-size knob: a number you have no basis on which to pick is the mistake
that removed `MaxExpandTop`'s own `1000` default, and a second one would need disambiguating at four
enforcement sites. The page size is **never** `MaxTop` — that is an independent knob with its own
default, and paging the continuation at it would serve `MaxExpandTop` rows on page 1 and `MaxTop`
rows on page 2, or (with `MaxTop = null`) an unbounded page 2.

`ExpandPagingEnabled` is a separate opt-in from the ceiling, not a refinement of it, because **a
continuation link is worse than a `400` for a client that does not read nested annotations** — that
client sees a complete-looking collection that has been silently truncated, with no error to notice.
Only turn it on if you know your clients follow `Nav@odata.nextLink`.

**OhData's own first-party [`OhData.Client`](client/index.md) does read this link**, through the
annotation-preserving terminal operations added in the same cycle:
[`ToAnnotatedPageAsync`](client/terminal-operations.md#annotation-preserving-reads),
`ToAnnotatedAsyncEnumerable` and `GetAnnotatedAsync` return entries exposing
`NextLinkFor(x => x.Nav)` and `CountFor(x => x.Nav)`. So a nested continuation is fully consumable
end to end by the first-party client — server emission and client read are covered together by
`ExpandPagingSeamTests`.

The caveat that remains true is narrower, and it is about **which call you make**, not about which
client you use:

- The **ordinary** read path still drops annotations. `ToListAsync`, `ToPageAsync`, `ToAsyncEnumerable`
  and `GetAsync` bind the envelope only, so a paged nested collection looks complete through them.
  Preserving annotations costs a buffered body and a second read of it, which is why it is a separate
  method rather than a client-wide default. Reach for the `Annotated` counterpart whenever a query
  carries `$expand` against a server with this knob on.
- A **third-party** client that ignores unknown annotations sees a complete-looking collection that
  has been silently truncated, with no error to notice. That is the failure mode this opt-in exists to
  keep you from causing by accident, and it is unchanged.

### The pageable set is exactly "a truly bare `$expand`"

**One shape pages: `$expand=Nav`, carrying no nested options at all.** The rule, stated once: *a
nested option list that normalizes to the identity transform is bare; anything else is not.* Only
two no-ops survive the parser, and both count as bare —

| Shape | Answer over the ceiling | Why |
|---|---|---|
| `$expand=Books` | **pages** | the case this feature is about |
| `$expand=Books($skip=0)` | **pages** | `$skip=0` is the identity; the continuation is still a faithful `?$skip={cap}` |
| `$expand=Books($count=false)` | **pages** | `$count=false` and an absent `$count` are already the same value |
| `$expand=Books($top=0)` | `200`, `[]`, **no link** | the client asked for zero rows and got zero rows — the response is complete with respect to the request |
| `$expand=Books($top=N)`, `N ≤ cap` | `200`, **no link** | same reasoning; an explicit `$top` wins over the default bound |
| `$expand=Books()` | `400` | rejected by the OData URI parser before OhData sees it — *"Missing expand option on navigation property 'Books'"* |
| `$expand=Books($filter=…)` / `($orderby=…)` / `($select=…)` | `400` | a `$skip`-only link cannot carry a nested option, so hop 2 could not reproduce hop 1 |
| `$expand=Books($skip=N)`, `N > 0` | `400` | same: the offset is already in play and the link carries only `$skip` |
| `$expand=Books($count=true)` | `400` | §11.2.5.5 requires a count to be *"the total count of results across all pages"*, i.e. the **full filtered** count; a paged collection cannot report one. `Nav@odata.count` and `Nav@odata.nextLink` therefore never coexist |
| `$expand=Books($expand=Chapters)` | `400` | a level with children is not SQL-bounded at all (`APPLY`/`LATERAL`); the rows were already fully materialized, so a link would advertise a bound that does not exist |
| `$expand=Nav($levels=N)` | `400` | same, at every level |
| a nav whose element type has a composite or unresolvable key | `400` | no single key ⇒ no total order ⇒ no sound `$skip` walk |
| depth ≥ 2 — the leaf under `$expand=Books($expand=Chapters)` | `400` | see [Deliberate limits](#deliberate-limits) |
| a nav **this profile** declares with a delegate | `400`, and no route | delegate safety; see below |

That is the whole matrix, and it **fails closed**: over the ceiling, a shape either pages or `400`s.
There is no third answer and no commit at which a bound existed without one or the other, so silent
truncation never occurs.

### The continuation

```jsonc
// GET /odata/BeAuthors?$filter=Id eq 1&$expand=Books      (MaxExpandTop = 3, ExpandPagingEnabled = true)
// 200
{
  "@odata.context": "http://localhost/odata/$metadata#BeAuthors",
  "value": [
    {
      "Id": 1, "Name": "Ann", "PublisherId": 100,
      "Books": [
        { "Id": 1, "AuthorId": 1, "Title": "Bk1" },
        { "Id": 2, "AuthorId": 1, "Title": "Bk2" },
        { "Id": 3, "AuthorId": 1, "Title": "Bk3" }
      ],
      "Books@odata.nextLink": "http://localhost/odata/BeAuthors(1)/Books?$skip=3"
    }
  ]
}
```

```jsonc
// GET /odata/BeAuthors(1)/Books?$skip=3
// 200
{
  "@odata.context": "http://localhost/odata/$metadata#BeAuthors(1)/Books",
  "value": [
    { "Id": 4, "AuthorId": 1, "Title": "Bk4" },
    { "Id": 5, "AuthorId": 1, "Title": "Bk5" }
  ]
}
```

Follow it to exhaustion the way you would any server-driven page. The continuation emits its own
envelope-level `@odata.nextLink` (at the absolute offset `$skip + MaxExpandTop`) while rows remain,
and omits it on the last page — a page that is exactly `MaxExpandTop` long is **not** assumed to have
more behind it, the same one-row probe the root path uses.

Four properties of that route worth knowing:

- **It accepts `$skip` and nothing else.** Every other system query option returns
  `400 UnsupportedQueryOption` — including `$select`/`$orderby`/`$top`/`$count`, which the
  *delegate-backed* [navigation route](navigation-routing.md) on the same URL shape does accept.
  There is nothing to carry: the link is only ever emitted for an expand that had no nested options at all. Rejection is
  by the `$` sigil rather than a name allowlist, so a future OData system option this build has never
  heard of is refused rather than silently ignored. This route's sigil check was the precedent
  [#359 generalised to every read route](unsupported-query-options.md),
  and it now shares that matcher rather than carrying its own copy.
- **`$format` is the one exemption, and it is not a data option.** §11.2.10 content negotiation is
  implemented once, on the group filter that wraps the whole OData surface, so `$format` never
  reaches this handler and cannot change a single row. Refusing it would make this the only route in
  the surface that `400`s a conformant, already-supported option, and would break the common client
  habit of appending it to a server-issued link. An unsupported `$format` **value** is still
  rejected, by that same group filter, unchanged.
- **It is ordered by the child key, unconditionally.** Not through the root path's
  `EnsureStableOrder`, which skips appending the key when the source is already ordered and would
  leave a pre-ordered parent's continuation without a total order. The key comes from the same
  resolution that composes the first page's tiebreaker, so both sides agree on the ordering column by
  construction. The emitted plan is an `INNER JOIN … LIMIT/OFFSET` index seek, not the partitioned
  `ROW_NUMBER()` window the first page uses.
- **It composes off the parent profile's own `GetQueryable`**, so a tenant filter or soft-delete
  predicate baked into that queryable scopes the continuation exactly as it scoped the first page,
  and the route requires no foreign-key knowledge (which the convention EDM does not have). Profile
  authorization applies to it as to every other route on the set.

The link's parent key is read from the **CLR entity**, never from the response JSON — a root
`$select` strips the key before the shaping pass runs, so `?$select=Name&$expand=Books` still emits
`"Books@odata.nextLink": ".../BeAuthors(1)/Books?$skip=3"` with a payload containing no `Id` at all.

Root paging and nested paging coexist without interacting. The root's continuation is a
`$skiptoken` on the collection route; the nested one is a plain `$skip` on a different path served by
a different route that has no `$skiptoken` concept. Neither link builder reads the response body, so
neither can rewrite the other, and a parent appearing on root page 2 gets its own independent child
links.

One **new startup failure**, and it can only fire on a registration that opted in: an entity-level
bound function sharing a name with a pageable navigation now throws from `MapOhData()`. Both would
claim `GET /{Set}({key})/{Name}`, and the pre-existing collision check compares bound functions
against structural properties only — which excludes declared navigations — so that pairing was legal
until this route existed. (The same check still does not cover a bound function colliding with a
**delegate-backed** navigation route; that collision is tracked in
[#416](https://github.com/en-gen/OhData/issues/416).)

### Deliberate limits

These are decisions, not gaps waiting to be filled. What each one means in practice:

- **A continuation for a parent key that does not exist returns `200` with an empty `value` and no
  link**, where `Microsoft.AspNetCore.OData` returns `404`. A documented divergence: the
  continuation cannot tell "no such parent" from "a parent with no children". The delegate-backed
  navigation route on the same URL shape *does* return `404`, because a handler is there to ask.
- **Depth ≥ 2 stays `400`.** `$expand=Books` pages; `$expand=Books($expand=Chapters)` does not, at
  either level.
- **Delegate-backed navigations stay unbounded.** A navigation declared with a handler gets no
  ceiling, no bound and no link; a nested `$top`/`$skip` on one is already `400`. A delegate is
  where you own the size of your own answer.
- **Delegate safety is the declaring set's own declaration.** A *sibling* profile declaring the same
  navigation with a delegate does not suppress paging on the delegate-less set.
- **`Prefer: odata.maxpagesize` is honoured on the nested page size.** It **narrows** the nested
  page and is clamped down to `MaxExpandTop`, never up. It applies only where a continuation link is
  actually going out; a non-pageable over-ceiling shape keeps its `400` and ignores the header. Both
  spellings are accepted (`odata.maxpagesize` is the 4.0 name, `maxpagesize` the 4.01 rename).
- **The continuation *link* is for a pushed expansion only; the *ceiling* applies to every
  raw-served one, at every level, as a `400`.** With `MaxExpandTop` set, a `GET /{Set}({key})`, a
  `GetAll`, a Priority-1 or a non-EF `GetQueryable` read whose expanded collection exceeds it
  returns `400` (`InvalidQueryOption`). `ExpandPagingEnabled` buys nothing on those. Note what the
  `400` does **not** buy: the collection was loaded by your own handler before the framework saw it,
  so this is a data ceiling, not a materialization bound. Size the eager loads in your
  `GetById`/`GetAll` accordingly — a `GetById` that does not `Include` the navigation serves `[]`
  and never trips it.

The reasoning behind each limit, and the alternatives measured and rejected, is recorded in
[design note](https://github.com/en-gen/OhData/blob/develop/docs/design/313-nested-expand-paging-limits.md).

---

## Complexity limits

Five ceilings bound how expensive a single request's query options may be, `MaxExpandTop` among
them. They have their own page: **[complexity limits](complexity-limits.md)**.
