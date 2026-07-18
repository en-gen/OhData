using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
            Id = 1,
            Name = "Widget",
            CostBasis = 8.5m,
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
    internal static IReadOnlyList<string>? LastPatchChangedNames;

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
        Patch = (id, delta, ct) =>
        {
            LastPatchChangedNames = delta.GetChangedPropertyNames().ToList();
            var existing = _store.FirstOrDefault(p => p.Id == id);
            if (existing is null) return Task.FromResult<IgnProduct?>(null);
            delta.Patch(existing);
            return Task.FromResult<IgnProduct?>(existing);
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
    public async Task Metadata_OmitsIgnoredProperties_PerType()
    {
        string xml = await _fx.Client.GetStringAsync("/odata/$metadata");

        // Suppression is per entity type: IgnProduct/IgnTag lose their ignored properties while
        // IgnControl — which has its own, un-ignored property named CostBasis — keeps it.
        string product = EntityTypeElement(xml, nameof(IgnProduct));
        Assert.DoesNotContain("CostBasis", product);
        Assert.DoesNotContain("Audit", product);
        Assert.Contains("Name", product);

        string tag = EntityTypeElement(xml, nameof(IgnTag));
        Assert.DoesNotContain("InternalCode", tag);
        Assert.Contains("Label", tag);

        string control = EntityTypeElement(xml, nameof(IgnControl));
        Assert.Contains("CostBasis", control);
    }

    private static string EntityTypeElement(string csdl, string typeName)
    {
        int start = csdl.IndexOf($"<EntityType Name=\"{typeName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"EntityType '{typeName}' not found in $metadata");
        int end = csdl.IndexOf("</EntityType>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"EntityType '{typeName}' element not terminated");
        return csdl[start..end];
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
        // OhDataBuilder wraps seal-time (VisitModelBuilder) failures in an "OhData: failed to
        // build EDM for profile ..." InvalidOperationException; the conflict detail is inner.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b.AddProfile<IgnNavConflictProfile>()));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Tags", ex.InnerException!.Message);
        Assert.Contains("Ignore()", ex.InnerException.Message);
    }

    [Fact]
    public async Task HasManyThenIgnore_SameProperty_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b.AddProfile<IgnNavConflictReversedProfile>()));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Tags", ex.InnerException!.Message);
        Assert.Contains("Ignore()", ex.InnerException.Message);
    }
}
