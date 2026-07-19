# #226 `Ignore(x => x.Property)` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `Ignore()` to `EntitySetProfile<TKey, TModel>` so a profile excludes CLR model properties from the entire OData surface — `$metadata`, query options, property routes, response bodies, and request binding — per the approved spec `docs/design/226-ignore-property-exclusion.md` and issue #226.

**Architecture:** Profile accumulates ignored CLR property names (expression-validated, key/nav-conflict-guarded) and exposes them via the internal `IEntitySetEndpointSource`. EDM removal rides the existing `_configurators` pipeline (auto-ejected by `AdvancedConfigure`). Wire suppression builds ONE derived `JsonSerializerOptions` per registration at `MapAll` using a `JsonTypeInfoResolver` modifier (A/B-benchmarked winner — see issue #226; do NOT use `JsonNode` post-processing, a measured 1.8×/4.3× regression). PATCH's CLR-reflection delta builder gets an explicit ignored-name filter (it does not consult the EDM, so EDM removal alone would be bypassable there).

**Tech Stack:** .NET 10 / C#, System.Text.Json `JsonTypeInfoResolver` modifiers, Microsoft.OData.ModelBuilder, xUnit + `WebApplicationFactory` (`TestHostBuilder`).

## Global Constraints

- `ImplicitUsings=disable` — every new/modified `.cs` file lists ALL `using` statements explicitly.
- No `Co-Authored-By` lines in any commit message.
- Never commit to local `develop`. All work on feature branch `feat/ignore-property-226`; merge via PR only (the user merges).
- PR body ends with `Fixes #226`, includes the A/B BenchmarkDotNet table from issue #226 as the perf evidence, and notes that k6 runs in CI on the PR.
- Match surrounding code style: file-scoped namespaces, existing comment density, `///` XML docs on public/protected API.
- Repo commands run from repo root `C:\Projects\OhData`. Build: `dotnet build src/OhData.sln`. Test: `dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~<X>"`.
- Husky pre-commit runs `dotnet-format` — if a commit fails formatting, re-stage the formatted files and commit again; never `--no-verify`.

---

### Task 0: Branch setup

**Files:** none (git only)

- [ ] **Step 1: Create the feature branch from up-to-date develop**

```bash
cd /c/Projects/OhData
git checkout develop
git pull --ff-only origin develop
git checkout -b feat/ignore-property-226
```

- [ ] **Step 2: Commit the already-written design doc**

The spec already exists at `docs/design/226-ignore-property-exclusion.md` (uncommitted).

```bash
git add docs/design/226-ignore-property-exclusion.md docs/superpowers/plans/2026-07-18-226-ignore-property-exclusion.md
git commit -m "docs: add #226 Ignore() design spec and implementation plan"
```

---

### Task 1: Profile API — `Ignore()`, validation, interface exposure, structural-route exclusion, EDM removal

**Files:**
- Modify: `src/OhData.AspNetCore/EntitySetProfile.cs` (fields near line 57, new method near the allowlist methods ~line 680, `BuildStructuralProperties` ~line 630, `VisitModelBuilder` ~line 524, interface impl block ~line 1587)
- Modify: `src/OhData.AspNetCore/IEntitySetEndpointSource.cs` (new member after `NavigationPropertyNames`, ~line 82)
- Test (create): `src/OhData.AspNetCore.Tests/IgnorePropertyProfileTests.cs`

**Interfaces:**
- Consumes: existing `ExtractNames`, `ThrowIfSealed`, `GetNavigationPropertyName`, `_configurators`, `_navigationPropertyNames`, `BuildStructuralProperties`.
- Produces (later tasks rely on these exact names):
  - `protected void Ignore(params Expression<Func<TModel, object?>>[] properties)` on `EntitySetProfile<TKey, TModel>`
  - `IReadOnlyCollection<string> IEntitySetEndpointSource.IgnoredPropertyNames { get; }` — the accumulated CLR names, `StringComparer.Ordinal` set semantics, empty when unused (never null)
  - `BuildStructuralProperties()` excludes ignored names → no property routes for them
  - Seal-time (`VisitModelBuilder`) `InvalidOperationException` when a name is both ignored and a navigation

- [ ] **Step 1: Write the failing tests**

Create `src/OhData.AspNetCore.Tests/IgnorePropertyProfileTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class IgnProfileModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal CostBasis { get; set; }
    public string InternalNotes { get; set; } = "";
    public List<IgnProfileChild>? Children { get; set; }
}

public sealed class IgnProfileChild
{
    public int Id { get; set; }
}

public class IgnorePropertyProfileTests
{
    private sealed class BasicIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public BasicIgnoreProfile() : base(x => x.Id)
        {
            Ignore(x => x.CostBasis, x => x.InternalNotes);
            Ignore(x => x.CostBasis); // duplicate — set semantics, harmless
        }
    }

    [Fact]
    public void Ignore_AccumulatesNames_ExposedViaEndpointSource()
    {
        IEntitySetEndpointSource source = new BasicIgnoreProfile();
        Assert.Equal(
            new[] { "CostBasis", "InternalNotes" },
            source.IgnoredPropertyNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Ignore_ExcludesNamesFromStructuralProperties()
    {
        IEntitySetEndpointSource source = new BasicIgnoreProfile();
        var names = source.StructuralProperties.Select(p => p.Name).ToList();
        Assert.DoesNotContain("CostBasis", names);
        Assert.DoesNotContain("InternalNotes", names);
        Assert.Contains("Name", names);
        Assert.Contains("Id", names);
    }

    private sealed class NoIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public NoIgnoreProfile() : base(x => x.Id) { }
    }

    [Fact]
    public void NoIgnore_ExposesEmptyCollection_NotNull()
    {
        IEntitySetEndpointSource source = new NoIgnoreProfile();
        Assert.NotNull(source.IgnoredPropertyNames);
        Assert.Empty(source.IgnoredPropertyNames);
    }

    private sealed class KeyIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public KeyIgnoreProfile() : base(x => x.Id)
        {
            Ignore(x => x.Id);
        }
    }

    [Fact]
    public void Ignore_KeyProperty_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new KeyIgnoreProfile());
        Assert.Contains("Id", ex.Message);
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public EmptyIgnoreProfile() : base(x => x.Id)
        {
            Ignore();
        }
    }

    [Fact]
    public void Ignore_NoSelectors_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new EmptyIgnoreProfile());
    }

    private sealed class NestedExpressionProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public NestedExpressionProfile() : base(x => x.Id)
        {
            Ignore(x => x.Name.Length);
        }
    }

    [Fact]
    public void Ignore_NestedExpression_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new NestedExpressionProfile());
    }
}
```

Note: profile constructors throw inside the test constructor call, so `Assert.Throws` wraps `new ...()` directly. The key-ignore and empty checks are call-time; nav-conflict is seal-time and is integration-tested in Task 3 (it requires `VisitModelBuilder`, which only the host pipeline drives).

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~IgnorePropertyProfileTests"
```

Expected: compile FAILURE — `'EntitySetProfile<int, IgnProfileModel>' does not contain a definition for 'Ignore'` and `'IEntitySetEndpointSource' does not contain a definition for 'IgnoredPropertyNames'`.

- [ ] **Step 3: Add the interface member**

In `src/OhData.AspNetCore/IEntitySetEndpointSource.cs`, directly after the `NavigationPropertyNames` property (line ~82):

```csharp
    /// <summary>
    /// Names of CLR properties excluded from the OData surface via
    /// <c>EntitySetProfile.Ignore(...)</c> (#226). Empty when the profile ignores nothing.
    /// Drives structural-route exclusion, the registration-wide serializer-options derivation,
    /// and the PATCH delta-builder filter.
    /// </summary>
    IReadOnlyCollection<string> IgnoredPropertyNames { get; }
```

- [ ] **Step 4: Implement `Ignore()` in the profile**

In `src/OhData.AspNetCore/EntitySetProfile.cs`:

(a) Field — next to `_navigationPropertyNames` (line ~57):

```csharp
    // Names of properties excluded from the OData surface via Ignore() (#226). Structural
    // properties, the EDM, response serialization, and request binding all consult this set.
    private readonly HashSet<string> _ignoredPropertyNames = new(StringComparer.Ordinal);
```

(b) Method — place directly after the `ExpandProperties(params string[]?)` overload (line ~754):

```csharp
    /// <summary>
    /// Excludes one or more model properties from the entire OData surface (#226): they are
    /// omitted from <c>$metadata</c>, rejected in <c>$select</c>/<c>$filter</c>/<c>$orderby</c>/
    /// <c>$expand</c> (as unknown properties), get no property routes, are omitted from every
    /// response body, and are not bound from POST/PUT/PATCH request bodies. Handlers still see
    /// the full CLR model — only the OData-exposed surface hides ignored properties.
    /// <para>
    /// Multiple calls accumulate. The key property cannot be ignored. A property declared as a
    /// navigation (<see cref="HasMany"/>/<see cref="HasOptional"/>/<see cref="HasRequired"/>)
    /// cannot also be ignored — that combination fails at startup.
    /// </para>
    /// </summary>
    /// <param name="properties">
    /// One or more direct property-access selectors, e.g. <c>x =&gt; x.InternalNotes</c>.
    /// At least one selector is required.
    /// </param>
    protected void Ignore(params Expression<Func<TModel, object?>>[] properties)
    {
        ThrowIfSealed();
        if (properties is null) throw new ArgumentNullException(nameof(properties));
        if (properties.Length == 0)
            throw new ArgumentException("At least one property selector is required.", nameof(properties));

        string keyPropertyName = GetNavigationPropertyName(_getKey.Body);
        string[] names = ExtractNames(properties);
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], keyPropertyName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The key property '{names[i]}' cannot be ignored — the key is required for " +
                    "routing, entity-id URLs, and $metadata.", nameof(properties));
            }

            if (!_ignoredPropertyNames.Add(names[i])) continue; // duplicate — already configured

            // EDM removal rides the configurator pipeline, so it is auto-ejected when
            // AdvancedConfigure is overridden (rows 1-2 of the spec's suppression table) while
            // runtime suppression (routes, wire, PATCH) still applies. ModelBuilder's
            // PropertySelectorVisitor strips the boxing Convert node, and Ignore<TProperty>
            // resolves the property by PropertyInfo, so passing the object?-typed selector is safe.
            Expression<Func<TModel, object?>> selector = properties[i];
            _configurators.Add(cfg => cfg.Ignore(selector));
        }
    }
