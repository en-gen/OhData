using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// The conformance oracle: every query construct, answered by the mapped profile and by a control
/// profile that has no mapper in it, must produce the <b>same response</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is a far stronger check than a hand-written expectation, and it is the one that catches the
/// defect class this package is exposed to. A mapper bug does not usually produce an error — it
/// produces a plausible <c>200</c> with the wrong rows, the wrong order, or a member quietly left at
/// its default. Only a second, independent answer to the same question detects that.
/// </para>
/// <para>
/// The control is deliberately the naive strategy: materialise everything, project into the model,
/// hand the framework a LINQ-to-objects queryable. It is what an adopter would write without this
/// package, and it is correct — just unusable at scale. Agreeing with it is exactly the claim being
/// made.
/// </para>
/// </remarks>
public sealed class MappedQueryConformanceTests
{
    private readonly ITestOutputHelper _out;

    public MappedQueryConformanceTests(ITestOutputHelper output) => _out = output;

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>
        {
            // Nothing at all: the plain read, which is where a projection defect shows first.
            "",

            // $filter -- comparison
            "?$filter=Id eq 1",
            "?$filter=Id ne 1",
            "?$filter=Rank gt 1",
            "?$filter=Rank ge 2",
            "?$filter=Rank lt 3",
            "?$filter=Rank le 2",
            "?$filter=Title eq 'Hammer'",

            // $filter -- a RENAMED member, which the entity does not call by that name
            "?$filter=Title ne 'Hammer'",

            // $filter -- a PATH across an optional reference, including the null row
            "?$filter=CategoryName eq 'Tools'",
            "?$filter=CategoryName ne 'Tools'",
            "?$filter=CategoryName eq null",
            "?$filter=CategoryName ne null",

            // $filter -- a FORMAT member, which exists on no column at all
            "?$filter=DisplayName eq 'Ada Lovelace'",
            "?$filter=startswith(DisplayName,'Ada')",

            // $filter -- logical
            "?$filter=Rank gt 1 and Title ne 'Ball'",
            "?$filter=Rank eq 1 or Rank eq 3",
            "?$filter=not (Rank eq 1)",
            "?$filter=(Rank eq 1 or Rank eq 2) and Title ne 'Ball'",

            // $filter -- arithmetic
            "?$filter=Rank add 1 eq 4",
            "?$filter=Rank sub 1 eq 0",
            "?$filter=Rank mul 2 eq 6",
            "?$filter=Rank div 3 eq 1",
            "?$filter=Rank mod 2 eq 1",

            // $filter -- in
            "?$filter=Rank in (1,3)",
            "?$filter=Title in ('Hammer','Ball')",

            // $filter -- canonical string functions
            "?$filter=contains(Title,'amm')",
            "?$filter=endswith(Title,'mer')",
            "?$filter=startswith(Title,'Ha')",
            "?$filter=length(Title) eq 6",
            "?$filter=indexof(Title,'amm') eq 1",
            "?$filter=substring(Title,1) eq 'ammer'",
            "?$filter=substring(Title,1,3) eq 'amm'",
            "?$filter=tolower(Title) eq 'hammer'",
            "?$filter=toupper(Title) eq 'HAMMER'",
            "?$filter=trim(Title) eq 'Hammer'",
            "?$filter=concat(Title,'!') eq 'Hammer!'",

            // $filter -- canonical math functions
            "?$filter=round(Rank) eq 3",
            "?$filter=floor(Rank) eq 3",
            "?$filter=ceiling(Rank) eq 3",

            // $filter -- lambdas over a RESHAPED collection (the join entity is invisible to it)
            "?$filter=Tags/any(t: t/Label eq 'sale')",
            "?$filter=Tags/any(t: t/Id eq 8)",
            "?$filter=Tags/all(t: t/Label ne 'sale')",
            "?$filter=Tags/any()",

            // $filter -- lambdas over an ordinary collection
            "?$filter=Reviews/any(r: r/Stars eq 5)",
            "?$filter=Reviews/all(r: r/Stars gt 1)",

            // $filter -- through a single-valued navigation
            "?$filter=Category/Name eq 'Tools'",

            // $orderby
            "?$orderby=Rank",
            "?$orderby=Rank desc",
            "?$orderby=Title",
            "?$orderby=Title desc",
            "?$orderby=CategoryName",
            "?$orderby=CategoryName desc",
            "?$orderby=DisplayName",
            "?$orderby=CategoryName,Rank desc",
            "?$orderby=Rank desc,Title",

            // $top / $skip
            "?$top=1",
            "?$top=2",
            "?$skip=1",
            "?$skip=1&$top=1",
            "?$skip=99",
            "?$top=0",

            // $count
            "?$count=true",
            "?$count=true&$filter=Rank gt 1",
            "?$count=true&$top=1",
            "?$count=true&$skip=1",

            // $select
            "?$select=Id",
            "?$select=Id,Title",
            "?$select=DisplayName",
            "?$select=CategoryName",

            // $expand
            "?$expand=Tags",
            "?$expand=Reviews",
            "?$expand=Category",
            "?$expand=Tags,Reviews",
            "?$expand=Tags($select=Label)",
            "?$expand=Tags($filter=Label eq 'sale')",
            "?$expand=Tags($orderby=Label desc)",
            "?$expand=Tags($top=1)",
            "?$expand=Tags($skip=1)",
            "?$expand=Reviews($filter=Stars gt 2)",
            "?$expand=Tags($count=true)",
            "?$expand=Tags($filter=Label eq 'new';$count=true)",
            "?$expand=Tags($orderby=Label;$top=1)",
            "?$expand=Tags($filter=Id gt 7;$orderby=Label desc;$select=Label)",
            "?$expand=Category($select=Name)",
            "?$expand=Reviews($orderby=Stars desc;$top=1)",

            // Null semantics across a mapped path and a mapped reference
            "?$filter=Category/Name eq null",
            "?$filter=Category/Name ne null",
            "?$orderby=CategoryName,Id",
            "?$orderby=CategoryName desc,Id desc",

            // Combinations -- the shapes a real grid sends
            "?$filter=Rank gt 0&$orderby=Rank desc&$top=2&$count=true",
            "?$select=Id,Title&$expand=Tags($select=Label)&$orderby=Title",
            "?$filter=Tags/any(t: t/Label eq 'new')&$expand=Tags&$count=true",
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task MappedAndControl_AnswerIdentically(string query)
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        HttpResponseMessage mapped = await host.Client.GetAsync($"/odata/{MappedTestHost.Mapped}{query}");
        HttpResponseMessage control = await host.Client.GetAsync($"/odata/{MappedTestHost.Control}{query}");

        string mappedBody = await mapped.Content.ReadAsStringAsync();
        string controlBody = await control.Content.ReadAsStringAsync();

        _out.WriteLine($"query  : {query}");
        _out.WriteLine($"mapped : {(int)mapped.StatusCode} {mappedBody}");
        _out.WriteLine($"control: {(int)control.StatusCode} {controlBody}");

        Assert.Equal(control.StatusCode, mapped.StatusCode);
        Assert.Equal(Normalize(controlBody), Normalize(mappedBody));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GetById_AnswersIdentically(int key)
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        HttpResponseMessage mapped = await host.Client.GetAsync($"/odata/{MappedTestHost.Mapped}({key})");
        HttpResponseMessage control = await host.Client.GetAsync($"/odata/{MappedTestHost.Control}({key})");

        Assert.Equal(control.StatusCode, mapped.StatusCode);
        Assert.Equal(
            Normalize(await control.Content.ReadAsStringAsync()),
            Normalize(await mapped.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task GetById_ForAMissingKey_Is404OnBoth()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        HttpResponseMessage mapped = await host.Client.GetAsync($"/odata/{MappedTestHost.Mapped}(9999)");
        HttpResponseMessage control = await host.Client.GetAsync($"/odata/{MappedTestHost.Control}(9999)");

        Assert.Equal(HttpStatusCode.NotFound, mapped.StatusCode);
        Assert.Equal(control.StatusCode, mapped.StatusCode);
    }

    /// <summary>
    /// The two entity sets differ by name, and by nothing else that a response may carry.
    /// </summary>
    private static string Normalize(string body) =>
        body.Replace(MappedTestHost.Control, MappedTestHost.Mapped, StringComparison.Ordinal);
}
