using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #358 adversarial review R2 (HIGH): the #358 fix's original catch sat on each collection-read
// route's OUTERMOST try, which also encloses handler invocation, ApplyCollectionPipelineAsync
// (ETag computation, nav delegates/batch handlers) and JSON serialization -- so ANY
// DivideByZeroException/OverflowException raised anywhere in that scope, even with no $filter or
// $orderby in the request at all, was relabeled 400 InvalidQueryOption and returned WITHOUT the
// group-level filter's diagnostic LogError. A genuine handler bug then produced a client-blamed
// 4xx with zero server-side trace -- worse than the 500 it replaced.
//
// The fix narrows the arithmetic-fault handling to a small local helper
// (EvaluateQueryWithArithmeticFaultGuard in OhDataEndpointFactory.cs) that wraps ONLY the
// enumeration/count of the $filter/$orderby-ApplyTo'd query, and only engages when the request
// actually carries $filter or $orderby. This file proves the three concrete fixtures the review
// used to demonstrate the regression now correctly 500 (logged), not 400.

/// <summary>Captures every log record so a test can assert an exception was logged at Error,
/// exactly like the group-level exception filter's own LogError call.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    private readonly ConcurrentQueue<Entry> _entries = new();
    public IReadOnlyCollection<Entry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<Entry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
        }
    }
}

internal sealed class HandlerFaultItem
{
    public int Id { get; set; }
    public int Quantity { get; set; }
}

/// <summary>#358 review "HandlerFault" fixture: the PROFILE's own GetQueryable projection
/// divides by zero (Quantity == 0 for one row) -- nothing to do with $filter at all.</summary>
internal sealed class HandlerFaultProfile : EntitySetProfile<int, HandlerFaultItem>
{
    public HandlerFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "HandlerFault";
        FilterEnabled = true;

        var store = new List<HandlerFaultItem>
        {
            new() { Id = 1, Quantity = 5 },
            new() { Id = 2, Quantity = 0 }, // 100 / 0 -> DivideByZeroException on enumeration
        };
        GetQueryable = () => store.AsQueryable().Select(x => new HandlerFaultItem
        {
            Id = 100 / x.Quantity,
            Quantity = x.Quantity,
        });
        GetById = (id, ct) => OhDataResult.Success(store.FirstOrDefault(x => x.Id == id));
    }
}

internal sealed class EtagFaultItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // A computed property, read by System.Text.Json during serialization (ApplyCollectionPipelineAsync),
    // not during $filter/$orderby evaluation. Faults unconditionally with the exact class of
    // exception (OverflowException, "Value was either too large or too small for an Int32")
    // the #358 review's "EtagFault" fixture demonstrated slipping past the original over-broad
    // catch. Routed through a field (not a literal) so `checked` is a runtime check, not a
    // compile-time constant-overflow error.
    private static readonly long _tooBig = long.MaxValue;
    public int Ratio => checked((int)_tooBig);
}

/// <summary>#358 review "EtagFault" fixture: a computed property faults during serialization
/// (ApplyCollectionPipelineAsync/JSON writing), not during $filter evaluation. Named to match the
/// review's fixture; the fault is in <see cref="EtagFaultItem.Ratio"/>, not the ETag mechanism
/// itself (this profile does not call <c>UseETag</c>).</summary>
internal sealed class EtagFaultProfile : EntitySetProfile<int, EtagFaultItem>
{
    public EtagFaultItem[] Store { get; } = { new() { Id = 1, Name = "Only" } };

    public EtagFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "EtagFault";

        GetQueryable = () => Store.AsQueryable();
        GetById = (id, ct) => OhDataResult.Success(Store.FirstOrDefault(x => x.Id == id));
    }
}

internal sealed class NavFaultParent
{
    public int Id { get; set; }
    public IEnumerable<NavFaultChild>? Children { get; set; }
}

internal sealed class NavFaultChild
{
    public int Id { get; set; }
}

/// <summary>#358 review "NavFault" fixture: a HasMany expand delegate divides by zero -- again
/// nothing to do with $filter/$orderby, just reached via $expand=Children.</summary>
internal sealed class NavFaultProfile : EntitySetProfile<int, NavFaultParent>
{
    private static readonly List<NavFaultParent> _parents = new() { new() { Id = 1 } };

