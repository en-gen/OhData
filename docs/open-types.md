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
| `$filter`/`$orderby`/`$select` over an **individual dynamic key** | **Not a supported surface.** Outcome depends on the read path and, for `$filter`/`$orderby`, on the LINQ provider — see [below](#filter-orderby-dynamic-key). Not gated by the property allowlists either — see [the next section](#dynamic-keys-outside-allowlists) |

Values arrive in the dictionary as `JsonElement` (System.Text.Json's representation for an
`object`-typed slot), which is what a handler should expect when reading them.

**The container is `null`, not empty, when a body carries no undeclared keys.** `System.Text.Json`
only materialises the extension-data dictionary once the first unmatched member arrives, so a
`POST` of `{"Metadata": {}}` leaves `KeyValuePairs` `null`. Every bag read in a handler needs a null
check:

```csharp
object? tier = entity.Metadata?.KeyValuePairs?.GetValueOrDefault("tier");
```

<a id="dynamic-keys-outside-allowlists"></a>
## Dynamic keys are outside the query-option property allowlists

> **`FilterProperties`, `OrderByProperties` and `SelectProperties` do not restrict dynamic keys. If
> you treat those allowlists as a security boundary, an open type is a hole in it.**

Those allowlists are enforced through the EDM's model-bound `NotFilterable` / `NotSortable` /
`NotSelectable` annotations. A dynamic property is **not in the EDM** — that is what makes it
dynamic — so there is nothing to annotate and nothing to enforce.
`Microsoft.AspNetCore.OData` behaves the same way, so this is not a divergence from the ecosystem;
it is simply not stated anywhere else.

Measured against a profile whose allowlists deny `Metadata` outright — `FilterProperties(x =>
x.Source)`, `OrderByProperties(x => x.Source)`, `SelectProperties(x => x.Id, x => x.Source)` — over
an in-memory `IQueryable`:

| Request | Result |
|---|---|
| `$select=Metadata` (declared) | `400` `InvalidQueryOption` — allowlist enforced |
| `$select=Metadata/Region` (declared) | `400` `InvalidQueryOption` — allowlist enforced |
| `$filter=Metadata/Region eq 'eu'` (declared) | `400` `InvalidQueryOption` — allowlist enforced |
| `$filter=Metadata/tier eq 3` (**dynamic**) | **`200`, correctly filtered** — allowlist not consulted |
| `$orderby=Metadata/tier` (**dynamic**) | **`200`, correctly sorted** — allowlist not consulted |
| `$select=Metadata/tier` (**dynamic**) | **`200`, and the body carries the whole `Metadata` value — including the denied declared `Region`** |

**The last row is the one to plan around.** Because `$select` over a dynamic key degrades to
`$select=<the whole container>`
([above](#filter-orderby-dynamic-key)), naming *any* undeclared key is a way to read declared
properties the allowlist denies. That row is provider-independent: it returns `200` and leaks the
denied declared property on EF Core too, where the `$filter`/`$orderby` rows instead return `500`
(#390) — a different failure, not an enforcement.

The open-type-ness is precisely what opens the hole. Against a **closed** complex type all three
dynamic-key requests are rejected with `400 InvalidQueryOption` ("Could not find a property named
'tier' on type …"), because the path cannot be parsed at all.

An **entity-root** container is the same story without the complex-type hop: with
`FilterProperties(x => x.Id)` / `OrderByProperties(x => x.Id)`, `$filter=<dynamicKey> eq 3` filters
and `$orderby=<dynamicKey>` sorts, both `200`. (`$select=<dynamicKey>` returns `200` with an empty
object — a root container is not flattened, so there is nothing to project.)

**There is no flag to turn this off today**, and one is not planned here; whether dynamic-key
queryability should be gateable is an open design question tracked with this behaviour in
[**#401**](https://github.com/en-gen/OhData/issues/401). Until it is decided: **if a value must not
be queryable, do not put it in a dynamic bag.** Declare it and deny it with the allowlist, or
`Ignore()` it off the OData surface entirely.

## Dynamic property names are validated on write

A dynamic key is persisted verbatim and echoed on every later read, so an unconstrained one is a
*stored* fault against other consumers rather than a one-request nuisance. `@odata.type` is what a
conforming reader (`Microsoft.OData.Client` among them) uses to resolve a value's type, and
`@odata.id` is an entity reference: nested under a declared container these are inert payload, but
flattened they become control information. That is what creates the need to police bag contents at
all — and it is why an `@`-containing name is [classified as control information and
removed](#control-information) rather than stored, wherever the contract still describes the level it
sits at.

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

That rules out the empty string, `has.dot`, `has space` and `kebab-case`. It **admits** any Unicode
letter and the combining marks that go with it — `नाम`, `ชื่อ`, `Ⅸ`, `𝐀bc`, and `naïve` in either
NFC or NFD form. That last pair matters in practice: macOS normalises to NFD where Windows
normalises to NFC, so a narrower rule would give the same key two different HTTP status codes
depending on the client's operating system.

Names containing `@` are also excluded by the grammar, but they are **not** rejected — they are
classified as control information before the grammar is consulted. See
[Control information](#control-information) below.

```jsonc
// POST /odata/ExternalReferences  →  400
{ "Source": "S", "Xref": "X", "Metadata": { "has space": 1 } }
```

```jsonc
{ "error": { "code": "InvalidBody", "target": "has space",
             "message": "'has space' is not a valid dynamic property name. A dynamic property of
                         an OData open type must be a simple identifier: it starts with a letter (in
                         any script) or '_', continues with letters, digits, combining marks or '_',
                         and is at most 128 characters long. '.', '-' and spaces are not
                         allowed." } }
```

The message is deliberately plain-language rather than a recital of the Unicode category codes — it
is read by an API consumer, and the formal grammar is the section above.

<a id="control-information"></a>
### Control information (`@`) is ignored, not rejected

**Any member name containing `@`, at any position, is OData control information and is skipped.** It
is not validated as a dynamic property name, it is not bound into the container, and it is not echoed
back.

OData JSON Format 4.01 gives `@` two shapes that differ only in position: §4.5's leading form
(`@odata.type`, `@odata.id`, `@odata.etag`) carries control information about the enclosing object,
and §18's embedded form (`Name@odata.type`, `Items@odata.count`) annotates a particular property. A
leading-only rule would tolerate the first and reject the second — the more common spelling on a
write body. Everything else containing `@` (`a@b`, `x@`, a bare `@`) is not valid control information
either, but `@` is `OtherPunctuation`, which the `odataIdentifier` grammar admits in no position, so
no legitimate dynamic property name is reclassified by the broad rule.

```jsonc
// POST /odata/ExternalReferences  →  201; the annotation is ignored, "tier" is the only dynamic key
{ "Source": "S", "Xref": "X", "Metadata": { "@odata.type": "#Ns.T", "tier": 3 } }
```

This matches `Microsoft.AspNetCore.OData` structurally rather than by imitation. That package
contains no `@`-handling code at all, because ODataLib's JSON reader consumes control information
into `ODataResource.TypeName`/`.Id`/`.ETag` and custom annotations into `.InstanceAnnotations` before
the deserializer runs — only `.Properties` can ever become a dynamic property there. OhData binds
with `System.Text.Json`, which has no equivalent reader stage (measured: it routes `@odata.type`,
`Name@odata.type`, `a@b`, `@` and `x@` alike straight into extension data), so the classification is
written down explicitly and the key is stripped from the body before binding.

**Stripping is not optional.** Classifying the key without removing it would be strictly worse than
rejecting it: the annotation would be stored in the container, and the read path holds bag keys to
this same grammar — so the row would return `500` on every later read, permanently.

Three things this does **not** change:

- A **declared** member whose JSON name contains `@` — via `[JsonPropertyName("weird@name")]` — still
  binds normally. Declared names are matched first.
- **`@odata.bind` is still `501 Not Implemented`** — on the collection `POST`, and on every other
  write route (`PUT`, `PATCH`, the navigation-`POST` create route, the structural-property writes,
  and each bound/unbound action parameter), **whether or not the registration has an open complex
  type**. The check runs before any of this, so a request to link an existing entity keeps its
  explicit non-support answer rather than being swallowed as an annotation. Deep insert by reference
  is unimplemented on every verb, not malformed on any of them, which is why it is `501` and not the
  `400` the identifier grammar used to give it incidentally. (It used to sit *below* the open-type
  gate, so on a registration with no open complex type it never ran at all and the annotation was
  silently discarded under a `200`/`201` — see
  [#456](https://github.com/en-gen/OhData/issues/456).)
- An `@` key **one level below an accepted dynamic key** is still `400`. Down there the contract has
  run out: the whole subtree is opaque data that will be stored and echoed verbatim, so there is no
  declared-versus-annotation distinction to draw and an unaddressable key is a stored fault.

  ```jsonc
  // 201 — annotation at a level the contract describes
  { "Metadata": { "@odata.type": "#Ns.T" } }
  // 400 — inside a dynamic value, where everything is opaque data
  { "Metadata": { "nested": { "@odata.type": "#Ns.T" } } }
  ```

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
// 400, target "has space" — reached through a dictionary-valued declared member
{ "Id": 0, "MetaMap": { "one": { "has space": 1 } } }

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

As with the collision, **a client cannot cause this**, by either of two routes: a dynamic key that
fails the grammar is [rejected with `400`](#dynamic-property-names-are-validated-on-write) before it
can bind, and a name containing `@` is [classified as control information and
removed](#control-information) from the body. Either way it never reaches the container, so this
check closes the server-side-data hole only.

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
already closes (`"has space"` with a `400`, `"@odata.type"` by [removing
it](#control-information)). It is now the full `odataIdentifier` grammar, identical to the one the
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
so full validation became cheaper than the lookup it accompanies.

In situ, under BenchmarkDotNet, serializing a 1,000-row page carrying 20 dynamic keys per row costs
this much more than the old whitespace-only check:

| Key shape | Delta | Marginal |
|---|---|---|
| Repeating ASCII keys — the common case, 20 names reused on every row | **+4.0%** | 5.8 ns/key |
| 20,000 distinct ASCII keys | +6.1% | 9.0 ns/key |
| 20,000 distinct **non-ASCII** keys | +14.7% | 28.8 ns/key |

Only the last row consults the validated-key cache at all — the cache is scoped to the non-ASCII
fallback — and it is the shape that saturates the 1,024-entry table and then revalidates everything
beyond it. Reaching it implies a handler synthesising per-row non-ASCII key names rather than using
open types as a schema extension with a bounded vocabulary. A model with **no** open complex type is
unaffected — none of this code runs for it.

An earlier in-situ figure of ~26% (~56 ns/key) came from a stopwatch harness and is **refuted** by
the run above; the benchmark that settled it is `OpenTypeKeyValidationBenchmarks`.

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

Put the bag on a complex type instead. Tracked by issue #398.

> **Correction.** Earlier releases of this document said the blocker was `PATCH`: that
> `Delta<TModel>` "has no mechanism" for routing an undeclared key, because the PATCH loop resolves
> each body member through the EDM/CLR property map and skips what it cannot resolve. **That was
> wrong.** `Microsoft.AspNetCore.OData`'s `Delta<T>` has always had a
> `dynamicDictionaryPropertyInfo` constructor parameter, and Microsoft's own
> `ODataResourceDeserializer` supplies it from the EDM. Measured against the referenced package
> (9.5.0, .NET 10.0.11): `new Delta<T>(typeof(T), updatableProperties, containerPropertyInfo)`
> accepts `TrySetPropertyValue("tier", "gold")`, and `Patch(target)` **merges** into an existing
> container rather than replacing it. The framework simply never passed the third argument. What is
> left for entity-root support is staged work, not a missing mechanism.

<a id="filter-orderby-dynamic-key"></a>
### `$filter` / `$orderby` over an individual dynamic key — **provider-dependent; do not rely on it**

This is worse than "not supported". **The same URL against the same model either returns correctly
filtered data or a `500`, decided by the LINQ provider behind the read path.** Measured with
identical CLR types on both sides — only the source of the `IQueryable` differs:

| Read path | `$filter=Metadata/tier eq 3` | `$orderby=Metadata/tier` |
|---|---|---|
| `GetAll` (`IEnumerable`) | `400` `UnsupportedQueryOption` | `400` `UnsupportedQueryOption` |
| `GetQueryable` over an **in-memory** `IQueryable` (`List<T>.AsQueryable()`) | **`200` — filters correctly** | **`200` — sorts correctly** |
| `GetQueryable` over **EF Core** (measured on SQLite) | **`500`** | **`500`** |

The `GetAll` row is not about dynamic keys at all: that path implements no `$filter`/`$orderby`
whatsoever, so it rejects *every* filter — dynamic or declared — with `"This resource does not
support $filter or $orderby. Configure GetQueryable to enable server-side query processing."`

The other two rows are the trap. Microsoft's query binder builds a property-bag indexer access for
a dynamic-key path; against EF Core that faults while the expression tree is still being
constructed:

```
System.ArgumentException: Method 'System.Object get_Item(System.String)' declared on type
'System.Collections.Generic.IDictionary`2[System.String,System.Object]' cannot be called with
instance of type 'System.Object'
```

**No query reaches the database** — the request fails before materialization, so it is not a silent
client-side evaluation of the whole table. That the failure surfaces as a `500` rather than a `400`
is pre-existing, originates inside `ODataQueryOptions.ApplyTo`, and is tracked separately as
[**#390**](https://github.com/en-gen/OhData/issues/390).

Over an in-memory `IQueryable` that same expression is built successfully and evaluated against the
CLR dictionary, so the request returns correctly filtered — and correctly sorted — data. **Treat
that as an accident of the provider, not as a feature.** A profile that starts on a
`List<T>.AsQueryable()` and later moves to an EF Core `DbSet` — otherwise a pure performance
improvement — turns every working dynamic-key `$filter` into a `500`, with no source change to
point at and no signal at the call site.

`$select` over a dynamic key (`$select=Metadata/tier`) faults on *neither* provider: it returns
`200` and behaves as `$select=Metadata`, emitting the whole complex value. That behaviour has a
security consequence — see
[Dynamic keys are outside the query-option property allowlists](#dynamic-keys-outside-allowlists).

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

  That disjointness is why `Ignore()` is safe here today, and it is *only* true while open types are
  scoped to complex types. It is worth knowing why, because the reason is not obvious:
  **`Ignore()` works by removing a member from its `JsonTypeInfo`, and extension data captures
  exactly what a `TypeInfoResolver` modifier removed.** Measured at raw `System.Text.Json` level —
  one modifier removing `Secret`, a second marking `Bag` as extension data — a body carrying
  `"Secret"` binds it *into the bag* and the next read echoes it back under the withheld name. (A
  `[JsonIgnore]`d member does **not** leak that way: those stay in `JsonTypeInfo.Properties`, so
  they are still declared as far as the binder is concerned.)

  OhData therefore already carries the containment, even though nothing can reach it yet: the
  withheld JSON names are captured before the removal and threaded into both directions of the
  open-type path, so a withheld name is dropped from a write body before binding and a container
  key spelled like one is a hard error on the way out.

  **"Spelled like one" means in any casing the binder would have matched**, not just the exact
  spelling. The withheld-name sets carry the *binder's* comparer — `OrdinalIgnoreCase` whenever
  `PropertyNameCaseInsensitive` is set, which in an ASP.NET Core host is always. That is deliberately
  a *different* comparer from the one the declared-name collision check uses, which stays ordinal:
  a case-differing key does not produce a duplicate JSON key, so faulting on it there would reject
  data that serializes perfectly well, whereas here a case-differing spelling is exactly the bypass.
  With `Secret` withheld and an ordinal set, a body key `secret` misses the declared lookup (the
  member is no longer in the contract), misses the withheld set, and is bagged as an ordinary
  dynamic key — measured, and fixed in review of #398. It ships ahead of the entity-root widening
  (#398) rather than alongside it, so the security-critical half is not landing at the same moment as
  its first real exercise. A write naming a withheld property is **silently dropped**, matching what
  the closed-type path already does for unknown members and what `Microsoft.AspNetCore.OData` does
  (`ODataInputFormatter` deliberately clears ODataLib's `ThrowOnUndeclaredPropertyForNonOpenType`);
  the spec does not settle it either way, since "property value" in Protocol §11.4.2 does not clearly
  cover a name the type does not declare, and no clause contemplates a server that deliberately
  conceals a declared property.

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