```

(c) Structural exclusion — in `BuildStructuralProperties()` (line ~639), extend the filter chain:

```csharp
            .Where(prop => !_navigationPropertyNames.Contains(prop.Name)))
```

becomes

```csharp
            .Where(prop => !_navigationPropertyNames.Contains(prop.Name))
            .Where(prop => !_ignoredPropertyNames.Contains(prop.Name))) // #226
```

(d) Seal-time nav-conflict validation — in `VisitModelBuilder`, immediately after `_structuralProperties = BuildStructuralProperties();` (line ~524):

```csharp
        // #226: a name that is both ignored and declared as a navigation is a config
        // contradiction. Checked here (not in Ignore()) so it is declaration-order-independent.
        foreach (string ignored in _ignoredPropertyNames)
        {
            if (_navigationPropertyNames.Contains(ignored))
            {
                throw new InvalidOperationException(
                    $"Entity set '{EntitySetName}': property '{ignored}' is declared both as a " +
                    "navigation property (HasMany/HasOptional/HasRequired) and in Ignore(). " +
                    "Remove one of the declarations.");
            }
        }
```

(e) Interface implementation — in the `IEntitySetEndpointSource` explicit-impl block, next to `NavigationPropertyNames` (line ~1587):

```csharp
    IReadOnlyCollection<string> IEntitySetEndpointSource.IgnoredPropertyNames => _ignoredPropertyNames;
```

- [ ] **Step 5: Run the new tests and the full suite**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~IgnorePropertyProfileTests"
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj
```

