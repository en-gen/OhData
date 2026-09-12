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

Spike. Nothing is wired into CI and nothing in `src/OhData.AspNetCore/` changed. Tests WERE
added under `src/OhData.AspNetCore.Tests/` -- see Outcome.

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

## Results: the first run

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

Every run writes its own `StrykerOutput/<timestamp>/reports/` — `stryker-pilot.html` to read,
`stryker-pilot.json` to query. Both are gitignored: a survivor list is true of one commit and
silently stale after the next touch to any of these six files, so the findings below carry the
mutation they came from rather than pointing at a checked-in dump.

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

## Outcome: tests written, and where it stopped

Three runs. The tests are in `src/OhData.AspNetCore.Tests/`; nothing in `src/OhData.AspNetCore/`
changed.

| Run | Killed | Survived | Timeout | Score | Suite |
|---|---|---|---|---|---|
| 1 — as found | 123 | 37 | 2 | 76.22% | 3,181 |
| 2 — F1–F5, F7 tests | 144 | 18 | 0 | 87.80% | 3,208 |
| 3 — F6 test | 145 | **17** | 0 | **88.41%** | 3,209 |

**Four of the six files are now clean**: `ODataKeyParser.cs`, `CapturedState.cs`,
`InheritedTypeConfig.cs`, `EdmClrTypeMap.cs` — 0 survivors each.

### What was added

| Finding | Test | Kills |
|---|---|---|
| F1 | `RuntimeTypeConfigResolutionTests.WithheldNames_UnionAcrossThreeLevels_KeepsTheMiddleLevel` + an `RtcLeafBag` third level | 1 |
| F2 | `ODataKeyParserTests` — the malformed/edge key shapes, plus a formatter→parser round trip | 6 |
| F3 | `ETagValueFormatterTests.Append_*` — the null tag, the `default` ImmutableArray, and the framing | 4 |
| F4 | `CapturedStateTests` — the constant judgment in both directions | 2 |
| F5 | `StableTypeName_KeepsNestedTypesDistinct_AndNamesThemOutwardIn` (strengthened from NotEqual to the literal chain) + a global-namespace fixture | 2 |
| F6 | `ACustomKeyType_ParsesThroughItsRegisteredTypeConverter` | 1 |
| F7 | Remedy-sentence assertions in `OperationBindValidationTests` | 5 of 19 |

Two existing tests were **strengthened rather than added to**, which is the honest shape of the
finding in both cases:

- `StableTypeName_KeepsNestedTypesDistinct` asserted only that two nested types differ. Dropping
  `segments.Reverse()` keeps them differing, so the test could not see it. It now asserts the
  literal chain, because that string *is* the hash discriminator.
- `OperationBindValidationTests` asserted the interpolated parts of each message (the operation
  name, `IResult`, `BindAction`) and none of the literal text. It now also asserts each message's
  **remedy sentence**.

### F6 is worth reading, because the obvious test did not work

`keyType == typeof(TimeOnly)` → `!=` survived run 2 even with a `TimeOnly` test present. Inverted,
a `TimeOnly` key falls through to the `TypeDescriptor` converter and parses **identically** — so
the mutation is invisible from that input. The difference is only observable from a type that
reaches that line and is *not* `TimeOnly`, and `Guid`/`DateTimeOffset`/`DateOnly` are all matched
earlier. It took a custom `TypeConverter` key, which also closed the parser's documented converter
fallback — previously untested.

The general lesson: *the test that covers the mutated line is not necessarily the test that kills
the mutant.*

### The 17 that remain, and why they stay

**Three are provably equivalent — no test can kill them, because no behaviour distinguishes them.**

- `ETagValueFormatter.cs:130` (`"c"` → `""`) and `:131` (`"D"` → `""`). Both types document the
  empty string as meaning the default specifier. Measured on .NET 10.0.11 rather than assumed:
  `TimeSpan.ToString("")` and `Guid.ToString("")` are byte-identical to `"c"` and `"D"`.
- `OperationSignatureValidation.cs:126`, the `IsVoidAsyncReturn` ternary. Its branches yield `void`
  and `Task`; the only consumer of the result is `typeof(IResult).IsAssignableFrom(...)`, which
  answers `false` for both.

Killing any of these would mean asserting that a specific literal appears in the source — pinning
the implementation, not the behaviour.

