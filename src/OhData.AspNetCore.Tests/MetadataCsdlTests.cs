using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── Fixtures ─────────────────────────────────────────────────────────────────
//
// These fixtures live in this file (rather than Fixtures.cs) so that the metadata
// contract suite does not require modifying shared test fixtures.

internal class CsdlSpotCheckItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Nickname { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal Price { get; set; }
    public decimal? Discount { get; set; }
    public int? Quantity { get; set; }
}

internal class CsdlChildItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal class CsdlParentItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<CsdlChildItem>? Children { get; set; }
    public CsdlChildItem? OptionalChild { get; set; }
    public CsdlChildItem? RequiredChild { get; set; }
}

/// <summary>
/// Profile with a property type spread (int, string, nullable string, DateTime, decimal,
/// nullable decimal, nullable int) plus collection- and entity-level bound function/action —
/// used to spot-check CSDL property types, nullability, and bound-operation declarations.
/// </summary>
internal class CsdlSpotCheckProfile : EntitySetProfile<int, CsdlSpotCheckItem>
{
    public CsdlSpotCheckProfile() : base(x => x.Id)
    {
        EntitySetName = "CsdlSpotCheckItems";
        GetAll = (ct) => Task.FromResult<IEnumerable<CsdlSpotCheckItem>>(Array.Empty<CsdlSpotCheckItem>());

        BindFunction(GetCount);
        BindAction(ResetAll);
        BindEntityFunction(GetLabel);
        BindEntityAction(Rename);
    }

    // Collection-bound function — Edm.Int32 (non-nullable) return.
    private Task<int> GetCount() => Task.FromResult(0);

    // Collection-bound action — void return (no ReturnType element).
    private Task ResetAll() => Task.CompletedTask;

    // Entity-bound function — Edm.String (nullable-by-default) return; first param is the key.
    private Task<string> GetLabel(int key) => Task.FromResult("");

    // Entity-bound action — key + one string parameter.
    private Task Rename(int key, string newName) => Task.CompletedTask;
}

/// <summary>
/// Parent entity declaring a collection nav (Children), an optional single nav (OptionalChild),
/// and a required single nav (RequiredChild) — used to verify NavigationProperty CSDL shape.
/// </summary>
internal class CsdlParentProfile : EntitySetProfile<int, CsdlParentItem>
{
    public CsdlParentProfile() : base(x => x.Id)
    {
        EntitySetName = "CsdlParents";
        GetAll = (ct) => Task.FromResult<IEnumerable<CsdlParentItem>>(Array.Empty<CsdlParentItem>());

        HasMany(x => x.Children!,
            getAll: (id, ct) => Task.FromResult<IEnumerable<CsdlChildItem>>(Array.Empty<CsdlChildItem>()));
        HasOptional(x => x.OptionalChild!);
        HasRequired(x => x.RequiredChild!);
    }
}

/// <summary>
/// Separately registered entity set for <see cref="CsdlChildItem"/> — its presence as its own
/// EntitySet is what makes the framework emit NavigationPropertyBinding elements on CsdlParents.
/// </summary>
internal class CsdlChildProfile : EntitySetProfile<int, CsdlChildItem>
{
    public CsdlChildProfile() : base(x => x.Id)
    {
        EntitySetName = "CsdlChildItems";
        GetAll = (ct) => Task.FromResult<IEnumerable<CsdlChildItem>>(Array.Empty<CsdlChildItem>());
    }
}

internal class CsdlV1Item { public int Id { get; set; } public string Name { get; set; } = ""; }
internal class CsdlV1Profile : EntitySetProfile<int, CsdlV1Item>
{
    public CsdlV1Profile() : base(x => x.Id)
    {
        EntitySetName = "V1OnlyItems";
        GetAll = (ct) => Task.FromResult<IEnumerable<CsdlV1Item>>(Array.Empty<CsdlV1Item>());
    }
}

