# OhData Typed Client Source Generator — Implementation Plan

## 1. Generator Project Structure

### New project: `OhData.Client.Generator`

Location: `src/OhData.Client.Generator/OhData.Client.Generator.csproj`

```
src/
  OhData.Client.Generator/
    OhData.Client.Generator.csproj
    EntitySetProfileReceiver.cs        # ISyntaxContextReceiver / ForAttributeWithMetadataName predicate
    ProfileDescriptor.cs               # Plain data model for one detected profile
    ClientEmitter.cs                   # Renders typed client class
    ExtensionEmitter.cs                # Renders OhDataClient extension method
    OhDataClientGenerator.cs           # IIncrementalGenerator entry point
    OperationDetector.cs               # Heuristic: which delegate fields are assigned in the ctor
    Helpers/
      NamingHelpers.cs                 # Pluralise, PascalCase, etc.
      SymbolExtensions.cs              # INamedTypeSymbol helpers
```

### csproj contents

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <IsRoslynComponent>true</IsRoslynComponent>
    <!-- Generators must not produce their own output as a binary reference -->
    <IncludeBuildOutput>false</IncludeBuildOutput>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.3.4" PrivateAssets="all">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

Key constraint: generators must target `netstandard2.0`. The `LangVersion` can be `latest` in the csproj — Roslyn 4.x provides the compiler regardless.

### Wiring the generator into OhData.Client

Add to `OhData.Client.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\OhData.Client.Generator\OhData.Client.Generator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

This causes the generator to run when any project references `OhData.Client`. Alternatively, ship the generator as a NuGet package with a `build/` props file that wires the analyzer — but using `ProjectReference` is fine for this in-repo scenario.

### Solution file

Add `OhData.Client.Generator` and `OhData.Client.Generator.Tests` to `OhData.sln` in a new "Generators" solution folder.

---

## 2. Detecting Profile Registrations at Compile Time

### What to scan

The generator must find every concrete class in the compilation under analysis that:
1. Inherits (transitively) from `OhData.Abstractions.EntitySetProfile<TKey, TModel>`
2. Is non-abstract
3. Is accessible (public or internal — internal is fine because the generated client lives in the same assembly)

### Incremental pipeline

Use `IIncrementalGenerator` with a syntax-first predicate followed by a semantic filter:

```csharp
SyntaxProvider
  .CreateSyntaxProvider(
      predicate: static (node, _) => node is ClassDeclarationSyntax c
                                     && c.BaseList is { Types.Count: > 0 },
      transform: static (ctx, ct) => GetProfileDescriptor(ctx, ct))
  .Where(x => x is not null)
  .Collect()
  → RegisterSourceOutput(...)
```

The `transform` step calls `ctx.SemanticModel.GetDeclaredSymbol(classDecl)`, then walks the base type chain using `INamedTypeSymbol.BaseType` until it either finds a constructed `EntitySetProfile<TKey, TModel>` or exhausts the chain. The comparison must check the full metadata name `OhData.Abstractions.EntitySetProfile`2`, not the display name, because the generator runs in user assemblies that reference Abstractions transitively through OhData.Client.

#### Why not `ForAttributeWithMetadataName`?

There is no attribute on profile classes. The `[ODataEntitySet]` attribute is on the *model* type, not the profile. Sticking with the class-declaration syntax provider is the right approach.

---

## 3. Determining Which Operations Are Available

This is the core challenge: delegate assignments (`GetById = ...`, `Post = ...`) happen at runtime inside constructors. The Roslyn semantic model sees the source text, not runtime values. Two strategies exist:

### Strategy A (Recommended): Syntax Analysis of Constructor Body

Walk the constructor's syntax tree looking for simple assignment expressions of the form:

```csharp
this.GetById = <some-non-null-value>;
// or
GetById = ...;
```

Specifically: look for `AssignmentExpressionSyntax` nodes whose left-hand side is a `MemberAccessExpressionSyntax` or `IdentifierNameSyntax` that resolves (semantically) to one of the known delegate fields declared on `EntitySetProfile<TKey, TModel>`:

- `GetAll`
- `GetQueryable`
- `GetById`
- `Post`
- `PutById`
- `Patch`
- `Delete`

If the right-hand side is not `null` (i.e., it is anything other than a `LiteralExpressionSyntax` with `SyntaxKind.NullLiteralExpression`), the operation is considered "present".

This analysis stays fully in Roslyn — no runtime execution. It handles the common pattern of `GetById = (id, ct) => ...` correctly.

