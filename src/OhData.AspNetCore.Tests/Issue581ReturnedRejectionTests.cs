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

public sealed class R581Thing { public int Id { get; set; } public string Name { get; set; } = ""; }

/// <summary>Every handler returns a rejection, one per factory, so each status is reachable.</summary>
public sealed class R581RejectingProfile : EntitySetProfile<int, R581Thing>
{
    public static int HandlerCalls;

    public R581RejectingProfile() : base(x => x.Id)
    {
        EntitySetName = "R581Things";

        GetAll = _ => Bump<IEnumerable<R581Thing>>(OhDataResult.Forbidden("ReadDenied", "read denied"));
        GetById = (id, _) => Bump<R581Thing>(OhDataResult.NotFound("Gone", $"{id} is gone"));
        Post = (m, _) => Bump<R581Thing>(OhDataResult.Conflict("Duplicate", $"{m.Name} exists", target: "Name"));
        Put = (id, m, _) => Bump<R581Thing>(OhDataResult.PreconditionFailed("Stale", "changed underneath you"));
        Patch = (id, d, _) => Bump<R581Thing>(OhDataResult.BadRequest("Rule", "that transition is not allowed"));
        Delete = (id, _) => Bump<bool>(OhDataResult.Conflict("InUse", "still referenced"));
    }

    private static Task<OhDataResult<T>> Bump<T>(OhDataResult rejection)
    {
        Interlocked.Increment(ref HandlerCalls);
        return Task.FromResult<OhDataResult<T>>(rejection);
    }
}

/// <summary>Succeeds, so the success path is asserted against the same fixture shape.</summary>
public sealed class R581SucceedingProfile : EntitySetProfile<int, R581Thing>
{
    public R581SucceedingProfile() : base(x => x.Id)
    {
        EntitySetName = "R581Ok";
        GetById = (id, _) => OhDataResult.SuccessTask(new R581Thing { Id = id, Name = "ok" });
        Post = (m, _) => OhDataResult.SuccessTask(m);
        Delete = (id, _) => OhDataResult.SuccessTask(true);
    }
}

