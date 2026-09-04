# Delta Mapping

`DeltaProfile` + `IDeltaFactory` give DTO-backed entity sets a clean **write** path — PATCH, PUT,
and POST — without AutoMapper or any other mapping dependency. You declare how a DTO/view model
maps onto its backing entity in a profile; the framework discovers, compiles, and validates every
mapping **once at startup**; and handlers consume a single injected `IDeltaFactory`.

The read direction is already covered by projection (`db.Set<Entity>().Select(e => new Dto { ... })`,
SQL pushdown intact). Projection has no inverse, so the write direction — applying a
`Delta<Dto>`'s changed properties onto an `Entity` while preserving PATCH semantics — is the gap
this fills.

## Declare — a `DeltaProfile`

Derive from `DeltaProfile` and call `For<TModel, TEntity>()` once per pair in the constructor. In
the common case (a DTO that mirrors its entity — same names, same types) that is the whole
declaration. Declare only the divergences.

```csharp
public class SalesDeltaProfile : DeltaProfile
{
    public SalesDeltaProfile()
    {
        For<WidgetDto, Widget>();                                    // Tier 1 — pure convention

        For<V2WidgetDto, Widget>()                                   // Tier 2 — only the exceptions
            .Rename(d => d.DisplayName, e => e.Name)
            .Ignore(d => d.ComputedTotal)                            // DTO-only, no entity target
            .Convert(d => d.Status, e => e.StatusCode, s => (int)s);  // explicit conversion
    }
}
```

There is no `.Build()` or finalizer — the startup scan is the finalizer, exactly like AutoMapper's
`CreateMap().ForMember()`. `For<,>()` eagerly registers the mapping and returns a mutable config;
`.Rename()`, `.Ignore()`, and `.Convert()` mutate it in place and return `this`. All selectors are
direct property accesses (`x => x.Prop`), so renames and ignores are refactor-safe.

A profile may declare many pairs, and `DeltaProfile` is not generic.

## Register

Individual registration uses the symmetric pair `AddEntitySetProfile<T>()` / `AddDeltaProfile<T>()`:

```csharp
builder.Services.AddOhData(o => o
    .AddEntitySetProfile<WidgetProfile>()
    .AddDeltaProfile<SalesDeltaProfile>());
```

> `AddEntitySetProfile<T>()` is the current name of the method previously called `AddProfile<T>()`.

Or let the existing assembly scanner discover both profile kinds in one pass — there is no separate
delta scanner:

```csharp
builder.Services.AddOhData(o => o
    .AddProfilesFromAssemblyOf<Program>());   // finds EntitySetProfile *and* DeltaProfile subclasses
```

The scan discovers concrete, non-abstract, **closed** profile types. An open generic
(`class MyProfile<T> : DeltaProfile`) is a template rather than a profile and is skipped, in either
order relative to any explicit registration: a scan followed by an explicit `AddDeltaProfile<T>()`
for a type the scan already found is a no-op, exactly as the reverse order already was. Two explicit
registrations of one type are still an error.

## Consume — one injected `IDeltaFactory`

`IDeltaFactory` is a DI singleton (mirroring AutoMapper's single `IMapper`, not a closed generic
per pair). Inject it once and call for whatever pair you need:

```csharp
public interface IDeltaFactory
{
    Delta<TEntity> Create<TModel, TEntity>(Delta<TModel> delta);   // PATCH:    delta → delta
    Delta<TEntity> Create<TModel, TEntity>(TModel model);          // PUT/POST: model → delta
}
```

`TModel` is inferable from the argument but `TEntity` (return-only) is not, so both type arguments
are given explicitly at the call site. The result is always a `Delta<TEntity>` — change-set and
updatable-property allowlist preserved — which the handler applies with the built-in
`Delta<TEntity>.Patch(entity)` and then persists.

