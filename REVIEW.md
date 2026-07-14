# OhData Deep Review

Code review of OhData.AspNetCore (server) and OhData.Client projects. Findings are categorized by severity: **H** = high, **M** = medium, **L** = low.

Resolved items have been closed and removed. This file contains only open findings.

---

## Server: OhData.AspNetCore

### OData Spec Compliance

### Spec deviations to fix or formally declare

---

## Summary

| Sev | ID | Area | Finding |
|-----|------|------|---------|

## Resolved this cycle (removed per convention)

H-1 ($count pre-page total -- `ODataQueryResult.TotalCount`), M-1 ($expand N+1 -- batch-aware `BatchHandler` on `NavigationRouteDefinition` + `HasMany`/`HasOptional`/`HasRequired` batch overloads, collapsing N×P sequential handler calls to P), M-2 ($ref real `@odata.id` -- `ChildKeyPropertyName`, single-valued case completed in PR #115), M-8 (client `GetIfChangedAsync`/If-None-Match, PR #116), M-9 (captured-variable filter translation ~17.8x faster, PR #116), L-6 (lambda names rejected at startup, PR #66; test gap tracked as L-14), L-8 (pluralization escape hatch documented, PR #114), L-12 (streaming reads via `ResponseHeadersRead`, PR #116).

**This cycle:** M-3 (nav-route `$orderby` now applied in-memory, ascending/descending, multi-key, 400 on unknown property; `docs/navigation-routing.md` updated), M-4 (`Prefer: maxpagesize` now capped at `MaxTop` -- honored size is `min(maxpagesize, MaxTop)`, `Preference-Applied` reflects the applied value; `docs/query-options.md` Known Limitation note removed), L-14 (regression test added for `BindFunction`/`BindAction` lambda rejection), Q-1 (owner decision, 2026-07-14: `$metadata`/service document anonymous access is public by design -- documented in `docs/authorization.md#metadata-and-the-service-document-are-always-anonymous`), L-13 (`round()` now round-half-away-from-zero by default per Part 2 §5.1.1.9, configurable via new `RoundingMode` setting -- `BankersRounding` restores the old behavior for EF Core providers that can't translate the two-argument `Math.Round` overload; `docs/query-options.md`/`docs/spec-compliance.md` updated). A POST-nav/bound-action route-collision guard was also added (startup `InvalidOperationException`, matching the existing structural-property/bound-function collision idiom) -- not a REVIEW.md item, found during this hardening pass.
