using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #206: byte-identity matrix — every response must be IDENTICAL with pushdown on and off.
// The projection changes what the LINQ provider materializes, never the wire.

public sealed class PushProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Sku { get; set; } = "";
    public PushDimensions? Dimensions { get; set; }
    public List<PushPart>? Parts { get; set; }
}

public sealed class PushDimensions
{
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class PushPart
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>ETag host model — same shape, separate type so UseETag state doesn't leak.</summary>
public sealed class PushTaggedItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; }
}

/// <summary>Positional record — no parameterless ctor, pushdown must fall back.</summary>
public sealed record PushRecordItem(int Id, string Name, decimal Price);

/// <summary>Get-only computed property — fallback only when it is actually selected.</summary>
public sealed class PushComputedItem
{
    public int Id { get; set; }
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    public string FullName => $"{First} {Last}";
}

internal static class PushData
{
    internal static List<PushProduct> Products() => new()
    {
        new PushProduct
        {
            Id = 1, Name = "Widget", Price = 19.99m, Sku = "W-1",
            Dimensions = new PushDimensions { Width = 2.5, Height = 4.0 },
        },
        new PushProduct { Id = 2, Name = "Gadget", Price = 5.25m, Sku = "G-2" },
        new PushProduct { Id = 3, Name = "Gizmo", Price = 12.00m, Sku = "Z-3" },
    };

    internal static List<PushPart> Parts() => new()
    {
        new PushPart { Id = 10, Label = "bolt" },
        new PushPart { Id = 11, Label = "washer" },
    };
}

public sealed class PushProductProfile : EntitySetProfile<int, PushProduct>
{
    private readonly List<PushProduct> _store = PushData.Products();

    public PushProductProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        HasMany(x => x.Parts!, (int key, CancellationToken ct) =>
            Task.FromResult<IEnumerable<PushPart>>(PushData.Parts()));

        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
    }
}

public sealed class PushTaggedProfile : EntitySetProfile<int, PushTaggedItem>
{
    private readonly List<PushTaggedItem> _store = new()
    {
        new PushTaggedItem { Id = 1, Name = "A", Version = 7 },
        new PushTaggedItem { Id = 2, Name = "B", Version = 3 },
    };

    public PushTaggedProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        UseETag(x => x.Version);
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

public sealed class PushRecordProfile : EntitySetProfile<int, PushRecordItem>
{
    private readonly List<PushRecordItem> _store = new()
    {
        new PushRecordItem(1, "R1", 1.5m),
        new PushRecordItem(2, "R2", 2.5m),
    };

    public PushRecordProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
    }
}

public sealed class PushComputedProfile : EntitySetProfile<int, PushComputedItem>
{
    private readonly List<PushComputedItem> _store = new()
    {
        new PushComputedItem { Id = 1, First = "Ada", Last = "Lovelace" },
    };

    public PushComputedProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
    }
}

/// <summary>
/// UseETag over a get-only computed property: the ETag name is capturable (direct member) but
/// the projection set then contains a setterless member — the realistic route to the
/// setter-check fallback, since the EDM excludes get-only properties from $select entirely.
/// </summary>
public sealed class PushEtagComputedItem
{
    public int Id { get; set; }
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    public string FullName => $"{First} {Last}";
}

public sealed class PushEtagComputedProfile : EntitySetProfile<int, PushEtagComputedItem>
{
    private readonly List<PushEtagComputedItem> _store = new()
    {
        new PushEtagComputedItem { Id = 1, First = "Grace", Last = "Hopper" },
    };

    public PushEtagComputedProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        UseETag(x => x.FullName);
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>
/// UseETag with a COMPUTED selector: ETag property names are unknowable, so pushdown must fall
/// back at request time (distinct from the metadata-level null check — this exercises the
/// request-path branch).
/// </summary>
public sealed class PushEtagUnknowableItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class PushEtagUnknowableProfile : EntitySetProfile<int, PushEtagUnknowableItem>
{
    private readonly List<PushEtagUnknowableItem> _store = new()
    {
        new PushEtagUnknowableItem { Id = 1, Name = "U1" },
    };

    public PushEtagUnknowableProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        UseETag(x => x.Name.Length); // computed — names unknowable
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>
/// UseETag over a NAVIGATION property: the name is capturable (direct member) but it is not a
/// structural property, so the ETag-name-not-structural fallback branch fires at request time.
/// </summary>
public sealed class PushEtagNavItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<PushPart>? Parts { get; set; }
}

public sealed class PushEtagNavProfile : EntitySetProfile<int, PushEtagNavItem>
{
    private readonly List<PushEtagNavItem> _store = new()
    {
        new PushEtagNavItem { Id = 1, Name = "N1" },
    };

    public PushEtagNavProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        HasMany(x => x.Parts!, (int key, CancellationToken ct) =>
            Task.FromResult<IEnumerable<PushPart>>(PushData.Parts()));
        UseETag(x => x.Parts); // direct member, but a navigation — not structural
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

public class SelectPushdownTests : IAsyncLifetime
{
    private TestFixture _on = null!;
    private TestFixture _off = null!;