**Limitation**: If a profile conditionally assigns a delegate based on constructor parameters or a flag (`if (someFlag) { GetById = ...; }`), the generator will conservatively assume the operation is present (any assignment found → generate the method). This is safe — a client method that calls an unregistered server endpoint returns a 404, which is better than silently omitting the method.

**Alternative conservative default**: If no assignment is found at all for a given delegate field, do not emit the corresponding client method. This matches the intent in the requirements ("only operations that exist on the profile").

### Strategy B (Fallback): Emit All Methods, Let User Trim with an Attribute

Define an attribute `[ODataClientOperations(ODataOperations.Get | ODataOperations.Post)]` on the profile class. The generator reads the attribute argument and generates only the flagged methods. This is opt-in and more explicit, but requires the user to maintain it.

**Decision**: Implement Strategy A as the primary approach, with Strategy B as an attribute-override escape hatch.

### Detecting `EntitySetName`

The profile's `EntitySetName` is a protected `string` property with an `init` setter. The generator must find the effective value:

1. Look in the constructor body for `EntitySetName = "literal-string"` assignments.
2. If not found, apply the same pluralisation logic as `EntitySetNameConvention.Pluralize(modelTypeName)` — a straight port of the four-rule algorithm from `EntitySetNameConvention.cs` into the generator's `NamingHelpers.cs`.

The entity set name drives the extension method name: `EntitySetName` → `Widgets` → extension method `Widgets(this OhDataClient client)`.

---

## 4. Generated Code Shape

For each detected profile, the generator emits two files.

### 4a. Typed client class: `{ModelName}Client.g.cs`

```csharp
// <auto-generated/>
#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhData.Client;

namespace {ProfileNamespace};   // same namespace as the profile

/// <summary>
/// Strongly-typed OData client for the <c>{EntitySetName}</c> entity set.
/// Generated from <see cref="{ProfileName}"/>.
/// </summary>
public sealed partial class {ModelName}Client
{
    private readonly OhDataClient _client;

    public {ModelName}Client(OhDataClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>Returns a fluent query builder for <c>{EntitySetName}</c>.</summary>
    public EntitySetClient<{ModelName}> Query()
        => _client.For<{ModelName}>();

    // ── Emitted only when GetAll or GetQueryable is assigned ────────────────────
    public Task<List<{ModelName}>> ToListAsync(CancellationToken ct = default)
        => _client.For<{ModelName}>().ToListAsync(ct);

    public Task<{ModelName}?> FirstOrDefaultAsync(CancellationToken ct = default)
        => _client.For<{ModelName}>().FirstOrDefaultAsync(ct);

    public Task<long> CountAsync(CancellationToken ct = default)
        => _client.For<{ModelName}>().CountAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct = default)
        => _client.For<{ModelName}>().AnyAsync(ct);

    public Task<ODataPage<{ModelName}>> ToPageAsync(CancellationToken ct = default)
        => _client.For<{ModelName}>().ToPageAsync(ct);

    // ── Emitted only when GetById is assigned ───────────────────────────────────
    public Task<{ModelName}?> GetAsync({KeyType} key, CancellationToken ct = default)
        => _client.For<{ModelName}>().Key(key).GetAsync(ct);

    // ── Emitted only when Post is assigned ──────────────────────────────────────
    public Task<{ModelName}> InsertAsync({ModelName} entity, CancellationToken ct = default)
        => _client.For<{ModelName}>().InsertAsync(entity, ct);

    // ── Emitted only when PutById is assigned ───────────────────────────────────
    public Task<{ModelName}?> ReplaceAsync({KeyType} key, {ModelName} entity, CancellationToken ct = default)
        => _client.For<{ModelName}>().Key(key).PutAsync(entity, ct);

    // ── Emitted only when Patch is assigned ─────────────────────────────────────
    public Task<{ModelName}?> PatchAsync({KeyType} key, object patch, CancellationToken ct = default)
        => _client.For<{ModelName}>().Key(key).PatchAsync(patch, ct);

    // ── Emitted only when Delete is assigned ────────────────────────────────────
    public Task DeleteAsync({KeyType} key, CancellationToken ct = default)
        => _client.For<{ModelName}>().Key(key).DeleteAsync(ct);
}
```

The `{KeyType}` is extracted from the first type argument of the `EntitySetProfile<TKey, TModel>` base type. For `EntitySetProfile<int, Widget>`, this is `int`. The generator must emit the fully-qualified CLR name (e.g., `global::System.Int32` → rendered as `int` via the `SpecialType` enum) to avoid namespace collisions.