```csharp
public class WidgetProfile : EntitySetProfile<int, WidgetDto>
{
    public WidgetProfile(AppDb db, IDeltaFactory deltas) : base(x => x.Id)
    {
        Patch = async (key, delta, ct) =>            // delta is Delta<WidgetDto>
        {
            var widget = await db.Widgets.FindAsync([key], ct);
            if (widget is null) return OhDataResult.Success<WidgetDto>(null);   // -> 404
            deltas.Create<WidgetDto, Widget>(delta).Patch(widget);   // DTO-delta → entity-delta → apply
            await db.SaveChangesAsync(ct);
            return OhDataResult.Success(widget.ToDto());
        };
    }
}
```

**The framework never applies or persists.** `IDeltaFactory` is a pure mapping service — it only
produces a `Delta<TEntity>`. The handler owns `.Patch(entity)` and persistence.

Calling `Create<,>` for a `(model, entity)` pair no profile declared throws a clear
`InvalidOperationException` ("no delta mapping registered for (Model → Entity)") at call time. The
*registration* is still fully startup-validated.

## Conversion policy — never `Convert.ChangeType` implicitly

Automatic conversion is a strict, safe subset; anything beyond it is explicit user code.

**Automatic (no declaration):**

| Case | Example |
|---|---|
| Identity — same type | `string → string` |
| Reference-assignable — `target.IsAssignableFrom(source)` | `Derived → Base` |
| Nullable-wrap — `T → T?` | `int → int?` |

**Explicit only — supply a `.Convert(...)` lambda:** narrowing, widening (`int → long`),
enum↔string, `T? → T` (null has no target), and everything else. The framework never guesses —
`Convert.ChangeType` is disqualified because it rounds/truncates silently, is culture-sensitive,
and throws at request time (defeating fail-fast). An unmapped case is a startup error, not a silent
coercion.

## Startup validation (fail-fast)

At startup (forced when `app.MapOhData()` runs) the framework walks every registered `DeltaProfile`,
resolves conventions, validates every rule, and compiles each plan once. It throws
`InvalidOperationException` if, for any mapping:

- a mappable model property is not convention-matched, renamed, converted, or ignored;
- a rename/convert target entity property does not exist or is not writable;
- a target entity property exists and is writable but `Delta<TEntity>` does not **track** it, so the
  write would be discarded at runtime (see below);
- the entity type cannot be instantiated by `Delta<TEntity>` (see below);
- a convention or convert mapping is type-incompatible (per the policy above);
- a `.Convert(...)` converter's input type does not match the model property (do **not** cast inside
  the source selector — write `.Convert(d => d.Count, e => e.Count, c => (long)c)`, not
  `.Convert(d => (long)d.Count, ...)`);
- one model property is declared in both `.Rename(...)` and `.Convert(...)`, or two model
  properties target the same entity property (ambiguous);
- the same `(model, entity)` pair is declared more than once across all profiles.

A "mappable model property" is a public instance property with a public getter that is **either**
settable **or** one `Delta<TModel>` really tracks. The second half matters: `Delta<T>` admits a
setter-less *collection* property, so `public List<int> Tags { get; }` can arrive in a
`Delta<TModel>` change set and therefore has to be mapped or `Ignore()`d like any other. Get-only
**scalar** properties are still out of scope automatically and need no `Ignore()`.

A `.Convert(...)` converter must not capture anything. It is compiled into a plan that is held for
the process lifetime, while the `DeltaProfile` that declared it is resolved in a scope disposed
immediately afterwards — so a converter closing over an injected `DbContext`, a constructor
parameter, or a local would be using stale or disposed state on every later call. It is refused at
declaration; write `static v => ...` or a static method. Delta mapping is dependency-free by design,
and a `DeltaProfile` constructor should not need injected services at all.