internal class CsdlV2Item { public int Id { get; set; } public string Name { get; set; } = ""; }
internal class CsdlV2Profile : EntitySetProfile<int, CsdlV2Item>
{
    public CsdlV2Profile() : base(x => x.Id)
    {
        EntitySetName = "V2OnlyItems";
        GetAll = (ct) => Task.FromResult<IEnumerable<CsdlV2Item>>(Array.Empty<CsdlV2Item>());
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class MetadataCsdlTests
{
    private static readonly XNamespace Edmx = "http://docs.oasis-open.org/odata/ns/edmx";
    private static readonly XNamespace Edm = "http://docs.oasis-open.org/odata/ns/edm";

    private static async Task<(XDocument Doc, HttpResponseMessage Response)> GetMetadataAsync(HttpClient client, string path = "/odata/$metadata")
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        string xml = await response.Content.ReadAsStringAsync();
        return (XDocument.Parse(xml), response);
    }

    private static IEnumerable<XElement> Schemas(XDocument doc) =>
        doc.Root!.Element(Edmx + "DataServices")!.Elements(Edm + "Schema");

    private static XElement? FindEntityType(XDocument doc, string name) =>
        Schemas(doc).Elements(Edm + "EntityType").FirstOrDefault(e => (string)e.Attribute("Name")! == name);

    private static XElement? FindEntityContainer(XDocument doc) =>
        Schemas(doc).Elements(Edm + "EntityContainer").FirstOrDefault();

    // ── Well-formedness / envelope / content-type ─────────────────────────────

    [Fact]
    public async Task Metadata_IsWellFormedXml_WithEdmxEnvelopeAndCorrectNamespaces()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        Assert.Equal(Edmx + "Edmx", doc.Root!.Name);
        Assert.Equal("4.0", (string)doc.Root!.Attribute("Version")!);
        var dataServices = doc.Root!.Element(Edmx + "DataServices");
        Assert.NotNull(dataServices);
        Assert.NotEmpty(dataServices!.Elements(Edm + "Schema"));
    }

