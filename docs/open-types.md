# Open Types (dynamic property bags)

An OData **open type** carries caller-supplied properties that are not declared in the EDM. OhData
supports this on **complex types**: give the type an `IDictionary<string, object?>` member and its
entries are written and read as **siblings** of the declared properties — never nested under the
member's own name. This is **on by default**; `WithOpenTypes(false)` is the escape hatch.

**No attributes. No model changes.** Support is driven entirely by the EDM, so a model published
in a shared contract package works as-is:

```csharp
public record ExternalReference
{
    public Guid Id { get; set; }
    public string Source { get; set; } = "";
    public string Xref { get; set; } = "";
    public ExternalReferenceMetadata? Metadata { get; set; }
}

public record ExternalReferenceMetadata
{
    public IDictionary<string, object?>? KeyValuePairs { get; set; }
}
```

```csharp
// Nothing to enable — a complex type with a dictionary member is an open type.
services.AddOhData(o => o
    .AddEntitySetProfile<ExternalReferenceProfile>());
```

```jsonc
// GET /odata/ExternalReferences
{
  "@odata.context": "…/$metadata#ExternalReferences",
  "value": [
    {
      "Id": "1111…",
      "Source": "LeaseAccounting",
      "Xref": "xref-1",
      "Metadata": { "organizationCreatedDate": "2026-01-01T00:00:00.0000000+00:00", "tier": 3 }
    }
  ]
}
```

`KeyValuePairs` appears nowhere on the wire, and nowhere in `$metadata`.

## On by default — and why

A complex type with a dictionary member **is** an open type, and the CSDL OhData emits has always
said so: `ODataConventionModelBuilder` marks the type `OpenType="true"` and omits the member from
the declared properties whether or not the flat wire shape is active. Leaving the payload nested
made `$metadata` and the body disagree, and made the conformant behaviour something you had to know
the spec to ask for.

It also put OhData at odds with the ecosystem. `Microsoft.AspNetCore.OData`'s
`ODataResourceSerializer.AppendDynamicProperties` reads the **same**
`DynamicPropertyDictionaryAnnotation` and appends dynamic properties flat, with no opt-in flag
anywhere in that path: annotation present ⇒ flat. OhData now auto-maps per the spec; you write a
declarative profile.

### If you are upgrading, read this

`WithOpenTypes(false)` restores the pre-#389 shape, in which the container is an ordinary nested
declared property:

```csharp
services.AddOhData(o => o
    .WithOpenTypes(false)
    .AddEntitySetProfile<ExternalReferenceProfile>());
```

**Does this affect me?** Ask one question: *do any of your complex types have an
`IDictionary<string, object>` member?* If none do, this setting is inert in either position — the
registration's serializer options are not even derived, no write body is walked, nothing is logged,
and every response including error responses is byte-identical to a pre-#389 build. If any do, read
on. **You do not have to work this out by hand: `MapOhData()` logs one warning per affected complex
type at startup, naming the CLR type and the container member.**

Once the container is extension data it is no longer a *declared* property. An existing client body

```jsonc
{ "Metadata": { "KeyValuePairs": { "a": 1 } } }
```

stops binding `{"a":1}` to the `KeyValuePairs` property and starts binding a **dynamic key literally
named `KeyValuePairs`** whose value is that dictionary. The handler then persists
`KeyValuePairs = { "KeyValuePairs": {"a":1} }`.

**The response echo of that mis-bound value is byte-identical to the correct one.** This is why the
startup warning exists rather than a line in the release notes: an ordinary breaking change shows up
in your tests or in a staging response diff, and this one shows up in neither. The wire looks right
while the stored shape is wrong. Migrate clients and stored data deliberately.

## How the container is discovered

`ODataConventionModelBuilder` — the builder OhData already uses to produce the EDM — infers a
dynamic-property container from an `IDictionary<string, object>`-assignable member. It marks the
containing type `OpenType="true"`, omits the member from the declared properties, and records the
backing `PropertyInfo` on the model as a `DynamicPropertyDictionaryAnnotation`.

