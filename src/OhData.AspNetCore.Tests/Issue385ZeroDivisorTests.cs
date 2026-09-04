using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class Z385Item
{
    public int Id { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
    public int Divisor { get; set; }
    public List<Z385Child> Children { get; set; } = new();
}

public sealed class Z385Child
{
    public int Id { get; set; }
    public int Value { get; set; }
}

public sealed class Z385Profile : EntitySetProfile<int, Z385Item>
{
    private static readonly List<Z385Item> Store = new()
    {
        new() { Id = 1, Quantity = 10, Amount = 5.5m, Divisor = 2,
                Children = { new Z385Child { Id = 100, Value = 4 } } },
        new() { Id = 2, Quantity = 20, Amount = 7.5m, Divisor = 0 },
    };

    public Z385Profile() : base(x => x.Id)
    {
        EntitySetName = "Z385Items";
        FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        GetQueryable = _ => Store.AsQueryable();
        HasMany(x => x.Children);
        GetById = (id, _) => OhDataResult.Success(Store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>
/// #385 — a literal zero divisor is refused before the query executes, so every provider agrees.
/// <para>
/// #358's runtime guard only fires where the CLR evaluates the expression. Measured, one URL split
/// three ways: <c>400</c> on LINQ-to-Objects/EF InMemory, <c>200</c> with zero rows on SQLite
/// (<c>x/0</c> is NULL there), and an unhandled <c>500</c> on SQL Server and PostgreSQL — which an
/// anonymous client could drive at will on the two databases most deployments use.
/// </para>
/// </summary>
public sealed class Issue385ZeroDivisorTests
{
    private static async Task AssertRefusedAsync(HttpResponseMessage response, string option)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("InvalidQueryOption", error.GetProperty("code").GetString());
        Assert.Contains($"The {option} expression divides by the literal 0",
            error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Quantity div 0 eq 1")]
    [InlineData("Quantity mod 0 eq 1")]
    [InlineData("Amount div 0 eq 1")]
    [InlineData("Quantity div 0.0 eq 1")]          // the literal's CLR type differs (Single, not Int32)
    [InlineData("Quantity div 0M eq 1")]
    [InlineData("Quantity div 0L eq 1")]            // Int64
    [InlineData("Quantity div 0e0 eq 1")]           // Double, via the exponent form
    [InlineData("Quantity div 0f eq 1")]
    [InlineData("Quantity div -0.0 eq 1")]          // negative zero divides no better
    [InlineData("Children/any(c: c/Value div 0 eq 1)")]   // inside a lambda body
    [InlineData("Children/all(c: c/Value div 0 eq 1)")]
    [InlineData("Id eq 1 or Quantity div 0 eq 1")] // nested inside a boolean tree
    [InlineData("not (Quantity div 0 eq 1)")]      // under a unary operator
    [InlineData("round(Amount div 0) eq 1")]       // inside a function argument
    public async Task AFilterDividingByALiteralZero_IsRefused(string filter)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Z385Profile>());

        await AssertRefusedAsync(
            await fx.Client.GetAsync("/odata/Z385Items?$filter=" + Uri.EscapeDataString(filter)),
            "$filter");
    }

    [Theory]
    [InlineData("Quantity div 0")]
    [InlineData("Quantity mod 0")]
    [InlineData("Id,Quantity div 0")]              // a later ThenBy clause, not just the first
    public async Task AnOrderByDividingByALiteralZero_IsRefused(string orderby)
    {
        // $orderby has the identical shape, and #358's runtime guard already covered it alongside
        // $filter, so gating one and not the other would leave the same split one option over.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Z385Profile>());

        await AssertRefusedAsync(
            await fx.Client.GetAsync("/odata/Z385Items?$orderby=" + Uri.EscapeDataString(orderby)),
            "$orderby");
    }

    [Fact]
    public async Task TheCountRouteIsRefusedToo()
    {
        // /$count applies $filter, so it could return a confidently wrong NUMBER rather than a
        // wrong row set.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Z385Profile>());

        await AssertRefusedAsync(
            await fx.Client.GetAsync("/odata/Z385Items/$count?$filter=" + Uri.EscapeDataString("Quantity div 0 eq 1")),
            "$filter");
    }

    [Fact]
    public async Task APerRowZeroDivisor_IsNotClaimedByThePreExecutionCheck()
    {
        // `A div B` where some row's B is 0 is deliberately OUT of scope: it is not decidable before
        // execution. This fixture's row 2 has Divisor = 0, and the request still 400s -- but from
        // #358's RUNTIME guard, which is the correct owner. Asserted on the message rather than the
        // status, because both answer 400 and only the wording distinguishes which fired.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Z385Profile>());

        var response = await fx.Client.GetAsync(
            "/odata/Z385Items?$filter=" + Uri.EscapeDataString("Quantity div Divisor eq 1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string message = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("message").GetString()!;
        Assert.DoesNotContain("divides by the literal 0", message, StringComparison.Ordinal);
        Assert.Contains("could not be evaluated", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Quantity div 2 eq 5")]            // a real divisor
    [InlineData("Quantity add 0 eq 10")]           // zero, but not a divisor
    [InlineData("Quantity sub 0 eq 10")]
    [InlineData("Quantity mul 0 eq 0")]
    public async Task LegitimateExpressionsAreUntouched(string filter)
    {
        // The controls. `A div B` where some row's B is 0 stays out of scope deliberately: it is not
        // decidable before execution, and #358's runtime guard still covers it where the CLR
        // evaluates it. Over-refusing here would reject a valid query outright.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Z385Profile>());

        var response = await fx.Client.GetAsync("/odata/Z385Items?$filter=" + Uri.EscapeDataString(filter));

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetByIdIsUnaffected()
    {
        // GetById applies neither $filter nor $orderby, and its zero-cost no-option path must stay
        // zero-cost: the check is not wired there.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Z385Profile>());

        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/Z385Items(1)")).StatusCode);
    }
}
