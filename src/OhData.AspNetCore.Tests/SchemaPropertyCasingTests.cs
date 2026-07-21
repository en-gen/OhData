using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OData.Edm;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #258: unit tests for the CLR-type → response-naming-policy map the OpenAPI companions consult to
/// rename schema property keys so schema casing matches response casing. Pins the null-collection
/// short-circuit, the transitive-closure walk (nested types), the [JsonPropertyName]/policy
/// precedence, and the first-registration-wins tiebreak for a shared model type.
/// </summary>
public class SchemaPropertyCasingTests
{
    public sealed class CasingModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        [JsonPropertyName("sku_code")]
        public string SkuCode { get; set; } = "";

        public List<CasingChild>? Children { get; set; }
    }

    public sealed class CasingChild
    {
        public string Label { get; set; } = "";
    }

    private sealed class CasingProfile : EntitySetProfile<int, CasingModel>
    {
        public CasingProfile() : base(x => x.Id) => EntitySetName = "CasingModels";
    }

    private static OhDataRegistration Registration(JsonNamingPolicy? policy, params IEntitySetEndpointSource[] profiles) =>
        new("/odata", new EdmModel(), profiles, null, policy);

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
        Assert.Empty(SchemaPropertyCasing.Build(null));
    }

    [Fact]
    public void Build_MapsModelType_AndNestedType_ToRegistrationPolicy()
    {
        var map = SchemaPropertyCasing.Build(
            Collection(Registration(JsonNamingPolicy.CamelCase, new CasingProfile())));

        Assert.True(map.TryGetValue(typeof(CasingModel), out JsonNamingPolicy? modelPolicy));
        Assert.Same(JsonNamingPolicy.CamelCase, modelPolicy);
        // The nested child type is reachable via CasingModel.Children, so the closure walk records
        // it too — its response schema must follow the same policy.
        Assert.True(map.TryGetValue(typeof(CasingChild), out JsonNamingPolicy? childPolicy));
        Assert.Same(JsonNamingPolicy.CamelCase, childPolicy);
    }

    [Fact]
    public void Build_DefaultPolicy_IsNull_ButTypeIsPresent()
    {
        var map = SchemaPropertyCasing.Build(
            Collection(Registration(null, new CasingProfile())));

        // Membership (a null policy = PascalCase) must be distinguishable from "not an OhData type".
        Assert.True(map.TryGetValue(typeof(CasingModel), out JsonNamingPolicy? policy));
        Assert.Null(policy);
    }

    [Fact]
    public void Build_TwoRegistrations_SameModelType_SamePolicy_IsDeterministic()
    {
        var map = SchemaPropertyCasing.Build(Collection(
            Registration(JsonNamingPolicy.CamelCase, new CasingProfile()),
            Registration(JsonNamingPolicy.CamelCase, new CasingProfile())));

        Assert.True(map.TryGetValue(typeof(CasingModel), out JsonNamingPolicy? policy));
        Assert.Same(JsonNamingPolicy.CamelCase, policy);
    }

    [Fact]
    public void Build_TwoRegistrations_SameModelType_ConflictingPolicies_PicksOne()
    {
        // A shared model type carrying different policies cannot be represented by the single
        // component schema an OpenAPI document holds per CLR type. One policy wins (unspecified
        // which — the registration collection is unordered); the map must not crash or drop the
        // type. Configuring a shared model type with one casing policy avoids the ambiguity.
        var map = SchemaPropertyCasing.Build(Collection(
            Registration(null, new CasingProfile()),
            Registration(JsonNamingPolicy.CamelCase, new CasingProfile())));

        Assert.True(map.TryGetValue(typeof(CasingModel), out JsonNamingPolicy? policy));
        Assert.True(policy is null || ReferenceEquals(policy, JsonNamingPolicy.CamelCase));
    }

    [Theory]
    [InlineData(nameof(CasingModel.Name), null, "Name")]        // null policy = PascalCase = CLR name
    [InlineData(nameof(CasingModel.Name), "camel", "name")]     // camelCase converts
    [InlineData(nameof(CasingModel.SkuCode), null, "sku_code")] // [JsonPropertyName] wins over policy
    [InlineData(nameof(CasingModel.SkuCode), "camel", "sku_code")]
    public void ResolveResponseName_MatchesResponsePrecedence(string clrProperty, string? policyKey, string expected)
    {
        JsonNamingPolicy? policy = policyKey == "camel" ? JsonNamingPolicy.CamelCase : null;
        string actual = SchemaPropertyCasing.ResolveResponseName(typeof(CasingModel).GetProperty(clrProperty)!, policy);
        Assert.Equal(expected, actual);
    }
}
