using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #426: <see cref="ODataQueryContext"/> must be built per request, never cached per entity set.
///
/// <para>
/// It used to be built once at startup and captured by all five read-route closures under the
/// comment "Both are read-only after construction". That comment was false.
/// <c>ODataQueryOptions</c>' constructor WRITES to the context it is handed
/// (<c>ODataQueryOptions.cs:76-80</c>):
/// </para>
/// <code>
/// Contract.Assert(context.RequestContainer == null);   // MS asserting the context is FRESH
/// context.RequestContainer = request.GetRouteServices();
/// context.Request = request;
/// </code>
/// <para>
/// and <c>Initialize</c> reads <c>context.Request</c> back off that shared field rather than using
/// the constructor's own <c>request</c> parameter (<c>:1165</c>, <c>IsNoDollarQueryEnable</c>). Two
/// requests in flight against one context therefore race: the second write lands between the first
/// request's write and its read, so a request ends up dereferencing a <i>different</i> request's
/// <c>HttpContext</c> — concurrently with that request's own owner. The resulting torn
/// <c>FeatureReferences</c> read throws <c>NullReferenceException</c> out of
/// <c>DefaultHttpContext.get_RequestServices</c>, which #402's deliberately broad catch relabels
/// <c>400 InvalidQueryOption</c>. A valid request — including one with no query string — therefore
/// intermittently failed under concurrent load.
/// </para>
/// </summary>
public class QueryContextPerRequestTests
{
    /// <summary>
    /// The invariant, asserted deterministically: two requests must not be handed the same
    /// <see cref="ODataQueryContext"/> instance. A Priority-1 profile is the only seam through
    /// which the framework's context is observable — it receives the built
    /// <see cref="ODataQueryOptions{TModel}"/> itself, and <c>options.Context</c> is public.
    ///
    /// <para>
    /// This is one route, but it covers all five call sites by construction: after the fix there is
    /// exactly ONE <c>new ODataQueryContext(...)</c> in the framework and it lives inside
    /// <c>TryBuildQueryOptions</c>, which takes an <c>IEdmModel</c> rather than a context precisely
    /// so that no caller is able to pass a shared one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoRequests_AreNotHandedTheSameODataQueryContext()
    {
        QueryContextCaptureProfile.Seen.Clear();

        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<QueryContextCaptureProfile>());

        (await fx.Client.GetAsync("/odata/CtxWidgets")).EnsureSuccessStatusCode();
        (await fx.Client.GetAsync("/odata/CtxWidgets")).EnsureSuccessStatusCode();

        ODataQueryContext[] seen = QueryContextCaptureProfile.Seen.ToArray();
        Assert.Equal(2, seen.Length);
        // Reference equality is the whole point: a cached context makes these the same object.
        Assert.NotSame(seen[0], seen[1]);
    }

    /// <summary>
    /// The isolated reproduction, with no web server in it: hammer the framework's own
    /// <c>TryBuildQueryOptions</c> from 16 threads, 2,000 iterations each, and require all 32,000 to
    /// succeed. Reproducing this over HTTP works but is orders of magnitude slower to fail, which is
    /// what makes <c>ConcurrencyTests.ConcurrentRequests_ResolveDistinctScopedServiceInstances</c>
    /// (#384) an unreliable symptom rather than a usable test.
    ///
    /// <para>
    /// Measured on the pre-fix code shape (this same loop, one shared context as production had):
    /// 43, 31, 16 and 89 failures in 32,000 over four runs — every one of them a
    /// <c>NullReferenceException</c> out of <c>DefaultHttpContext.get_RequestServices</c>, the
    /// exact frame the issue's captured stack trace names. Measured on this shape: 0 in 32,000.
    /// </para>
    ///
    /// <para>
    /// Called by reflection because <c>TryBuildQueryOptions</c> is <c>private static</c> — same
    /// technique <c>SerializeBoundedWalkerTests</c> uses, and deliberately not a widening of the
    /// framework's internal surface just to be testable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConcurrentOptionBuilds_NeverFail_16Threads_2000Iterations()
    {
        const int threads = 16;
        const int iterations = 2000;

        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<QueryContextCaptureProfile>());

        var registration = fx.App.Services.GetRequiredKeyedService<OhDataRegistration>(
            OhDataDefaults.DefaultRegistrationName);
        IEdmModel model = registration.EdmModel;

        MethodInfo tryBuild = typeof(OhDataEndpointFactory)
            .GetMethod("TryBuildQueryOptions", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Widget));

        // Guards the reflection above against a silent signature drift: the first parameter being
        // an IEdmModel (not an ODataQueryContext) IS the fix. If this ever fails, the context is
        // being passed in from outside again and the defect is back.
        Assert.Equal(typeof(IEdmModel), tryBuild.GetParameters()[0].ParameterType);

        var failures = new ConcurrentBag<string>();
        using var start = new Barrier(threads);

        var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < iterations; i++)
            {
                var ctx = new DefaultHttpContext { RequestServices = fx.App.Services };
                ctx.Request.Method = "GET";
                ctx.Request.QueryString = new QueryString("?$top=5");

                object?[] args = { model, ctx, null, null, null };
                try
                {
                    if (!(bool)tryBuild.Invoke(null, args)!)
                    {
                        failures.Add("returned false (400)");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add((ex.InnerException ?? ex).ToString());
                }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        await Task.WhenAll(workers);

        Assert.True(failures.IsEmpty,
            $"{failures.Count} of {threads * iterations} option builds failed. First: " +
            failures.FirstOrDefault());
    }
}

/// <summary>
/// Priority-1 profile whose only job is to record the <see cref="ODataQueryContext"/> the framework
/// built for each request. Priority-1 is the one path that hands the profile the built
/// <see cref="ODataQueryOptions{TModel}"/>, so it is the only place a test can see the context.
/// </summary>
internal class QueryContextCaptureProfile : ODataEntitySetProfile<int, Widget>
{
    internal static readonly ConcurrentQueue<ODataQueryContext> Seen = new();

    private static readonly List<Widget> Store = Enumerable.Range(1, 3)
        .Select(i => new Widget { Id = i, Name = $"Widget{i}" }).ToList();

    public QueryContextCaptureProfile() : base(x => x.Id)
    {
        EntitySetName = "CtxWidgets";

        GetODataQueryable = (options, ct) =>
        {
            Seen.Enqueue(options.Context);
            return Task.FromResult(new ODataQueryResult<Widget> { Items = Store.AsQueryable() });
        };
    }
}