OhData reads that annotation back at `MapOhData()` and marks exactly that member as
`System.Text.Json` extension data on the registration's `JsonSerializerOptions`. **The same
registration that produces the CSDL produces the wire shape**, so the two cannot drift, and the
CLR model needs no `[JsonExtensionData]` (or any other) attribute. Nothing is matched by property
name or by convention.

```xml
<ComplexType Name="ExternalReferenceMetadata" OpenType="true" />
```

The container must have **both a getter and a setter** — `System.Text.Json` populates it on read
and enumerates it on write. The idiomatic collection-initializer shape

```csharp
public IDictionary<string, object?> Bag { get; } = new Dictionary<string, object?>();   // ✗
```

is inferred as a container by the model builder but cannot be bound into, so `MapOhData()` throws
and names the member. Give it a setter:

```csharp
public IDictionary<string, object?>? Bag { get; set; }                                   // ✓
```

## What is supported

| Surface | Behavior |
|---|---|
| `GET /Set` and `GET /Set({key})` | Dynamic keys flat, alongside declared properties |
| `GET /Set({key})/{ComplexProp}` and `$expand` targets | Same |
| `POST` / `PUT` | Undeclared keys inside a complex value bind into the dictionary and reach the handler |
| `PATCH` | Same — but the complex value is **replaced**, not merged; see below |
| `PUT`/`PATCH /Set({key})/{ComplexProp}` | Same (the whole complex value is replaced) |
| `$select=<ComplexProp>` | The container is preserved in full — dynamic keys are not stripped |
| `$metadata` | `OpenType="true"`; container omitted from the declared properties |
| Inheritance | A derived complex type inherits its base's container |

Values arrive in the dictionary as `JsonElement` (System.Text.Json's representation for an
`object`-typed slot), which is what a handler should expect when reading them.

**The container is `null`, not empty, when a body carries no undeclared keys.** `System.Text.Json`
only materialises the extension-data dictionary once the first unmatched member arrives, so a
`POST` of `{"Metadata": {}}` leaves `KeyValuePairs` `null`. Every bag read in a handler needs a null
check:

```csharp
object? tier = entity.Metadata?.KeyValuePairs?.GetValueOrDefault("tier");
```

## Dynamic property names are validated on write

A dynamic key is persisted verbatim and echoed on every later read, so an unconstrained one is a
*stored* fault against other consumers rather than a one-request nuisance: `@odata.type` inside a
complex value is what a conforming reader (`Microsoft.OData.Client` among them) uses to resolve that
value's type, and `@odata.id` is an entity reference. Nested under a declared container these are
inert payload; flattened, they are control information.

Every route that binds a body which can reach a dynamic bag therefore rejects a dynamic key that is
not an OData **simple identifier**:

| Route | |
|---|---|
| `POST` / `PUT` / `PATCH /Set` and `/Set({key})` | the entity body |
| `PUT` / `PATCH /Set({key})/{ComplexProp}` | the property-route write |
| `POST /Set({key})/{Nav}` | the navigation-create route |
| `POST /Set/{Action}`, `POST /Set({key})/{Action}`, `POST /{Action}` | each action **parameter**, against that parameter's declared type |

An action's `{ "paramName": value }` envelope is not itself a bag — its keys are parameter names
matched against the operation's signature — so only the parameter *values* are walked.

### The grammar

CSDL §4.1 `odataIdentifier`: one leading character from Unicode category **L** or **Nl** (or `_`),
followed by up to 127 characters from **L**, **Nl**, **Nd**, **Mn**, **Mc**, **Pc** or **Cf**. The
length is a count of **code points**, so an astral-plane identifier is not charged double for its
surrogate pair.

