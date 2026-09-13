# Nested `$expand` paging — the deliberate limits

- **Issues:** [#313](https://github.com/en-gen/OhData/issues/313),
  [#410](https://github.com/en-gen/OhData/issues/410) (umbrella),
  [#412](https://github.com/en-gen/OhData/issues/412),
  [#415](https://github.com/en-gen/OhData/issues/415) (refuted),
  [#418](https://github.com/en-gen/OhData/issues/418),
  [#421](https://github.com/en-gen/OhData/issues/421)
- **Status:** Shipped. Recorded here so the limits are not rediscovered as bugs.

The user-facing statement of each limit lives in
[`docs/expand.md`](../expand.md#deliberate-limits). This note carries the reasoning behind each
one — including the behaviour that preceded it and the alternatives that were measured and
rejected — which is maintainer context rather than adopter guidance.

## The limits and why they are limits

These are decisions, not gaps waiting to be filled. The first three are tracked together in
[#410](https://github.com/en-gen/OhData/issues/410) so they are not rediscovered as bugs.

- **A continuation for a parent key that does not exist returns `200` with an empty `value` and no
  link**, where `Microsoft.AspNetCore.OData` returns `404`. **This is a documented divergence.** The
  continuation is a `SelectMany` over the pinned parent, and a `SelectMany` cannot distinguish "no
  such parent" from "a parent that has no children" — both yield zero rows. Telling them apart would
  cost an existence probe, i.e. a second round trip on **every** continuation, to improve the status
  code of a request a well-behaved client never issues (it only ever follows a link the server
  emitted, which by construction names a parent that existed). Note the contrast with the
  delegate-backed navigation route on the same URL shape, where the handler decides — returning
  `null` there produces `404`. This route has no handler to ask, and does not probe for one.
- **Depth ≥ 2 stays `400`.** `$expand=Books` pages; `$expand=Books($expand=Chapters)` does not, at
  either level. The asymmetry is real and deliberate: a level with children cannot be SQL-bounded at
  all, so it is **unbounded in materialization** regardless of what the response says — a link there
  would advertise a bound that does not exist. Restricting emission to depth 1 also removes the
  set-authority question entirely, because at depth 1 the URL already names the parent set and there
  is no child entity set to disambiguate.
- **Delegate-backed navigations stay unbounded, and #313 does not close their DoS.** A navigation
  declared with a handler is never in the engaged pushdown tree, so no ceiling, no bound and no link
  applies to it; a nested `$top`/`$skip` on one is already `400` (#294). Bounding it would mean the
  framework silently truncating a collection the developer's delegate deliberately returned, which
  directly weakens the delegate-safety invariant. The real fix is a **contract** change — a delegate
  overload taking `(key, skip, take, ct)` — not a ceiling applied behind the delegate's back. Until
  then, a delegate is where you own the size of your own answer.
- **Delegate safety is the declaring set's own declaration, and a sibling's delegate does not
  suppress paging** ([#421](https://github.com/en-gen/OhData/issues/421)). Route registration and
  link emission share one predicate, whose `ServeRaw` test is resolved by `ResolveNavTreatment` over
  **the URL-named set alone** — byte-for-byte the candidate set the root read path uses. A navigation
  *this* profile declares with a delegate is `RunDelegate`, so it never gets a raw continuation route
  or a link; that is the invariant, and it is unchanged.

  Until #421 the predicate resolved over the whole sibling union instead, so a *sibling* profile
  declaring the navigation with a delegate suppressed both the route and the link on the
  **delegate-less** set. That protected nothing: under declaring-set authority the root `$expand` on
  the delegate-less set serves those rows **raw** regardless — the root resolves its treatment
  against the URL-named profile alone — so the withheld route only
  removed the paging escape hatch, leaving an over-ceiling bare `$expand` at a permanent `400` on a
  navigation the profile itself declared delegate-less, with `ExpandPagingEnabled` silently inert for
  that entity set. The continuation reads the parent profile's own `GetQueryable` under that set's
  own authorization, so the rows it serves are a strict subset of what the `$expand` beside it
  already returns to the same caller; nothing crosses an entity-set boundary. The related claim that
  a sibling delegate blanks a *root-level* `$expand` was measured false and is why
  [#415](https://github.com/en-gen/OhData/issues/415) was closed as refuted.
- **`Prefer: odata.maxpagesize` *is* honoured on the nested page size, as of
  [#412](https://github.com/en-gen/OhData/issues/412).** It **narrows** the nested page and is clamped
  down to `MaxExpandTop`, never up — the ceiling is the server's DoS bound and a request header may
  not lift it, exactly as the root collection clamps `maxpagesize` to `MaxTop`. It applies only where
  a continuation link is actually going out (a truly bare expand on a pageable navigation): trimming
  a collection that gets no link would be the silent truncation the M1 rule forbids, so a
  non-pageable over-ceiling shape keeps its `400` and ignores the header entirely. Both spellings are
  accepted (`odata.maxpagesize` is the OData 4.0 name, `maxpagesize` the 4.01 rename). The
  continuation route honours it too, so a client that keeps sending the header gets a consistent page
  size all the way down; a client that stops simply gets `MaxExpandTop`-sized pages from there on,
  and nothing is skipped or repeated either way because `$skip` is an absolute offset advanced by the
  rows each hop actually served. `Preference-Applied` is **unchanged** — §8.2.8.5 makes the echo a
  `MAY` and gives it a single value for the whole response ("the maximum page size applied"), so
  there is no per-collection echo to add. This is what closes #412's stated blocker: the spec says in
  terms that *"the client MAY specify a different value for this preference with every request
  following a next link"*, so the page size is expected to travel on the request rather than inside
  the link, and the `$skip`-only continuation surface did not have to widen.
- **The continuation *link* is for a pushed expansion only. The *ceiling* applies to every
  raw-served one, at every level, as a `400`** ([#418](https://github.com/en-gen/OhData/issues/418),
  widened by [#463](https://github.com/en-gen/OhData/issues/463) and
  [#464](https://github.com/en-gen/OhData/issues/464)). With `MaxExpandTop` set, a `GET /{Set}({key})`,
  a `GetAll`, a Priority-1 or a non-EF `GetQueryable` read whose expanded collection exceeds it
  returns `400` (`InvalidQueryOption`) instead of the whole collection — and so does a deeper level
  of any of them, or of a pushed branch the planner declined. `ExpandPagingEnabled` buys nothing on
  those: the link would need page 1 and the continuation to agree on an order, and there the
  framework composes neither side — the child rows arrive already materialized inside whatever the
  handler returned, while the continuation orders by the child key *in the database*.
  Re-sorting the serialized JSON cannot reconcile the two (a JSON compare is not the column's
  collation, and is not SQL Server's `uniqueidentifier` order), and a link over a disagreeing order
  skips and duplicates rows invisibly. So the M1 rule is satisfied with the `400`, per #418's own
  recommendation for exactly this case. **What the `400` does *not* buy is a materialization bound:**
  the collection was loaded by your own handler before the framework saw it, so this is a data
  ceiling only (the #299 trade). Size the eager loads in your `GetById`/`GetAll` accordingly, or do
  not eager-load at all — a `GetById` that does not `Include` the navigation serves `[]` and never
  trips it.
