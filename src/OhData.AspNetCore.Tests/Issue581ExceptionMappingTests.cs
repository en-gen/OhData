using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class X581Thing { public int Id { get; set; } public string Name { get; set; } = ""; }

/// <summary>A domain exception, standing in for the adopter's own (or EF's).</summary>
public sealed class X581DuplicateException : InvalidOperationException
{
    public X581DuplicateException(string sku) : base("duplicate " + sku) => Sku = sku;
    public string Sku { get; }
}

public sealed class X581Profile : EntitySetProfile<int, X581Thing>
{
    public X581Profile() : base(x => x.Id)
    {
        EntitySetName = "X581Things";

        GetAll = _ => throw new X581DuplicateException("read");
        GetById = (id, _) => throw new X581DuplicateException("id-" + id);
        Post = (m, _) => throw new X581DuplicateException(m.Name);
        Put = (id, m, _) => throw new X581DuplicateException(m.Name);
        Patch = (id, d, _) => throw new X581DuplicateException("patch");
        Delete = (id, _) => throw new X581DuplicateException("del");

        ConfigureExceptions(e => e
            .Map<X581DuplicateException>((ctx, ex) =>
                OhDataResult.Conflict(
                    "DuplicateSku",
                    $"{ctx.EntitySetName}/{ctx.Operation}/key={ctx.Key ?? "-"}/" +
                    $"model={ctx.Model?.Name ?? "-"}/delta={(ctx.Delta is null ? "-" : "yes")}/" +
                    $"qs={ctx.QueryString ?? "-"}/sku={ex.Sku}",
                    target: "Name")));
    }
}

/// <summary>Same handlers, no mappings — the control for every assertion below.</summary>
public sealed class X581UnmappedProfile : EntitySetProfile<int, X581Thing>
{
    public X581UnmappedProfile() : base(x => x.Id)
    {
        EntitySetName = "X581Unmapped";
        GetAll = _ => throw new X581DuplicateException("read");
        Post = (m, _) => throw new X581DuplicateException(m.Name);
    }
}

/// <summary>Most-derived wins regardless of declaration order — base declared FIRST here.</summary>
public sealed class X581BaseFirstProfile : EntitySetProfile<int, X581Thing>
{
    public X581BaseFirstProfile() : base(x => x.Id)
    {
        EntitySetName = "X581BaseFirst";
        Post = (m, _) => throw new X581DuplicateException(m.Name);
        ConfigureExceptions(e => e
            .Map<InvalidOperationException>(_ => OhDataResult.BadRequest("Base", "base"))
            .Map<X581DuplicateException>(_ => OhDataResult.Conflict("Derived", "derived")));
    }
}

/// <summary>...and with the derived declared FIRST.</summary>
public sealed class X581DerivedFirstProfile : EntitySetProfile<int, X581Thing>
{
    public X581DerivedFirstProfile() : base(x => x.Id)
    {
        EntitySetName = "X581DerivedFirst";
        Post = (m, _) => throw new X581DuplicateException(m.Name);
        ConfigureExceptions(e => e
            .Map<X581DuplicateException>(_ => OhDataResult.Conflict("Derived", "derived"))
            .Map<InvalidOperationException>(_ => OhDataResult.BadRequest("Base", "base")));
    }
}

/// <summary>An unmapped exception type must stay a 500 even when other mappings exist.</summary>
public sealed class X581PartialProfile : EntitySetProfile<int, X581Thing>
{
    public X581PartialProfile() : base(x => x.Id)
    {
        EntitySetName = "X581Partial";
        Post = (m, _) => throw new NotSupportedException("not mapped");
        ConfigureExceptions(e => e.Map<X581DuplicateException>(_ => OhDataResult.Conflict("D", "d")));
    }
}

