# Mutation-survivor triage

A decision record, not a scoreboard. It names the behaviours this suite does **not** constrain, and
records a decision for each — including the ones deliberately left unconstrained, so nobody has to
re-derive them from a fresh Stryker run.

Stryker.NET 5.0.0, `mutation-level: Standard`, `coverage-analysis: perTest`. Two runs: the **core**
package (2 h 04 m, **70.02 %**) and the **mapper** package (**68.54 %**). Reproduce with
`./stryker-sweep.sh core` / `./stryker-sweep.sh mapper`, then classify with
`python stryker-triage.py <report.json> [out.md]` — the tiering below is that script's output, so the
concern map it carries is the thing to edit when a file's failure mode changes.

**Provenance.** The runs started at 11:05 and 12:43; `59a7b10` was committed at 13:00. The mutated
source is nonetheless `59a7b10`'s — `git diff f4154f0 59a7b10 -- src/` is empty, so the commit
touched no mutated file.

## How to read a survivor

A surviving mutant is a behaviour change no test objected to. That is **not** the same as a missing
test — it can equally be an equivalent mutant (provably unobservable), a deliberate non-contract
(message prose), or a behaviour the project has decided not to pin. Chasing the number produces
contorted tests, so findings are ranked by **failure-mode silence** instead: one matters when the
wrong behaviour it describes would ship without anyone noticing.

Survived and uncovered both mean "nothing objected" and are **not** the same finding: **Survived**
means a test ran and passed anyway, **uncovered** means no test reached the line at all. Both the
tool and this document keep them apart.

| Tier | Meaning | Core (surv/uncov) | Mapper (surv/uncov) |
|---|---|---|---|
| **A** | Silent **and** harmful — the client or operator cannot tell it went wrong | 67 (46/21) | 84 (69/15) |
| **B** | Wrong but inspectable — a wrong document, a wrong refusal | 667 (513/154) | 15 (9/6) |
| **U** | Unclassified — no concern recorded for the file | 0 | 0 |
| **C** | Loud or inert — it throws, it is logged, or nothing observable changes | 486 (378/108) | 125 (57/68) |
| | **Total** | **1,220** (937/283) | **224** (135/89) |

Tier **U** exists so an unmapped file is *visible* rather than silently defaulted into a bucket. Both
runs show U = 0 only because the map covers every file in both assemblies today. `CONCERN` is keyed
by **basename**, so any new or renamed file lands in U until someone classifies it.

## Three biases in the number itself

**Do not quote 70.02 % as "the suite is 70 % effective."** Three measurement artifacts sit under it.
The first two are mechanisms `stryker-mutation-testing.md` already explains; what is new here is the
arithmetic and where the losses land.

