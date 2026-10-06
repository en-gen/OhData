# Project Layout

Read this when you need the project list, target frameworks, or InternalsVisibleTo grants.

### Project layout

Eighteen projects, all in `src/OhData.sln`. The `Target` column is the literal
`TargetFramework(s)` from each `.csproj` - **read it before reaching for a .NET 9+ or .NET 10 API**.

| Project | Target | Role |
|---|---|---|
| `OhData.AspNetCore` | net8.0;net10.0 | Ships as `EnGen.OhData.AspNetCore`. All core and runtime types: `EntitySetProfile<TKey,TModel>`, `IEntitySetEndpointSource` (internal), `IVisitModelBuilder` (internal), `AuthorizationConfig`, `NavigationRouteDefinition`, `BoundOperationDefinition`, `OhDataBuilder`, `OhDataEndpointFactory`, `ExpandEngine` (internal — the `$expand` engine, extracted from `OhDataEndpointFactory` by #671 phase 1), `QueryOptionGate` (internal — the sigil/capability query-option gate and the #358/#385 arithmetic-fault and #494/#662 translation-fault classifiers, extracted from `OhDataEndpointFactory` by #671 phase 1), `BoundOperationResults` (internal — what a bound operation's result looks like on the wire and what bounds it, plus the response and paging metadata an operation route advertises, extracted from `OhDataEndpointFactory` by #671 phase 1), `OhDataRegistration`, `OhDataRegistrationCollection`, `OhDataDefaults`, `AddOhDataVersion` / `MapOhDataVersion` versioning helpers, `ODataEntitySetProfile<TKey,TModel>`, `IODataEntitySetEndpointSource`, `OpenTypeJsonOptions` / `IgnoredPropertyJsonOptions` (internal), `Delta<T>` sugar (`DeltaExtensions`), extension methods |
| `OhData.Client` | net8.0;net10.0 | Ships as `EnGen.OhData.Client`. Typed .NET OData 4.0 client with fluent LINQ-based filter/select/expand translation and pagination |
| `OhData.AspNetCore.OpenApi` | **net10.0** | Ships as `EnGen.OhData.AspNetCore.OpenApi` (#264). `Microsoft.AspNetCore.OpenApi` integration: `IOpenApiOperationTransformer`/`IOpenApiSchemaTransformer` implementations that document the OData query parameters per entity set's capability flags, apply auth/security requirements, and omit `Ignore()`d properties. **Single-TFM** - `Microsoft.AspNetCore.OpenApi` is referenced at `[10.*, 11)`, so there is no net8.0 build of this one |
| `OhData.AspNetCore.NSwag` | net8.0;net10.0 | Ships as `EnGen.OhData.AspNetCore.NSwag` (#264). Same surface as an NSwag `IOperationProcessor`/`ISchemaProcessor` |
| `OhData.AspNetCore.Mapper` | net8.0;net10.0 | Ships as `EnGen.OhData.AspNetCore.Mapper` (#651). API-model / entity separation, both halves: `MappedEntitySetProfile<TKey,TModel,TEntity>`, `ModelMap`/`ModelMapBuilder`, `ModelToEntityRewriter`, `MappedQueryComposer`, `MappedNavigationLoader`, `ModelMapValidator` (read) and `DeltaProfile` / `DeltaMapping<TModel,TEntity>` / `IDeltaFactory` (write, moved from the core in 2.0.0) |
| `OhData.AspNetCore.Swashbuckle` | net8.0;net10.0 | Ships as `EnGen.OhData.AspNetCore.Swashbuckle` (#264). Same surface as a Swashbuckle `IOperationFilter`/`ISchemaFilter` |
| `OhData.TestBench.AspNetCore` | net10.0 | Runnable demo app with EF Core InMemory, Swagger UI (via the Swashbuckle companion package) + Scalar, versioned v1/v2 registrations |
| `OhData.ClientTestBench.AspNetCore` | net10.0 | Runnable demo app used as server target for client integration tests |
| `OhData.AspNetCore.Tests` | net10.0 | xUnit integration tests using `WebApplicationFactory` via `TestHostBuilder`. The main suite |
| `OhData.AspNetCore.OpenApi.Tests` | net10.0 | xUnit tests for the `Microsoft.AspNetCore.OpenApi` companion package |
| `OhData.AspNetCore.NSwag.Tests` | net10.0 | xUnit tests for the NSwag companion package |
| `OhData.AspNetCore.Swashbuckle.Tests` | net10.0 | xUnit tests for the Swashbuckle companion package |
| `OhData.AspNetCore.Mapper.Tests` | net10.0 | xUnit tests for the mapper package, BOTH halves: the read half including the conformance oracle (a mapped profile and a mapper-free control profile must answer ~85 query constructs identically), and since #675 the delta-mapping half, whose five test files moved here from `OhData.AspNetCore.Tests` to sit with the code #665 moved. It links `TestHostBuilder.cs` from the core test project as a `<Compile>` item rather than copying it |
| `OhData.TestBench.AspNetCore.Tests` | net10.0 | xUnit tests over the test bench host (`DbContext` lifetime / scoped-profile wiring) |
| `OhData.Client.Tests` | net10.0 | xUnit tests for OhData.Client |
| `OhData.MicrosoftODataClient.Tests` | net10.0 | Compatibility tests against Microsoft.OData.Client |
| `OhData.Client.Benchmarks` | net10.0 | BenchmarkDotNet project for client library performance |
| `OhData.Server.Benchmarks` | net10.0 | BenchmarkDotNet project comparing OhData's minimal-API pipeline against `Microsoft.AspNetCore.OData`'s `ODataController`+`[EnableQuery]` pipeline; report in `docs/performance.md` (a docsite page — it carries the provenance of every figure) |

**`net8.0` on the two shipping libraries is load-bearing, not vestigial.** Several deliberate API
choices exist *because* of it and will silently break the net8.0 build if "simplified" against the
net10.0 API surface: `OpenTypeJsonOptions.FindInvalidDynamicKey` resolves array element and
dictionary value types by CLR reflection rather than `JsonTypeInfo.ElementType` (.NET 9+), and
`ETagValueFormatter.StableTypeName` avoids `Type.FullName` because a constructed generic embeds
`Version=8.0.0.0` on net8.0 and `Version=10.0.0.0` on net10.0. Each is commented at the site; check
the TFM before removing one. `dotnet build src/OhData.sln` builds every TFM, so a net8.0-only break
does surface locally.

### `InternalsVisibleTo`

There is no `AssemblyInfo.cs`. The grants are MSBuild `<InternalsVisibleTo Include="..." />` items in `src/OhData.AspNetCore/OhData.AspNetCore.csproj` (the SDK turns each into an assembly attribute), seven of them:

| Grantee | Why |
|---|---|
| `OhData.AspNetCore.Tests` | Access to the internal `IEntitySetEndpointSource` and `IVisitModelBuilder` interfaces. |
| `OhData.AspNetCore.OpenApi` | #228: the OpenAPI companion packages read the internal per-profile `IgnoredPropertyNames` (via `IgnoredPropertyDocsMap`) so generated schemas omit `Ignore()`d properties, matching the real wire shape. They also read the internal `SchemaPropertyCasing` so schema property names match the casing the serializer actually emits. |
| `OhData.AspNetCore.NSwag` | Same as above. |
| `OhData.AspNetCore.Swashbuckle` | Same as above. |
| `OhData.AspNetCore.Mapper` | #665: the three seams the delta move cost the core -- `OhDataBuilder.Services` (the registration is an extension method with no access to private state), `ProfileScanner`'s kind predicate (one scanner, both kinds, without the core naming a type it cannot reference), and `IOhDataStartupValidated` (the marker `MapOhData` resolves to force construction). Internal rather than public on all three: one consumer each. |
| `OhData.AspNetCore.Mapper.Tests` | #675: the delta-mapping tests moved into the mapper package's own test project, with the code #665 moved. One line needs it -- `Issue488DeltaMappingGapTests` constructs `ProfileScanner` directly, whose constructors and `Scan()` are internal. |
| `OhData.Server.Benchmarks` | #389: the open-type serialize path is reachable only through internals (`OpenTypeJsonOptions`, its `Build`/`BuildOpenComplexTypeContainerMap` entry points, `IsValidDynamicPropertyNameCached`). The grant exists so `OpenTypeKeyValidationBenchmarks` measures the **shipped** validator rather than a transcribed copy of it — a copy is exactly the mistake that produced a 12x-wrong number for this code path once already. It widens no public API and changes no behaviour. |

`src/OhData.Client/OhData.Client.csproj` carries the same pattern for `OhData.Client.Tests` and `OhData.Client.Benchmarks`.
