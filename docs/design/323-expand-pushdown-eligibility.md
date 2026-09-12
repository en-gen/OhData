# `$expand` pushdown — eligibility and the cycle classes

- **Issues:** [#206](https://github.com/en-gen/OhData/issues/206) (pushdown direction),
  [#298](https://github.com/en-gen/OhData/issues/298) /
  [#300](https://github.com/en-gen/OhData/issues/300) (fail loud on translation failure),
  [#305](https://github.com/en-gen/OhData/issues/305) (the `Include` fallback),
  [#323](https://github.com/en-gen/OhData/issues/323) (projectable elements push down),
  [#325](https://github.com/en-gen/OhData/issues/325) /
  [#326](https://github.com/en-gen/OhData/issues/326) (the bounded walker)
- **Status:** Shipped. The adopter-facing rules are in
  [`docs/expand.md`](../expand.md#expand-pushdown-delegate-less-navigations-join-automatically);
  this note carries the history behind them.

## How the eligibility rule arrived at its current shape

Pushdown originally deferred for **any** related type carrying a back-reference cycle. #323 narrowed
that to types that are both cyclic and not member-init-projectable: a projectable element is always
materialized through a fresh POCO rather than the bare EF-tracked entity, which forecloses a
serialization cycle structurally regardless of what navigations the related type declares. The
consequence is that a standard bidirectional relationship (`Author.Books` / `Book.Author`) pushes
down and JOINs like any other navigation, where it previously deferred.

`IsMemberInitProjectable` requires a public setter on every EDM structural property — a hard
requirement, not a preference. A related type with a get-only structural property therefore falls
back to the bare, untransformed leaf, which is also why it does not receive the #323 cycle fix.

## The wire change #323 accepted by design

Before #323, a **leaf** expand (one with no nested `$expand` of its own) was materialized as the bare
entity while intermediate levels of a multi-level expand or `$levels` already went through the
member-init projection. #323 made leaves consistent with intermediate levels rather than a special
case. The cost is that a public CLR property that is not an EDM structural property is no longer
materialized on a leaf-expanded entity — it comes back as its type's default, exactly as it already
did one level up.

## The three cycle classes, and why the guard was removed rather than widened

#323 introduced a `400` for the root-back-reference case specifically ("Change C"). #326 then
identified two further cycle classes that guard did not cover — a back-reference to a sibling leaf,
and a self-reference. #325/#326 chose Option B and removed the guard entirely rather than widening
it: `Include` populates *tracked* entities and EF Core's relationship fixup wires the back-reference
up, but the response serializes through the same clause-bounded `SerializeBounded` walker every other
path uses, which never hands an un-expanded navigation to `System.Text.Json` at all. A reference
cycle among tracked entities is therefore structurally unreachable regardless of which two instances
it closes between, so the underlying request can be answered correctly and needs no rejection.

The one residual is a cycle closed by an entity-typed CLR property that is not an EDM navigation at
all. `SerializeBounded` does not bound it, and by that point the query has already succeeded, so it
rethrows as a generic `500`.

## Why a translation failure is a `400` and not a silent degrade

Before the #298/#300 review, a nested option that could not be bound — or a composed query the
provider could not translate despite everything looking eligible — degraded silently to EDM-only
under a `200`. For the specific shapes those issues identified, that meant the affected navigation,
or in some cases the whole parent collection, came back wrong or empty with no indication anything
had failed.

## Implementation sites

`TryApplySelectProjection` binds the structural properties plus the navigations that engaged
pushdown, which is the mechanism behind the "a pushed sibling empties an EDM-only navigation" rule.
`ApplySelectPushdown` is deliberately *not* gated on an EF Core provider, which is why `$select` +
`$expand` against a non-EF queryable also omits an unbound navigation. `OmitUnexpandedNavigations`
never strips an `$expand`'d navigation, whatever produced its data. `ApplyIncludeFallback` is the
#305 path.