/// <summary>
/// #581 part 2 — a handler can now RETURN a rejection rather than only throw one. Part 1 gave the
/// framework the type and the translation point; this is the direct channel.
/// </summary>
public sealed class Issue581ReturnedRejectionTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, JsonElement Error)> ErrorAsync(HttpResponseMessage r)
    {
        JsonElement body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (r.StatusCode, body.GetProperty("error"));
    }

    public static TheoryData<string, string, string, int, string> Cases() => new()
    {
        { "GET",    "/odata/R581Things",     "",                          403, "ReadDenied" },
        { "GET",    "/odata/R581Things(1)",  "",                          404, "Gone" },
        { "POST",   "/odata/R581Things",     "{\"Id\":1,\"Name\":\"w\"}", 409, "Duplicate" },
        { "PUT",    "/odata/R581Things(1)",  "{\"Id\":1,\"Name\":\"w\"}", 412, "Stale" },
        { "PATCH",  "/odata/R581Things(1)",  "{\"Name\":\"w\"}",          400, "Rule" },
        { "DELETE", "/odata/R581Things(1)",  "",                          409, "InUse" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AReturnedRejection_BecomesThatStatusAndEnvelope(
        string method, string url, string body, int expectedStatus, string expectedCode)
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<R581RejectingProfile>());

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body.Length > 0) request.Content = Json(body);
        var (status, error) = await ErrorAsync(await fx.Client.SendAsync(request));

        Assert.Equal(expectedStatus, (int)status);
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ARejectionCarriesItsTarget()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<R581RejectingProfile>());

        var (_, error) = await ErrorAsync(
            await fx.Client.PostAsync("/odata/R581Things", Json("{\"Id\":1,\"Name\":\"w\"}")));

        Assert.Equal("Name", error.GetProperty("target").GetString());
    }

    [Fact]
    public async Task TheSuccessPathIsUnchanged()
    {
        // The control: wrapping the return type must not disturb what a succeeding handler serves.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<R581SucceedingProfile>());

        var get = await fx.Client.GetAsync("/odata/R581Ok(3)");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Contains("\"Id\":3", await get.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var post = await fx.Client.PostAsync("/odata/R581Ok", Json("{\"Id\":9,\"Name\":\"n\"}"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var del = new HttpRequestMessage(HttpMethod.Delete, "/odata/R581Ok(3)");
        Assert.Equal(HttpStatusCode.NoContent, (await fx.Client.SendAsync(del)).StatusCode);
    }

    [Fact]
    public async Task AReturnedRejection_IsNotLoggedAsAWarning()
    {
        // A returned rejection is an ordinary outcome the handler chose, unlike a mapped fault --
        // which IS logged at Warning because it was reclassified. A Warning per business rejection
        // would drown that signal.
        var capture = new WarningCapture();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<R581RejectingProfile>(),
            configureServices: sv => sv.AddLogging(lb =>
            {
                lb.SetMinimumLevel(LogLevel.Debug);
                lb.AddProvider(capture);
            }));

        await fx.Client.PostAsync("/odata/R581Things", Json("{\"Id\":1,\"Name\":\"w\"}"));

        Assert.DoesNotContain(capture.Warnings, w => w.Contains("R581Things", StringComparison.Ordinal));
    }
}

public sealed class C581Thing { public int Id { get; set; } }

/// <summary>Handshake so the test cancels only once the handler is genuinely awaiting.</summary>
public sealed class C581Coordinator
{
    private static TaskCompletionSource<bool> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> StartedGetById { get; } = NewTcs();
    public TaskCompletionSource<bool> ObservedCancelGetById { get; } = NewTcs();
}

/// <summary>Maps the cancellation family, to probe the #493 guard from both sides.</summary>
public sealed class C581Profile : EntitySetProfile<int, C581Thing>
{
    public C581Profile(C581Coordinator coord) : base(x => x.Id)
    {
        EntitySetName = "C581Things";

        // Not an abort: the shape HttpClient produces on its OWN timeout (#493's motivating case).
        GetAll = _ => throw new TaskCanceledException("downstream timed out");

        GetById = async (id, ct) =>
        {
            coord.StartedGetById.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                coord.ObservedCancelGetById.TrySetResult(true);
                throw;
            }
            return OhDataResult.Success<C581Thing>(null);
        };

        ConfigureExceptions(e => e
            .Map<OperationCanceledException>(_ =>
                OhDataResult.Conflict("MappedCancellation", "should never be seen on a real abort")));
    }
}

/// <summary>
/// #581 — the mapping declines a request the client actually aborted, which is #493's exact
/// condition. Both sides of the <c>&amp;&amp;</c> are covered: an <c>OperationCanceledException</c>
/// on a live request IS mapped, and one on an aborted request is NOT.
/// </summary>
public sealed class Issue581CancellationGuardTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ACancellationThatIsNotAnAbort_IsMappedLikeAnyOtherException()
    {
        // TaskCanceledException is what HttpClient throws on its own timeout -- a dependency fault
        // wearing cancellation's clothes, per #493. The request is alive, so the guard must not
        // decline, or an adopter could never map it.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<C581Profile>(),
            configureServices: s => s.AddSingleton(new C581Coordinator()));

        var response = await fx.Client.GetAsync("/odata/C581Things");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MappedCancellation", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AGenuineAbort_IsNeverMapped()
    {
        // The guard. There is no response left to write, so the abort is left to ASP.NET Core's own
        // client-disconnect handling -- exactly as #493 requires -- rather than becoming a 409 that
        // nobody can receive.
        var coord = new C581Coordinator();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<C581Profile>(),
            configureServices: s => s.AddSingleton(coord));

        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> responseTask = fx.Client.GetAsync("/odata/C581Things(1)", cts.Token);

        await coord.StartedGetById.Task.WaitAsync(SafetyNet);
        cts.Cancel();

        await coord.ObservedCancelGetById.Task.WaitAsync(SafetyNet);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
    }
}
