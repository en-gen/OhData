# Samples

Standalone, clone-and-run example apps. Each sample is a self-contained project with its own
build. None of them is listed in `src/OhData.sln`, but a sample that has tests is referenced by its
test project, so `dotnet build src/OhData.sln` compiles it and `dotnet test src/OhData.sln` drives it
(`OhData.Sample.EfCoreSqlite` is covered by `SampleAppTests` in `OhData.AspNetCore.Mapper.Tests`).

The samples reference the framework by `ProjectReference` into `../src` so they always
exercise the current source. In your own application, install the NuGet package instead:

```
dotnet add package EnGen.OhData.AspNetCore
```

| Sample | What it shows |
|--------|---------------|
| [OhData.Sample.EfCoreSqlite](OhData.Sample.EfCoreSqlite/) | A real relational database (EF Core + SQLite, committed migrations) behind `GetQueryable` — `$filter`/`$orderby`/`$skip`/`$top` translate into SQL `WHERE`/`ORDER BY`/`LIMIT`/`OFFSET`, with SQL logging turned on so you can watch it happen. Also: batch-loaded `$expand` (no N+1), `$select`, `$count`, full CRUD, a DTO-projection entity set (wire model decoupled from the EF entities), a many-to-many whose join table never reaches the wire, and an API-model-versus-entity set (`Orders`) read through `MappedEntitySetProfile` and written through `DeltaProfile`. |