### 4b. Extension method file: `OhDataClientExtensions.g.cs`

All extension methods are accumulated into a single file to avoid partial class complications. The generator uses `RegisterSourceOutput` with the collected list and emits one file with one `static partial class OhDataClientExtensions`:

```csharp
// <auto-generated/>
#nullable enable
using OhData.Client;

namespace OhData.Client;

public static partial class OhDataClientExtensions
{
    // One extension per detected profile:
    public static WidgetClient Widgets(this OhDataClient client)
        => new(client);

    public static OrderClient Orders(this OhDataClient client)
        => new(client);
}
```

The extension method name is the `EntitySetName` value (e.g., `"Widgets"` → method name `Widgets`).

### Using `partial class` on the typed client

The generated class is `sealed partial`. This lets users add hand-written extra methods in their own partial file — e.g., to add a `SearchAsync` convenience method that composes filter + orderby. This is why `sealed partial` rather than `sealed` only.

### Namespace resolution

The generated typed client class goes in the same namespace as the profile class (resolved from `INamedTypeSymbol.ContainingNamespace`). The extension methods go in `OhData.Client` namespace since `OhDataClient` lives there, which avoids requiring a `using` statement.

---

## 5. Integration with OhData.Client

### No runtime changes required

The generator exclusively wraps existing public API (`OhDataClient.For<T>()`, `EntitySetClient<T>`, `KeyedEntitySetClient<T>`). No changes to `OhData.Client`, `OhData.Abstractions`, or `OhData.AspNetCore` are needed.

### Reference chain

User server project:
```
UserApp.Api
  → OhData.AspNetCore (server-side)
  → OhData.Abstractions (profiles live here)
```

User client project (or the same project for monorepo):
```
UserApp.Client
  → OhData.Client          (brings in the generator as an Analyzer)
  → UserApp.Shared         (or wherever Widget and WidgetProfile are defined)
```

The generator runs in the compilation of `UserApp.Client` (or whatever project references `OhData.Client`). It scans all `ClassDeclarationSyntax` nodes in that compilation's syntax trees — this includes types from *all source files* in the project, but crucially *not* types compiled into referenced assemblies.

**Implication**: Profile classes must be source-compiled into the project being generated — they cannot be in a separate pre-compiled assembly. This is the most important limitation (see section 7).

### Opting out

Because the generator runs for every project that references `OhData.Client`, it must silently produce no output when no profile classes are found. This is trivially handled: if the `Collect()` step returns an empty list, `RegisterSourceOutput` emits nothing.

Users who want to suppress generation can set:
```xml
<PropertyGroup>
  <OhDataGenerateTypedClient>false</OhDataGenerateTypedClient>
</PropertyGroup>
```
The generator reads this via `AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.OhDataGenerateTypedClient", out _)` and short-circuits if set to `false`.

---

## 6. Testing Strategy

### Test project: `OhData.Client.Generator.Tests`

```
src/
  OhData.Client.Generator.Tests/
    OhData.Client.Generator.Tests.csproj
    Snapshots/                           # Verify snapshot files (.verified.cs)
    GeneratorTests/
      FullProfileGeneratorTests.cs       # All six operations assigned
      PartialProfileGeneratorTests.cs    # Subsets (GetById only, Post only, etc.)
      NamingTests.cs                     # EntitySetName override, consonant-y pluralisation
      NoProfileGeneratorTests.cs         # Zero profiles → zero output
      MultipleProfilesGeneratorTests.cs  # Two profiles → two client classes, both extensions
      DiagnosticsTests.cs               # Future: unsupported patterns emit a warning
```

### csproj for the test project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" />
    <!-- Verify snapshot testing -->
    <PackageReference Include="Verify.Xunit" Version="23.*" />
    <PackageReference Include="Verify.SourceGenerators" Version="2.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OhData.Client.Generator\OhData.Client.Generator.csproj" />
    <ProjectReference Include="..\OhData.Abstractions\OhData.Abstractions.csproj" />
    <ProjectReference Include="..\OhData.Client\OhData.Client.csproj" />
  </ItemGroup>
