# Spike: Stryker.NET mutation testing

**Question.** The suite is large and green. Does it actually *constrain* the code, or does it
mostly exercise the happy path and assert that nothing threw?

Line coverage cannot answer that — a test that calls a method and asserts nothing covers every line
in it. Mutation testing can: it changes the code in small, behaviour-preserving-looking ways
(`>` → `>=`, `&&` → `||`, drop a statement, negate a condition) and re-runs the tests. A mutant the
suite still passes is a **survivor** — a change to production behaviour that no test objects to.

This repo already does the manual version of this. `CLAUDE.md` is full of *"ablation-verified —
removing the EF gate fails 2 tests, removing the source probe fails 8"* and *"verified to fail when
nav suppression is derived from `startupJsonOptions`"*. Those are hand-built mutants, one at a time,
for the conditions somebody thought to check. Stryker is the systematic form of the same move.

## Status

Spike. Nothing here is wired into CI, and nothing in `src/` changed.

## Running it

Stryker is a **local** tool (`.config/dotnet-tools.json`), not global, so the version is pinned with
the repo:

```bash
dotnet tool restore
dotnet stryker
```

`stryker-config.json` at the repo root carries the run. The config file rejects unknown keys, so it
cannot hold comments — the rationale for each setting is here instead.

| Setting | Why |
|---|---|
| `target-framework: net10.0` | `OhData.AspNetCore` multi-targets `net8.0;net10.0`; Stryker mutates one TFM. |
| `coverage-analysis: perTest` | Not an optimisation. 146 of the 173 files in the main suite boot a `TestServer`, so running all 3,181 tests per mutant is the difference between hours and weeks. `perTest` runs only the tests that actually execute the mutated statement. |
| `mutate: [6 files]` | Scope. See below. |
| `concurrency: 16` | Half of this machine's 32 cores, leaving room for the test hosts each worker spawns. |
| `thresholds.break: 0` | The first run is a **measurement**. A threshold picked before seeing the number is a number we invented. |
| `mutation-level: Standard` | `Advanced` adds mutations that produce more *equivalent* mutants — ones no test can kill because behaviour genuinely did not change — which pad the survivor list with noise on a first read. |

## Why these six files

