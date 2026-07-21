using System;
using System.Collections.Generic;
using Microsoft.OData.Edm;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class DocsMapModel
{
    public int Id { get; set; }
    public decimal CostBasis { get; set; }
    public string InternalNotes { get; set; } = "";
}

/// <summary>
/// #228: unit tests for the CLR-type → ignored-names map the OpenAPI companions consult.
/// Pin the null-collection short-circuit, the nothing-ignored result, and the cross-registration
/// union semantics (a document holds one component schema per CLR type, so differing sets from
/// separate registrations merge conservatively).
/// </summary>
public class IgnoredPropertyDocsMapTests
{
    private sealed class IgnoringProfile : EntitySetProfile<int, DocsMapModel>
    {
        public IgnoringProfile() : base(x => x.Id)
        {
            EntitySetName = "DocsMapA";
            Ignore(x => x.CostBasis);
        }
    }

    private sealed class OtherIgnoringProfile : EntitySetProfile<int, DocsMapModel>
    {
        public OtherIgnoringProfile() : base(x => x.Id)
        {
            EntitySetName = "DocsMapB";
            Ignore(x => x.InternalNotes);
        }
    }

    private sealed class PlainProfile : EntitySetProfile<int, DocsMapModel>
    {
        public PlainProfile() : base(x => x.Id)
        {
            EntitySetName = "DocsMapC";
        }
    }

    private static OhDataRegistration Registration(params IEntitySetEndpointSource[] profiles) =>
        new("/odata", new EdmModel(), profiles);

    private static OhDataRegistrationCollection Collection(params OhDataRegistration[] registrations)
    {
        var collection = new OhDataRegistrationCollection();
        for (int i = 0; i < registrations.Length; i++)
        {
            collection.Add($"reg{i}", registrations[i]);
        }
        return collection;
    }

    [Fact]
    public void Build_NullCollection_ReturnsEmptyMap()
    {
        var map = IgnoredPropertyDocsMap.Build(null);
        Assert.Empty(map);
    }

    [Fact]
    public void Build_NothingIgnored_ReturnsEmptyMap()
    {
        var map = IgnoredPropertyDocsMap.Build(
            Collection(Registration(new PlainProfile())));
        Assert.Empty(map);
    }

    [Fact]
    public void Build_SingleRegistration_MapsIgnoredNamesByModelType()
    {
        var map = IgnoredPropertyDocsMap.Build(
            Collection(Registration(new IgnoringProfile(), new PlainProfile())));
        var names = Assert.Contains(typeof(DocsMapModel), (IReadOnlyDictionary<Type, IReadOnlySet<string>>)map);
        Assert.Contains("CostBasis", names);
        Assert.DoesNotContain("InternalNotes", names);
    }

    [Fact]
    public void Build_TwoRegistrations_SameModelType_UnionsIgnoredSets()
    {
        // v1 ignores CostBasis, v2 ignores InternalNotes — the shared component schema hides both
        // (conservative: prefer under-documenting over listing a deliberately hidden member).
        var map = IgnoredPropertyDocsMap.Build(Collection(
            Registration(new IgnoringProfile()),
            Registration(new OtherIgnoringProfile())));
        var names = Assert.Contains(typeof(DocsMapModel), (IReadOnlyDictionary<Type, IReadOnlySet<string>>)map);
        Assert.Contains("CostBasis", names);
        Assert.Contains("InternalNotes", names);
    }
}