</Project>
```

### How to write a generator test with Verify

The standard pattern (Verify.SourceGenerators):

```csharp
[UsesVerify]
public class FullProfileGeneratorTests
{
    [Fact]
    public Task FullProfile_GeneratesAllMethods()
    {
        var source = """
            using OhData.Abstractions;
            namespace MyApp;
            public class Widget { public int Id { get; set; } public string Name { get; set; } = ""; }
            internal class WidgetProfile : EntitySetProfile<int, Widget>
            {
                public WidgetProfile() : base(x => x.Id)
                {
                    GetAll    = (ct) => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
                    GetById   = (id, ct) => Task.FromResult<Widget?>(null);
                    Post      = (w, ct)  => Task.FromResult(w);
                    PutById   = (id, w, ct) => Task.FromResult(w);
                    Patch     = (id, w, ct) => Task.FromResult<Widget?>(null);
                    Delete    = (id, ct) => Task.FromResult(false);
                }
            }
            """;

        return TestHelper.Verify<OhDataClientGenerator>(source);
    }
}
```

On first run, Verify creates `.verified.cs` snapshot files. On subsequent runs, it diffs against them. Approve snapshots with `dotnet verify accept` or the IDE plugin.

### Key test cases to write

| Test | Input | Expected output |
|---|---|---|
| Full profile | All 6 delegate fields assigned | All 7 methods (Query + 6 operations) |
| GetById only | Only `GetById` assigned | `Query()`, `GetAsync(key)` |
| Post only | Only `Post` assigned | `Query()`, `InsertAsync(entity)` |
| EntitySetName override | `EntitySetName = "Things"` in ctor | Extension named `Things()` |
| Consonant-y plural | Model named `Category` | Extension named `Categories()` |
| Two profiles | `WidgetProfile` + `OrderProfile` | Two client classes, two extension entries |
| No profiles | Source with no `EntitySetProfile` subclass | Zero generated files |
| Abstract profile | Abstract subclass | Zero generated files |
| Internal model | `internal class Widget` | Generated class matches accessibility |

---

## 7. Tradeoffs and Limitations

### Limitation 1: Profile classes must be source-compiled in the referencing project

Roslyn incremental generators operate on the syntax trees of the compilation being analyzed. They do not see types defined in pre-compiled `.dll` references — only the source files of the current project. If a user puts their profiles in a shared class library compiled separately, the generator produces no output.

**Workaround**: Users must either compile profiles alongside client code, or manually write wrapper classes. Document this prominently.

### Limitation 2: Dynamic delegate detection can produce false negatives

If a delegate is assigned through a helper method call rather than directly in the constructor body, the generator may not detect it:

```csharp
// Generator will NOT detect this as "GetById assigned"
public WidgetProfile() : base(x => x.Id)
{
    Configure(this);
}
private static void Configure(WidgetProfile p)
{
    p.GetById = ...; // not visible in constructor body
}
```

**Workaround**: Provide the `[ODataClientOperations]` attribute escape hatch (Strategy B) so users can explicitly declare which operations exist when the syntax heuristic misses them.

### Limitation 3: Conditional assignments generate methods even if never reachable

```csharp
if (Environment.GetEnvironmentVariable("ENABLE_DELETE") == "1")
    Delete = (id, ct) => Task.FromResult(true);
```

The generator sees an assignment to `Delete` and emits `DeleteAsync`. At runtime, if the condition is false and the server handler is null, the server returns 405 or 404. The client method will throw an `ODataClientException`.

**Mitigation**: Document that generated methods reflect compile-time intent.

### Limitation 4: `EntitySetName` detection is syntax-only

If `EntitySetName` is set via a computed expression rather than a string literal:
```csharp
EntitySetName = typeof(Widget).Name + "s"; // generator cannot evaluate this
```
The generator falls back to the pluralisation algorithm. Document that `EntitySetName` must be a literal for the generator to use it.

### Limitation 5: `GetAll` and `GetQueryable` both map to the same client-side methods

The generated `ToListAsync`, `FirstOrDefaultAsync`, etc. delegate to `_client.For<Widget>().ToListAsync()` regardless of whether the server uses `GetAll` or `GetQueryable`. This is correct behavior — the client does not need to distinguish.

### Limitation 6: No support for bound operations in v1

`BindFunction`/`BindAction` call sites are detectable in syntax, but generating typed client methods for bound OData operations requires knowing parameter types and return types. The initial version should skip bound operation generation — document as a future v2 enhancement.

### Design decision: `sealed partial` vs `sealed`

The class is `sealed partial` to allow users to extend with hand-written methods in a companion file. This adds zero cost — if no companion file exists, the compiler treats it as a normal sealed class.

### Design decision: extension method namespace

The `OhDataClientExtensions` class lives in `OhData.Client` namespace. This means consumers get the extension methods with just `using OhData.Client;` — the same using they need to use `OhDataClient`.
