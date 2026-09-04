# Differences from Microsoft.AspNetCore.OData

OhData and [`Microsoft.AspNetCore.OData`][ms] implement the same specification, and OhData is built
*on* Microsoft's OData libraries — `Microsoft.OData.Core` parses every query option, `Microsoft.OData.Edm`
holds the model, and `Microsoft.OData.ModelBuilder` builds it. Most behaviour is therefore identical
by construction, and where OhData had a free choice it deliberately matched Microsoft's, because an
adopter's existing OData clients and tooling should keep working.

This page documents the places where the two genuinely answer differently, and — just as important —
several places where OhData deliberately kept Microsoft's answer even though the specification would
have permitted another. If you are migrating, this is the list of behaviour to check;
[migrating-from-microsoft-odata.md](migrating-from-microsoft-odata.md) covers the API and hosting
model.

**Provenance.** Every claim about `Microsoft.AspNetCore.OData` below was read from its source at
commit [`a05e1ad0`][ms] (9.5.0-7) and is cited by file and line. Behaviour changes between versions;
re-read the cited line before relying on a row.

---

## Where OhData answers differently

### 1. An unrecognized `$`-prefixed query option

| | Behaviour |
|---|---|
| `Microsoft.AspNetCore.OData` | Parsed if recognized, otherwise ignored. `ODataQueryOptions.BuildQueryOptions` ends with `default: // we don't throw if we can't recognize the query` (`ODataQueryOptions.cs:1060-1062`). |
| **OhData** | `501 Not Implemented` (`UnsupportedQueryOption`) on every read route. |

`?$slect=Name` — a typo — is silently dropped by an ignoring server, which returns the full entity
under a `200`. The client cannot tell the difference between "applied" and "ignored".

Protocol §11.2.5 requires the opposite: *"If a data service does not support a system query option,
it MUST fail any request that contains the unsupported option."* §9.3.1 makes `501` the status for
unimplemented functionality, and §13.1.1 item 7 puts that `501` inside the Minimal Conformance MUST
list, which OhData claims.

OhData matches on the `$` **sigil**, not a name list, so an option no version of the spec defines yet
is refused rather than quietly dropped. Note that `Microsoft.AspNetCore.OData`'s own
`IsSystemQueryOption` recognizes thirteen names and `$index` is **not** among them
(`ODataQueryOptions.cs:185-197`) — so `$index` is not a case of "Microsoft knows it and OhData does
not"; neither implements it.

Names are compared **case-insensitively**, which matches Microsoft — `$Select` and `$TOP` are honoured
by both.