1. **The CS0165 Safe Mode cascade discards 37 % of the core's mutants before any test runs.** 7,735
   created − **2,885 compile errors** (2,874 "mutant caused compile errors" plus 11 "could not be
   injected in code") − 283 uncovered − 781 filtered = **3,786 actually tested**. The discards are
   not random: **2,558 of the 2,885 are in `OhDataEndpointFactory.cs`**, concentrated in
   `MapEntitySet` — the route table. **Nothing in this report says anything about those 2,885.**
2. **A timeout counts as a kill**, so a score taken against an oversized test project is
   optimistically biased — which is why the sweep runs one project at a time.
3. **A core MEMBER whose only consumer lives in a companion package is measured by neither run.**
   Verified on `CapturedState.IsCapturedByDelegate` (`CapturedState.cs:100`): its one production
   caller is `OhData.AspNetCore.Mapper/DeltaProfile.cs:177` and its only tests are in
   `OhData.AspNetCore.Mapper.Tests/Issue488DeltaMappingGapTests`. The **core** run mutates it
   against a test project that cannot reach it (its 11 mutants, all at `:105-117`, report
   `NoCoverage`); the **mapper** run's tests do reach it, but that run does not mutate the core
   assembly, so `CapturedState.cs` is absent from the mapper report entirely. Read this at
   **member** scope, not file scope: `CapturedState.cs` carries 24 mutants and the core suite kills
   8 of them, because `CapturedStateTests` does cover `IsCapturedByExpression`, which has four
   callers in `EntitySetProfile.cs`.

## Tier A — core (8 files, 67 items)

- **`OpenTypeJsonOptions.cs:764`** — *worth constraining.* `PropertyNameCaseInsensitive ?
  OrdinalIgnoreCase : Ordinal`. Both conditional mutants survive. This is the withheld-name comparer
  from #398 review HIGH-1, and the defect it closed was exactly a comparer mismatch: a body key
  `secret` against a withheld `Secret` bypassed containment and was echoed back. The comparer is a
  disclosure boundary and nothing pins which one it picks.
- **`IgnoredPropertyJsonOptions.cs:231`** — *worth constraining.* `for (int i =
  typeInfo.Properties.Count - 1; i >= 0; i--)`. The loop that actually removes withheld properties
  from the contract. An off-by-one leaves a withheld property in it, which is disclosure under a
  200.
- **`ModelBoundAllowlists.cs:48-49`** — *worth constraining.* `Equals`, including an **uncovered**
  `Applied != other.Applied`. #458's divergence detector; if it stops detecting, two profiles over
  one CLR type silently union their allowlists again and each set accepts what the other allows.
  **Only 3 of this file's 12 items are these**; the other 9 are in `GetHashCode` (`:55`) and
  `Describe` (`:57`, `:59`), a message builder — see the classifier limitation below.
- **`DeltaExtensions.cs`** — *split.* `:49` (`value = boxed is null ? default! : (TValue)boxed;`) is
  the file's only non-guard item and is worth constraining; its `ArgumentNullException` guards, and
  `DeltaExpressionHelper.cs:22`'s `ConvertChecked` branch, are loud when wrong and are not.
- **`ETagValueFormatter.cs:130-131`** — *decided against:* **equivalent mutants, unkillable.** Both
  replace the format specifier with the empty string (`"c"` → `""`, `"D"` → `""`), and .NET
  documents `""` as falling back to `"c"` for `TimeSpan` and `"D"` for `Guid`. Verified on .NET
  10.0.11: both produce byte-identical output. No test can kill these. Note the mutation is to `""`,
  not to a *different* specifier — an argument about specifiers colliding would be about a mutant
  Stryker does not make.
- **`CapturedState.cs` (all 11)** — *decided against:* bias 3 above. Not a test gap but a
  sweep-topology artifact; pinning them from the core suite would mean duplicating the mapper's
  fixtures to reach a member the core package does not call.
- **`OhDataAuthRequirementsText.cs` (2)** — *decided against:* empty-collection edges on a file that
  went from 34 mutants with zero coverage to 2, in #673. See `stryker-mutation-testing.md` for that
  measurement's provenance. Good enough.

## Tier A — mapper (7 files, 84 items)

- **`MappedNextLink.cs:47` and `:92`** — *worth constraining, and the sharpest items in either run.*
  `:47` is `request.Query.Where(e => !IsSkip(e.Key) && !IsTop(e.Key))`, which strips the incoming
  `$skip`/`$top` before rebuilding the continuation; invert either negation and the link carries the
  **old** offset, so a client paging a mapped set silently repeats or skips rows under a 200. `:92`
  (`&& parsed > 0`) bounds the offset read back off that link. This is #651's continuation, and the
  failure mode is the one the file's concern names.
- **`MappedEntitySetProfile.cs:180`, `:200`, `:206`** — *worth constraining.* `:180` (`rows.Count >
  pageSize`) decides whether a `@odata.nextLink` is emitted at all; `:200`/`:206` are the
  `MappedPageSize` ceiling comparisons. A wrong comparison there silently disables the bound, which
  is precisely the bypassable-ceiling class #357 and #543 exist to close, one package over.
- **`MapExpressions.cs:69`, `:75-76`, `:110`** — *worth constraining.* `GuardAndNarrow`'s null-guard
  and widening logic. #651 records that the projection and the predicate were once derived
  separately and disagreed (a row served `"CatId": 0` that then did not match `CatId eq 0`); these
  are the comparisons that keep them agreeing.
- **`DeltaFactory.cs` (35)** — *split.* The in-scope-surface union (`:138-140`), `:355`
  (`hasPublicSetter`), the `IsIgnoredProperty` mirror of `Delta<T>`'s own predicate (`:505-513`) and
  the nullable-wrap at `:537` are real: #479/#488's failure mode is a 200 with nothing persisted,
  and these decide what is writable. The remainder are reflection shape-walks over generic
  interfaces, killable only by exotic type shapes. Constrain the first group if this file is
  touched.
- **`ModelToEntityRewriter.cs` (9), `MappedNavigationLoader.cs` (7), `MappedQueryComposer.cs` (1)**
  — *deferred, not dismissed.* These are covered end-to-end by the conformance oracle (a mapped
  profile and a mapper-free control must answer ~85 query constructs identically), which is a
  stronger constraint than a unit test on a reflection helper and is why the survivors here are
  concentrated in `MethodInfo` lookups rather than in the rewriting itself.

## Tier B — not enumerated, deliberately

667 core items, **441 of them in `OhDataEndpointFactory.cs`** and 105 in `EntitySetProfile.cs`. A list
that long is not a list anyone acts on, and the concentration restates #671 (the factory is 12,944
lines) rather than saying something about tests, so it is recorded as a count, not a queue.

## Tier C — decided against, once

486 core and 125 mapper items are exception messages, log prose, accumulated validation errors, and
strings that are not contracts. **Killing these means pinning implementation text**, which makes every
reworded message a failing test and teaches the suite to resist improvement.

The line is drawn at *contract* strings — `@odata.*` names, format specifiers, media types, header
values — which stay in their file's tier. Everything else is dropped.

## Two known limitations of the classifier

Stated because the tiering above is only as good as these. Both fail toward **over-promotion**,
which is the safe direction: a decided-about item that turns out to be prose costs a sentence; the
reverse costs a silent defect.

1. **Prose is recognised per line, not per value.** `stryker-triage.py` drops a mutant whose *line*
   builds a message (`throw new`, a `Log*` call, `errors.Add`). It cannot see that
   `ModelBoundAllowlists.Describe()` returns a string that is later interpolated into a startup
   exception, so that file's 9 `Describe`/`GetHashCode` items sit in tier A on their file's concern
   rather than on their own.
2. **`CONTRACT` matches substrings.** A prose line merely *mentioning* a media type is promoted out
   of prose into its file's tier. Measured blast radius on the core report: one line,
   `OhDataEndpointFactory.cs:1678`. Cosmetic today.

## What would change the answer

- **#671 splitting `OhDataEndpointFactory`** would move 441 tier-B and 226 tier-C items into new
  files — which land in tier **U** until the concern map is edited — and would likely shrink the
  CS0165 cascade, since Safe Mode discards per *method* within a file.
- **A companion-aware sweep topology** — mutating the core assembly against the union of the test
  projects that consume it — would close bias 3, at the cost of widening bias 2.
- Neither is scheduled.