That rules out the empty string, `@odata.type`, `Meta@odata.count`, `has.dot`, `has space` and
`kebab-case`. It **admits** any Unicode letter and the combining marks that go with it — `नाम`,
`ชื่อ`, `Ⅸ`, `𝐀bc`, and `naïve` in either NFC or NFD form. That last pair matters in practice:
macOS normalises to NFD where Windows normalises to NFC, so a narrower rule would give the same key
two different HTTP status codes depending on the client's operating system.

```jsonc
// POST /odata/ExternalReferences  →  400
{ "Source": "S", "Xref": "X", "Metadata": { "@odata.type": "#Evil.Type" } }
```

```jsonc
{ "error": { "code": "InvalidBody", "target": "@odata.type",
             "message": "'@odata.type' is not a valid dynamic property name. A dynamic property of
                         an OData open type must be a simple identifier: it starts with a letter (in
                         any script) or '_', continues with letters, digits, combining marks or '_',
                         and is at most 128 characters long. '@', '.', '-' and spaces are not
                         allowed; names containing '@' are reserved for control information such as
                         '@odata.type'." } }
```

The message is deliberately plain-language rather than a recital of the Unicode category codes — it
is read by an API consumer, and the formal grammar is the section above.

### Invisible characters are permitted — treat dynamic keys as untrusted display text

`Cf` (format) is one of the `identifierCharacter` categories, so the grammar admits invisible
characters in *following* position: zero-width joiners and non-joiners (U+200D, U+200C), U+200B,
U+FEFF, the soft hyphen U+00AD, and the bidi controls (U+202E `RLO`, U+2066 `LRI`, …). They are
correctly rejected as the *leading* character, and OhData does not deviate from the normative
grammar to reject them elsewhere.

The consequence is that two visually identical dynamic keys can be distinct strings, and a key
containing a bidi control can reorder the text rendered around it. Keys are echoed verbatim, so a
consumer that displays them in a UI should escape or strip format characters exactly as it would for
any other untrusted string.

### It applies at every depth

A dynamic value is stored exactly as sent, so the rule holds all the way down — not just for the
first level of bag keys. Every object key below an accepted dynamic key must also be a simple
identifier, including through arrays:

```jsonc
// 400, target "@odata.type" — the reserved key is one level below the accepted key "nested"
{ "Source": "S", "Xref": "X",
  "Metadata": { "Region": "us", "nested": { "@odata.type": "#Evil.Type" } } }

// 400, target "@odata.id" — arrays under a dynamic key are walked too
{ "Source": "S", "Xref": "X", "Metadata": { "list": [ { "@odata.id": "http://evil/x" } ] } }
```

"Every depth" includes the path *to* a bag as well as the path below it. A declared member typed
`IDictionary<string, TOpenComplex>` is a JSON object that is not itself a bag, but the values under
it are — so the values are walked. The dictionary's own keys are map keys of a declared property,
never dynamic property names, and are not held to the identifier grammar:

```jsonc
// 400, target "@odata.type" — reached through a dictionary-valued declared member
{ "Id": 0, "MetaMap": { "one": { "@odata.type": "#Evil.Type" } } }

// 201 — "has space" is a map key of MetaMap, not a dynamic property name
{ "Id": 0, "MetaMap": { "has space": { "tier": 7 } } }
```

The check runs only against members that will actually land in a bag — unknown members of a
*non*-open type are ignored on binding exactly as they were before, so a client may still send
`@odata.context` at the entity root. A model with no open complex type never runs it at all.

## `PATCH` replaces the complex value — it does not merge it

**This is the sharpest edge of the feature.** `PATCH` of a complex member deserializes that member
wholesale into the CLR property, so the value the handler receives is built *only* from what the
request restated. Everything else in the old complex value is gone — dynamic keys and declared
properties alike.

```jsonc
// stored:  "Metadata": { "Region": "eu", "keepMe": "important", "tier": 3 }
// PATCH:
{ "Metadata": { "tier": 4 } }
// result:  "Metadata": { "Region": null, "tier": 4 }        ← keepMe gone, Region nulled
```