    public NavFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "NavFault";
        ExpandEnabled = true;

        GetQueryable = () => _parents.AsQueryable();
        GetById = (id, ct) => OhDataResult.Success(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!, getAll: (parentId, ct) =>
        {
            int zeroDivisor = 0;
            int _ = 1 / zeroDivisor; // DivideByZeroException, unconditional
            return Task.FromResult<IEnumerable<NavFaultChild>>(Array.Empty<NavFaultChild>());
        });
    }
}

public class FilterArithmeticFaultScopeTests
{
    private static async Task<(TestFixture Fixture, CapturingLoggerProvider Logs)> BuildAsync(
        Action<OhDataBuilder> configure)
    {
        var logs = new CapturingLoggerProvider();
        var fx = await TestHostBuilder.BuildAsync(configure,
            configureServices: services => services.AddLogging(b => b.AddProvider(logs)));
        return (fx, logs);
    }

    private static async Task AssertLogged500(HttpResponseMessage response, CapturingLoggerProvider logs)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InternalServerError", json.GetProperty("error").GetProperty("code").GetString());
        // The group-level exception filter's own diagnostic: an Error-level record carrying the
        // real exception, proving the fault was NOT silently relabeled 400 with no trace.
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception is not null);
    }

    [Fact]
    public async Task HandlerFault_NoFilterInRequest_Returns500AndLogsRealException()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<HandlerFaultProfile>());
        await using var _ = fx;

        var response = await fx.Client.GetAsync("/odata/HandlerFault");
        await AssertLogged500(response, logs);
        Assert.Contains(logs.Entries, e => e.Exception is DivideByZeroException);
    }

    [Fact]
    public async Task HandlerFault_WithFilterPresent_StaysA400_KnownGuardTradeoff()
    {
        // Sanity companion documenting the guard's known, accepted limitation: once $filter (or
        // $orderby) IS present in the request, the guard cannot tell whether the profile's own
        // Select or the ApplyTo-emitted filter predicate is the one that actually faulted --
        // both run inside the same enumeration. Here it's genuinely the profile's Select (it
        // faults on Quantity==0 unconditionally, before the filter predicate even runs), yet
        // this still reports 400 because a client-supplied $filter was present and could
        // plausibly have been the cause. The review's guard is deliberately presence-based, not
        // a perfect causation test -- see EvaluateQueryWithArithmeticFaultGuard's doc comment.
        // The case that MUST be right (and is the actual #358 regression fixed here) is the
        // "no $filter/$orderby at all" case covered by the other tests in this file.
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<HandlerFaultProfile>());
        await using var _ = fx;

        var response = await fx.Client.GetAsync(
            "/odata/HandlerFault?$filter=" + Uri.EscapeDataString("Quantity div 0 eq 1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EtagFault_NoFilterInRequest_Returns500AndLogsRealException()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<EtagFaultProfile>());
        await using var _ = fx;

        var response = await fx.Client.GetAsync("/odata/EtagFault");
        await AssertLogged500(response, logs);
        Assert.Contains(logs.Entries, e => e.Exception is OverflowException);
    }

    [Fact]
    public async Task EtagFault_GetByIdRoute_AlsoReturns500_ConsistentWithCollectionRoute()
    {
        // Review item: route inconsistency was itself a symptom of the same over-broad catch
        // (GetById never had a #358 catch, so it always 500'd) -- now that the collection route
        // is narrowed/guarded, both routes must agree for the identical fault.
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<EtagFaultProfile>());
        await using var _ = fx;

        var response = await fx.Client.GetAsync("/odata/EtagFault(1)");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task NavFault_ExpandNoFilterInRequest_Returns500AndLogsRealException()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<NavFaultProfile>());
        await using var _ = fx;

        var response = await fx.Client.GetAsync("/odata/NavFault?$expand=Children");
        await AssertLogged500(response, logs);
        Assert.Contains(logs.Entries, e => e.Exception is DivideByZeroException);
    }

    [Fact]
    public async Task NavFault_StandaloneNavRoute_AlsoReturns500_ConsistentWithExpand()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<NavFaultProfile>());
        await using var _ = fx;

        var response = await fx.Client.GetAsync("/odata/NavFault(1)/Children");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
