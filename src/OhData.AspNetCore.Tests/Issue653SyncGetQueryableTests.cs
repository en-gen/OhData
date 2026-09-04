using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #653 — <c>GetQueryable</c> is <c>Func&lt;CancellationToken, IQueryable&lt;TModel&gt;&gt;</c>:
/// no <c>OhDataResult</c>, no <c>Task</c>.
/// <para>
/// Composing a query performs no I/O and produces no result — the framework appends the query
/// options and executes it later — so both wrappers were ceremony. Measured before the change:
/// <b>0 of 164</b> assignments in this repo were <c>async</c>.
/// </para>
/// <para>
/// The load-bearing test here is <see cref="SynchronousThrow_StillGetsTheErrorEnvelope"/>. Every
/// other <c>Invoke*</c> member is an <c>async</c> method, which captures a synchronously-throwing
/// user lambda into the returned Task; a plain forwarder does not, and the caller evaluates
/// <c>InvokeGetQueryableAsync(ct)</c> as an ARGUMENT to <c>AsHandlerFault</c> — so a synchronous
/// throw would escape past the wrapper entirely. That is exactly the hole
/// <c>InvokeDeleteAsync</c> had until #581 made it <c>async</c>. Because a sync delegate can now
/// throw on the calling thread, the seam catches and returns <c>Task.FromException</c>.
/// </para>
/// </summary>
public sealed class Issue653SyncGetQueryableTests
{
    public sealed class S653Thing
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class S653PlainProfile : EntitySetProfile<int, S653Thing>
    {
        public S653PlainProfile() : base(x => x.Id)
        {
            EntitySetName = "S653Plains";
            FilterEnabled = OrderByEnabled = true;
            // The whole point of the change: the query, bare.
            GetQueryable = _ => new[]
            {
                new S653Thing { Id = 1, Name = "alpha" },
                new S653Thing { Id = 2, Name = "beta" },
            }.AsQueryable();
        }
    }

    public sealed class S653ThrowingProfile : EntitySetProfile<int, S653Thing>
    {
        public S653ThrowingProfile() : base(x => x.Id)
        {
            EntitySetName = "S653Throwers";
            // Throws on the CALLING thread, before any Task exists.
            GetQueryable = _ => throw new InvalidOperationException("compose failed");
        }
    }

    public sealed class S653RejectingProfile : EntitySetProfile<int, S653Thing>
    {
        public S653RejectingProfile() : base(x => x.Id)
        {
            EntitySetName = "S653Rejecters";
            // The documented replacement for returning a rejection from this seam.
            ConfigureExceptions(e => e.Map<S653DeniedException>(_ => OhDataResult.Forbidden("OutOfTenantScope", "tenant scope")));
            GetQueryable = _ => throw new S653DeniedException();
        }
    }

    public sealed class S653DeniedException : Exception
    {
    }

    [Fact]
    public async Task TheQueryIsReturnedBare_AndQueryOptionsStillApply()
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<S653PlainProfile>());

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/S653Plains?$filter=Name eq 'beta'");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"beta\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"alpha\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynchronousThrow_StillGetsTheErrorEnvelope()
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<S653ThrowingProfile>());

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/S653Throwers");
        string body = await res.Content.ReadAsStringAsync();

        // Not an empty 500, and not an unhandled escape past the group filter.
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains("InternalServerError", body, StringComparison.Ordinal);
        // The handler's own message must never reach the client.
        Assert.DoesNotContain("compose failed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectionIsStillExpressible_ViaConfigureExceptions()
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<S653RejectingProfile>());

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/S653Rejecters");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("tenant scope", body, StringComparison.Ordinal);
    }
}
