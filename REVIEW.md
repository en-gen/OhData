# OhData Deep Review

Code review of OhData.AspNetCore (server) and OhData.Client projects. Findings are categorized by severity: **H** = high, **M** = medium, **L** = low.

Resolved items have been closed and removed. This file contains only open findings.

---

## Server: OhData.AspNetCore

### OData Spec Compliance

#### M-1: `$expand` is N+1 per entity per navigation property

`OhDataEndpointFactory.cs` -- `ApplyExpandAsync` iterates every item and calls `navRoute.Handler(keyVal, ct)` individually. For 100 items with 2 expanded properties, this is 200 sequential async calls. OData **11.2.4.2** allows `$expand`, and users will reasonably expect it to perform well.

**Status:** Design adjudicated (additive `BatchHandler` on `NavigationRouteDefinition`, batch overloads on `HasMany`/`HasOptional`/`HasRequired`, auto-derived single-key handler). Implementation queued behind the malformed-payload fix PR.

#### M-3: Navigation-route `$orderby` is silently ignored

On `GET /{Set}({key})/{nav}` collection routes, `$top`/`$skip`/`$count`/`$select` are honored but `$orderby` is accepted and silently dropped (no sort, no error). OData 4.0 Minimal conformance requires a service to parse the system query options it supports and either apply them or reject the request -- silently returning unsorted data misleads clients. Documented in `docs/navigation-routing.md`; either apply `$orderby` on the returned collection or return `400 UnsupportedQueryOption`.

#### M-4: `Prefer: maxpagesize` overrides `MaxTop` with no ceiling

A client-supplied `maxpagesize` unconditionally replaces the server's `MaxTop` cap when `$top` is absent, with no upper bound -- a client can request arbitrarily large pages, defeating the server-side paging protection (DoS exposure). Documented as a Known Limitation in `docs/query-options.md`; fix by capping the honored `maxpagesize` at `MaxTop`.

### Spec deviations to fix or formally declare

#### L-13: `round()` uses banker's rounding

The `$filter` canonical function `round()` follows .NET's default `MidpointRounding.ToEven` (2.5 → 2), while OData-ABNF/Part 2 §5.1.1.9 specifies round-half-away-from-zero (2.5 → 3). Behavior is pinned by test `MathFn_Round_Double_UsesBankersRoundingOnMidpoint`. Either translate to away-from-zero rounding or declare the deviation in `docs/spec-compliance.md`.

#### L-14: `BindFunction`/`BindAction` lambda rejection lacks a regression test

The startup validation rejecting compiler-generated delegate names (added in PR #66, `BoundOperationDefinition.From`) has no test exercising it. Add a test passing a lambda and asserting `InvalidOperationException`.

### Design questions (owner decision, then close)

#### Q-1: `$metadata` and service document are never auth-protected

Both are mapped before per-profile authorization is applied, so they remain anonymous even when every registered profile requires auth. Behavior is documented and pinned by tests in `AuthorizationMatrixTests`. Decide: intentional (metadata is public by design) or add an opt-in to protect them.

---

## Summary

| Sev | ID | Area | Finding |
|-----|------|------|---------|
| M | M-1 | Server/Spec | `$expand` is N+1 (fix in flight) |
| M | M-3 | Server/Spec | Nav-route `$orderby` silently ignored |
| M | M-4 | Server/Hardening | `maxpagesize` bypasses `MaxTop` cap |
| L | L-13 | Server/Spec | `round()` banker's rounding deviates from spec |
| L | L-14 | Server/Test | No test for lambda rejection in bound operations |
| Q | Q-1 | Server/Design | `$metadata`/service doc never auth-protected |

## Resolved this cycle (removed per convention)

H-1 ($count pre-page total -- `ODataQueryResult.TotalCount`), M-2 ($ref real `@odata.id` -- `ChildKeyPropertyName`, single-valued case completed in PR #115), M-8 (client `GetIfChangedAsync`/If-None-Match, PR #116), M-9 (captured-variable filter translation ~17.8x faster, PR #116), L-6 (lambda names rejected at startup, PR #66; test gap tracked as L-14), L-8 (pluralization escape hatch documented, PR #114), L-12 (streaming reads via `ResponseHeadersRead`, PR #116).