This is **pre-existing behavior for every complex member**, not something open types introduced —
but open types widen its blast radius from "one nullable member" to "the entire caller-supplied
bag", so plan for it.

The only reading under which "undeclared keys survive a PATCH" is true is the weak one: a `PATCH`
that **omits** the complex member entirely leaves it, and everything in it, untouched.

**To change one dynamic key, read-modify-write the whole complex value:**

```csharp
// 1. read
var current = await client.GetAsync<ExternalReference>(id);
var bag = new Dictionary<string, object?>(current.Metadata?.KeyValuePairs
    ?? new Dictionary<string, object?>());

// 2. modify
bag["tier"] = 4;

// 3. write the WHOLE complex value back
await client.PatchAsync(id, new { Metadata = MergeWithDeclaredProperties(current.Metadata, bag) });
```

`PATCH` on the property route (`PATCH /Set({key})/Metadata`) is not a merge either — it returns
`400 NotSupported`. Use `PUT /Set({key})/Metadata`, which is an explicit whole-value replace.

## A bag key that collides with a declared property name — this fails the request

Server-side data can put a key in the bag that equals one of the complex type's own declared
property names (a handler merging a caller-supplied dictionary, for instance). Emitting both would
produce a **duplicate JSON property name**, which every .NET reader tested resolves in the *bag's*
favour, making the declared value unreachable.

**Contract: the request fails with `500` and the OData error envelope.** The exception names the CLR
type and the colliding key (never the value) and is logged; the client gets the ordinary
`{"error":{"code":"InternalServerError",…}}` body, not a bare 500.

```jsonc
// Meta.Channel = "declared";  Meta.KeyValuePairs = { "Channel": "fromBag", "ok": 1 }
// GET /odata/Things  →  500
{ "error": { "code": "InternalServerError", "message": "…" } }
```

**The cost is deliberate and worth stating plainly: a collection endpoint faults on the bad data
rather than serving the remaining rows.**

### Why a hard failure rather than dropping the key

**The spec does not decide this.** OData CSDL 4.01 §6.3 (open entity type) and §9.3 (open complex
type) say only that an open type "allows clients to add properties dynamically to instances of the
type by specifying *uniquely named* property values in the payload" — directional, but not a
prohibition addressed to the server. OData JSON Format 4.01 does not address it and defers to
RFC 8259, where object member names "SHOULD be unique". Dropping the key and failing the request are
**both** conformant; the only thing actually ruled out is *emitting* the duplicate, which neither
does.

With no spec constraint, two things decide it:

