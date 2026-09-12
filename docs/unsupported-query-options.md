# Unsupported system query options

Every read route refuses a `$`-prefixed query option it does not implement, rather than ignoring it.
This page explains which status code you get and why — `501` means *this route can never do that*,
`400` means *it could, and this entity set is not offering it*.

OData Part 1 §11.2.5: *"If a data service does not support a system query option, it MUST fail any
request that contains the unsupported option."* Every read route enforces that, and enforces it by
the **`$` sigil** rather than by a list of names it happens to know about.

Concretely, a request is refused with **`501 Not Implemented`** (`UnsupportedQueryOption`) when it
carries a query key that begins with `$` and is not in the route's own implemented set:

```
GET /odata/Products?$unknown=1        -> 501  "The query option '$unknown' is not supported."
GET /odata/Products?$slect=Name       -> 501
GET /odata/Products?$levels=2         -> 501   ($levels is a real option, but only inside $expand)
GET /odata/Products(1)?$filter=…      -> 501   (a single entity has nothing to filter)
GET /odata/Products/$count?$search=x  -> 501
GET /odata/Products?$apply=…          -> 501   (was 400 from 1.0.0 through 1.6.0)
```

Until 1.7.0 each of these returned `200` with the option parsed and thrown away - and on the
collection routes the discarded option was echoed back into the `@odata.nextLink` the server
generated. A `200` from `?$filter=…` reasonably tells a client that the filter was applied.

## `501` or `400`: which, and why

Both statuses are correct, for different conditions, and OData decides between them.

- **§9.3.1** (a MUST): *"If the client requests functionality not implemented by the OData Service,
  the service MUST respond with 501 Not Implemented and the response body SHOULD describe the
  functionality not implemented."*
- **§13.1.1 item 7**, inside the Minimal Conformance MUST list: *"MUST successfully parse the
  request according to [OData-ABNF] for any supported system query string options and either follow
  the specification or return 501 Not Implemented (section 9.3.1) for any unsupported
  functionality"*.
- **§11.2.5**'s own status advice is only a SHOULD, but it points the same way.

OhData claims Minimal conformance, so the `501` is not optional. In five words:

> **`501` is "can't". `400` is "won't".**

The test for which side a refusal falls on is mechanical: **could any setting on the profile make
this same request succeed on this same route?** Yes -> `400`. No -> `501`.

| Condition | Status | Code |
|---|---|---|
| An unrecognized `$`-name, or an option this build implements nowhere (`$apply` `$compute` `$index` `$deltatoken`) | `501` | `UnsupportedQueryOption` |
| An option the addressed **route** does not implement (`$filter` on `GET /Set({key})`, `$search` on a `/$count`, `$select` on a single-valued navigation) | `501` | `UnsupportedQueryOption` |
| `$filter`/`$orderby` on the `GetAll` path, and `$filter` on the `GetAll`-backed `/$count` | `501` | `UnsupportedQueryOption` |
| A capability flag left `false` (`FilterEnabled`, `OrderByEnabled`, `SelectEnabled`, `ExpandEnabled`, `CountEnabled`) | `400` | `UnsupportedQueryOption` |
| A property allowlist rejection (`FilterProperties` and friends) | `400` | `InvalidQueryOption` |
| `$search` with no `Search` handler, on a route that has a `$search` leg | `400` | `UnsupportedQueryOption` |
| A malformed or empty option **value** on a route that implements the option (`$top=abc`, `$skiptoken=`) | `400` | `InvalidQueryOption` |
| A value outside a configured bound (`MaxTop`, `MaxExpandTop`) | `400` | `InvalidQueryOption` |

`$search` shows both sides of the line for one option: with no `Search` handler it is a `400` on the
collection `GET`s, which really do invoke one when configured, and a `501` on `/$count` and
`GET /Set({key})`, which have no `$search` leg at all.

`$filter`/`$orderby` on `GetAll` is "can't". Its message names a configuration change
(*"Configure GetQueryable…"*), which reads like the `400` side, but the refusal is
flag-**independent**: there is no `IQueryable` on that path and therefore no filter code, so
`FilterEnabled = true` changes nothing, and the remedy supplies a **different handler** — it has the
request served by a different route implementation rather than switching this one on. That is
§9.3.1's *"functionality not implemented"*, and the existing message is already what its `SHOULD`
asks the body to do: it describes the unimplemented functionality.

