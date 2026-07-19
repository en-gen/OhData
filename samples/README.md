# Samples

Standalone, clone-and-run example apps. Each sample is a self-contained project with its own
build — none of them are part of `src/OhData.sln`, so they never affect the framework's CI
build or test runs.

The samples reference the framework by `ProjectReference` into `../src` so they always
exercise the current source. In your own application, install the NuGet package instead:

```
dotnet add package EnGen.OhData.AspNetCore
```

| Sample | What it shows |
|--------|---------------|
| [OhData.Sample.EfCoreSqlite](OhData.Sample.EfCoreSqlite/) | A real relational database (EF Core + SQLite, committed migrations) behind `GetQueryable` — `$filter`/`$orderby`/`$skip`/`$top` translate into SQL `WHERE`/`ORDER BY`/`LIMIT`/`OFFSET`, with SQL logging turned on so you can watch it happen. Also: batch-loaded `$expand` (no N+1), `$select`, `$count`, and full CRUD. |
