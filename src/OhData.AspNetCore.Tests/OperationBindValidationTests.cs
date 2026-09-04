using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #498: five bind-time validation gaps in the operations surface. Each produced either a hostile
/// startup failure (a raw <c>ArgumentNullException</c> from deep inside
/// <c>Microsoft.OData.ModelBuilder</c>, naming nothing) or a silently-wrong runtime result, where a
/// clear bind-time rejection naming the operation was possible and cheap.
/// </summary>
public class OperationBindValidationTests
{
    // ── §1: a void-returning FUNCTION ────────────────────────────────────────────────────────
    //
    // RegisterEdmOperation / RegisterUnboundOpReturnType silently SKIPPED Returns for a
    // void/Task/ValueTask return, and GetEdmModel() then died inside Microsoft.OData.ModelBuilder
    // with `ArgumentNullException: 'returnType'` — no OhData message, no operation name. CSDL
    // requires a function to have a return type, so refusing is right; it must be an OhData error
    // naming the operation and pointing at BindAction/AddAction.

    [Fact]
    public async Task VoidReturningBoundFunction_IsRefusedAtStartup_NamingTheOperation()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<ObvVoidFunctionProfile>()));

        Assert.Contains("Ping", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BindAction", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VoidReturningUnboundFunction_IsRefusedAtRegistration_NamingTheOperation()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData(o => o
                .AddEntitySetProfile<ObvOpsProfile>()
                .AddFunction((Func<Task>)ObvUnbound.Ping)));

        Assert.Contains("Ping", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddAction", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and the reason the refusal is scoped to functions: a void ACTION is legal in
    /// CSDL and already works — every invocation produces 204 No Content.
    /// </summary>
    [Fact]
    public async Task VoidReturningActions_StillWork_AndStillProduce204()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ObvVoidActionProfile>()
                  .AddAction((Func<Task>)ObvUnbound.Ping, "UnboundPing"));

        using var bound = await fx.Client.PostAsync("/odata/ObvVoidActions/Ping", null);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, bound.StatusCode);

        using var unbound = await fx.Client.PostAsync("/odata/UnboundPing", null);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, unbound.StatusCode);
    }

    // ── §2: a non-trailing (or nullable) CancellationToken ───────────────────────────────────
    //
    // AsyncDispatchHelper.SplitCancellationToken strips only a TRAILING CancellationToken, by
    // exact type; RegisterEdmOperation filtered CancellationToken out at EVERY position. The two
    // disagreed, so $metadata declared one parameter list and the route handler demanded another:
    // a metadata-conformant request got 400 MissingParameter 'ct', and NO value could ever satisfy
    // it (there is no string→CancellationToken conversion). The operation was unreachable.

    [Fact]
    public async Task NonTrailingCancellationToken_IsRefusedAtStartup_NamingTheOperation()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<ObvLeadingTokenProfile>()));

        Assert.Contains("Leading", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullableCancellationToken_IsRefusedAtStartup_NamingTheOperation()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<ObvNullableTokenProfile>()));

        Assert.Contains("Nullable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonTrailingCancellationTokenOnAnUnboundOperation_IsRefusedAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData(o => o
                .AddEntitySetProfile<ObvOpsProfile>()
                .AddFunction((Func<CancellationToken, int, Task<int>>)ObvUnbound.Leading)));

        Assert.Contains("Leading", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a TRAILING CancellationToken is the supported idiom and keeps working.</summary>
    [Fact]
    public async Task TrailingCancellationToken_IsStillAccepted_AndStillInvoked()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ObvOpsProfile>());

        using var response = await fx.Client.GetAsync("/odata/ObvOps/Doubled?n=21");
        response.EnsureSuccessStatusCode();
        Assert.Contains("42", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ── §3: a handler returning IResult ──────────────────────────────────────────────────────
    //
    // Startup SUCCEEDED (Returns<IResult> maps the interface into the EDM) and the response was
    // the OkObjectHttpResult's own property bag serialized as a 200 body:
    // {"Value":{"A":1},"StatusCode":200}. Silent garbage, plus a polluted model.

    [Fact]
    public async Task BoundOperationReturningIResult_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<ObvResultReturnProfile>()));

        Assert.Contains("Wrapped", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IResult", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnboundOperationReturningIResult_IsRefusedAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData(o => o
                .AddEntitySetProfile<ObvOpsProfile>()
                .AddFunction((Func<Task<IResult>>)ObvUnbound.Wrapped)));

        Assert.Contains("Wrapped", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IResult", ex.Message, StringComparison.Ordinal);
    }

    // ── §4: byte[] — CSDL declared Collection(Edm.Byte), the wire served Edm.Binary ──────────
    //
    // GetCollectionElementType treated byte[] as an array (→ ReturnsCollection<byte>), while
    // WrapBoundOpResult's primitive map hits byte[] → Edm.Binary first. A clean advertise-vs-serve
    // mismatch. byte[] is now special-cased the way string already was, in BOTH copies.

    [Fact]
    public async Task ByteArrayReturn_IsDeclaredAsEdmBinary_AndAgreesWithTheWire()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ObvOpsProfile>()
                  .AddFunction((Func<Task<byte[]>>)ObvUnbound.Blob, "UnboundBlob"));

        using var metadataResponse = await fx.Client.GetAsync("/odata/$metadata");
        metadataResponse.EnsureSuccessStatusCode();
        string csdl = await metadataResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Collection(Edm.Byte)", csdl, StringComparison.Ordinal);
        Assert.Contains("Edm.Binary", csdl, StringComparison.Ordinal);

        // And the wire agrees, which is the whole point of the fix: WrapBoundOpResult's primitive
        // map has always hit byte[] -> Edm.Binary, so it was the EDM that was wrong.
        using var boundResponse = await fx.Client.GetAsync("/odata/ObvOps/Blob");
        boundResponse.EnsureSuccessStatusCode();
        Assert.Contains("#Edm.Binary", await boundResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An UNBOUND operation's success response is the bare Invoke() result with no
        // @odata.context envelope (see AddUnboundOperationProduces), so there is nothing to agree
        // with there — only the declared type in $metadata, asserted above.
        using var unboundResponse = await fx.Client.GetAsync("/odata/UnboundBlob");
        unboundResponse.EnsureSuccessStatusCode();
        Assert.Equal("\"AQID\"", await unboundResponse.Content.ReadAsStringAsync());
    }

    /// <summary>The control: a genuine collection return is still declared as a collection.</summary>
    [Fact]
    public async Task GenuineCollectionReturn_IsStillDeclaredAsACollection()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ObvOpsProfile>());

        using var metadataResponse = await fx.Client.GetAsync("/odata/$metadata");
        string csdl = await metadataResponse.Content.ReadAsStringAsync();
        Assert.Contains("Collection(Edm.Int32)", csdl, StringComparison.Ordinal);
    }

    // ── §5: culture-sensitive DefaultValue formatting in $metadata ───────────────────────────
    //
    // RegisterEdmOperation used $"{param.DefaultValue}", which formats under the CURRENT culture,
    // so `decimal maxPrice = 1.5m` rendered as DefaultValue="1,5" on a de-DE server. Same class as
    // the /$count culture inconsistency in #496.

    [Fact]
    public async Task DefaultValuesAreFormattedInvariantly_WhateverTheServerCulture()
    {
        Task<TestFixture> build = CultureScope.Run("de-DE", () => TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ObvDefaultValueProfile>()));

        await using TestFixture fx = await build;

        using var metadataResponse = await fx.Client.GetAsync("/odata/$metadata");
        metadataResponse.EnsureSuccessStatusCode();
        string csdl = await metadataResponse.Content.ReadAsStringAsync();

        // CSDL renders an optional parameter's default as an Org.OData.Core.V1.OptionalParameter
        // annotation record, so the value lands in a String attribute rather than a DefaultValue one.
        Assert.Contains("<PropertyValue Property=\"DefaultValue\" String=\"1.5\" />", csdl, StringComparison.Ordinal);
        Assert.DoesNotContain("1,5", csdl, StringComparison.Ordinal);
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

