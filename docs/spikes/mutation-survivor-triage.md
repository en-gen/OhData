# Mutation-survivor triage

A decision record, not a scoreboard. It names the behaviours this suite does **not** constrain, and
records a decision for each — including the ones deliberately left unconstrained, so nobody has to
re-derive them from a fresh Stryker run.

Taken at `59a7b10` (develop) with Stryker.NET 5.0.0, `mutation-level: Standard`,
`coverage-analysis: perTest`. Two runs: the core package (2 h 04 m, **70.02 %**) and the mapper
package (**69.24 %**). Reproduce with `./stryker-sweep.sh core` / `./stryker-sweep.sh mapper`, then
classify with `python stryker-triage.py <report.json> [out.md]` — the tiering below is that script's
output, so the concern map it carries is the thing to edit when a file's failure mode changes.

## How to read a survivor

A surviving mutant is a behaviour change no test objected to. That is **not** the same as a missing
test — it can equally be an equivalent mutant (provably unobservable), a deliberate non-contract
(message prose), or a behaviour the project has decided not to pin. Chasing the number produces
contorted tests, so survivors are ranked by **failure-mode silence** instead: a survivor matters when
the wrong behaviour it describes would ship without anyone noticing.

| Tier | Meaning | Core | Mapper |
|---|---|---:|---:|
| **A** | Silent **and** harmful — the client or operator cannot tell it went wrong | 71 | 108 |
| **B** | Wrong but inspectable — a wrong document, a wrong refusal | 693 | 34 |
| **C** | Loud or inert — it throws, it is logged, or nothing observable changes | 456 | 82 |

## Three biases in the number itself

**Do not quote 70.02 % as "the suite is 70 % effective."** Three measurement artifacts sit under it,
each verified rather than assumed.

1. **The CS0165 Safe Mode cascade discards 37 % of the core's mutants before any test runs.** Stryker
   mutates `is { } x` / `out var x`, the mutant fails definite assignment, and Safe Mode then
   discards **every mutant in the enclosing method**. Measured: 7,735 created, **2,874 discarded**
   this way, 283 uncovered, 781 filtered — **3,786 actually tested**. The discarded population is not
   random: it is concentrated in `OhDataEndpointFactory.MapEntitySet`, i.e. the route table. Nothing
   in this report says anything about those 2,874.
2. **A timeout counts as a kill.** A score taken against an oversized test project is therefore
   optimistically biased. This is why the sweep runs one project at a time rather than the solution.
3. **A core type whose only consumer lives in a companion package is measured by neither run.**
   Verified on `CapturedState.IsCapturedByDelegate`: its one production caller is
   `OhData.AspNetCore.Mapper/DeltaProfile.cs:177` and its only tests are
   `OhData.AspNetCore.Mapper.Tests/Issue488DeltaMappingGapTests`. The **core** run mutates it against
   a test project that cannot reach it (all 11 mutants report `NoCoverage`); the **mapper** run's
   tests do reach it but that run does not mutate the core assembly, so `CapturedState.cs` is absent
   from the mapper report entirely. The member is genuinely tested. The sweep cannot see it.

## Tier A — decided

Eight core files and eight mapper files. Per-file decisions below; the row-level tables are
regenerable from the reports and are deliberately not pasted here.

### Worth constraining

- **`OpenTypeJsonOptions.cs:764`** — `PropertyNameCaseInsensitive ? OrdinalIgnoreCase : Ordinal`.
  Both conditional mutants survive. This is the withheld-name comparer from #398 review HIGH-1, and
  the defect it closed was exactly a comparer mismatch: a body key `secret` against a withheld
  `Secret` bypassed containment and was echoed back. The comparer is a disclosure boundary and
  nothing pins which one it picks.
- **`IgnoredPropertyJsonOptions.cs:231`** — `for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)`.
  The loop that actually removes withheld properties from the contract. An off-by-one leaves a
  withheld property in it, which is disclosure under a 200.
- **`ModelBoundAllowlists.cs:48-49`** — `Equals`, including an **uncovered** `Applied != other.Applied`.
  This is #458's divergence detector; if it stops detecting, two profiles over one CLR type silently
  union their allowlists again and each set accepts what the other allows.
- **`DeltaExtensions.cs:49`** — `value = boxed is null ? default! : (TValue)boxed;`. The one non-guard
  mutant in the file, on the write path's changed-value read.

### Decided against

- **`ETagValueFormatter.cs:130-131`** — `ts.ToString("c")` / `g.ToString("D")`. The concern recorded
  for this file is *collision* (a collision makes `If-Match` a silent no-op, #351's failure mode).
  Both alternative specifiers are injective over their domains, so neither mutant can produce a
  collision — they are equivalent **for the stated concern** and change only the ETag's bytes. Byte
  stability across releases is a separate property this project has not claimed. Not pinned.
- **`CapturedState.cs` (all 11)** — bias 3 above. Not a test gap; a sweep-topology artifact. Pinning
  them from the core suite would mean duplicating the mapper's fixtures to reach a member the core
  package does not call.
- **`NavigationTargetAuthorization.cs` (all 11)** — **this file was classified too strictly, and the
  correction is carried in `stryker-triage.py` rather than only argued here.** It was mapped tier A as
  "disclosure", but #481's *enforcement* was refused by owner ruling and this code only emits a
  `Warning`. A wrong answer is a missing or spurious diagnostic, not a bypass — tier **B** by the
  rubric, which is what the committed concern map now says. That reclassification is the whole
  difference between the 82/682 split a first pass produced and the 71/693 in the table above.
- **`DeltaExtensions.cs:25,26,43,44` and `DeltaExpressionHelper.cs:22`** — `ArgumentNullException`
  guards and a `ConvertChecked` branch. Loud when wrong. Low value.
- **`OhDataAuthRequirementsText.cs` (2)** — empty-collection edges on a file that went from **34
  mutants with zero coverage** to 2 survivors in #673. Good enough.

## Tier B — not enumerated, deliberately

693 core survivors, **446 of them in `OhDataEndpointFactory.cs`** and 118 in `EntitySetProfile.cs`. A
list that long is not a list anyone acts on, and the concentration restates #671 (the factory is
12,944 lines) rather than saying something about tests. Tier B is where a wrong answer produces a
wrong document or a wrong refusal — visible on inspection, recoverable — so it is recorded as a
count, not a queue. Regenerate it if #671 splits the factory and the distribution changes.

## Tier C — decided against, once

456 core and 82 mapper survivors are exception messages, log prose, and strings that are not
contracts. **Killing these means pinning implementation text**, which makes every reworded message a
failing test and teaches the suite to resist improvement.

The line is drawn at *contract* strings — `@odata.*` names, format specifiers, media types, header
values — which stay in their file's tier and which several Tier A entries above are. Everything else
is dropped, and this paragraph is the decision, so it is not rediscovered on the next sweep.

## What would change the answer

- **#671 splitting `OhDataEndpointFactory`** would redistribute 667 of the 1,220 core survivors and
  would likely shrink the CS0165 cascade, since Safe Mode discards per *method* within a file.
- **A companion-aware sweep topology** — mutating the core assembly against the union of the test
  projects that consume it — would close bias 3, at the cost of widening bias 2.
- Neither is scheduled. This document is the current state of what is known to be unconstrained.