/// <summary>
/// #581 — <c>ConfigureExceptions</c> lets a handler produce a client error. Before this, user code
/// had exactly two exits (return a value, or throw) and the throw exit was hard-wired to
/// <c>500</c>, so a rejection depending on domain state the framework cannot see had no honest way
/// to reach the client.
/// </summary>
public sealed class Issue581ExceptionMappingTests
{
    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, JsonElement Error)> ErrorAsync(
        HttpResponseMessage response)
    {
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("error"));
    }

    [Fact]
    public async Task MappedException_BecomesTheChosenStatusAndEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<X581Profile>());

        var (status, error) = await ErrorAsync(
            await fx.Client.PostAsync("/odata/X581Things", Json("{\"Id\":1,\"Name\":\"widget\"}")));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("DuplicateSku", error.GetProperty("code").GetString());
        Assert.Equal("Name", error.GetProperty("target").GetString());
        Assert.Contains("sku=widget", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAMapping_TheSameThrowIsStillAnOpaque500()
    {
        // The control. #496's envelope is unchanged for anything unmapped, and the handler's own
        // message must never reach the client.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<X581UnmappedProfile>());

        var (status, error) = await ErrorAsync(
            await fx.Client.PostAsync("/odata/X581Unmapped", Json("{\"Id\":1,\"Name\":\"widget\"}")));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("InternalServerError", error.GetProperty("code").GetString());
        Assert.DoesNotContain("widget", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnmappedTypeStays500_EvenWhenOtherMappingsExist()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<X581PartialProfile>());

        var (status, _) = await ErrorAsync(
            await fx.Client.PostAsync("/odata/X581Partial", Json("{\"Id\":1,\"Name\":\"w\"}")));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
    }

    // The context is the reason this is more than "throw a status": each seam contributes what it
    // has, and Operation says which members are populated.
    [Theory]
    [InlineData("GET", "/odata/X581Things", "X581Things/Read/key=-/model=-/delta=-")]
    [InlineData("GET", "/odata/X581Things(7)", "X581Things/Read/key=7/model=-/delta=-")]
    [InlineData("DELETE", "/odata/X581Things(7)", "X581Things/Delete/key=7/model=-/delta=-")]
    public async Task TheContextCarriesWhatTheSeamHas(string method, string url, string expected)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<X581Profile>());

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        var (status, error) = await ErrorAsync(await fx.Client.SendAsync(request));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains(expected, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheContextCarriesTheModelOnPut_AndTheDeltaOnPatch()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<X581Profile>());

        using var put = new HttpRequestMessage(HttpMethod.Put, "/odata/X581Things(3)")
        { Content = Json("{\"Id\":3,\"Name\":\"put-model\"}") };
        var (_, putError) = await ErrorAsync(await fx.Client.SendAsync(put));
        Assert.Contains("key=3/model=put-model", putError.GetProperty("message").GetString()!, StringComparison.Ordinal);

        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/odata/X581Things(4)")
        { Content = Json("{\"Name\":\"patched\"}") };
        var (_, patchError) = await ErrorAsync(await fx.Client.SendAsync(patch));
        Assert.Contains("key=4/model=-/delta=yes", patchError.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheContextCarriesTheQueryString()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<X581Profile>());

        var (_, error) = await ErrorAsync(await fx.Client.GetAsync("/odata/X581Things?$format=json"));

        Assert.Contains("qs=?$format=json", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(X581BaseFirstProfile), "X581BaseFirst")]
    [InlineData(typeof(X581DerivedFirstProfile), "X581DerivedFirst")]
    public async Task MostDerivedWins_WhicheverOrderTheyWereDeclaredIn(Type profileType, string set)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
        {
            if (profileType == typeof(X581BaseFirstProfile)) o.AddEntitySetProfile<X581BaseFirstProfile>();
            else o.AddEntitySetProfile<X581DerivedFirstProfile>();
        });

        var (status, error) = await ErrorAsync(
            await fx.Client.PostAsync($"/odata/{set}", Json("{\"Id\":1,\"Name\":\"w\"}")));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Derived", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AMappedExceptionIsStillLogged_WithTheOriginalException()
    {
        // Converting a fault into a 4xx removes it from error dashboards. The Warning is the only
        // thing left of it, so it has to carry the real exception, not just the mapped message.
        var capture = new WarningCapture();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<X581Profile>(),
            configureServices: sv => sv.AddLogging(lb =>
            {
                lb.SetMinimumLevel(LogLevel.Debug);
                lb.AddProvider(capture);
            }));

        await fx.Client.PostAsync("/odata/X581Things", Json("{\"Id\":1,\"Name\":\"widget\"}"));

        Assert.Contains(capture.Exceptions, e => e is X581DuplicateException);
    }

    [Fact]
    public void MappingExceptionItself_IsRefusedAtDeclaration()
    {
        // #494 one layer down: SqlClient reports connection-pool exhaustion as a plain
        // InvalidOperationException, so a catch-everything mapping reports infrastructure faults as
        // client errors. Exception itself is never defensible, so it cannot be spelled.
        var ex = Assert.Throws<ArgumentException>(() => new X581MapsExceptionProfile());
        Assert.Contains("Name the specific exception type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameExceptionTypeTwice_IsRefusedAtDeclaration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new X581DuplicateMappingProfile());
        Assert.Contains("already declared", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureExceptionsTwice_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new X581TwiceProfile());
        Assert.Contains("already been called", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class X581MapsExceptionProfile : EntitySetProfile<int, X581Thing>
{
    public X581MapsExceptionProfile() : base(x => x.Id)
    {
        EntitySetName = "X581MapsException";
        ConfigureExceptions(e => e.Map<Exception>(_ => OhDataResult.Conflict("C", "c")));
    }
}

public sealed class X581DuplicateMappingProfile : EntitySetProfile<int, X581Thing>
{
    public X581DuplicateMappingProfile() : base(x => x.Id)
    {
        EntitySetName = "X581DuplicateMapping";
        ConfigureExceptions(e => e
            .Map<X581DuplicateException>(_ => OhDataResult.Conflict("A", "a"))
            .Map<X581DuplicateException>(_ => OhDataResult.Conflict("B", "b")));
    }
}

public sealed class X581TwiceProfile : EntitySetProfile<int, X581Thing>
{
    public X581TwiceProfile() : base(x => x.Id)
    {
        EntitySetName = "X581Twice";
        ConfigureExceptions(e => e.Map<X581DuplicateException>(_ => OhDataResult.Conflict("A", "a")));
        ConfigureExceptions(e => e.Map<NotSupportedException>(_ => OhDataResult.Conflict("B", "b")));
    }
}

/// <summary>
/// #581 — the factory set itself: each factory's status, and the two argument guards. Unit-level
/// because the closed set is the design (an unrepresentable status rather than a validated one), so
/// it is worth pinning independently of any route.
/// </summary>
public sealed class Issue581OhDataResultFactoryTests
{
    public static TheoryData<OhDataResult, int> Factories() => new()
    {
        { OhDataResult.BadRequest("c", "m"), 400 },
        { OhDataResult.Forbidden("c", "m"), 403 },
        { OhDataResult.NotFound("c", "m"), 404 },
        { OhDataResult.Conflict("c", "m"), 409 },
        { OhDataResult.PreconditionFailed("c", "m"), 412 },
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void EachFactory_CarriesItsStatusAndEnvelope(OhDataResult result, int expected)
    {
        Assert.Equal(expected, result.StatusCode);
        Assert.Equal("c", result.ErrorCode);
        Assert.Equal("m", result.Message);
        Assert.Null(result.Target);
    }

    [Fact]
    public void TargetIsCarriedWhenGiven() =>
        Assert.Equal("Name", OhDataResult.Conflict("c", "m", target: "Name").Target);

    [Theory]
    [InlineData("", "message")]
    [InlineData("   ", "message")]
    [InlineData("code", "")]
    [InlineData("code", "   ")]
    public void AnEmptyCodeOrMessage_IsRefused(string errorCode, string message)
    {
        // The envelope's code and message are contractual; a blank one would ship a valid-looking
        // OData error that says nothing.
        Assert.Throws<ArgumentException>(() => OhDataResult.Conflict(errorCode, message));
    }
}