Expected: new tests PASS; full suite PASS (the interface gained a member — `EntitySetProfile` is its only implementer, already updated).

- [ ] **Step 6: Commit**

```bash
git add src/OhData.AspNetCore/EntitySetProfile.cs src/OhData.AspNetCore/IEntitySetEndpointSource.cs src/OhData.AspNetCore.Tests/IgnorePropertyProfileTests.cs
git commit -m "feat: add EntitySetProfile.Ignore() with key/nav validation and EDM removal (#226)"
```

---

### Task 2: Serializer-options derivation helper

**Files:**
- Create: `src/OhData.AspNetCore/IgnoredPropertyJsonOptions.cs`
- Test (create): `src/OhData.AspNetCore.Tests/IgnoredPropertyJsonOptionsTests.cs`

**Interfaces:**
- Consumes: `IEntitySetEndpointSource.IgnoredPropertyNames`, `.ModelType`, `.EntitySetName` (Task 1).
- Produces (Task 3 relies on these exact signatures, both `internal static` on `internal static class IgnoredPropertyJsonOptions`, namespace `OhData.AspNetCore`):
  - `IReadOnlyDictionary<Type, IReadOnlySet<string>> BuildIgnoredPropertyMap(IEnumerable<IEntitySetEndpointSource> profiles)` — throws `InvalidOperationException` on same-`ModelType` set mismatch; returns only types with ≥1 ignored name.
  - `JsonSerializerOptions Build(JsonSerializerOptions baseOptions, IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredByType)` — returns `baseOptions` reference-unchanged when the map is empty.

- [ ] **Step 1: Write the failing tests**

Create `src/OhData.AspNetCore.Tests/IgnoredPropertyJsonOptionsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class IgnOptModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal CostBasis { get; set; }
}

public class IgnoredPropertyJsonOptionsTests
{
    private static readonly JsonSerializerOptions s_camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IReadOnlyDictionary<Type, IReadOnlySet<string>> Map(params string[] names) =>
        new Dictionary<Type, IReadOnlySet<string>>
        {
            [typeof(IgnOptModel)] = new HashSet<string>(names, StringComparer.Ordinal),
        };

    [Fact]
    public void Build_EmptyMap_ReturnsBaseOptionsReference()
    {
        var result = IgnoredPropertyJsonOptions.Build(
            s_camel, new Dictionary<Type, IReadOnlySet<string>>());
        Assert.Same(s_camel, result);
    }

    [Fact]
    public void Build_RemovesIgnoredMember_OnSerialize()
    {
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Map("CostBasis"));
        string json = JsonSerializer.Serialize(
            new IgnOptModel { Id = 1, Name = "W", CostBasis = 9.5m }, options);
        Assert.Contains("\"name\"", json);
        Assert.DoesNotContain("costBasis", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_IgnoredMember_NotBound_OnDeserialize()
    {
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Map("CostBasis"));
        var model = JsonSerializer.Deserialize<IgnOptModel>(
            "{\"id\":1,\"name\":\"W\",\"costBasis\":9.5}", options)!;
        Assert.Equal("W", model.Name);
        Assert.Equal(0m, model.CostBasis);
    }

    [Fact]
    public void Build_MapKeysAreClrNames_ImmuneToNamingPolicy()
    {
        // Map uses CLR name "CostBasis"; wire name is camelCase "costBasis" — still removed.
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Map("CostBasis"));
        string json = JsonSerializer.Serialize(new IgnOptModel { CostBasis = 1m }, options);
        Assert.DoesNotContain("costBasis", json, StringComparison.OrdinalIgnoreCase);

        // And with no naming policy the PascalCase wire name is removed too.
        var pascal = IgnoredPropertyJsonOptions.Build(new JsonSerializerOptions(), Map("CostBasis"));
        string pjson = JsonSerializer.Serialize(new IgnOptModel { CostBasis = 1m }, pascal);
        Assert.DoesNotContain("CostBasis", pjson);
    }

    [Fact]
    public void Build_UnmappedType_SerializesUnchanged()
    {
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Map("CostBasis"));
        string json = JsonSerializer.Serialize(new { costLike = 1 }, options);
        Assert.Contains("costLike", json);
    }

    private sealed class MapProfileA : EntitySetProfile<int, IgnOptModel>
    {
        public MapProfileA() : base(x => x.Id) { Ignore(x => x.CostBasis); EntitySetName = "SetA"; }
    }

    private sealed class MapProfileB : EntitySetProfile<int, IgnOptModel>
    {
        public MapProfileB() : base(x => x.Id) { Ignore(x => x.CostBasis); EntitySetName = "SetB"; }
    }

    private sealed class MapProfileNoIgnore : EntitySetProfile<int, IgnOptModel>
    {
        public MapProfileNoIgnore() : base(x => x.Id) { EntitySetName = "SetC"; }
    }

    [Fact]
    public void BuildMap_IdenticalSets_SameModelType_Allowed()
    {
        var map = IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap(
            new IEntitySetEndpointSource[] { new MapProfileA(), new MapProfileB() });
        Assert.Single(map);
        Assert.Contains("CostBasis", map[typeof(IgnOptModel)]);
    }

    [Fact]
    public void BuildMap_ConflictingSets_SameModelType_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap(
                new IEntitySetEndpointSource[] { new MapProfileA(), new MapProfileNoIgnore() }));
        Assert.Contains("SetA", ex.Message);
        Assert.Contains("SetC", ex.Message);
        Assert.Contains(nameof(IgnOptModel), ex.Message);
    }

    [Fact]
    public void BuildMap_NoIgnores_ReturnsEmptyMap()
    {
        var map = IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap(
            new IEntitySetEndpointSource[] { new MapProfileNoIgnore() });
        Assert.Empty(map);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~IgnoredPropertyJsonOptionsTests"
```

Expected: compile FAILURE — `The name 'IgnoredPropertyJsonOptions' does not exist`.

- [ ] **Step 3: Implement the helper**

