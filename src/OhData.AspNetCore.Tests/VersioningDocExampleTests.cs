using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Executes the examples in <c>docs/versioning.md</c> against a real host (#377).
///
/// <para>
/// The defect that produced #377 was not a weak guard — it was a documented example that had never
/// been run. <c>docs/versioning.md</c> claimed registrations were "completely isolated - no shared
/// state" while its own flagship example registered the <b>same profile type</b> in two
/// registrations, which <c>OhDataBuilder</c>'s cross-registration guard rejects at
/// <c>AddEntitySetProfile</c> call time. Reading the doc could not reveal that; starting a host
/// does. These tests therefore boot each documented snippet rather than asserting anything about
/// the source.
/// </para>
///
/// <para>
/// Keep these in step with the doc. If a snippet in <c>docs/versioning.md</c> changes, change the
/// matching test — the point is that every example in that file is known-executable.
/// </para>
/// </summary>
public class VersioningDocExampleTests
{
    // ── docs/versioning.md § "Named registrations" ─────────────────────────────

    /// <summary>
    /// The corrected flagship example: one profile type per registration, but the same
    /// <c>EntitySetName</c> ("Products") in both, which is what makes the documented route table
    /// (<c>/v1/Products</c>, <c>/v2/Products</c>, <c>/v2/Customers</c>) real. Duplicate entity set
    /// names across registrations are allowed; duplicate profile *types* are not.
    /// </summary>
    [Fact]
    public async Task NamedRegistrations_DocExample_AllThreeDocumentedRoutesRespond()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services.AddOhData("v1", o => o
            .WithPrefix("/v1")
            .AddEntitySetProfile<DocProductProfileV1>());

        builder.Services.AddOhData("v2", o => o
            .WithPrefix("/v2")
            .AddEntitySetProfile<DocProductProfileV2>()
            .AddEntitySetProfile<DocCustomerProfileV2>());

        await using var app = builder.Build();
        app.MapOhData("v1");
        app.MapOhData("v2");
        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/Products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v2/Products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v2/Customers")).StatusCode);

        // /v1 has no Customers — the doc's "v2 only" annotation.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/Customers")).StatusCode);
    }

    /// <summary>
    /// Pins the reason the doc had to change: the shape #377 documented — one profile type shared
    /// by two registrations — throws at <c>AddEntitySetProfile</c> call time, before any host is
    /// built. This is the guard working as designed, not a bug to route around.
    /// </summary>
    [Fact]
    public void NamedRegistrations_SharedProfileType_ThrowsAtRegistrationTime()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("v1", o => o
            .WithPrefix("/v1")
            .AddEntitySetProfile<DocProductProfileV1>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("v2", o => o
                .WithPrefix("/v2")
                .AddEntitySetProfile<DocProductProfileV1>()));

        Assert.Contains("cannot be shared across registrations", ex.Message, StringComparison.Ordinal);
    }

    // ── docs/versioning.md § "Versioning convenience helpers" ──────────────────

    /// <summary>
    /// The same corrected example through <c>AddOhDataVersion</c>/<c>MapOhDataVersion</c>, which had
    /// no test coverage at all before #377. These helpers fold name and prefix into one call, so a
    /// broken example here is just as unrunnable as the one above.
    /// </summary>
    [Fact]
    public async Task VersionHelpers_DocExample_AllThreeDocumentedRoutesRespond()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services.AddOhDataVersion("v1", "/v1", o => o.AddEntitySetProfile<DocProductProfileV1>());
        builder.Services.AddOhDataVersion("v2", "/v2", o => o
            .AddEntitySetProfile<DocProductProfileV2>()
            .AddEntitySetProfile<DocCustomerProfileV2>());

        await using var app = builder.Build();
        app.MapOhDataVersion("v1");
        app.MapOhDataVersion("v2");
        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/Products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v2/Products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v2/Customers")).StatusCode);
    }

    // ── docs/versioning.md § "Default (unnamed) registration" ──────────────────

    /// <summary>
    /// The unnamed registration coexisting with a named one. This example was already correct (it
    /// used distinct profile types); the test exists so it stays that way.
    /// </summary>
    [Fact]
    public async Task DefaultAndNamedRegistration_DocExample_BothRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services.AddOhData(o => o.WithPrefix("/odata").AddEntitySetProfile<DocProductProfileV1>());
        builder.Services.AddOhData("v2", o => o.WithPrefix("/v2").AddEntitySetProfile<DocProductProfileV2>());

        await using var app = builder.Build();
        app.MapOhData();
        app.MapOhData("v2");
        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/odata/Products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v2/Products")).StatusCode);
    }

    // ── docs/versioning.md § "Sharing behaviour between versions" ──────────────

    /// <summary>
    /// The pattern the doc recommends when two versions should expose identical behaviour: an empty
    /// subclass per version. Distinct <see cref="Type"/>s satisfy the guard while the behaviour is
    /// declared once. This is what the shipped TestBench does
    /// (<c>public class GenreProfileV2 : GenreProfile { }</c>), so the recommendation is not
    /// theoretical — but it is asserted here rather than assumed.
    /// </summary>
    [Fact]
    public async Task ThinSubclassPerVersion_SharesBehaviour_AndSatisfiesTheGuard()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services.AddOhData("v1", o => o
            .WithPrefix("/v1")
            .AddEntitySetProfile<DocCustomerProfileV2>());
        builder.Services.AddOhData("v2", o => o
            .WithPrefix("/v2")
            .AddEntitySetProfile<DocCustomerProfileV3>());

        await using var app = builder.Build();
        app.MapOhData("v1");
        app.MapOhData("v2");
        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();

        // Same entity set name and same behaviour, declared once on the base.
        string v1 = await client.GetStringAsync("/v1/Customers");
        string v2 = await client.GetStringAsync("/v2/Customers");
        Assert.Contains("v2 customer", v1, StringComparison.Ordinal);
        Assert.Contains("v2 customer", v2, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second half of the subclassing guidance: when the base profile injects a service, the
    /// per-version subclass must <b>forward the constructor</b> — C# does not inherit constructors,
    /// so an empty <c>{ }</c> body does not compile against a base with no parameterless
    /// constructor. This test exists because that is exactly the kind of snippet that reads fine and
    /// does not build; it is compiled and resolved through DI here.
    /// </summary>
    [Fact]
    public async Task SubclassForwardingAConstructor_ResolvesInjectedServices()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<DocProductStore>();

        builder.Services.AddOhData("v1", o => o
            .WithPrefix("/v1")
            .AddEntitySetProfile<DocInjectedProductProfile>());
        builder.Services.AddOhData("v2", o => o
            .WithPrefix("/v2")
            .AddEntitySetProfile<DocInjectedProductProfileV2>());

        await using var app = builder.Build();
        app.MapOhData("v1");
        app.MapOhData("v2");
        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();

        string v1 = await client.GetStringAsync("/v1/Products");
        string v2 = await client.GetStringAsync("/v2/Products");
        Assert.Contains("injected product", v1, StringComparison.Ordinal);
        Assert.Contains("injected product", v2, StringComparison.Ordinal);
    }

    // ── docs/versioning.md § "Startup validation" ──────────────────────────────

    /// <summary>
    /// The doc's claim that duplicate entity set names across registrations are allowed. Both
    /// product profiles above name their set "Products", so the passing tests above already
    /// demonstrate it; this asserts it directly so the claim is not incidental.
    /// </summary>
    [Fact]
    public void DuplicateEntitySetName_AcrossRegistrations_IsAllowed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("v1", o => o.WithPrefix("/v1").AddEntitySetProfile<DocProductProfileV1>());
        services.AddOhData("v2", o => o.WithPrefix("/v2").AddEntitySetProfile<DocProductProfileV2>());
        // No throw: "Products" in two registrations is fine.
    }

    /// <summary>
    /// The other half of the doc's validation table: the same <c>EntitySetName</c> from two
    /// <b>distinct</b> profile types inside <b>one</b> registration throws, and throws later than
    /// the type guard — when the registration is built (i.e. at <c>MapOhData()</c>), not at
    /// <c>AddEntitySetProfile</c>.
    /// <para>
    /// Worth asserting separately: the pre-existing
    /// <c>OhDataBuilderTests.Startup_DuplicateEntitySetName_Throws</c> registers the same profile
    /// <i>type</i> twice, which trips the duplicate-type guard inside <c>AddEntitySetProfile</c>
    /// first — so it never reaches the name check it is named for. Two distinct types are required
    /// to exercise this path.
    /// </para>
    /// </summary>
    [Fact]
    public void DuplicateEntitySetName_WithinOneRegistration_ThrowsWhenRegistrationIsBuilt()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Both name their set "Products". Distinct types, so AddEntitySetProfile accepts them.
        services.AddOhData(o => o
            .AddEntitySetProfile<DocProductProfileV1>()
            .AddEntitySetProfile<DocProductProfileV2>());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>());

        Assert.Contains("duplicate entity set name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Fixtures for the doc examples ─────────────────────────────────────────────

internal class DocProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal class DocCustomer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>v1's Products. Distinct type from <see cref="DocProductProfileV2"/>, same set name.</summary>
internal class DocProductProfileV1 : EntitySetProfile<int, DocProduct>
{
    public DocProductProfileV1() : base(x => x.Id)
    {
        EntitySetName = "Products";
        GetAll = ct => OhDataResult.Success<IEnumerable<DocProduct>>(
            new[] { new DocProduct { Id = 1, Name = "v1 product" } });
    }
}

/// <summary>v2's Products. Distinct type from <see cref="DocProductProfileV1"/>, same set name.</summary>
internal class DocProductProfileV2 : EntitySetProfile<int, DocProduct>
{
    public DocProductProfileV2() : base(x => x.Id)
    {
        EntitySetName = "Products";
        GetAll = ct => OhDataResult.Success<IEnumerable<DocProduct>>(
            new[] { new DocProduct { Id = 1, Name = "v2 product" } });
    }
}

/// <summary>The entity set that exists only in v2.</summary>
internal class DocCustomerProfileV2 : EntitySetProfile<int, DocCustomer>
{
    public DocCustomerProfileV2() : base(x => x.Id)
    {
        EntitySetName = "Customers";
        GetAll = ct => OhDataResult.Success<IEnumerable<DocCustomer>>(
            new[] { new DocCustomer { Id = 1, Name = "v2 customer" } });
    }
}

/// <summary>
/// Thin per-version subclass: a distinct <see cref="Type"/> for the guard, identical behaviour
/// inherited from the base. Mirrors the TestBench's <c>GenreProfileV2 : GenreProfile { }</c>.
/// Compiles with an empty body only because the base has a parameterless constructor.
/// </summary>
internal class DocCustomerProfileV3 : DocCustomerProfileV2 { }

/// <summary>Stand-in for an injected scoped dependency (a DbContext in a real app).</summary>
internal sealed class DocProductStore
{
    public IEnumerable<DocProduct> Products { get; } =
        new[] { new DocProduct { Id = 1, Name = "injected product" } };
}

/// <summary>Base profile that injects a service, so its subclass cannot have an empty body.</summary>
internal class DocInjectedProductProfile : EntitySetProfile<int, DocProduct>
{
    public DocInjectedProductProfile(DocProductStore store) : base(x => x.Id)
    {
        EntitySetName = "Products";
        GetAll = ct => OhDataResult.Success(store.Products);
    }
}

/// <summary>Per-version subclass forwarding the base constructor.</summary>
internal class DocInjectedProductProfileV2(DocProductStore store) : DocInjectedProductProfile(store);