    public async Task InitializeAsync()
    {
        static void Configure(OhDataBuilder b) => b
            .AddEntitySetProfile<PushProductProfile>()
            .AddEntitySetProfile<PushTaggedProfile>()
            .AddEntitySetProfile<PushRecordProfile>()
            .AddEntitySetProfile<PushComputedProfile>()
            .AddEntitySetProfile<PushEtagComputedProfile>()
            .AddEntitySetProfile<PushEtagUnknowableProfile>()
            .AddEntitySetProfile<PushEtagNavProfile>();

        _on = await TestHostBuilder.BuildAsync(Configure);
        _off = await TestHostBuilder.BuildAsync(b =>
        {
            b.WithDefaults(d => d.SelectPushdownEnabled = false);
            Configure(b);
        });
    }

    public async Task DisposeAsync()
    {
        await _on.DisposeAsync();
        await _off.DisposeAsync();
    }

    private async Task AssertByteIdentical(string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var responseOn = await _on.Client.GetAsync(url);
        var responseOff = await _off.Client.GetAsync(url);

        Assert.Equal(responseOff.StatusCode, responseOn.StatusCode);
        Assert.Equal(expected, responseOn.StatusCode);
        Assert.Equal(
            await responseOff.Content.ReadAsStringAsync(),
            await responseOn.Content.ReadAsStringAsync());
        Assert.Equal(
            responseOff.Headers.ETag?.ToString(),
            responseOn.Headers.ETag?.ToString());
    }

    [Theory]
    [InlineData("/odata/PushProducts?$select=name")]
    [InlineData("/odata/PushProducts?$select=name,price")]
    [InlineData("/odata/PushProducts?$select=name&$filter=price%20gt%206&$orderby=name%20desc&$skip=1&$top=1")]
    [InlineData("/odata/PushProducts?$select=name&$count=true")]
    [InlineData("/odata/PushProducts?$select=name&$expand=Parts")]
    [InlineData("/odata/PushProducts?$select=dimensions")]
    [InlineData("/odata/PushProducts?$select=dimensions/width")]
    [InlineData("/odata/PushProducts")]
    [InlineData("/odata/PushTaggedItems?$select=name")]
    [InlineData("/odata/PushRecordItems?$select=name")]
    [InlineData("/odata/PushComputedItems?$select=first")]
    [InlineData("/odata/PushEtagComputedItems?$select=first")]
    [InlineData("/odata/PushEtagUnknowableItems?$select=name")]
    [InlineData("/odata/PushEtagNavItems?$select=name")]
    public Task Responses_AreByteIdentical_PushdownOnVsOff(string url) => AssertByteIdentical(url);

    [Fact]
    public Task GetOnlyProperty_NotInEdm_400IdenticallyOnBothHosts() =>
        // Convention EDM excludes get-only CLR properties, so selecting one is an unknown
        // property on BOTH hosts — identity must hold for the error path too.
        AssertByteIdentical("/odata/PushComputedItems?$select=fullName", HttpStatusCode.BadRequest);

    [Fact]
    public async Task ETagValues_IdenticalUnderPushdown_WhenETagPropNotSelected()
    {
        // $select=name does NOT include Version (the ETag input) — the projection must add it,
        // otherwise @odata.etag would hash a default and diverge from the off host.
        string bodyOn = await _on.Client.GetStringAsync("/odata/PushTaggedItems?$select=name");
        string bodyOff = await _off.Client.GetStringAsync("/odata/PushTaggedItems?$select=name");
        Assert.Contains("@odata.etag", bodyOn);
        Assert.Equal(bodyOff, bodyOn);
    }

    [Fact]
    public async Task NextLink_IdenticalUnderPushdown()
    {
        // Server paging (MaxTop) + $select: the continuation link must match. MaxTop unset here,
        // so page via Prefer: maxpagesize to engage nextLink emission.
        using var reqOn = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "/odata/PushProducts?$select=name");
        reqOn.Headers.Add("Prefer", "maxpagesize=2");
        using var reqOff = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "/odata/PushProducts?$select=name");
        reqOff.Headers.Add("Prefer", "maxpagesize=2");

        var respOn = await _on.Client.SendAsync(reqOn);
        var respOff = await _off.Client.SendAsync(reqOff);
        string bodyOn = await respOn.Content.ReadAsStringAsync();
        string bodyOff = await respOff.Content.ReadAsStringAsync();
        Assert.Contains("@odata.nextLink", bodyOn);
        Assert.Equal(bodyOff, bodyOn);
    }

    [Fact]
    public async Task ProjectedValues_AreCorrect_NotDefaults()
    {
        // Belt-and-braces: the pushdown host actually returns real values for selected
        // properties (guards against a projection that silently materializes defaults).
        string body = await _on.Client.GetStringAsync("/odata/PushProducts?$select=name,price&$orderby=id");
        Assert.Contains("\"name\":\"Widget\"", body);
        Assert.Contains("19.99", body);
        Assert.DoesNotContain("\"sku\"", body); // unselected — trimmed as always
    }
}