Create `src/OhData.AspNetCore/IgnoredPropertyJsonOptions.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Builds the registration-wide <see cref="JsonSerializerOptions"/> that suppress properties
/// excluded via <c>EntitySetProfile.Ignore(...)</c> (#226) from response serialization and
/// request binding.
/// </summary>
/// <remarks>
/// Mechanism chosen by A/B benchmark (see issue #226): a <c>TypeInfoResolver</c> modifier removes
/// each ignored member from its type's <see cref="JsonTypeInfo"/>. The modifier runs once per
/// type — the resulting <see cref="JsonTypeInfo"/> is cached on the options instance — so steady
/// state simply has fewer members to emit/bind (measured 0.82× baseline time, 0.81× allocations
/// on a 100-item page). Do NOT replace this with post-serialization <c>JsonNode</c> key-stripping
/// for stylistic consistency with the <c>$select</c> pipeline: that alternative measured 1.83×
/// time and 4.32× allocations.
/// </remarks>
internal static class IgnoredPropertyJsonOptions
{
    /// <summary>
    /// Collects the ignored-property map for a registration, keyed by CLR model type. Throws
    /// <see cref="InvalidOperationException"/> when two profiles expose the same model type with
    /// different ignore sets — the derived options are keyed by CLR type, so a silent union
    /// would over-hide one set and taking either side alone would leak the other's secrets.
    /// Identical sets (including both-empty) are fine. Only types with at least one ignored
    /// name appear in the result.
    /// </summary>
    internal static IReadOnlyDictionary<Type, IReadOnlySet<string>> BuildIgnoredPropertyMap(
        IEnumerable<IEntitySetEndpointSource> profiles)
    {
        var firstSeen = new Dictionary<Type, (string EntitySetName, HashSet<string> Names)>();
        var result = new Dictionary<Type, IReadOnlySet<string>>();

        foreach (IEntitySetEndpointSource profile in profiles)
        {
            var names = new HashSet<string>(profile.IgnoredPropertyNames, StringComparer.Ordinal);
            if (firstSeen.TryGetValue(profile.ModelType, out (string EntitySetName, HashSet<string> Names) first))
            {
                if (!first.Names.SetEquals(names))
                {
                    throw new InvalidOperationException(
                        $"Entity sets '{first.EntitySetName}' and '{profile.EntitySetName}' both expose " +
                        $"model type '{profile.ModelType.Name}' but declare different Ignore() sets. " +
                        "Ignored properties are suppressed per CLR type across the whole registration, " +
                        "so the sets must match exactly (or the entity sets must use distinct CLR types).");
                }
                continue;
            }

            firstSeen[profile.ModelType] = (profile.EntitySetName, names);
            if (names.Count > 0) result[profile.ModelType] = names;
        }

        return result;
    }

    /// <summary>
    /// Returns <paramref name="baseOptions"/> unchanged (reference-equal) when
    /// <paramref name="ignoredByType"/> is empty — zero delta when the feature is unused.
    /// Otherwise returns one derived options instance whose resolver modifier removes the mapped
    /// members. Matching uses the CLR property name (via
    /// <see cref="JsonPropertyInfo.AttributeProvider"/>), so the map is immune to the
    /// configured naming policy.
    /// </summary>
    internal static JsonSerializerOptions Build(
        JsonSerializerOptions baseOptions,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredByType)
    {
        if (ignoredByType.Count == 0) return baseOptions;

        var derived = new JsonSerializerOptions(baseOptions);
        IJsonTypeInfoResolver resolver = derived.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        derived.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            if (!ignoredByType.TryGetValue(typeInfo.Type, out IReadOnlySet<string>? names)) return;
            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                if (typeInfo.Properties[i].AttributeProvider is PropertyInfo prop && names.Contains(prop.Name))
                    typeInfo.Properties.RemoveAt(i);
            }
        });
        return derived;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~IgnoredPropertyJsonOptionsTests"
```

Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OhData.AspNetCore/IgnoredPropertyJsonOptions.cs src/OhData.AspNetCore.Tests/IgnoredPropertyJsonOptionsTests.cs
git commit -m "feat: registration-wide ignored-property serializer options via resolver modifier (#226)"
```

---

### Task 3: `MapAll` wiring + end-to-end integration tests

**Files:**
- Modify: `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — `MapAll` only: after the `startupJsonOptions` resolution (~line 349), and the two thread-through sites (~line 589 `MapEntitySet` invoke, ~line 601 `MapUnboundOperations` call)
- Test (create): `src/OhData.AspNetCore.Tests/IgnorePropertyIntegrationTests.cs`