    [Fact]
    public async Task Metadata_ContentType_IsApplicationXml()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var response = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
    }

    // ── EntityType: key + property types/nullability ──────────────────────────

    [Fact]
    public async Task Metadata_EntityType_KeyPropertyRef_MatchesKeySelector()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var entityType = FindEntityType(doc, nameof(CsdlSpotCheckItem));
        Assert.NotNull(entityType);
        var keyRefs = entityType!.Element(Edm + "Key")!.Elements(Edm + "PropertyRef").ToList();
        Assert.Single(keyRefs);
        Assert.Equal("Id", (string)keyRefs[0].Attribute("Name")!);
    }

    [Theory]
    [InlineData("Id", "Edm.Int32", false)]       // non-nullable int
    [InlineData("Name", "Edm.String", false)]    // non-nullable reference type (init-assigned default)
    [InlineData("Nickname", "Edm.String", true)] // nullable reference type (string?)
    [InlineData("CreatedAt", "Edm.DateTimeOffset", false)] // DateTime -> Edm.DateTimeOffset, non-nullable
    [InlineData("Price", "Edm.Decimal", false)]  // non-nullable decimal
    [InlineData("Discount", "Edm.Decimal", true)] // nullable decimal
    [InlineData("Quantity", "Edm.Int32", true)]  // nullable int
    public async Task Metadata_EntityType_PropertyType_And_Nullability_MatchModel(
        string propertyName, string expectedEdmType, bool expectedNullable)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var entityType = FindEntityType(doc, nameof(CsdlSpotCheckItem));
        Assert.NotNull(entityType);
        var prop = entityType!.Elements(Edm + "Property")
            .FirstOrDefault(p => (string)p.Attribute("Name")! == propertyName);
        Assert.NotNull(prop);
        Assert.Equal(expectedEdmType, (string)prop!.Attribute("Type")!);

        // Nullable defaults to true when the attribute is omitted (OData CSDL spec).
        bool actualNullable = prop.Attribute("Nullable") is not { } n || (bool)n;
        Assert.Equal(expectedNullable, actualNullable);
    }

    // ── NavigationProperty: collection vs single, nullability ─────────────────

    [Fact]
    public async Task Metadata_NavigationProperty_CollectionNav_HasCollectionType()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CsdlParentProfile>()
            .AddProfile<CsdlChildProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var parentType = FindEntityType(doc, nameof(CsdlParentItem));
        Assert.NotNull(parentType);
        var childrenNav = parentType!.Elements(Edm + "NavigationProperty")
            .FirstOrDefault(n => (string)n.Attribute("Name")! == "Children");
        Assert.NotNull(childrenNav);
        string type = (string)childrenNav!.Attribute("Type")!;
        Assert.StartsWith("Collection(", type, StringComparison.Ordinal);
        Assert.Contains(nameof(CsdlChildItem), type, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_NavigationProperty_OptionalSingleNav_IsNullable()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CsdlParentProfile>()
            .AddProfile<CsdlChildProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var parentType = FindEntityType(doc, nameof(CsdlParentItem));
        var optionalNav = parentType!.Elements(Edm + "NavigationProperty")
            .FirstOrDefault(n => (string)n.Attribute("Name")! == "OptionalChild");
        Assert.NotNull(optionalNav);
        // Single-valued nav properties default Nullable="true" when omitted.
        bool nullable = optionalNav!.Attribute("Nullable") is { } n ? (bool)n : true;
        Assert.True(nullable);
        Assert.DoesNotContain("Collection(", (string)optionalNav.Attribute("Type")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_NavigationProperty_RequiredSingleNav_IsNotNullable()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CsdlParentProfile>()
            .AddProfile<CsdlChildProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var parentType = FindEntityType(doc, nameof(CsdlParentItem));
        var requiredNav = parentType!.Elements(Edm + "NavigationProperty")
            .FirstOrDefault(n => (string)n.Attribute("Name")! == "RequiredChild");
        Assert.NotNull(requiredNav);
        Assert.Equal("false", (string)requiredNav!.Attribute("Nullable")!);
    }

    // ── EntitySet + NavigationPropertyBinding ──────────────────────────────────

    [Fact]
    public async Task Metadata_EntityContainer_ContainsEntitySetForEachProfile()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CsdlParentProfile>()
            .AddProfile<CsdlChildProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var container = FindEntityContainer(doc);
        Assert.NotNull(container);
        var sets = container!.Elements(Edm + "EntitySet").Select(e => (string)e.Attribute("Name")!).ToList();
        Assert.Contains("CsdlParents", sets);
        Assert.Contains("CsdlChildItems", sets);
    }

    [Fact]
    public async Task Metadata_NavigationPropertyBinding_PresentWhenTargetEntitySetRegistered()
    {
        // Both the parent (CsdlParents) and the nav target (CsdlChildItems) are registered as
        // entity sets in this host, so the framework should emit NavigationPropertyBinding
        // elements linking each nav property to its target set.
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CsdlParentProfile>()
            .AddProfile<CsdlChildProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var container = FindEntityContainer(doc);
        var parentSet = container!.Elements(Edm + "EntitySet")
            .First(e => (string)e.Attribute("Name")! == "CsdlParents");
        var bindings = parentSet.Elements(Edm + "NavigationPropertyBinding")
            .ToDictionary(b => (string)b.Attribute("Path")!, b => (string)b.Attribute("Target")!);

        Assert.Equal("CsdlChildItems", bindings["Children"]);
        Assert.Equal("CsdlChildItems", bindings["OptionalChild"]);
        Assert.Equal("CsdlChildItems", bindings["RequiredChild"]);
    }

    [Fact]
    public async Task Metadata_NavigationPropertyBinding_AbsentWhenTargetEntitySetNotRegistered()
    {
        // CsdlChildProfile is NOT registered here — only the nav property's declaring type is.
        // The framework cannot bind to an entity set that doesn't exist in this registration.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlParentProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var container = FindEntityContainer(doc);
        var parentSet = container!.Elements(Edm + "EntitySet")
            .First(e => (string)e.Attribute("Name")! == "CsdlParents");
        Assert.Empty(parentSet.Elements(Edm + "NavigationPropertyBinding"));
    }

    // ── Bound function / action declarations ───────────────────────────────────

    [Fact]
    public async Task Metadata_BoundFunction_CollectionLevel_IsBoundTrue_WithCollectionBindingParameter()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var fn = Schemas(doc).Elements(Edm + "Function")
            .FirstOrDefault(f => (string)f.Attribute("Name")! == "GetCount");
        Assert.NotNull(fn);
        Assert.Equal("true", (string)fn!.Attribute("IsBound")!);

        var bindingParam = fn.Elements(Edm + "Parameter").First();
        Assert.Equal("bindingParameter", (string)bindingParam.Attribute("Name")!);
        string bindingType = (string)bindingParam.Attribute("Type")!;
        Assert.StartsWith("Collection(", bindingType, StringComparison.Ordinal);
        Assert.Contains(nameof(CsdlSpotCheckItem), bindingType, StringComparison.Ordinal);

        var returnType = fn.Element(Edm + "ReturnType");
        Assert.NotNull(returnType);
        Assert.Equal("Edm.Int32", (string)returnType!.Attribute("Type")!);
        Assert.Equal("false", (string)returnType.Attribute("Nullable")!);
    }

    [Fact]
    public async Task Metadata_BoundAction_CollectionLevel_IsBoundTrue_NoReturnType()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var action = Schemas(doc).Elements(Edm + "Action")
            .FirstOrDefault(a => (string)a.Attribute("Name")! == "ResetAll");
        Assert.NotNull(action);
        Assert.Equal("true", (string)action!.Attribute("IsBound")!);
        Assert.Null(action.Element(Edm + "ReturnType"));
    }

    [Fact]
    public async Task Metadata_BoundFunction_EntityLevel_BindingParameterIsSingleEntityType()
    {
        // Entity-bound functions/actions bind to the single entity type, not Collection(...).
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var fn = Schemas(doc).Elements(Edm + "Function")
            .FirstOrDefault(f => (string)f.Attribute("Name")! == "GetLabel");
        Assert.NotNull(fn);
        Assert.Equal("true", (string)fn!.Attribute("IsBound")!);
        var bindingParam = fn.Elements(Edm + "Parameter").First();
        string bindingType = (string)bindingParam.Attribute("Type")!;
        Assert.DoesNotContain("Collection(", bindingType, StringComparison.Ordinal);
        Assert.Contains(nameof(CsdlSpotCheckItem), bindingType, StringComparison.Ordinal);

        var returnType = fn.Element(Edm + "ReturnType");
        Assert.NotNull(returnType);
        Assert.Equal("Edm.String", (string)returnType!.Attribute("Type")!);
    }

    [Fact]
    public async Task Metadata_BoundAction_EntityLevel_HasKeyBindingParameterPlusNamedParameter()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<CsdlSpotCheckProfile>());
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var action = Schemas(doc).Elements(Edm + "Action")
            .FirstOrDefault(a => (string)a.Attribute("Name")! == "Rename");
        Assert.NotNull(action);
        Assert.Equal("true", (string)action!.Attribute("IsBound")!);
        var parameters = action.Elements(Edm + "Parameter").ToList();
        Assert.Equal(2, parameters.Count);
        Assert.Equal("bindingParameter", (string)parameters[0].Attribute("Name")!);
        Assert.DoesNotContain("Collection(", (string)parameters[0].Attribute("Type")!, StringComparison.Ordinal);
        Assert.Equal("newName", (string)parameters[1].Attribute("Name")!);
        Assert.Equal("Edm.String", (string)parameters[1].Attribute("Type")!);
    }

    // ── Unbound operations: FunctionImport / ActionImport ──────────────────────

    [Fact]
    public async Task Metadata_UnboundFunction_RegisteredAsFunctionImport_IncludedInServiceDocument()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CsdlSpotCheckProfile>()
            .AddFunction((Func<int, int>)(x => x * 2), "UnboundDouble"));
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var container = FindEntityContainer(doc);
        var import = container!.Elements(Edm + "FunctionImport")
            .FirstOrDefault(f => (string)f.Attribute("Name")! == "UnboundDouble");
        Assert.NotNull(import);
        Assert.Equal("true", (string)import!.Attribute("IncludeInServiceDocument")!);

        // The operation itself is declared unbound (no IsBound="true" attribute).
        var fn = Schemas(doc).Elements(Edm + "Function")
            .First(f => (string)f.Attribute("Name")! == "UnboundDouble");
        Assert.Null(fn.Attribute("IsBound"));
    }

    [Fact]
    public async Task Metadata_UnboundAction_RegisteredAsActionImport()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CsdlSpotCheckProfile>()
            .AddAction((Action<string>)(s => { }), "UnboundNoop"));
        var (doc, _) = await GetMetadataAsync(fx.Client);

        var container = FindEntityContainer(doc);
        var import = container!.Elements(Edm + "ActionImport")
            .FirstOrDefault(a => (string)a.Attribute("Name")! == "UnboundNoop");
        Assert.NotNull(import);

        var action = Schemas(doc).Elements(Edm + "Action")
            .First(a => (string)a.Attribute("Name")! == "UnboundNoop");
        Assert.Null(action.Attribute("IsBound"));
    }

    // ── Multiple named registrations ────────────────────────────────────────────

    [Fact]
    public async Task Metadata_MultipleNamedRegistrations_EachContainsOnlyOwnSets()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData("v1", o => o.WithPrefix("/v1").AddProfile<CsdlV1Profile>());
        builder.Services.AddOhData("v2", o => o.WithPrefix("/v2").AddProfile<CsdlV2Profile>());
        await using var app = builder.Build();
        app.MapOhData("v1");
        app.MapOhData("v2");
        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();

        var (v1Doc, _) = await GetMetadataAsync(client, "/v1/$metadata");
        var (v2Doc, _) = await GetMetadataAsync(client, "/v2/$metadata");

        var v1Sets = FindEntityContainer(v1Doc)!.Elements(Edm + "EntitySet")
            .Select(e => (string)e.Attribute("Name")!).ToList();
        var v2Sets = FindEntityContainer(v2Doc)!.Elements(Edm + "EntitySet")
            .Select(e => (string)e.Attribute("Name")!).ToList();

        Assert.Contains("V1OnlyItems", v1Sets);
        Assert.DoesNotContain("V2OnlyItems", v1Sets);

        Assert.Contains("V2OnlyItems", v2Sets);
        Assert.DoesNotContain("V1OnlyItems", v2Sets);

        // Entity types are similarly isolated per registration.
        Assert.NotNull(FindEntityType(v1Doc, nameof(CsdlV1Item)));
        Assert.Null(FindEntityType(v1Doc, nameof(CsdlV2Item)));
        Assert.NotNull(FindEntityType(v2Doc, nameof(CsdlV2Item)));
        Assert.Null(FindEntityType(v2Doc, nameof(CsdlV1Item)));
    }
}