**The refusal is broader than "captures a dependency", and two of the shapes it catches are ones you
would not expect** (#551). A delegate is opaque, so *"captures nothing"* is the only property that
can be checked from outside it — and C# compiles a method group's receiver into the delegate exactly
as it compiles a captured local:

| converter | |
|---|---|
| `ParseSize` — a private **instance** method on the profile | **refused** |
| `s => Convert.ToInt32(s, radix)` — captures a local | **refused** |
| `s => int.Parse(s)` — non-capturing lambda | accepted |
| `static s => int.Parse(s)` | accepted |
| `ParseSize` — a **static** method | accepted |

Neither refused shape touches a dependency, and a private instance helper is arguably the tidiest
way to write a non-trivial converter — so if that is what you have, make it `static`. Note also that
a plain **non-capturing** lambda is accepted even though the exception text recommends `static`:
Roslyn compiles one to a cached fieldless singleton rather than a display class.

Each of `.Rename(...)` and `.Convert(...)` may be declared **once** per model property. A second
declaration for the same source throws rather than silently replacing the first.

### What the entity type has to satisfy

`Delta<TEntity>` is `Microsoft.AspNetCore.OData`'s type, and it keeps its own rules about what it
will track and what it can construct. Both are checked at startup, against `Delta<TEntity>` itself
rather than against a copy of its rules:

- **It must be trackable.** `Delta<T>` tracks a public instance property only when it has a public
  getter *and* a public setter (or is a collection, see below), and is not excluded by
  `[NotMapped]`, `[IgnoreDataMember]`, or being
  an unmarked property of a `[DataContract]` type. Note the last one is a **whole-type** switch: put
  `[DataContract]` on an entity and every property without `[DataMember]` stops being tracked at
  once. A mapping onto an untracked property used to compile clean and then discard the write
  silently — it is now a startup error naming the property and the attribute responsible. Remedy:
  `Ignore()` the model property, or map it onto a tracked entity property.
- **It must be instantiable.** `Delta<T>` builds an instance with `Activator.CreateInstance` on every
  `Create` call, so the entity type must be a concrete class with a **public** parameterless
  constructor. A protected/private parameterless constructor beside a public parameterized one (the
  usual EF Core shape), a positional `record`, and an abstract type all fail this — previously at
  request time, on every request; now at startup.
- **A setter-less collection target must actually be writable.** `Delta<T>` applies a write to a
  setter-less collection by clearing and refilling the instance already there, so
  `public List<int> Tags { get; }` is a legal target. An **array** is not: it has no `Clear` method
  and `Delta<T>` throws when the write is applied. The framework decides this by performing the
  write on a throwaway delta at startup, so an entity property it cannot land on is reported as
  "not writable" there rather than becoming a 500 on every request.

If a write is ever rejected at runtime despite this validation, `Create` throws
`InvalidOperationException` naming the model and entity property rather than returning a delta that
silently lost it.

## Updatable-property allowlist translation

The produced `Delta<TEntity>.UpdatableProperties` is seeded from the model-side allowlist — the
mapping's structural properties minus `Ignore()`d names — translated through the rename/convert map.
This carries immutability/security constraints across the DTO→entity boundary: an ignored or
unmapped property cannot be patched onto the entity even by a hostile request body.

## Changed-flag sugar

Expression-based, refactor-safe helpers over `Delta<T>`:

```csharp
if (delta.IsChanged(x => x.Name)) { /* the client sent Name */ }

if (delta.TryGetChanged(x => x.Price, out decimal price)) { /* price was sent */ }
```

## Scope

`Create` is intended for scalar/structural properties. Navigation writes stay with `$ref`,
[deep insert](deep-insert.md), or custom handler logic — nested-object mapping and implicit type
coercion are out of scope by design (that is where a full object-mapper begins). There is no
convention-based read projector; the read side already works with hand-written `.Select(...)`.

### What "no navigation writes" enforces, exactly

The enforcement is narrower than the intent, and the difference is worth stating rather than
discovering. A model property is refused as a navigation when it is a **collection whose element
type is a class** (`List<Order>`, `Order[]`, `IReadOnlyList<Order>`, a bare non-generic
`IEnumerable`) — with or without a setter. Collections of scalars stay mappable, and so do `string`
and `byte[]`, which are collection-shaped scalars.

A **single, non-collection reference** is *not* refused. Reflection cannot tell an EDM complex type
(structural, and legitimately mappable) from an entity type (a navigation), so
`public Customer Customer { get; set; }` on a DTO auto-maps by identity and `Patch` writes the whole
related-entity reference onto the graph. If your DTO reuses an entity type for a single reference,
`Ignore()` it — the framework will not stop you.

Delta mapping is dependency-free and ships in the core `OhData.AspNetCore` package.
