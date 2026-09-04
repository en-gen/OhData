# Polymorphic entity sets (TPH inheritance)

An entity set whose model type has derived types — EF Core's table-per-hierarchy (TPH) mapping being
the common case — is served differently from a flat one, in ways worth knowing before you model it.
Everything below was measured against EF Core 10; where a number or a payload is quoted, it came from
a running server, not from reading the code.

```csharp
public class Award                        // the entity set's declared type
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<AwardNomination> Nominations { get; set; } = new();
}

public class AcademyAward : Award         // derived
{
    public string Ceremony { get; set; } = "";
    public bool IsWinner { get; set; }
}
```

The derived types must be **in the EDM**, which for the EF-backed path means naming them in
`OnModelCreating` (`modelBuilder.Entity<AcademyAward>()`). A CLR type that inherits but is not in the
model is invisible to everything below.

## What the wire looks like

A derived row carries its own properties **and** an `@odata.type` annotation naming its type. A row of
the *declared* type carries neither:

```json
GET /v2/Awards
{
  "@odata.context": "…/$metadata#Awards",
  "value": [
    { "Ceremony": "67th Academy Awards", "IsWinner": true, "Id": 1, "Name": "Best Picture",
      "@odata.type": "#OhData.TestBench.AspNetCore.AcademyAward" },
    { "Festival": "Cannes", "Jury": "Clint Eastwood", "Id": 2, "Name": "Palme d'Or",
      "@odata.type": "#OhData.TestBench.AspNetCore.FestivalAward" },
    { "Id": 3, "Name": "Audience Choice" }
  ]
}
```

The third row is a plain `Award`, and its absent annotation is deliberate. JSON Format §4.5.3 requires
`odata.type` when *"the type is derived from the type specified for the (collection of) entities"* —
a row of the declared type is not, and the client determines it from `@odata.context`. §3.1.1 then
asks a minimal-metadata service to omit control information the client can compute.
`Microsoft.AspNetCore.OData` draws the same line.

The annotation survives `$select` (it is control information, not a property) and appears on the
single-entity read and on expanded navigation values, not only on collection rows.

`$metadata` declares the hierarchy, so a client can resolve those type names:

```xml
<EntityType Name="AcademyAward" BaseType="OhData.TestBench.AspNetCore.Award"> …
<EntityType Name="FestivalAward" BaseType="OhData.TestBench.AspNetCore.Award"> …
```

## How the query is built, and why it differs

For a flat model, an `$expand` is folded into a **member-init projection** over the model type — one
`Select` that EF translates into a single query. A member-init can construct nothing but the type it
names, so on a hierarchy every row would materialize as the base and the derived properties would
vanish. Measured before this was fixed: `GET /Things` emitted
`SELECT Id, Discriminator, Name, Extra, Rank` while `?$expand=Children` emitted
`SELECT t0.Id, t0.Name, c.Id, c.BaseId, c.Body` — the discriminator not even selected, so EF could not
have materialized the derived type even if the projection had wanted to.

So a **polymorphic root is refused the projection** and served through EF Core's `Include` instead,
which loads real entities and preserves each row's runtime type. That is invisible from the outside
except in the ways below.

### What works

| | |
|---|---|
| `$filter`, `$orderby`, `$select`, `$top`, `$skip`, `$count` | unchanged |
| `$expand=Nav` | yes — a filtered `Include` |
| `$expand=Nav($filter=…;$orderby=…;$top=…)` | yes, pushed to SQL |
| `$expand=Nav($expand=Deeper)` | yes — chained `ThenInclude` |
| sibling nested expands, `$expand=Nav($expand=A,B)` | yes |

### What does not

**`$levels` is refused with `400`.** Its depth is decided per request by the data, so there is no
statically-known nesting to build an `Include` chain from — and the projection path serves it by
emitting a `Select` per level, which a filtered `Include` does not accept. The error names `$levels`
specifically.

**A very deep nested `$expand` may hit a provider limit** that the projection path would not. A
windowed parent collection beside a nested collection requires `SQL APPLY`, which SQLite cannot emit;
OhData avoids generating that shape (a level with children composes no SQL window, and its paging
moves to the JSON pass), so you should not meet it — but if a nested combination fails to translate,
the error says so rather than returning wrong rows.

## Known gap: OpenAPI does not model the hierarchy

The generated OpenAPI document describes the **base type only** — no `allOf`, no `discriminator`, and
no schema for the derived types. A client generated from it deserializes an `AcademyAward` row into an
`Award` and silently drops `Ceremony`/`IsWinner`.

`$metadata` is correct, so an OData-native client is unaffected; this is specific to the OpenAPI
companions. Tracked in [#626](https://github.com/en-gen/OhData/issues/626).

## Authorization

A navigation into a polymorphic set is authorized by the profile that **declares** it, exactly as for
a flat model — see [authorization.md](authorization.md#authorization-is-per-profile-and-does-not-compose-across-a-navigation).
Inheritance does not change that: there is no per-derived-type rule.

## Try it

The TestBench ships a polymorphic set at `/v2/Awards` — `AcademyAward`, `FestivalAward`, and a plain
`Award`, so a single page mixes all three shapes. `src/OhData.TestBench.AspNetCore/Models.cs` carries
the model and the reasoning behind the (deliberately unidirectional) navigation.