An unscoped run is not a first experiment. `OhData.AspNetCore` is ~23,800 source lines and
`OhDataEndpointFactory.cs` is 54% of it (12,944 lines — see #671), so mutating everything would
produce thousands of mutants before we know what one costs.

These six are small, pure, and each carries a documented correctness claim — which is exactly what a
mutant is built to refute:

| File | Lines | The claim a mutant attacks |
|---|---|---|
| `CapturedState.cs` | 131 | #483/#488 — "a non-compiler-generated target is a captured receiver outright; a compiler-generated one is captured only when it holds something", and "the judgment deliberately errs toward captured" |
| `ODataKeyParser.cs` | 77 | Quote handling, escaping, and the single `ODataKeyFormatException` throw site #496 made structural |
| `ETagValueFormatter.cs` | 370 | #351 — the supported-selector-type matrix, and `StableTypeName` avoiding `Type.FullName` for the net8/net10 reason |
| `InheritedTypeConfig.cs` | 155 | #462 — two policies that are explicitly "not interchangeable" (union for a disclosure boundary, nearest-wins for single-valued config) |
| `OperationSignatureValidation.cs` | 145 | #498 — three bind-time rules |
| `EdmClrTypeMap.cs` | 103 | #508 — lookup is exact and "deliberately offers no base-chain walk" |

## Baseline

Measured at `414d633`, Release, `--no-build`, 32-core machine:

```
Passed!  - Failed: 0, Passed: 3181, Skipped: 17, Total: 3198, Duration: 49 s
ELAPSED: 53s
```

That 53 s is the number `perTest` has to avoid paying per mutant.

## Results

Run at `414d633` + this branch's config, 2026-09-11, 19m38s wall.

```
7735  mutants created
4619  Ignored       - outside the `mutate` scope (expected)
2879  CompileError  - the CS0165 cascade, below
  38  Ignored       - block already covered
  11  CompileError  - could not be injected
   2  NoCoverage
 162  tested  ->  123 Killed / 37 Survived / 2 Timeout
```

Reported score: **76.22%**.

### Do not read that 76.22% as a grade for the suite

It is computed over the **162 mutants that ran**, which is 2% of the 7,735 created, covering ~1,000
lines of a 23,800-line project. Coverage inside the scope is uneven too: `EdmClrTypeMap.cs` got
**3** mutants tested for 103 lines — Safe Mode discarded the rest. The score is a denominator
artifact. The findings below are the output that means anything.

| File | Killed | Survived | Timeout | NoCoverage |
|---|---|---|---|---|
| `OperationSignatureValidation.cs` | 22 | 19 | 2 | — |
| `ODataKeyParser.cs` | 15 | 8 | — | 1 |
| `ETagValueFormatter.cs` | 43 | 7 | — | 1 |
| `InheritedTypeConfig.cs` | 23 | 1 | — | — |
| `CapturedState.cs` | 17 | 2 | — | — |
| `EdmClrTypeMap.cs` | 3 | 0 | — | — |

Full dump: `survivors.txt`. HTML report (22 MB): `StrykerOutput/<run>/reports/stryker-pilot.html`.

---

## F1 — `InheritedTypeConfig.cs:150`, union accumulator, on a disclosure boundary

**Mutation:** `union ??= new HashSet<string>(first, _unionComparer);` → `union = ...`. **Survived.**

```csharp
for (Type? t = runtimeType; t is not null && t != typeof(object); t = t.BaseType)
{
    if (!_declared.TryGetValue(t, out IReadOnlySet<string>? names) || names.Count == 0) continue;
    if (first is null) { first = names; continue; }
    union ??= new HashSet<string>(first, _unionComparer);
    union.UnionWith(names);
}
```

With `=` instead of `??=`, the accumulator is **rebuilt from `first` on every contributor after the
first**, discarding everything unioned so far. It is therefore indistinguishable from the correct
code for one or two contributors, and wrong from the third onward.

`WithheldNames_UnionUpTheChain_RatherThanBeingShadowed` (`RuntimeTypeConfigResolutionTests.cs:794`)
declares exactly **two**: `RtcBaseBag` and `RtcDerivedBag`. That is why the mutant lives.

This is not a cosmetic gap. The set being unioned is the `Ignore()` withheld-name set, and
`CLAUDE.md` states the policy as a security property: *"a withheld-name set is a **disclosure
boundary**, so a derived profile's own `Ignore(...)` set must not shadow its base's."* A three-level
hierarchy (base → mid → derived) drops the **middle** level's withheld names, which is #462's defect
— an `Ignore()`d property served on some rows and not others — reintroduced one inheritance level
further down.

**Fix:** a third contributor in that test. One added type and two asserts.

---

## F2 — `ODataKeyParser.cs:30`, six survivors on one line

**Mutations, all survived:** `Conditional(true)` (the whole guard forced true), `Logical` ×2
(`&&`→`||`), `Equality` (`>= 2` → `> 2`), `String` ×2 (`"'"` → `""`).

```csharp
if (keyType == typeof(string))
{
    return rawKey.StartsWith("'") && rawKey.EndsWith("'") && rawKey.Length >= 2
        ? rawKey[1..^1].Replace("''", "'")
        : rawKey;
}
```

Six independent ways to break string-key parsing, none observed. Read as a set they say the string
branch is exercised for exactly one shape — a well-formed, non-empty quoted key — and for nothing
else. Specifically untested:

- an **unquoted** string key (`Conditional(true)` surviving means no test reaches the `: rawKey` arm
  in a way that matters);
- a **mismatched-quote** key (`'abc` or `abc'`) — what the two `&&`s exist to reject;
- the **two-character** key `''`, the empty quoted string — the single input that separates
  `>= 2` from `> 2`.

`CLAUDE.md` presents this as half of a round trip: `ODataEntityKeyUrlFormatter` *"single-quotes and
percent-encodes string keys and doubles embedded quotes — the same escaping `ODataKeyParser` expects
on the way back in."* Nothing asserts the two agree at this boundary.

**Fix:** a theory over the four shapes above, plus a formatter→parser round-trip over keys
containing `'`.

---

## F3 — `ETagValueFormatter.cs:204` and `:220`, dropped null tags

**Mutations:** remove `destination.WriteByte(TagNull);` at both sites. **Both survived.**

```csharp
if (value is null) { destination.WriteByte(TagNull); return; }          // :204
case ImmutableArray<byte> immutable:
    if (immutable.IsDefault) { destination.WriteByte(TagNull); return; } // :220
```

`TagNull` is `0x00`, a single byte, and it is the **only** thing a null contributes to the hash
stream. Remove it and a null contributes nothing at all — so for a profile with more than one
selector (`ETagSelectors` is `IReadOnlyList<ETagSelectorInfo>`), `[null, X]` and `[X]` serialize to
identical bytes and hash to one ETag.

Two entities that differ only in whether a nullable selector is set would share an entity tag, which
makes `If-Match` a silent no-op between them. That is the #351 failure class the file's own guard
exists to prevent — *"every entity in the set would share one ETag value and `If-Match` would never
detect a conflict"* — reached by a different road.

Confirmed untested: `grep -rn "TagNull|ETagValueFormatter.Append"` over the test project returns
**nothing**. The self-delimiting encoding has no direct coverage at all; the 43 killed mutants in
this file are all reached incidentally through end-to-end ETag routes.

**Fix:** direct `Append` tests asserting that a null and an absent value produce different streams,
and that `default(ImmutableArray<byte>)` differs from `ImmutableArray<byte>.Empty`.

---

## F4 — `CapturedState.cs:127`, the documented judgment is not pinned

**Mutations:** `Logical` ×2 (`&&` → `||`). **Both survived.**

```csharp
if (value is not null && !value.GetType().IsValueType && value is not string) Found = true;
```

`CLAUDE.md` spends a paragraph on this one line: *"Value-typed and `string` constants — a literal
`3`, `"x"`, an enum such as `StringComparison.Ordinal` — are immutable and belong to no instance, so
they are not captures. Anything else non-null is treated as one… the judgment deliberately errs
toward 'captured'."*

Two of the three conjunctions can become disjunctions without any test noticing, so that judgment is
documentation rather than a checked property. A selector closing over a `string` constant, or over
an enum, would be classified as capturing — which per #483 costs an `Expression.Compile()` on **every
request**, the ~2.5–3.5× / +0.4 ms per-request regression that issue measured.

**Fix:** three cases against `IsCapturedByExpression` — a `string` literal, an enum constant, and a
captured reference-typed local — asserting the first two are *not* captures and the third is.

---

## F5 — `ETagValueFormatter.cs:296`, `segments.Reverse()` removed

**Survived.** `StableTypeName` walks the nesting chain outward-in and reverses; without the reverse a
nested type renders `Inner+Outer`. Still stable and still collision-free, so severity is low — but
it means **no nested type is used as an ETag selector anywhere in the suite**, and the comment
directly above (*"keep `Outer+Inner` distinct from another `Outer`'s `Inner`"*) is unverified.
`:299`'s `Conditional(false)` on the namespace-null branch survives for the same reason.

## F6 — `ODataKeyParser.cs:59`, `TimeOnly` keys

**Survived:** `keyType == typeof(TimeOnly)` equality mutation. No test uses a `TimeOnly` key.
`:74`'s `String` mutation (the `ODataKeyFormatException` message) survives with it — the message
#496 made structural is asserted nowhere.

## F7 — `OperationSignatureValidation.cs`, 16 message-text survivors

Sixteen of the 37 are `String` mutations blanking fragments of #498's three bind-time messages
(lines 86–101, 120–123, 138–142). Individually these are the weakest class of survivor — Stryker
blanks a string literal and a test asserting only the exception *type* cannot care.

Collectively they are still a finding, because this repo does not treat messages as incidental:
#322's rule is that an envelope *"names the check that failed"*, #468 chose a bind-time throw
specifically so the message could name the remedy, and #498's own entry argues the messages are the
reason validation lives where it does. None of that text is asserted.

Also here: `:126` `Conditional(false)` on the `IsVoidAsyncReturn` ternary survives, and `:56`/`:57`
**timed out** rather than being killed — removing the two validation calls put the suite into a hang
instead of a failure, which is worth understanding before it is counted as a pass.

---

## The structural blocker: 2,879 mutants discarded as compile errors

Every one is the same shape:

```
An unidentified mutation ... resulted in a compile error with id: CS0165,
message: Use of unassigned local variable 'typeInfo'
Safe Mode! Stryker will remove all mutations in ValidateOrThrow and mark them as 'compile error'.
```

Stryker mutates a construct that feeds a definite-assignment analysis — `is { } x` patterns and
`TryGet(out var x)`, both pervasive here — the mutant does not compile, and Stryker's recovery is to
**discard every mutant in the enclosing method**, not just the offending one.

Methods hit in this run include `ValidateOrThrow`, `RewriteWithoutUnbindableKeys`, `Ignore`,
`GetDirectMember`, `BuildIgnoredJsonNameMap`, `BuildInvoker`, `BuildBinderBodyNameTable`,
`MaterializeRootQuery`, `AddBoundOperationPagingMetadata`, `AnnotateDerivedType`,
`TranslateThenMaterialize`, `SerializeBoundedCollection`, `ScalarStructuralClrProps`,
`ResolveExpandPagingNavigations`, and `MapEntitySet` — the last one six separate times.

**This is where #671 stops being a readability argument.** The unit of loss is the method, so one
unmutable construct in `MapEntitySet` discards mutation coverage for all **4,486** of its lines at a
stroke. Splitting it into 200-line methods would not make the CS0165 go away; it would cap each
occurrence's blast radius at 200 lines instead of 4,486.

Before scaling this past a pilot, that has to be understood — whether it is a known Stryker issue
with a workaround, whether `ignore-mutations` can exclude the offending mutator, or whether it is
simply the ceiling on what mutation testing can see in this codebase as currently shaped.

---

## What this pilot establishes

1. Mutation testing **works** on this repo and is affordable at this scope: 19m38s for 162 mutants
   against a suite that is 84% integration tests, which `perTest` coverage analysis is what makes
   possible.
2. It found **six substantive gaps** in ~1,000 lines of the most carefully-documented code in the
   project — including one (F1) on a stated security boundary and one (F3) in the concurrency
   primitive whose failure mode the file's own guard exists to prevent.
3. Every one of those six sits behind a claim `CLAUDE.md` states as settled. That is the argument
   for the technique here: this codebase's documentation is unusually specific, which makes an
   unkilled mutant an unusually sharp signal — it names a sentence that is not true of the tests.
4. The **CS0165 cascade**, not runtime, is what bounds how far this can scale.

## Not done

- No tests were written. F1–F4 each want one, and F1 wants it soonest.
- Nothing is wired into CI. A `--since` diff-scoped run on changed files is the plausible shape, but
  a threshold gate should wait until the CS0165 question is answered — a score that swings on which
  methods happened to survive Safe Mode is not a gate.
- The other five test projects were not measured.

## Reading a survivor

A survivor is a finding, not a failure. Three things it can mean, and they need different responses:

1. **A real coverage gap.** The mutated behaviour matters and nothing checks it. Write the test.
2. **An equivalent mutant.** The change genuinely cannot alter observable behaviour (a redundant
   bound, a defensive branch that is unreachable given the callers). Nothing to do but recognise it
   — and Stryker cannot tell this apart from (1), which is why a mutation score is never read as a
   percentage to maximise.
3. **Dead or untestable code.** The mutant survives because the statement does not matter. That is
   worth knowing on its own.

Only (1) is a bug in the tests. Chasing the score without that split is how mutation testing turns
into busywork.
