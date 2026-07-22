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
            if (widget is null) return null;
            deltas.Create<WidgetDto, Widget>(delta).Patch(widget);   // DTO-delta → entity-delta → apply
            await db.SaveChangesAsync(ct);
            return widget.ToDto();
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

- a writable model property is not convention-matched, renamed, converted, or ignored;
- a rename/convert target entity property does not exist or is not writable;
- a convention or convert mapping is type-incompatible (per the policy above);
- a `.Convert(...)` converter's input type does not match the model property (do **not** cast inside
  the source selector — write `.Convert(d => d.Count, e => e.Count, c => (long)c)`, not
  `.Convert(d => (long)d.Count, ...)`);
- one model property is declared in both `.Rename(...)` and `.Convert(...)`, or two model
  properties target the same entity property (ambiguous);
- the same `(model, entity)` pair is declared more than once across all profiles.

A "writable model property" is a public instance property with both a public getter and a public
setter. Get-only computed properties are out of scope automatically and need no `Ignore()`.

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

`Create` touches only scalar/structural properties. Navigation writes stay with `$ref`,
[deep insert](deep-insert.md), or custom handler logic — nested-object mapping and implicit type
coercion are out of scope by design (that is where a full object-mapper begins). There is no
convention-based read projector; the read side already works with hand-written `.Select(...)`.

Delta mapping is dependency-free and ships in the core `OhData.AspNetCore` package.