1. **It matches `Microsoft.AspNetCore.OData`.** That library guards the same condition and treats it
   as `InvalidOperation` — a 500 — on both sides of the wire:
   `ODataResourceSerializer.AppendDynamicProperties` throws
   `DynamicPropertyNameAlreadyUsedAsDeclaredPropertyName` ("The name of dynamic property '{0}' was
   already used as the declared property name of open type '{1}'."), and
   `DeserializationHelper` throws `DuplicateDynamicPropertyNameFound` on a dynamic property name
   conflict inbound. Diverging from the ecosystem needs a specific reason, and there is not one
   strong enough here.
2. **The failure is systematic, not per-row.** A client *cannot* create this collision:
   `System.Text.Json` binds a body key matching a declared name to that declared property, so it
   never reaches the bag. The only source is server-side code, and if it can fire at all it fires for
   every row carrying that key. A warning in a log stream is the wrong signal for a systematic defect.

The match is **ordinal**: a key differing only in case (`channel` beside a declared `Channel`) does
**not** fail, because it produces no duplicate JSON key and serializes perfectly well.

> **Recorded consequence of the ordinal comparison.** OhData binds request bodies
> *case-insensitively*, so a body key `channel` binds to the declared `Channel` property — but a
> `channel` key placed in the bag by server-side code round-trips into the response as a distinct
> sibling of `Channel`. A client reading that response and PUTting it back will bind `channel` to the
> declared property, silently overwriting it. This is a deliberate trade (faulting on a
> case-differing key would reject data that is valid JSON and valid OData) and not an oversight —
> but if your handlers merge caller-supplied dictionaries into a container, exclude declared property
> names **case-insensitively** even though the fault only fires on an exact match.

This cannot be checked at startup, because the keys are dynamic and the condition depends on the
runtime instance rather than on the type.

### Do not pre-seed a container with a declared property's name

```csharp
public class Meta
{
    public string? Region { get; set; }                                   // declared
    public IDictionary<string, object?>? Bag { get; set; }
        = new Dictionary<string, object?> { ["Region"] = "preset" };      // ...and pre-seeded  ✗
}
```

Every instance of this type collides on construction, so every read *and* every write of it fails.
The shape is self-contradictory to begin with — it declares `Region` and simultaneously asserts a
dynamic key called `Region`.

Under the previous drop-based behaviour this same shape failed far worse: the collision made the
container's getter hand `System.Text.Json` a filtered clone, and because that getter is also called
on the **deserialize** path to find a dictionary to populate, every dynamic key in the request was
written into the discarded clone. A `POST` of `{"Meta":{"alpha":1,"beta":2}}` returned `201`, looked
clean in the echo, and stored nothing. Failing loudly removes that corner entirely.

Pre-seeding a container with any *other* key is fine and has no effect on binding.

## A bag key that is not a valid identifier — this also fails the request

Server-side data can equally put a key in the bag that is not an `odataIdentifier`. Two kinds, both
handled identically:

- **Not a name at all** — `""`, `"   "`, `"\t\n"`, or a `null` key from a custom
  `IDictionary<string, object?>` implementation.
- **A name that is not a legal identifier** — `"has space"`, `"@odata.type"`, `"kebab-case"`,
  `"1leading"`, or a key made only of format characters such as U+200B.

**Contract: the request fails with `500` and the OData error envelope**, exactly as the
declared-name collision above does. The exception names the CLR type and states the condition; it
never quotes a value, and — unlike the collision message — it does not quote the offending key
either, because a key rejected by the grammar can carry newlines or control characters into a log
line.

```jsonc
// Meta.Channel = "web";  Meta.KeyValuePairs = { "": "x", "tier": 3 }
// GET /odata/Things  →  500   (measured before the fix: {"Channel":"web","":"x","tier":3})
{ "error": { "code": "InternalServerError", "message": "…" } }
```

Such a key is not an `odataIdentifier` (CSDL 4.01 §4.1), so emitting it produces a property that no
conforming OData reader can address — it cannot be selected with `$select`, referenced, or
round-tripped.

As with the collision, **a client cannot cause this**: an invalid dynamic key in a
write body is already rejected with `400` before it can bind (see
[Dynamic property names are validated on write](#dynamic-property-names-are-validated-on-write)
above). This check closes the server-side-data hole only.

> **On a bound or unbound function/action route this currently surfaces as `200` with a truncated
> body rather than as `500`.** Operation routes do not run through the group-level exception filter
> that renders the OData error envelope, so the response has already begun before the throw. That is
> pre-existing and applies to *any* handler fault on those routes, not just this one — it is tracked
> separately as **#396** and is not addressed here. On entity-set routes the `500` + envelope
> contract above holds.

### This is a deliberate divergence from `Microsoft.AspNetCore.OData`

`Microsoft.AspNetCore.OData` **silently skips** the empty key rather than failing, and does not police
the rest of the grammar on this path at all:

```csharp
// ODataResourceSerializer.cs:820, immediately above the declared-name collision check
if (string.IsNullOrEmpty(dynamicProperty.Key))
{
    continue;
}
```

OhData follows that library closely elsewhere — the declared-name collision above is modelled
directly on the check that sits three lines below this one — so diverging here is recorded rather
than accidental. Three reasons:

1. **Matching the skip would mean resurrecting deleted code.** OhData's container getter no longer
   produces a filtered copy; it inspects the bag and hands back the *same reference*, which is
   precisely what removed the corner where a pre-seeded container silently lost every write. Dropping
   a key requires substituting a filtered dictionary — the `TryCreateEmptyLike` / `DropShadowedKeys`
   machinery that was deliberately deleted. Bringing it back to produce a **silent** drop is the
   wrong trade.
2. **It is consistent with the collision case.** Both conditions have the same cause — server-side
   code put a key in the container that cannot be a valid dynamic property name — and the same fix.
3. **An unaddressable property is not a lesser fault than a duplicated one.**

### The line is the full grammar, and what that costs

This check used to be `string.IsNullOrWhiteSpace` — "not a name at all" — on the grounds that
`"has space"` and `"@odata.type"` *are* names and rejecting them costs rune enumeration plus a
Unicode-category lookup per key per instance, on **every serialize**, to close a hole the write path
already closes with a `400`. It is now the full `odataIdentifier` grammar, identical to the one the
write path applies, so the container's contents **are** fully validated in both directions.

How it is made affordable:

- **An ASCII fast path.** `SearchValues<char>` plus `MemoryExtensions.ContainsAnyExcept` answers
  "is every character in `[A-Za-z0-9_]`" in one SIMD pass. Within that subset the whole grammar
  reduces to a length test and a leading-digit test, because the digits are its only members illegal
  in leading position. Anything with a non-ASCII character falls back to the unchanged
  rune-and-category walk, which remains the definition of the grammar — the fast path is an
  accelerator, and the two are pinned against a shared corpus (Devanagari, Thai, NFC/NFD pairs, `Nl`,
  astral-plane, ZWNJ, lone surrogates, the 128-code-point cap, every ASCII code point in both
  positions) with zero permitted disagreement.
- **A bounded cache of validated *non-ASCII* keys.** Scoped to the fallback deliberately: an ordinal
  cache lookup hashes the whole string, which is *more* work than the fast path itself, so caching
  ASCII keys measured slower than revalidating them. Capacity is 1,024, after which insertion stops
  and everything else simply revalidates — no eviction, so it cannot thrash. Bounding is safe because
  a client cannot drive growth: an invalid key is rejected with `400` before it can bind, so only
  server-side data ever reaches the table.

**Measured cost.** In isolation the fast path is **4.6 ns/key**, against **16.9 ns/key** for the naive
rune walk and **5.4 ns/key** for the declared-name hash lookup that sits beside it in the same loop —
so full validation became cheaper than the lookup it accompanies. That isolated figure is *not*
predictive of the in-situ cost: serializing a 1,000-row page carrying 20 dynamic keys per row is
**~26% slower** (4.28 ms → 5.41 ms), roughly 56 ns/key of marginal cost, because the check reads every
character of every key where `IsNullOrWhiteSpace` read one. A hand-rolled scalar-bitmask variant
measured slower still, so that cost is inherent to the scan rather than to `SearchValues`. A model
with **no** open complex type is unaffected — none of this code runs for it.

`char.IsWhiteSpace` is no longer involved, but nothing narrowed: NBSP (U+00A0) and EM SPACE (U+2003)
are rejected by the grammar as surely as they were by the whitespace test, since neither is in any
permitted category.

Like the collision, this cannot be checked at startup: the keys are dynamic and the condition depends
on the runtime instance rather than on the type.

## What is *not* supported

### Entity-root dynamic containers

A dictionary member on an **entity** type is not flattened. `ODataConventionModelBuilder` still
consumes it (so it disappears from the CSDL) while `System.Text.Json` still writes it as a
declared property — an EDM/wire mismatch that predates this feature and is not fixed by it:

```jsonc
// entity-level container — NOT flattened
{ "Id": 1, "Name": "n", "Extras": { "dyn": "v" } }
```

The write half is the blocker: `PATCH` builds its `Delta<TModel>` by resolving each body member
through the EDM/CLR property map and skipping what it cannot resolve, so a root-level undeclared
key would be silently dropped. Half-working would be worse than absent. Put the bag on a complex
type instead.

### `$filter` / `$orderby` over an individual dynamic key

Not supported, and **it does not degrade gracefully**. Microsoft's query binder builds a
property-bag indexer access for `$filter=Metadata/tier eq 3`, which faults while the expression
tree is being constructed:

```
System.ArgumentException: Method 'System.Object get_Item(System.String)' declared on type
'System.Collections.Generic.IDictionary`2[System.String,System.Object]' cannot be called with
instance of type 'System.Object'
```

Over an EF Core-backed `GetQueryable` this surfaces as a **500**, and **no query reaches the
database** — the request fails before materialization, so it is not a silent client-side
evaluation of the whole table. `$orderby` over a dynamic key faults identically. `$select` over a
dynamic key (`$select=Metadata/tier`) does *not* fault: it returns `200` and behaves as
`$select=Metadata`, emitting the whole complex value.

Filter on a **declared** property of the complex type (`$filter=Metadata/Region eq 'eu'`) — that
translates to SQL and pushes down normally.

### Other boundaries

- A container `System.Text.Json` cannot use as extension data — in practice, one with no setter —
  **fails at `MapOhData()`** with a message naming the type, the member and the fix. It is not
  skipped: a silent skip leaves the CSDL saying `OpenType="true"` while the wire nests the bag under
  its own name. Since open types are on by default, this now surfaces without the registration
  mentioning them; `WithOpenTypes(false)` is the other way out.
- A type that already carries its own `[JsonExtensionData]` member (a `JsonObject`, say — the model
  builder does not see that as a container, so both survive) would end up with two extension-data
  members, which `System.Text.Json` rejects. That is also caught at `MapOhData()`, not left to
  become a 500 on the first response.
- A derived type that *shadows* the container with `new` **is** flattened, using the shadowing
  member. `ODataConventionModelBuilder` records the derived member as that derived EDM type's own
  container, so the derived type gets its own entry.
- `Ignore(x => x.Container)` does **not** interact with this feature at all. `Ignore(...)` takes a
  root member of a profile's *entity* type, and a container lives on a *complex* type, which is
  never a profile's model type — the two never meet.

## Interaction with `$expand` and cycle safety

OhData serializes clause-bounded (`#325`/`#326`): a navigation property is never handed to
`System.Text.Json` unless the `$expand` clause asked for it, which is what makes a reference cycle
in the underlying object graph structurally unreachable.

Open types do **not** widen that. The bag's values already reached the serializer before this
feature — they were simply written one level deeper. Flattening changes only where the keys land
in the emitted JSON; it adds no object to the graph the serializer walks, applies only to complex
types (which carry no EDM navigations), and never touches an entity type or a navigation property.

Be aware of the corollary: a dynamic value is arbitrary caller-supplied CLR state, so **whatever
a handler puts in the bag is serialized as-is**. Putting a tracked entity (or anything with a
reference cycle) into a dynamic bag is a serialization fault waiting to happen — the clause-bounded
walker cannot protect a value it has no EDM description of.

## Performance

Flattening is one `JsonTypeInfoResolver` modifier baked into the registration's derived
`JsonSerializerOptions` — the same mechanism `Ignore(...)` uses. It runs once per CLR type
(`JsonTypeInfo` is cached on the options instance), never per request. When the model declares no
open complex type — or the registration called `WithOpenTypes(false)` — the derived options are
skipped entirely and the pipeline is reference-identical to before.

Write-side dynamic-key validation walks the request body once, and only for a registration whose EDM
actually declares an open complex type; otherwise it is a single `bool` test and the body is never
walked. Now that open types are on by default, **that EDM condition is the whole blast-radius bound**:
it is what makes "a model with no dictionary member is untouched" literally true rather than nearly
true. Such a registration does not buffer write bodies, and its responses — including error
responses — are byte-identical between the default and `WithOpenTypes(false)`.
