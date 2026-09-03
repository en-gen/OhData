# Error handling

## How a handler reports failure

The framework validates a great deal at its own boundary — key format, EDM nullability, capability
flags, property allowlists, deep-write shape — and answers `400` for each. But a rejection that
depends on domain state the framework cannot see (*"that SKU already exists"*, *"this transition is
not allowed"*) is the handler's to report.

Until `ConfigureExceptions`, it could not. Every handler delegate returns a domain type, so a
handler had exactly two exits — return a value, or throw — and throwing is always a logged `500`.
The options were to pretend the write succeeded, or to report a server fault for a client error.

```csharp
public class OrderProfile : EntitySetProfile<int, Order>
{
    public OrderProfile(AppDb db) : base(x => x.Id)
    {
        Post = async (order, ct) => { db.Add(order); await db.SaveChangesAsync(ct); return order; };

        ConfigureExceptions(e => e
            .Map<DbUpdateConcurrencyException>((ctx, ex) =>
                OhDataResult.Conflict(
                    "ConcurrencyConflict",
                    $"{ctx.EntitySetName} {ctx.Key} was modified by another request.")));
    }
}
```

The framework has **no compile-time dependency on any data-access library** and never will — it does
not know what `DbUpdateConcurrencyException` is. You name the type; it only maps.

## The rejections you can produce

`OhDataResult`'s constructor is private and the factory set is closed, so a status the framework
does not serve is unrepresentable rather than validated.

| factory | status | for |
|---|---|---|
| `BadRequest` | 400 | malformed, or fails a rule the framework cannot see |
| `Forbidden` | 403 | authenticated but not permitted |
| `NotFound` | 404 | the addressed resource does not exist |
| `Conflict` | 409 | well-formed and permitted, but conflicts with current state |
| `PreconditionFailed` | 412 | a precondition the handler checked itself |

Two absences are deliberate:

- **No `Created`.** The framework already decides `201` vs `204` from `Prefer: return=minimal`,
  builds the `@odata.id`, sets `Location` and injects the ETag. A handler choosing `Created` would
  be a second authority on a question it cannot answer — it never sees `Prefer`.
- **No `Unauthorized`.** `401` is about authentication, which is settled before a handler runs, so a
  handler producing one would be describing a decision it did not make. Use `Forbidden` for a
  domain rule; use [authorization](authorization.md) for the rest.

`PreconditionFailed` exists because the `If-Match` gate is **not** atomic — see
[etags.md](etags.md) — so a handler that closes that window itself with database-level optimistic
concurrency needs a way to say so.

## Choosing the exception type: narrow, always

**Name the narrowest exception type that means what you want to report.** A broad mapping reports
infrastructure failures as client errors, which tells retry logic the opposite of the truth.

This is not hypothetical. The `$expand` pushdown once caught `InvalidOperationException` and
answered `400`, and it turned out that:

- SqlClient reports **connection-pool exhaustion** as a plain `InvalidOperationException`
  (*"Timeout expired … max pool size was reached"*),
- `ObjectDisposedException` **derives from** `InvalidOperationException`, so a disposed `DbContext`
  matched too,
- and EF's *"a second operation was started on this context instance"* is one as well.

So `Map<InvalidOperationException>(…)` would report all three as your chosen client error. Mapping
`Exception` itself is refused outright at declaration for the same reason.

When several mappings match, **the most derived wins**, whichever order they were declared in — so
a base mapping plus a specific one behaves the way a `catch` ladder reads.

## A mapped exception is still logged

Converting a fault into a `4xx` removes it from error dashboards, so the framework logs every
mapped exception at `Warning` **with the original exception and its stack**. That line is the only
remaining trace of it; do not filter it out.

Anything you have *not* mapped is unchanged: a logged `500` carrying the generic OData envelope,
with the handler's own message never reaching the client.

## What the mapping sees

`OhDataExceptionContext<TModel>` is a union whose members are populated per seam and discriminated
by `Operation`. Switch on that and read what it implies, rather than null-checking defensively.

| member | populated |
|---|---|
| `EntitySetName`, `Operation`, `QueryString` | always |
| `Key` | keyed routes |
| `Model` | `Post`, `Put` |
| `Delta` | `Patch` |
| `Navigation` | navigation seams |

There is also a context-free overload — `Map<TException>(ex => …)` — for the common case where the
rejection does not depend on the request.

A request the client actually **aborted** is never mapped: there is no response left to write, and
that case is left to ASP.NET Core's own client-disconnect handling.