See [query-options.md](query-options.md#unsupported-system-query-options-are-rejected-359-380-353).

### 2. `$search` with no search binder configured

| | Behaviour |
|---|---|
| `Microsoft.AspNetCore.OData` | Ignored. `SearchQueryOption.ApplyTo` returns the query untouched when no `ISearchBinder` is registered: *"If the developer doesn't provide the search binder, let's ignore the $search clause"* (`SearchQueryOption.cs:140-144`). There is deliberately no default implementation (`ISearchBinder.cs:17-18`). |
| **OhData** | `400` when the resource has a `$search` leg but no handler; `501` on routes with no `$search` leg at all. |

Same reasoning as (1), and the failure is particularly hard to detect here: an unfiltered result is
indistinguishable from a search that matched everything, so the client receives *more* data than it
asked for and has no way to know.

### 3. Dynamic (open-type) property names on the wire

| | Behaviour |
|---|---|
| `Microsoft.AspNetCore.OData` | Skips an empty key and does not otherwise police names (`ODataResourceSerializer.cs:820-822`). |
| **OhData** | Validates every dynamic key against the OData `odataIdentifier` grammar, in **both** directions — `400` on the way in, and a logged `500` rather than an unreadable payload on the way out. |

Both agree on the neighbouring rule — a dynamic key colliding with a declared property name is an
error rather than a duplicate JSON key. OhData widens the check to the full grammar so a key
containing a space, an `@`, or only formatting characters cannot reach storage and then break
serialization on every subsequent read.

Measured cost of the widening: **+4.0%** on repeating ASCII keys (the common shape), rising to
+14.7% only when a handler synthesizes 20,000 distinct non-ASCII key names per page. See
[open-types.md](open-types.md).

### 4. `If-Match` with a weak validator

| | Behaviour |
|---|---|
| `Microsoft.AspNetCore.OData` | Emits weak ETags unconditionally — `new EntityTagHeaderValue(tag, isWeak: true)` (`DefaultODataETagHandler.cs:64`) — and its `ETag` comparison carries no weakness concept at all (`Query/ETag.cs`). |
| **OhData** | Never emits a weak ETag. On `If-Match`, a `W/`-prefixed entry is **dropped, never unwrapped**, so `If-Match: W/"<current>"` is a `412`. `If-None-Match` uses weak comparison and does match. |

RFC 9110 §13.1.1 requires **strong** comparison for `If-Match`, and §8.8.3.2 says a weak validator can
never participate in one. Weak comparison on a write is what makes a lost update possible.

**This is the row most likely to affect a migration**: a client that echoes back a `W/`-prefixed tag
it received from a Microsoft-hosted service will get `412` from OhData. Send back exactly the `ETag`
header value OhData gave you. See [etags.md](etags.md#weak-validators-are-rejected-by-if-match).

### 5. CSDL validation at startup

| | Behaviour |
|---|---|
| `Microsoft.AspNetCore.OData` | `CsdlWriter.TryWriteCsdl` does not run the EDM validation rules, and `CsdlReader.TryParse` does not police them either. |
| **OhData** | Calls `EdmValidator.Validate` from `MapOhData()` and throws on an invalid model before the first request is served. |

An invalid construct otherwise reaches `$metadata` and breaks only the consumers that validate —
typically client code generators, i.e. someone else's build, long after the change that caused it.
When this was first wired in it caught a real violation in four of OhData's own fixtures.

### 6. `$expand` continuation for a key that matches no entity

| | Behaviour |
|---|---|
| `Microsoft.AspNetCore.OData` | `404`. |
| **OhData** | `200` with an empty `value` and no link. |

Documented as an accepted limit rather than a preference. The continuation route composes
`parents.Where(p => p.Key == k).SelectMany(p => p.Nav)`, and a `SelectMany` cannot distinguish "no
such parent" from "a parent with no children". Telling them apart costs an existence probe — a second
round trip on **every** continuation — to improve the status code of a request a conforming client
never issues, since it only ever follows a link the server itself emitted. Tracked as
[#410](https://github.com/en-gen/OhData/issues/410).

---

## Where OhData deliberately matches, though the spec left room

These are the more interesting half. In each case OhData had an argument for diverging and did not.

### `/$count` negotiates nothing

§11.2.9 says content negotiation *"is not allowed"* with `/$count`. OhData originally read that as
binding the **client**, and therefore kept returning `406` for `Accept: application/xml` on the
grounds that a server may still decline a media type the client refused (RFC 9110 §12.5.1).

Microsoft settles it the other way, and the agreement is load-bearing:
`ODataCountMediaTypeMapping.TryMatchMediaType` returns quality **1** for every `/$count` path
(`ODataCountMediaTypeMapping.cs:33-41`), and `ODataOutputFormatter` then **overrides** the content
type — *"If a media mapping was found, use that and override the value specified by the controller"*
(`ODataOutputFormatter.cs:115-119`). Microsoft never negotiates on this segment and never `406`s.

`Microsoft.OData.Client` depends on this: it translates `LongCount()` by appending `/$count` to the
query string it has already built and strips nothing, so `q.OrderBy(…).LongCount()`,
`q.Take(n).LongCount()` and `q.Skip(n).LongCount()` all send options along. OhData reversed its own
ruling in 2.0.0 and now ignores both `Accept` and `$format` on that segment. Every shape above is
pinned end-to-end against the real client in `OhData.MicrosoftODataClient.Tests`.

### Authorization does not compose across a navigation

With an admin-gated child entity set and an anonymous parent that declares a navigation into it, the
navigation routes run under the **parent's** rule. OhData measured this across 19 route shapes and
**refused to enforce** the child's rule.

`Microsoft.AspNetCore.OData` behaves the same way and cannot do otherwise — verified against its
source, it contains **no authorization code at all**, and it routes the navigation action onto the
*parent's* controller. Failing closed would diverge from that norm, break the idiomatic
scoped-navigation shape with no opt-out to point at, and is undefined where two entity sets share one
EDM type.

OhData emits a startup **warning** naming the declaring set, the navigation and the stricter target,
rather than changing the answer. See [authorization.md](authorization.md).

### An omitted required property is accepted

A `POST` that omits a property the EDM declares `Nullable="false"` succeeds; only an explicit `null`
is rejected. Microsoft lands in the same place —
`ODataResourceDeserializer.ApplyStructuralProperties` loops over **payload** properties only, with no
reverse pass (`ODataResourceDeserializer.cs:484-492`).

§11.4.2's only MUST-fail concerns *"property values specified in the request"*, and §11.4.3 — PUT-only
— prescribes defaulting rather than refusal. OhData shipped a stricter rule in 1.x and **withdrew**
it in 2.0.0, because it made the wire answer depend on a CLR initializer that `$metadata` does not
describe.

### An unknown property in a request body is ignored, not refused

Microsoft clears `ThrowOnUndeclaredPropertyForNonOpenType` deliberately
(`ODataInputFormatter.cs:203`). OhData follows, including for a name withheld by `Ignore()` — the
spec does not settle it, and silently dropping is what an OData client is built to expect.

### Property allowlists do not gate dynamic properties

A dynamic (open-type) property is not in the EDM, so it carries no `NotFilterable`/`NotSortable`
annotation and is not gated. `Microsoft.AspNetCore.OData` behaves identically. Do not read OhData's
allowlists as a security boundary over a model with an open complex type; the measured matrix is in
[open-types.md](open-types.md#dynamic-keys-outside-allowlists).

### Case-insensitive system query option names

`ODataQueryOptions.IsSystemQueryOption` lowercases the name before comparing whenever the URI resolver
enables case-insensitivity, which is the default. `$Select` and `$TOP` are honoured by both. When
OhData tightened unrecognized-option handling it deliberately did **not** start rejecting `$Select`;
the fix was to reject `$slect`.

---

## How these decisions get made

Two rules, in order:

1. **Where the specification states a MUST, the specification wins** — that is what produced rows 1,
   2 and 4 above.
2. **Everywhere else, match `Microsoft.AspNetCore.OData`**, because an adopter's clients, code
   generators and existing knowledge are built around it. A divergence that only reflects a taste
   preference is a cost with no benefit.

Microsoft's behaviour is verified by reading its source, never from memory, and each decision is
recorded with the clause and the citation that drove it — including the ones where OhData was wrong
first and reversed itself, like `/$count` above.

[ms]: https://github.com/OData/AspNetCoreOData/tree/a05e1ad0