> **The same option can be `501` on one entity set and `400` on another**, decided by which read
> handler the profile supplies. `$filter` on a `GetAll`-backed set is `501` — the framework *can't*
> filter it, under any configuration. `$filter` on a `GetQueryable`-backed set with
> `FilterEnabled = false` is `400` — it *can*, and you chose not to expose it. That is correct, not
> an inconsistency: conformance is per-**resource**, and the two answers tell a client genuinely
> different things — *"no configuration of this endpoint will ever do that"* versus *"this endpoint
> could, and is not offering it"*.

> **⚠ BREAKING for `$apply`/`$compute`/`$index`/`$deltatoken`.** Those four have answered
> `400 UnsupportedQueryOption` since 1.0.0 and now answer `501`. The error **code** and the message
> **bytes** are unchanged, so a client matching on the envelope keeps working; code branching on
> `StatusCode == 400` for this condition must add `501`.

## What is *not* touched

- **Custom query options.** Part 2 §5.2 requires a custom query option to *not* begin with `$`, so
  any key without the sigil is passed through untouched: `?myTenant=acme` is your business, and the
  framework's own `ohdata-skiptoken` continuation offset is deliberately spelled without a `$` for
  the same reason.
- **Parameter aliases** (§5.3) begin with `@`, not `$`, and are likewise untouched.
- **Mixed-case spellings of real options.** `$Select` and `$TOP` are honoured, as they always have
  been. `Microsoft.AspNetCore.OData` lowercases an option name before matching it whenever the URI
  resolver enables case-insensitivity - the default - so this is alignment with the stack OhData
  sits on, not leniency. The reported inconsistency (`$Select` applied, `$slect` ignored,
  neither rejected) is resolved by rejecting `$slect`.
- **`$format`.** Accepted on every route: §11.2.10 content negotiation is implemented once, on the
  group filter that wraps the whole OData surface, so it never reaches a route handler and cannot
  change a row. An unsupported `$format` *value* is still rejected there.
- **Routes outside the table, which still ignore every query option.** Read the table as a list of
  what *is* gated, not as the whole URL surface. The structural-property **writes**
  (`PUT|PATCH|DELETE /{Set}({key})/{Prop}`), the service document (`GET /{prefix}`) and
  `GET /{prefix}/$metadata` are ungated: `GET /odata?$unknown=1` answers `200` with the service
  document. None of them builds a link, so none can echo an option back into one. The
  property writes are ungated *consistently with the entity writes* — `PUT|PATCH|DELETE
  /{Set}({key})` are not gated either, so no two routes over one resource disagree.

  The structural-property **reads** are gated too:
  `GET /{Set}({key})/{Prop}` and its `/$value` implement `$format` and nothing else, so every other
  `$`-option is `501`. They were the one residual that produced the split this whole rule exists to
  remove — `GET /Widgets(1)?$filter=…` answered `501` while `GET /Widgets(1)/Name?$filter=…`
  answered `200` with the filter silently dropped. Note `$select` and `$expand` are refused here
  although the sibling entity route implements them: the property handler goes straight from the
  property accessor to the envelope and reads no option at all.

## The per-route sets

The sets differ, and that is the point - `$filter` is implemented on a collection GET and
meaningless on a single entity.

| Route | Accepted |
|---|---|
| `GET /{Set}` (`GetQueryable`) | `$filter` `$orderby` `$top` `$skip` `$select` `$expand` `$count` `$search` `$skiptoken` `$format` |
| `GET /{Set}` (`GetODataQueryable`, Priority-1) | **whatever the profile declares** in `HonouredQueryOptions`, plus `$format`. The default is what `ODataQueryOptions.ApplyTo` honours - the row above **minus `$search`**, because `ApplyTo` drops `$search` when no `ISearchBinder` is registered |
| `GET /{Set}` (`GetAll`) | the same, **minus `$skiptoken`** - this path continues with `$skip` and never read a `$skiptoken` |
| `GET /{Set}/$count` | `$filter` `$top` `$skip` `$orderby` `$expand` `$select` `$format` - only `$filter` is applied; §11.2.9 requires the rest to be ignored, and the segment negotiates nothing at all (any `Accept`, any `$format` value, always `text/plain`) |
| `GET /{Set}({key})` | `$select` `$expand` `$format` |
| `GET /{Set}({key})/{Nav}` — **collection**-valued (`HasMany`) | `$select` `$orderby` `$skip` `$top` `$count` `$format` |
| `GET /{Set}({key})/{Nav}` — **single**-valued (`HasOptional`/`HasRequired`) | `$format` only |
| `GET /{Set}({key})/{Nav}/$count` | `$top` `$skip` `$orderby` `$expand` `$select` `$format` - it applies **none** of them, and refuses `$filter` as well as `$search` |
| `GET /{Set}({key})/{Nav}?$skip=N` (the `$expand` continuation) | `$skip` `$format` |
| `GET /{Set}({key})/{Prop}` and `GET /{Set}({key})/{Prop}/$value` | `$format` only |
| `GET\|POST /{Set}/{Op}` and `GET\|POST /{Set}({key})/{Op}` (bound operations) | `$top` `$skip` `$format` |
| `GET\|POST /{Op}` (unbound operations) | `$format` only |

