using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OhData;
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

    // #462: Build no longer takes a raw dictionary — it takes the base-chain resolver, which is what
    // stops a consumer keying it by the exact runtime type. Same data, wrapped.
    private static InheritedNameSets Sets(params string[] names) =>
        new(Map(names), StringComparer.Ordinal);

    [Fact]
    public void Build_EmptyMap_ReturnsBaseOptionsReference()
    {
        var result = IgnoredPropertyJsonOptions.Build(s_camel, InheritedNameSets.Empty);
        Assert.Same(s_camel, result);
    }

    [Fact]
    public void Build_RemovesIgnoredMember_OnSerialize()
    {
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Sets("CostBasis"));
        string json = JsonSerializer.Serialize(
            new IgnOptModel { Id = 1, Name = "W", CostBasis = 9.5m }, options);
        Assert.Contains("\"name\"", json);
        Assert.DoesNotContain("costBasis", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_IgnoredMember_NotBound_OnDeserialize()
    {
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Sets("CostBasis"));
        var model = JsonSerializer.Deserialize<IgnOptModel>(
            "{\"id\":1,\"name\":\"W\",\"costBasis\":9.5}", options)!;
        Assert.Equal("W", model.Name);
        Assert.Equal(0m, model.CostBasis);
    }

    [Fact]
    public void Build_MapKeysAreClrNames_ImmuneToNamingPolicy()
    {
        // Map uses CLR name "CostBasis"; wire name is camelCase "costBasis" — still removed.
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Sets("CostBasis"));
        string json = JsonSerializer.Serialize(new IgnOptModel { CostBasis = 1m }, options);
        Assert.DoesNotContain("costBasis", json, StringComparison.OrdinalIgnoreCase);

        // And with no naming policy the PascalCase wire name is removed too.
        var pascal = IgnoredPropertyJsonOptions.Build(new JsonSerializerOptions(), Sets("CostBasis"));
        string pjson = JsonSerializer.Serialize(new IgnOptModel { CostBasis = 1m }, pascal);
        Assert.DoesNotContain("CostBasis", pjson);
    }

    [Fact]
    public void Build_UnmappedType_SerializesUnchanged()
    {
        var options = IgnoredPropertyJsonOptions.Build(s_camel, Sets("CostBasis"));
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

    // ── #398 review HIGH-1: the withheld sets carry the BINDER's comparer ─────────────────────────
    //
    // This is the PRODUCER half of the fix, and the one the other three sites lean on: the walk, the
    // rewriter and the read-side inspection all consult these sets and none of them re-wraps (two of
    // them run per request). Revert the comparer here to StringComparer.Ordinal and the theory below
    // fails on every spelling but the exact one — as do the walk/rewriter/read-side theories in
    // OpenTypeJsonOptionsTests, which is the containment being case-sensitive again.

    [Theory]
    [InlineData("costBasis")]
    [InlineData("COSTBASIS")]
    [InlineData("CostBasis")]
    [InlineData("cOsTbAsIs")]
    public void BuildIgnoredJsonNameMap_UnderCaseInsensitiveBinding_ContainsEverySpelling(string spelling)
    {
        var binder = new JsonSerializerOptions(s_camel) { PropertyNameCaseInsensitive = true };
        var map = IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap(Map("CostBasis"), binder);

        // The JSON name, not the CLR name: the naming policy is camelCase here, and the set is read
        // off the real pre-ignore contract rather than re-derived.
        Assert.Contains(
            spelling, map.Resolve(typeof(IgnOptModel))!);
    }

    /// <summary>
    /// The opt-out, and the reason the comparer is a function of the options rather than a blanket
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>: with the binder configured case-sensitive, a
    /// case-differing body key would not have matched the declared member either, so it genuinely is
    /// not the withheld one.
    /// </summary>
    [Fact]
    public void BuildIgnoredJsonNameMap_UnderCaseSensitiveBinding_ContainsOnlyTheExactSpelling()
    {
        var binder = new JsonSerializerOptions(s_camel) { PropertyNameCaseInsensitive = false };
        var map = IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap(Map("CostBasis"), binder);

        Assert.Contains("costBasis", map.Resolve(typeof(IgnOptModel))!);
        Assert.DoesNotContain("CostBasis", map.Resolve(typeof(IgnOptModel))!);
    }

    [Fact]
    public void BuildIgnoredJsonNameMap_EmptyIn_EmptyOut()
    {
        Assert.Same(
            InheritedNameSets.Empty,
            IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap(
                new Dictionary<Type, IReadOnlySet<string>>(), s_camel));
    }

    // ── #398 review LOW-1: the eager startup resolution is wrapped, not raw ───────────────────────

    /// <summary>
    /// A resolver that refuses the model type — the shape a consumer reaches by registering their own
    /// <see cref="IJsonTypeInfoResolver"/> (a source-generated context that does not know the model)
    /// on the host's <c>JsonOptions</c>.
    /// </summary>
    private sealed class ThrowingResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            throw new InvalidOperationException("resolver refuses " + type.Name);
    }

    /// <summary>
    /// <c>BuildIgnoredJsonNameMap</c> resolves a contract per <c>Ignore()</c>d model type at
    /// <c>MapOhData()</c> — a startup resolution that did not exist before #398 stage 1. A raw
    /// System.Text.Json exception escaping from here would report the fault with no indication that
    /// <c>Ignore()</c> was what forced the resolution, which is precisely the gap
    /// <c>OpenTypeJsonOptions.ValidateOrThrow</c> wraps for its own probe. Same four clauses, same
    /// treatment.
    /// </summary>
    [Fact]
    public void BuildIgnoredJsonNameMap_WrapsAContractResolutionFailureWithContext()
    {
        var binder = new JsonSerializerOptions { TypeInfoResolver = new ThrowingResolver() };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap(Map("CostBasis"), binder));

        Assert.Contains(nameof(IgnOptModel), ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ignore(", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("resolver refuses", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same wrapping against a contract <c>System.Text.Json</c> itself rejects, reachable with no
    /// custom resolver at all: two members whose JSON names collide. Measured — the RESOLVER-level
    /// <c>GetTypeInfo</c> throws <see cref="InvalidOperationException"/> ("The JSON property name for
    /// '…' collides with another property") for this, which is the entry point this code path uses.
    /// </summary>
    [Fact]
    public void BuildIgnoredJsonNameMap_WrapsAContractSystemTextJsonRejects()
    {
        var map = new Dictionary<Type, IReadOnlySet<string>>
        {
            [typeof(IgnOptDuplicateJsonName)] = new HashSet<string>(new[] { "Kept" }, StringComparer.Ordinal),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap(map, new JsonSerializerOptions()));

        Assert.Contains(nameof(IgnOptDuplicateJsonName), ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ignore(", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
        Assert.Contains("collides", ex.InnerException!.Message, StringComparison.Ordinal);
    }
}

/// <summary>Two members whose JSON names collide — a contract System.Text.Json refuses to build.</summary>
public sealed class IgnOptDuplicateJsonName
{
    [JsonPropertyName("dup")] public string? First { get; set; }
    [JsonPropertyName("dup")] public string? Second { get; set; }
    public string? Kept { get; set; }
}