**Fourteen are explanatory prose in #498's three messages, and are declined deliberately.** The
remedy sentences are asserted; sentences like *"the EDM omits one at any position -- so this
operation would advertise one parameter list in $metadata and demand another at the route"* are
not. Killing them needs `Assert.Equal` over whole paragraphs, which constrains nothing a caller can
observe and fails the next time someone improves the wording.

That 5-of-19 split is the most useful thing this exercise produced: **the tool ranked all nineteen
message mutants identically, and only reading them separated the contract from the commentary.**

### So "clean" was not reached, and should not be

88.41% with 17 survivors — 3 impossible, 14 declined — is where this stops. The remaining points
are purchasable only with tests that would be argued against in review. A gate set above this
number would be a gate that rewards writing them.

---

## Is this a good quality gate — especially for agent-generated tests?

Partly, and the qualifier matters more than the answer. What follows is drawn from this spike, not
from the technique's reputation.

### Where it is genuinely better than coverage

The characteristic failure of a generated test is the **vacuous assertion** — code is executed and
then something that cannot fail is asserted. `var x = Parse(input); Assert.NotNull(x);` reaches
100% line coverage of `Parse` and constrains nothing. Coverage rewards that exactly; mutation
testing is structurally immune to it, because a vacuous test kills no mutants. It measures
*constraint*, which is the property actually wanted, and it is a materially harder target to fake.

This spike is evidence for that. `KeyParsingTests` covers `ODataKeyParser` end to end and is not a
bad test file — but it exercises the parser only through a route, with well-formed keys, so **six
independent ways to break string-key parsing** sat there unobserved. No coverage report would ever
have said so. The same for `ETagValueFormatter.Append`, which had no direct coverage at all, and
for the `InheritedTypeConfig` union, where the existing test was correct, deliberate, and *one
contributor short* of constraining the thing its own doc comment says it protects.

### Where it inverts, and this session demonstrated it

**Pointed at a survivor list with "make it clean", the shortest path is to assert the implementation
back to itself.** Those tests kill mutants and are worse than no test: they fail on every
legitimate refactor, and a team that hits enough of them starts deleting tests.

This was not hypothetical here. Sixteen of the 37 survivors were `String` mutations blanking
fragments of #498's three bind-time messages. The score-maximising move is
`Assert.Equal(<entire paragraph>)`, which kills all sixteen. What went in instead asserts the
**remedy sentence** — the actionable, contractual part — and deliberately leaves the explanatory
prose free to be reworded. That is the right test and a worse score. An agent optimising for a clean
run does not make that distinction, because nothing in the signal contains it.

**Equivalent mutants make "clean" unreachable, and chasing it does the damage.**
`OperationSignatureValidation.cs:126` is one: the ternary's two branches yield `void` and `Task`
respectively, and the only consumer of the result is `typeof(IResult).IsAssignableFrom(...)`, which
answers `false` for both. No test can distinguish them, because no behaviour distinguishes them.
Establishing that took reading three methods. An agent instructed to iterate until the run is clean
will not stop there — it will keep writing tests, each more contorted than the last, about a
difference that does not exist. **This is the single biggest hazard in using the tool as a gate
rather than as a report.**

**The percentage is not a quantity worth gating on.** 76.22% was computed over the 162 mutants that
ran, out of 7,735 created — the rest were filtered by scope or discarded by the CS0165 cascade. A
threshold on that number would mostly be measuring which methods happened to survive Safe Mode.

### What it is actually good for

- **As a review input: strongly yes**, and most of all for generated tests. "Did this test constrain
  anything?" is precisely the review question, and this answers it mechanically instead of by
  reading. A survivor in code a PR claims to have tested is a specific, checkable objection.
- **As a CI gate: only in a narrow form.** Diff-scoped (`--since`), over changed files, reporting a
  survivor *list* rather than a percentage, and advisory rather than blocking.
- **The defensible gate is directional, not absolute**: *a change that adds tests should kill
  mutants that survived before it.* That is immune to the equivalent-mutant problem, needs no
  invented threshold, and cannot be satisfied by a vacuous test.

### The specific recommendation for agent-written tests

Use it, and **keep the triage away from whoever is optimising for green**. The judgment — real gap
vs. equivalent mutant vs. legitimately-don't-care — is the whole value, and it is the part an agent
aiming at a clean run is worst at, because "clean" and "correct" come apart exactly there.

Handing an agent the survivor list and saying "make it clean" turns a measurement into a target.
The honest split from this session: F1–F4 produced tests worth keeping on their own merits, which
is the tool working. F7 produced pressure to pin paragraphs of prose, which is the tool being
misread. Both came out of one run, and only reading them told them apart.
