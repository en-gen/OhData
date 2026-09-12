# `MaxExpandTop` — how the nested `$expand` ceiling reached its current shape

- **Issues:** [#254](https://github.com/en-gen/OhData/issues/254) (nested `$count` breach is a 400),
  [#294](https://github.com/en-gen/OhData/issues/294) /
  [#296](https://github.com/en-gen/OhData/issues/296) (the model-bound `MaxTop` of 0),
  [#299](https://github.com/en-gen/OhData/issues/299) (open — the unbounded-materialize cost),
  [#304](https://github.com/en-gen/OhData/issues/304) (deferred window instead of a 400),
  [#313](https://github.com/en-gen/OhData/issues/313) (bare expands bounded),
  [#316](https://github.com/en-gen/OhData/issues/316) (the `$levels` JSON-windowing gap),
  [#320](https://github.com/en-gen/OhData/issues/320) (options dropped under a raw-served parent),
  [#334](https://github.com/en-gen/OhData/issues/334) (count as a correlated subquery),
  [#463](https://github.com/en-gen/OhData/issues/463) /
  [#464](https://github.com/en-gen/OhData/issues/464) (raw-served paths beyond depth 1)
- **Status:** Shipped, except #299. The adopter-facing rules are in
  [`docs/complexity-limits.md`](../complexity-limits.md#what-it-bounds); this note carries the history.

## What each stage covered

**Before #313** the ceiling covered an *explicit* nested `$top` and the nested-`$count`
materialization only. `$expand=Nav` with no nested `$count`/`$top` — the most common `$expand` shape
there is — composed no SQL `Take` and got no post-hoc size check even with a ceiling configured, so a
5,000-row related collection under a `MaxExpandTop` of 1000 returned all 5,000 rows. #313 widened the
rule to "neither a nested `$count` nor an explicit nested `$top`", which is what makes it broad enough
to cover `($select=…)`, `($orderby=…)`, `($filter=…)` and `($skip=N)`.

**Before #463/#464** the raw-served group was checked at **depth 1 of the single-entity read only**,
so `?$expand=Books($expand=Chapters)` and every non-EF collection path served unbounded collections
under documentation that claimed otherwise. Those two issues widened where the check is applied
without changing the rule.

**Before #334** the nested count *was* the materialized array's length, so `$count=true` had to
suppress the client's `$top` and compose the ceiling bound instead: `?$expand=Children($top=10;$count=true)`
fetched `MaxExpandTop + 1` rows to return 10, and with the ceiling unset it composed **no bound at
all**. #334 split the count into a correlated `COUNT(*)` scalar subquery, matching the split
`Microsoft.AspNetCore.OData` makes between `CreateTotalCountExpression` and the projected collection.
A breach is now detected from the exact count rather than an over-fetched probe row, so it is caught
even when only the requested window was fetched. Pinned by `NestedCountTopSqlBoundTests`.

**Before #304** a nested `$top`/`$skip` at a level with its own nested `$expand` failed loud with
`400` outright (`?$expand=Books($top=1;$expand=Chapters)`). It is now windowed in the JSON pass
instead — the same trade `$levels` and `$count` already made. #316 closed the matching ceiling gap on
the `$levels` JSON-windowing path.

## The shape asymmetry, and why it is not a bug

At a projection leaf the bound is composed *after* any nested `$skip`, so the ceiling measures the
post-`$skip` remainder. At a level with its own nested `$expand`, or inside a `$levels` recursion, no
SQL window is composable, so `EnsureWithinExpandCeiling` necessarily runs before the JSON-pass window
— there is nothing to window until the collection is materialized — and the check runs over the full
pre-window collection. The same `Children($skip=4995)` request therefore succeeds on one shape and is
rejected on the other.

That asymmetry predates #313 (it is #304's deferred-window shape); #313 only makes it reachable from
more requests. It goes away if and when #299 removes the unbounded materialization, and not before.

## The self-referential `$top` that never reached OhData

A nested `$top` on a self-referential navigation used to be rejected by the underlying OData
validator before OhData's pushdown code ever ran: the navigation's target type is necessarily its own
entity set — the same thing that makes `$levels` legal on it at all — so its model-bound `MaxTop`
always defaulted to `0`.

`OhDataBuilder.MarkNavigationTargetTypesFullyQueryable` clears that model-bound `MaxTop` for a
root-and-nav-target ("shared"/self-referential) type exactly as it already did for a pure
nav-target-only type, so the pre-emptive `400` no longer fires. The fix generalizes to any
non-self-referential shared type — one that is both a root entity set and someone else's navigation
target; see `SharedNavTargetTypePushdownTests.cs`. `$skip` never carried a model-bound ceiling and
always reached OhData's code even before #294/#296.

A plain (non-`$levels`) `$expand=Children($top=…)` against a delegate-less self-referential navigation
was a separate, orthogonal limitation: the member-init projection for a self-reference was treated as
genuinely cyclic, so it never engaged pushdown regardless of `$top`, and the `$top` silently went
unapplied. #323 resolved it — a self-referential related type is still projectable, since
`IsMemberInitProjectable` only requires a public parameterless constructor and settable scalar
structural properties, and cyclicity is orthogonal to that. See `LevelsWithOptionsPushdownSqliteTests`, T19.

## Options dropped under a raw-served parent

Before #320, a navigation reached only through a delegate-less parent's already-materialized graph
returned `200` with every related row and the nested option dropped without a trace, because the
walker's serve-raw branch never descended into it. The rejection is now resolved from the navigation's
Model B treatment rather than from which navigation the expansion walker happens to reach, so it does
not depend on how the navigation was arrived at.
