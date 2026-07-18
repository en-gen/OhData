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