internal class ObvThing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>Unbound handlers, as named methods so the #468 identifier check is satisfied.</summary>
internal static class ObvUnbound
{
    internal static Task Ping() => Task.CompletedTask;
    internal static Task<int> Leading(CancellationToken ct, int x) => Task.FromResult(x);
    internal static Task<IResult> Wrapped() => Task.FromResult(Results.Ok(new { A = 1 }));
    internal static Task<byte[]> Blob() => Task.FromResult(new byte[] { 1, 2, 3 });
}

/// <summary>The healthy baseline: a trailing CancellationToken, a real collection return, and a
/// byte[] return, all on one profile.</summary>
internal class ObvOpsProfile : EntitySetProfile<int, ObvThing>
{
    public ObvOpsProfile() : base(x => x.Id)
    {
        EntitySetName = "ObvOps";
        GetAll = ct => OhDataResult.Success<IEnumerable<ObvThing>>(Array.Empty<ObvThing>());
        BindFunction(Doubled);
        BindFunction(Tags);
        BindFunction(Blob);
    }

    private Task<int> Doubled(int n, CancellationToken ct) => Task.FromResult(n * 2);
    private Task<IEnumerable<int>> Tags() => Task.FromResult<IEnumerable<int>>(new[] { 1, 2 });
    private Task<byte[]> Blob() => Task.FromResult(new byte[] { 1, 2, 3 });
}