**Interfaces:**
- Consumes: `IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap` / `.Build` (Task 2); `_camelCaseSerializerOptions` (existing private static in the factory, ~line 1090); `registration.Profiles` (`IReadOnlyList<IEntitySetEndpointSource>`).
- Produces: every route in the registration serializes/deserializes through `effectiveJsonOptions`; startup throws on same-`TModel` ignore conflicts and on nav+ignore conflicts (Task 1's seal check fires during registration build).

- [ ] **Step 1: Wire the derived options into `MapAll`**

In `OhDataEndpointFactory.MapAll`, directly after the `startupJsonOptions` assignment (line ~349):

```csharp
        // #226: registration-wide ignored-property suppression. Validates same-model-type
        // conflicts, then — only when at least one profile declares ignores — derives a single
        // options instance whose resolver modifier removes the ignored members. When no profile
        // ignores anything the original options are threaded through unchanged (zero delta).
        var ignoredByType = IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap(registration.Profiles);
        JsonSerializerOptions? effectiveJsonOptions = ignoredByType.Count == 0
            ? startupJsonOptions
            : IgnoredPropertyJsonOptions.Build(startupJsonOptions ?? _camelCaseSerializerOptions, ignoredByType);
```

Then replace the two downstream uses of `startupJsonOptions`:
- line ~589: `.Invoke(null, new object?[] { group, profile, registration, loggerFactory, effectiveJsonOptions });`
- line ~601: `MapUnboundOperations(group, registration.UnboundOperations, effectiveJsonOptions);`

Add `using System.Text.Json;` only if not already present in the file (it is — verify, don't duplicate).

Note: `BuildIgnoredPropertyMap` runs during `MapAll`, i.e. inside `app.MapOhData()`, after the registration (and every profile's `VisitModelBuilder`) has been built — so Task 1's seal-time nav-conflict check fires first if both kinds of conflict exist. Both surface as `InvalidOperationException` from `MapOhData()`.

- [ ] **Step 2: Build to verify compilation**

```bash
dotnet build src/OhData.sln
```

Expected: 0 errors.

- [ ] **Step 3: Write the integration test matrix**

Create `src/OhData.AspNetCore.Tests/IgnorePropertyIntegrationTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

// Parent entity: ignores a primitive (CostBasis) and a complex property (Audit).
public sealed class IgnProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal CostBasis { get; set; }
    public IgnAudit? Audit { get; set; }
    public List<IgnTag>? Tags { get; set; }
}

public sealed class IgnAudit
{
    public string CreatedBy { get; set; } = "";
}

// Navigation child with its own profile ignoring InternalCode — proves $expand-nested hiding.
public sealed class IgnTag
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string InternalCode { get; set; } = "";
}

// Control entity in the same registration: no ignores; has a property whose name matches an
// ignored name on IgnProduct to prove suppression is per-type, not global.
public sealed class IgnControl
{
    public int Id { get; set; }
    public decimal CostBasis { get; set; }
}

internal static class IgnData
{
    internal static List<IgnProduct> Products() => new()
    {
        new IgnProduct
        {
            Id = 1, Name = "Widget", CostBasis = 8.5m,
            Audit = new IgnAudit { CreatedBy = "internal-user" },
        },
        new IgnProduct { Id = 2, Name = "Gadget", CostBasis = 12.0m },
    };

    internal static List<IgnTag> Tags() => new()
    {
        new IgnTag { Id = 10, Label = "blue", InternalCode = "SECRET-B" },
        new IgnTag { Id = 11, Label = "round", InternalCode = "SECRET-R" },
    };
}

public sealed class IgnProductProfile : EntitySetProfile<int, IgnProduct>
{
    // Captures what the handlers actually received, for binding assertions.
    internal static IgnProduct? LastPosted;
    internal static IgnProduct? LastPut;

    private readonly List<IgnProduct> _store = IgnData.Products();

    public IgnProductProfile() : base(x => x.Id)
    {
        Ignore(x => x.CostBasis, x => x.Audit);
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        ExpandEnabled = true;

        HasMany(x => x.Tags!, (int key, CancellationToken ct) =>
            Task.FromResult<IEnumerable<IgnTag>>(IgnData.Tags()));

        GetQueryable = ct => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(p => p.Id == id));
        Post = (model, ct) =>
        {
            LastPosted = model;
            model.Id = 99;
            _store.Add(model);
            return Task.FromResult<IgnProduct?>(model);
        };
        Put = (id, model, ct) =>
        {
            LastPut = model;
            return Task.FromResult(model);
        };
    }
}

public sealed class IgnTagProfile : EntitySetProfile<int, IgnTag>
{
    public IgnTagProfile() : base(x => x.Id)
    {
        Ignore(x => x.InternalCode);
        GetById = (id, ct) => Task.FromResult(IgnData.Tags().FirstOrDefault(t => t.Id == id));
    }
}

public sealed class IgnControlProfile : EntitySetProfile<int, IgnControl>
{
    public IgnControlProfile() : base(x => x.Id)
    {
        GetById = (id, ct) => Task.FromResult<IgnControl?>(new IgnControl { Id = id, CostBasis = 5m });
    }
}

public class IgnorePropertyIntegrationTests : IAsyncLifetime
{
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _fx = await TestHostBuilder.BuildAsync(b => b
            .AddProfile<IgnProductProfile>()
            .AddProfile<IgnTagProfile>()
            .AddProfile<IgnControlProfile>());
    }

    public async Task DisposeAsync() => await _fx.DisposeAsync();

    // ---- $metadata ----

    [Fact]
    public async Task Metadata_OmitsIgnoredProperties()
    {
        string xml = await _fx.Client.GetStringAsync("/odata/$metadata");
        Assert.DoesNotContain("CostBasis", xml);
        Assert.DoesNotContain("InternalCode", xml);
        Assert.Contains("Name", xml);
    }

    // ---- response bodies ----

    [Fact]
    public async Task CollectionGet_OmitsIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts");
        Assert.Contains("\"name\"", json);
        Assert.DoesNotContain("costBasis", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audit", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleGet_OmitsIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts(1)");
        Assert.Contains("\"name\"", json);
        Assert.DoesNotContain("costBasis", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audit", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpandedChild_HidesItsOwnIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts?$expand=Tags");
        Assert.Contains("\"label\"", json);
        Assert.DoesNotContain("internalCode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-", json);
    }

    [Fact]
    public async Task NavigationGet_HidesChildIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts(1)/Tags");
        Assert.Contains("\"label\"", json);
        Assert.DoesNotContain("internalCode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ControlEntity_SameNamedProperty_NotSuppressed()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnControls(1)");
        Assert.Contains("costBasis", json); // per-type suppression only
    }

    // ---- query options ----

    [Theory]
    [InlineData("/odata/IgnProducts?$select=CostBasis")]
    [InlineData("/odata/IgnProducts?$filter=CostBasis gt 1")]
    [InlineData("/odata/IgnProducts?$orderby=CostBasis")]
    public async Task QueryOption_NamingIgnoredProperty_Returns400(string url)
    {
        var resp = await _fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- property routes ----

    [Fact]
    public async Task PropertyRoute_ForIgnoredProperty_NotRegistered()
    {
        var resp = await _fx.Client.GetAsync("/odata/IgnProducts(1)/CostBasis");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var respValue = await _fx.Client.GetAsync("/odata/IgnProducts(1)/CostBasis/$value");
        Assert.Equal(HttpStatusCode.NotFound, respValue.StatusCode);

        var respOk = await _fx.Client.GetAsync("/odata/IgnProducts(1)/Name");
        Assert.Equal(HttpStatusCode.OK, respOk.StatusCode);
    }

    // ---- request binding ----

    [Fact]
    public async Task Post_IgnoredMembersInBody_NotBound()
    {
        IgnProductProfile.LastPosted = null;
        var body = new StringContent(
            "{\"name\":\"New\",\"costBasis\":42.5,\"audit\":{\"createdBy\":\"attacker\"}}",
            Encoding.UTF8, "application/json");
        var resp = await _fx.Client.PostAsync("/odata/IgnProducts", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(IgnProductProfile.LastPosted);
        Assert.Equal("New", IgnProductProfile.LastPosted!.Name);
        Assert.Equal(0m, IgnProductProfile.LastPosted.CostBasis);
        Assert.Null(IgnProductProfile.LastPosted.Audit);
    }

    [Fact]
    public async Task Put_IgnoredMembersInBody_NotBound()
    {
        IgnProductProfile.LastPut = null;
        var body = new StringContent(
            "{\"id\":1,\"name\":\"Renamed\",\"costBasis\":42.5}",
            Encoding.UTF8, "application/json");
        var resp = await _fx.Client.PutAsync("/odata/IgnProducts(1)", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(IgnProductProfile.LastPut);
        Assert.Equal("Renamed", IgnProductProfile.LastPut!.Name);
        Assert.Equal(0m, IgnProductProfile.LastPut.CostBasis);
    }
}

// ---- startup validation (separate hosts that must FAIL to build) ----

public sealed class IgnConflictA : EntitySetProfile<int, IgnProduct>
{
    public IgnConflictA() : base(x => x.Id)
    {
        EntitySetName = "ConflictA";
        Ignore(x => x.CostBasis);
        GetById = (id, ct) => Task.FromResult<IgnProduct?>(null);
    }
}

public sealed class IgnConflictB : EntitySetProfile<int, IgnProduct>
{
    public IgnConflictB() : base(x => x.Id)
    {
        EntitySetName = "ConflictB"; // same TModel, DIFFERENT ignore set (none)
        GetById = (id, ct) => Task.FromResult<IgnProduct?>(null);
    }
}

public sealed class IgnNavConflictProfile : EntitySetProfile<int, IgnProduct>
{
    public IgnNavConflictProfile() : base(x => x.Id)
    {
        Ignore(x => x.Tags);
        HasMany(x => x.Tags!); // same property declared as navigation — seal-time conflict
    }
}

public sealed class IgnNavConflictReversedProfile : EntitySetProfile<int, IgnProduct>
{
    public IgnNavConflictReversedProfile() : base(x => x.Id)
    {
        HasMany(x => x.Tags!); // declaration order reversed — must still throw
        Ignore(x => x.Tags);
    }
}

public class IgnorePropertyStartupValidationTests
{
    [Fact]
    public async Task SameModelType_DifferentIgnoreSets_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b
                .AddProfile<IgnConflictA>()
                .AddProfile<IgnConflictB>()));
        Assert.Contains("ConflictA", ex.Message);
        Assert.Contains("ConflictB", ex.Message);
        Assert.Contains(nameof(IgnProduct), ex.Message);
    }

    [Fact]
    public async Task IgnoreThenHasMany_SameProperty_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b.AddProfile<IgnNavConflictProfile>()));
        Assert.Contains("Tags", ex.Message);
    }

    [Fact]
    public async Task HasManyThenIgnore_SameProperty_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b.AddProfile<IgnNavConflictReversedProfile>()));
        Assert.Contains("Tags", ex.Message);
    }
}
```

- [ ] **Step 4: Run the integration tests**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~IgnoreProperty"
```

Expected: PASS. If `Metadata_OmitsIgnoredProperties` fails with `CostBasis` still present, the EDM `Ignore(selector)` call in Task 1 did not remove the property — debug via a minimal repro of `cfg.Ignore()` with the boxed `object?` selector before touching anything else (ModelBuilder's `PropertySelectorVisitor` is documented to strip `Convert` nodes; if it does not on this package version, rebuild an unboxed lambda with `Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(TModel), prop.PropertyType), Expression.Property(param, prop), param)` and invoke `Ignore<TProperty>` via `MakeGenericMethod(prop.PropertyType)`).

- [ ] **Step 5: Run the full suite**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj
```

Expected: PASS, no regressions (1053+ tests).

- [ ] **Step 6: Commit**

```bash
git add src/OhData.AspNetCore/OhDataEndpointFactory.cs src/OhData.AspNetCore.Tests/IgnorePropertyIntegrationTests.cs
git commit -m "feat: thread ignored-property serializer options through MapAll (#226)"
```

---

### Task 4: PATCH delta-builder filter

**Files:**
- Modify: `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — the PATCH handler's delta loop (~line 2913, inside `MapEntitySet<TKey, TModel>`)
- Modify (append tests): `src/OhData.AspNetCore.Tests/IgnorePropertyIntegrationTests.cs`

**Interfaces:**
- Consumes: `source.IgnoredPropertyNames` (Task 1; `source` is the startup-captured `IEntitySetEndpointSource`, already in scope in the handler closure).
- Produces: PATCH bodies naming an ignored member silently skip it — identical treatment to an unknown member today.

- [ ] **Step 1: Write the failing test**

Append to `IgnProductProfile`'s fields (in `IgnorePropertyIntegrationTests.cs`):

```csharp
    internal static IReadOnlyList<string>? LastPatchChangedNames;
```

and add a `Patch` handler in its constructor, after `Put`:

```csharp
        Patch = (id, delta, ct) =>
        {
            LastPatchChangedNames = delta.GetChangedPropertyNames().ToList();
            var existing = _store.FirstOrDefault(p => p.Id == id);
            if (existing is null) return Task.FromResult<IgnProduct?>(null);
            delta.Patch(existing);
            return Task.FromResult<IgnProduct?>(existing);
        };
```

Add the test to `IgnorePropertyIntegrationTests`:

```csharp
    [Fact]
    public async Task Patch_IgnoredMemberInBody_NotInDelta()
    {
        IgnProductProfile.LastPatchChangedNames = null;
        var body = new StringContent(
            "{\"name\":\"Patched\",\"costBasis\":99.9}",
            Encoding.UTF8, "application/json");
        var resp = await _fx.Client.PatchAsync("/odata/IgnProducts(2)", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(IgnProductProfile.LastPatchChangedNames);
        Assert.Contains("Name", IgnProductProfile.LastPatchChangedNames!);
        Assert.DoesNotContain("CostBasis", IgnProductProfile.LastPatchChangedNames!);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "FullyQualifiedName~Patch_IgnoredMemberInBody_NotInDelta"
```

Expected: FAIL — `CostBasis` IS in the changed names (the delta loop resolves body members via CLR reflection and binds it). This failure is the proof the bypass hole is real.

- [ ] **Step 3: Implement the filter**

In `OhDataEndpointFactory.cs`, PATCH handler delta loop (~line 2913). Current code:

```csharp
                    var patchDelta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                    foreach (var prop in body.EnumerateObject())
                    {
                        var clrProp = typeof(TModel).GetProperty(prop.Name,
                            BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                        if (clrProp is not null)
                        {
                            object? value = prop.Value.Deserialize(clrProp.PropertyType, jsonOptions);
                            patchDelta.TrySetPropertyValue(clrProp.Name, value);
                        }
                    }
```

Change the `if` to:

```csharp
                        // #226: ignored properties get the same silent-skip as unknown members.
                        // This loop resolves members via CLR reflection (not the EDM), so EDM
                        // removal alone would not stop an ignored member from binding here.
                        if (clrProp is not null && !source.IgnoredPropertyNames.Contains(clrProp.Name))
```

(`Enumerable.Contains` delegates to the underlying `HashSet<string>.Contains` — O(1), ordinal. `clrProp.Name` is the exact CLR name even for a case-insensitive body match, so ordinal is correct.)

- [ ] **Step 4: Run the test to verify it passes, then the full suite**

```bash
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "FullyQualifiedName~Patch_IgnoredMemberInBody_NotInDelta"
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj
```

Expected: both PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OhData.AspNetCore/OhDataEndpointFactory.cs src/OhData.AspNetCore.Tests/IgnorePropertyIntegrationTests.cs
git commit -m "feat: filter ignored properties out of the PATCH delta builder (#226)"
```

---

### Task 5: Documentation, CHANGELOG, follow-up issue, PR

**Files:**
- Create: `docs/ignoring-properties.md`
- Modify: `README.md` (the "Beyond the basics" feature list — add one bullet linking the new doc, matching the style of the existing bullets)
- Modify: `docs/property-access.md` (in "Enabling it", after the sentence about navigation-property exclusion, ~line 35)
- Modify: `CHANGELOG.md` (`[Unreleased]` → `### Added`)

**Interfaces:** none produced; documents Tasks 1–4.

- [ ] **Step 1: Write `docs/ignoring-properties.md`**

```markdown
# Ignoring Properties

`Ignore(...)` excludes model properties from the entire OData surface without touching the CLR
type — no `[JsonIgnore]`, no DTO split. The profile, not the POCO, defines what is exposed.

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        Ignore(x => x.CostBasis, x => x.InternalNotes);
        GetById = ...;
    }
}
```

## What "ignored" means

Handlers and the data layer still see the complete CLR model. The OData surface hides the
property everywhere:

| Surface | Behavior |
|---|---|
| `$metadata` (CSDL) | Property omitted |
| `$select` / `$filter` / `$orderby` / `$expand` | `400` — same error as any unknown property |
| Property routes (`GET/PUT/PATCH/DELETE /Set({key})/{Prop}`, `/$value`) | Not registered → `404` |
| Response bodies (collection, single, navigation, `$expand`-nested) | Member omitted |
| POST / PUT request bodies | Member not bound — silently skipped like an unknown member |
| PATCH request bodies | Member not in the `Delta<TModel>` |

An `$expand`-nested child hides *its own* profile's ignored properties automatically.

## Rules

- **Expression selectors only** (`x => x.Prop`) — the member must exist on the model, so typos
  are compile errors. Multiple calls accumulate; duplicates are harmless.
- **The key property cannot be ignored** (`ArgumentException` at the `Ignore` call).
- **A navigation property cannot be ignored.** Declaring the same property in `Ignore(...)` and
  `HasMany`/`HasOptional`/`HasRequired` (either order) throws `InvalidOperationException` at
  startup.
- **Entity sets sharing a CLR model type must declare identical ignore sets.** Suppression is
  keyed by CLR type across a registration, so `app.MapOhData()` throws at startup if two
  profiles over the same type disagree. Separate registrations (`AddOhData("v1", ...)` /
  `AddOhData("v2", ...)`) are independent — v2 may expose a property v1 ignores.
- **`AdvancedConfigure`** ejects the automatic EDM removal like all automatic EDM config — call
  `configuration.EntityType.Ignore(...)` yourself. Route suppression, wire suppression, and the
  validations above still apply.
- **ETags:** an ignored property MAY participate in `UseETag(...)` — useful for row-version
  columns that should never be exposed.
- **Navigation-only types** (a related type with no profile of its own) have no `Ignore`
  surface; give the type a profile if its wire shape needs trimming.

## Performance

Wire suppression uses a `JsonTypeInfoResolver` modifier baked into one derived
`JsonSerializerOptions` per registration. The modifier runs once per type (cached), so steady
state serializes *fewer* members than an un-ignored model — measured at 0.82× baseline time and
0.81× allocations for a 100-entity page ([#226](https://github.com/en-gen/OhData/issues/226) has
the full A/B table). When no profile ignores anything, the pipeline is byte-identical to before.

## Limitations

- The OpenAPI/NSwag companion packages generate schemas from CLR types and currently still list
  ignored properties in generated documents (runtime behavior is unaffected). Tracked as a
  follow-up alongside the other doc-reflection items.
```

- [ ] **Step 2: Add the README bullet, the property-access note, and the CHANGELOG entry**

README ("Beyond the basics" list) — add, in the style of neighboring bullets:

```markdown
- **[Ignoring properties](docs/ignoring-properties.md)** — `Ignore(x => x.CostBasis)` hides a
  property from `$metadata`, query options, routes, and every request/response body, without
  touching the CLR model.
```

`docs/property-access.md`, after the sentence ending "…declared as navigations via `HasMany`/`HasOptional`/`HasRequired`." (~line 35), append to that paragraph:

```markdown
Properties excluded via `Ignore(...)` also get no property routes — see
[ignoring-properties.md](ignoring-properties.md).
```

`CHANGELOG.md` under `[Unreleased]` / `### Added`:

```markdown
- `EntitySetProfile.Ignore(x => x.Property)` (#226): excludes model properties from the whole
  OData surface — `$metadata`, query options, property routes, response bodies, and request
  binding — via a per-registration `JsonTypeInfoResolver` modifier (A/B-benchmarked; zero cost
  when unused). Startup validation rejects ignoring the key, ignore/navigation conflicts, and
  same-model-type profiles with mismatched ignore sets.
```

If `[Unreleased]` or its `### Added` section does not exist yet (post-1.4.0 cut state), create them at the top following the file's existing section conventions.

- [ ] **Step 3: Full build + suite, commit**

```bash
dotnet build src/OhData.sln
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj
git add docs/ignoring-properties.md README.md docs/property-access.md CHANGELOG.md
git commit -m "docs: Ignore() property exclusion guide + README/CHANGELOG (#226)"
```

- [ ] **Step 4: File the OpenAPI-companion follow-up issue**

```bash
gh issue create --title "Companions: omit Ignore()d properties from generated OpenAPI/NSwag schemas" --milestone "1.5.0" --label "enhancement" --body "Follow-up to #226. The OpenApi/NSwag companion packages generate schemas from CLR types, so properties excluded via EntitySetProfile.Ignore() still appear in generated documents (runtime behavior is correct — they never cross the wire). Teach the companions to consult IEntitySetEndpointSource.IgnoredPropertyNames (or the registration's derived JsonSerializerOptions) so generated schemas match the real wire shape. Same family as #219/#220 (reflecting profile config into API docs)."
```

- [ ] **Step 5: Push and open the PR**

```bash
git push -u origin feat/ignore-property-226
gh pr create --base develop --title "feat: Ignore(x => x.Property) — exclude model properties from the OData surface (#226)" --body "$(cat <<'EOF'
## Summary

Adds `EntitySetProfile.Ignore(params Expression<Func<TModel, object?>>[])` (#226): full-hide
property exclusion across `$metadata`, query options, property routes, response bodies, and
request binding. Design doc: `docs/design/226-ignore-property-exclusion.md`.

- EDM removal rides the `_configurators` pipeline (auto-ejected under `AdvancedConfigure`)
- Wire suppression: ONE derived `JsonSerializerOptions` per registration via a
  `JsonTypeInfoResolver` modifier; original options threaded unchanged when unused (zero delta)
- PATCH delta builder explicitly filters ignored names (it resolves members via CLR reflection,
  not the EDM — without the filter, PATCH would be a bypass hole; the new test proves it)
- Startup validation: key-ignore, ignore/navigation conflicts (order-independent), and
  same-`TModel` profiles with mismatched ignore sets all fail fast

## Perf evidence (BenchmarkDotNet A/B, .NET 10.0.10, from issue #226)

| Read path (SerializeToNode, 100 items) | Mean | Ratio | Allocated | Alloc ratio |
|---|---:|---:|---:|---:|
| A — baseline (feature unused) | 118.4 us | 1.00x | 64,096 B | 1.00x |
| B — resolver modifier (this PR) | 96.9 us | 0.82x | 51,696 B | 0.81x |
| C — serialize-then-strip JsonNode (rejected) | 214.7 us | 1.83x | 276,953 B | 4.32x |

Write path: B 96.9 us / 37,784 B vs baseline 106.8 us / 48,184 B. The chosen mechanism is
faster than not having the feature at all (fewer members to emit/bind, modifier cached per
type); k6 runs in CI on this PR for the end-to-end numbers.

## Tests

Profile-level unit tests (accumulation, key/empty/nested-expression validation), options-helper
unit tests (zero-delta reference equality, removal, naming-policy immunity, map conflicts), and
an integration matrix ($metadata, response bodies incl. $expand-nested + navigation, per-type
control, query-option 400s, property-route 404s, POST/PUT/PATCH binding, startup validation
both declaration orders).

Fixes #226
EOF
)"
```

- [ ] **Step 6: Post-PR checks**

Monitor CI (build, tests, k6, codecov — codecov is informational). Address every code-quality
bot comment before handing to the user for merge; re-verify the PR head SHA afterward (bot
"potential fix" commits can land post-push and may not compile). The user merges the PR.

---

## Self-review notes

- **Spec coverage:** suppression rows 1–2 (Task 1 EDM), row 3 (Task 1 structural exclusion + Task 3 route 404 tests), row 4 (Tasks 2–3), row 5 (Tasks 2–3 POST/PUT tests), row 6 (Task 4). Validation table: key (Task 1), nav conflict both orders (Tasks 1/3), same-`TModel` conflict (Tasks 2/3), non-member expression (Task 1), seal (existing guard, Task 1 code path). Zero-delta guard (Task 2 reference-equality test). Interaction notes → docs (Task 5). Follow-up issue (Task 5).
- **Spec test 8** ("threads the original options instance") is verified at the unit level (`Build_EmptyMap_ReturnsBaseOptionsReference`) — reference equality is not observable over HTTP.
- **Type consistency:** `IgnoredPropertyNames` (`IReadOnlyCollection<string>`) is the single name used across Tasks 1/2/4; `IgnoredPropertyJsonOptions.Build`/`BuildIgnoredPropertyMap` signatures match between Tasks 2 and 3; test model/profile names (`IgnProduct`, `IgnProductProfile`, …) are consistent across Tasks 3–4.
