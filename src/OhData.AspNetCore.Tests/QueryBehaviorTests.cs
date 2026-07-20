using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

public class QueryBehaviorTests
{
    // ── Inline profiles ──────────────────────────────────────────────────────────

    private class OptionalParamProfile : EntitySetProfile<int, Widget>
    {
        internal static string? LastGreeting;
        private static readonly List<Widget> Store = new() { new() { Id = 1, Name = "Widget1" } };

        public OptionalParamProfile() : base(x => x.Id)
        {
            EntitySetName = "OptionalParamWidgets";
            GetAll = (ct) => System.Threading.Tasks.Task.FromResult<IEnumerable<Widget>>(Store);
            BindFunction(Greet);
            BindAction(Configure);
        }

        private Task<string> Greet(string name = "World") =>
            System.Threading.Tasks.Task.FromResult($"Hello, {name}!");

        private void Configure(int timeout = 30, string mode = "default")
        {
            LastGreeting = $"{timeout}/{mode}";
        }
    }

    private class LargeStoreProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store;
        static LargeStoreProfile()
        {
            Store = new List<Widget>();
            for (int i = 1; i <= 10; i++) Store.Add(new Widget { Id = i, Name = $"Item{i}" });
        }
        public LargeStoreProfile() : base(x => x.Id)
        {
            EntitySetName = "LargeStoreWidgets";
            GetQueryable = (ct) => System.Threading.Tasks.Task.FromResult(Store.AsQueryable());
        }
    }

    private class SearchAndFilterProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store = new()
        {
            new() { Id = 1, Name = "Alpha Widget" },
            new() { Id = 2, Name = "Beta Widget" },
            new() { Id = 3, Name = "Alpha Gadget" },
        };

        public SearchAndFilterProfile() : base(x => x.Id)
        {
            EntitySetName = "SearchFilterWidgets";
            FilterEnabled = true;
            GetQueryable = (ct) => System.Threading.Tasks.Task.FromResult(Store.AsQueryable());
            Search = (term, ct) => System.Threading.Tasks.Task.FromResult<IEnumerable<Widget>>(
                Store.Where(w => w.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
    }

    // ── M-4: Bound ops optional/default parameters ───────────────────────────────

    [Fact]
    public async Task BoundFunction_OptionalParam_WhenOmitted_UsesDefault()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OptionalParamProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/OptionalParamWidgets/Greet");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hello, World!", body);
    }

    [Fact]
    public async Task BoundFunction_OptionalParam_WhenProvided_UsesProvided()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OptionalParamProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/OptionalParamWidgets/Greet?name=Alice");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hello, Alice!", body);
    }

    [Fact]
    public async Task BoundAction_OptionalParams_WhenOmitted_UsesDefaults()
    {
        OptionalParamProfile.LastGreeting = null;
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OptionalParamProfile>());
        HttpResponseMessage response = await fx.Client.PostAsync(
            "/odata/OptionalParamWidgets/Configure",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("30/default", OptionalParamProfile.LastGreeting);
    }

    // ── L-14: BindFunction/BindAction reject compiler-generated (lambda) names ───

    private class LambdaBoundFunctionProfile : EntitySetProfile<int, Widget>
    {
        public LambdaBoundFunctionProfile() : base(x => x.Id)
        {
            EntitySetName = "LambdaBoundFunctionWidgets";
            // A lambda's compiler-generated method name (e.g. "<.ctor>b__2_0") is not a valid
            // EDM/OData operation identifier — BoundOperationDefinition.From must reject it.
            BindFunction((Func<Task<string>>)(() => Task.FromResult("x")));
        }
    }

    private class LambdaBoundActionProfile : EntitySetProfile<int, Widget>
    {
        public LambdaBoundActionProfile() : base(x => x.Id)
        {
            EntitySetName = "LambdaBoundActionWidgets";
            BindAction((Action)(() => { }));
        }
    }

    [Fact]
    public void BindFunction_Lambda_ThrowsAtMapOhData()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData(o => o.AddEntitySetProfile<LambdaBoundFunctionProfile>());
        var app = builder.Build();

        // MapOhData() resolves the OhDataRegistration keyed singleton, which builds the EDM
        // model (IVisitModelBuilder.VisitModelBuilder) for every registered profile. OhDataBuilder
        // wraps any exception from that step in an outer InvalidOperationException naming the
        // failing profile; the actual BoundOperationDefinition.From rejection is the InnerException.
        var ex = Assert.Throws<InvalidOperationException>(() => app.MapOhData());
        Assert.Contains("LambdaBoundFunctionProfile", ex.Message, StringComparison.Ordinal);

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("lambda", inner.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("function", inner.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BindFunction", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindAction_Lambda_ThrowsAtMapOhData()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData(o => o.AddEntitySetProfile<LambdaBoundActionProfile>());
        var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapOhData());
        Assert.Contains("LambdaBoundActionProfile", ex.Message, StringComparison.Ordinal);

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("lambda", inner.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("action", inner.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BindAction", inner.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BindFunction", inner.Message, StringComparison.Ordinal);
    }

    // ── M-5: Invalid maxpagesize values ──────────────────────────────────────────

    [Fact]
    public async Task MaxPageSize_Zero_TreatedAsNoLimit_ReturnsAllItems()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LargeStoreProfile>());
        var req = new HttpRequestMessage(HttpMethod.Get, "/odata/LargeStoreWidgets");
        req.Headers.Add("Prefer", "maxpagesize=0");
        HttpResponseMessage response = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task MaxPageSize_Negative_TreatedAsNoLimit_ReturnsAllItems()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LargeStoreProfile>());
        var req = new HttpRequestMessage(HttpMethod.Get, "/odata/LargeStoreWidgets");
        req.Headers.Add("Prefer", "maxpagesize=-1");
        HttpResponseMessage response = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task MaxPageSize_NonNumeric_TreatedAsNoLimit_ReturnsAllItems()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LargeStoreProfile>());
        var req = new HttpRequestMessage(HttpMethod.Get, "/odata/LargeStoreWidgets");
        req.Headers.Add("Prefer", "maxpagesize=abc");
        HttpResponseMessage response = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
    }

    // ── M-6: Corrupted $skiptoken ─────────────────────────────────────────────────

    [Fact]
    public async Task SkipToken_Corrupted_Returns400()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LargeStoreProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/LargeStoreWidgets?$skiptoken=!!!notbase64!!!");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SkipToken_ValidBase64ButWrongLength_Returns400()
    {
        // "YQ==" is valid base64 for 1 byte — BitConverter.ToInt32 needs 4 bytes
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LargeStoreProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/LargeStoreWidgets?$skiptoken=YQ%3D%3D");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── M-7: $search combined with $filter ───────────────────────────────────────

    [Fact]
    public async Task Search_WithFilter_BothApplied()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SearchAndFilterProfile>());
        // $search=Widget matches items 1 ("Alpha Widget") and 2 ("Beta Widget")
        // $filter=Id gt 1 further restricts to item 2 only ("Beta Widget")
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/SearchFilterWidgets?$search=Widget&$filter=Id gt 1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement values = json.GetProperty("value");
        Assert.Equal(1, values.GetArrayLength());
        Assert.Equal(2, values[0].GetProperty("id").GetInt32());
        Assert.Equal("Beta Widget", values[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Search_WithoutFilter_ReturnsAllMatches()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SearchAndFilterProfile>());
        // $search=Alpha matches items 1 ("Alpha Widget") and 3 ("Alpha Gadget")
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/SearchFilterWidgets?$search=Alpha");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement values = json.GetProperty("value");
        Assert.Equal(2, values.GetArrayLength());
    }
}