/// <summary>#498 §1: a void-returning bound FUNCTION.</summary>
internal class ObvVoidFunctionProfile : EntitySetProfile<int, ObvThing>
{
    public ObvVoidFunctionProfile() : base(x => x.Id)
    {
        EntitySetName = "ObvVoidFunctions";
        BindFunction(Ping);
    }

    private Task Ping() => Task.CompletedTask;
}

/// <summary>The control for §1: a void-returning bound ACTION, which is legal and produces 204.</summary>
internal class ObvVoidActionProfile : EntitySetProfile<int, ObvThing>
{
    public ObvVoidActionProfile() : base(x => x.Id)
    {
        EntitySetName = "ObvVoidActions";
        BindAction(Ping);
    }

    private Task Ping() => Task.CompletedTask;
}

/// <summary>#498 §2: a CancellationToken that is not the trailing parameter.</summary>
internal class ObvLeadingTokenProfile : EntitySetProfile<int, ObvThing>
{
    public ObvLeadingTokenProfile() : base(x => x.Id)
    {
        EntitySetName = "ObvLeadingTokens";
        BindFunction(Leading);
    }

    private Task<int> Leading(CancellationToken ct, int x) => Task.FromResult(x);
}

/// <summary>#498 §2: a nullable CancellationToken, which SplitCancellationToken never detected.</summary>
internal class ObvNullableTokenProfile : EntitySetProfile<int, ObvThing>
{
    public ObvNullableTokenProfile() : base(x => x.Id)
    {
        EntitySetName = "ObvNullableTokens";
        BindFunction(Nullable);
    }

    private Task<int> Nullable(int x, CancellationToken? ct) => Task.FromResult(x);
}

/// <summary>#498 §3: a handler that returns the HTTP envelope the framework owns.</summary>
internal class ObvResultReturnProfile : EntitySetProfile<int, ObvThing>
{
    public ObvResultReturnProfile() : base(x => x.Id)
    {
        EntitySetName = "ObvResultReturns";
        BindFunction(Wrapped);
    }

    private Task<IResult> Wrapped() => Task.FromResult(Results.Ok(new { A = 1 }));
}

/// <summary>#498 §5: an optional parameter whose default formats differently under de-DE.</summary>
internal class ObvDefaultValueProfile : EntitySetProfile<int, ObvThing>
{
    public ObvDefaultValueProfile() : base(x => x.Id)
    {
        EntitySetName = "ObvDefaultValues";
        BindFunction(Priced);
    }

    private Task<int> Priced(decimal maxPrice = 1.5m) => Task.FromResult((int)maxPrice);
}