Being in a set means *the route implements the option*, not that this profile permits it: a
`$filter` on a set with `FilterEnabled = false` is still a `400`, with the capability flag's own
message naming the flag. A recognized-but-not-implemented-here option and a completely unrecognized
one share one code, because the client's remedy is identical.

Two things in that table are worth reading twice.

**The two navigation rows are one URL shape with two handlers.** `GET /{Set}({key})/{Nav}` is
mapped once, and which branch runs is decided by whether the navigation was declared with
`HasMany` or with `HasOptional`/`HasRequired`. Only the collection branch applies query options:
the single-valued branch serializes the related entity and reads nothing off the query string,
not even `$select`. It therefore accepts `$format` and refuses everything else — including
`$select`, which its collection sibling really does implement. If you need a projection of a
single related entity, read it from its own entity set (`GET /{ChildSet}({childKey})?$select=…`).

**Bound operations honour `$top`/`$skip`, unbound ones do not.** A bound function or action that
returns a collection of the profile's model type is bounded by `MaxTop` and pages with a
`$skip` continuation, so `$top`/`$skip` are real
there and are listed unconditionally — the server can emit a `$skip` link on any of those routes, and refusing the option would mean
refusing a link the server itself issued. Unbound operations have no such pipeline. An
operation's **own parameters** are query-string keys without a `$` (functions) or JSON body
members (actions), so the sigil rule never examines them.

**The two `/$count` rows are governed by §11.2.9 rather than by this feature's general rule, and
they are the one place an accepted option is deliberately ignored.** That clause partitions the
system query options for a count segment: the count is taken *after applying any `$filter` or
`$search`*, and it *MUST NOT be affected by `$top`, `$skip`, `$orderby`, or `$expand`*. So the
options in the first class are **applied where the route can and refused where it cannot** —
ignoring one would answer a wrong number under a `200` — and the options in the second class are
**accepted and ignored**, because that is the behaviour the clause specifies rather than a
shortfall to confess with a `501`.

The two rows differ only in how much the route can apply. The entity-set segment applies `$filter`
and refuses `$search`; the navigation segment invokes the navigation delegate and counts what comes
back, so it can apply neither and refuses both. `$select` is not named by §11.2.9 but is ignored on
the same reasoning as the four that are: it changes an item's shape, never its membership, and the
response is a bare scalar. `$format` is accepted-and-ignored too — §11.2.9 disallows content
negotiation on this segment, so unlike every other row in the table it does not mean "negotiated
here". See the [`/$count` table](query-options.md#count) above.

## Why `501`, and what it costs

§11.2.5's status advice is a SHOULD, and an earlier revision of this feature leaned on that to
answer `400` throughout. Two other clauses settle it the other way: **§9.3.1** makes `501` a MUST
for *"functionality not implemented by the OData Service"*, and **§13.1.1 item 7** puts that same
`501` inside the Minimal Conformance MUST list, which this project claims. See the table above for
the `501`/`400` split.

The cost is a wire break on `$apply`/`$compute`/`$index`/`$deltatoken`, which had answered `400`
since 1.0.0. It was accepted because the alternative is failing a MUST in the conformance level the
project advertises. What is preserved instead is the **envelope**: the error `code`
(`UnsupportedQueryOption`) and the message bytes are identical to the `400` they replace, on every
route, so one condition still produces one body and only the status line moves.
