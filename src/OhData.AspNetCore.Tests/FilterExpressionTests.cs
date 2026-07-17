using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Full $filter expression-language coverage on the GetQueryable path: logical operators,
/// arithmetic, canonical string/date/math functions, and any/all lambdas. These are
/// contract tests proving OhData wires Microsoft.OData's ODataQueryOptions.ApplyTo
/// correctly end-to-end (correct results, camelCase JSON, 400 on unsupported input) —
/// not tests of Microsoft.OData's parser/translator internals.
///
/// Every test computes its expected result via a plain LINQ "oracle" over
/// <see cref="QueryOptionData.Items"/> using .NET semantics that should match the
/// corresponding OData operator/function, then compares against the ids returned by
/// the live HTTP endpoint. This avoids hand-computed expectations drifting from the
/// fixture data as it evolves.
/// </summary>
public class FilterExpressionTests
{
    private const string Url = "/odata/QueryOptionItems";

    private static async Task<TestFixture> BuildAsync() =>
        await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryOptionProfile>());

    private static async Task<int[]> GetIdsAsync(HttpClient client, string query)
    {
        JsonElement json = await client.GetFromJsonAsync<JsonElement>(query);
        return json.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt32())
            .ToArray();
    }

    private static void AssertSameIds(IEnumerable<int> expected, IEnumerable<int> actual)
    {
        int[] exp = expected.OrderBy(x => x).ToArray();
        int[] act = actual.OrderBy(x => x).ToArray();
        Assert.Equal(exp, act);
    }

    // ── 1. Logical operators ────────────────────────────────────────────────────

    [Fact]
    public async Task Logical_And()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.IsActive && x.Category == "Tools")
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=IsActive eq true and Category eq 'Tools'");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Logical_Or()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Category == "Office" || x.Category == "Parts")
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Category eq 'Office' or Category eq 'Parts'");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Logical_Not()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => !x.IsActive)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=not (IsActive eq true)");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Logical_Precedence_AndBindsTighterThanOr_WithoutParens()
    {
        // Default precedence: `and` binds tighter than `or`, i.e. this parses as
        // (Category eq 'Tools' and Quantity gt 5) or IsActive eq false.
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => (x.Category == "Tools" && x.Quantity > 5) || !x.IsActive)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client,
            $"{Url}?$filter=Category eq 'Tools' and Quantity gt 5 or IsActive eq false");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Logical_Precedence_ParensOverrideDefault()
    {
        // Explicit parens change grouping vs. the previous test:
        // Category eq 'Tools' and (Quantity gt 5 or IsActive eq false)
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Category == "Tools" && (x.Quantity > 5 || !x.IsActive))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client,
            $"{Url}?$filter=Category eq 'Tools' and (Quantity gt 5 or IsActive eq false)");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);

        // Sanity: the two groupings must actually produce different result sets on this
        // fixture, otherwise the precedence assertion above is vacuous.
        int[] ungroupedExpected = QueryOptionData.Items
            .Where(x => (x.Category == "Tools" && x.Quantity > 5) || !x.IsActive)
            .Select(x => x.Id).ToArray();
        Assert.NotEqual(ungroupedExpected.OrderBy(x => x), expected.OrderBy(x => x));
    }

    [Fact]
    public async Task Logical_EqNull_MatchesNullProperty()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Category == null)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Category eq null");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Logical_NeNull_ExcludesNullProperty()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Rank != null)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Rank ne null");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    // ── 2. Arithmetic operators ─────────────────────────────────────────────────

    [Fact]
    public async Task Arithmetic_Add()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Quantity + 1 == 5)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Quantity add 1 eq 5");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Arithmetic_Sub()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Quantity - 1 == 5)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Quantity sub 1 eq 5");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Arithmetic_Mul()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Quantity * 2 == 20)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Quantity mul 2 eq 20");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Arithmetic_Div_IntegerTruncation()
    {
        // `div` on an Int32 property performs integer division (truncating), matching C#'s
        // int / int semantics.
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Quantity / 2 == 5)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Quantity div 2 eq 5");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Arithmetic_Mod()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Quantity % 3 == 0)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Quantity mod 3 eq 0");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    // ── 3. String functions ─────────────────────────────────────────────────────

    [Fact]
    public async Task StringFn_Contains()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.Contains("Widget", StringComparison.Ordinal))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=contains(Name,'Widget')");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_StartsWith()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.StartsWith("A", StringComparison.Ordinal))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=startswith(Name,'A')");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_EndsWith()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.EndsWith("Gadget", StringComparison.Ordinal))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=endswith(Name,'Gadget')");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_ToLower()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.ToLowerInvariant() == "alpha widget")
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=tolower(Name) eq 'alpha widget'");
        AssertSameIds(expected, actual);
        // Matches both "Alpha Widget" (Id 1) and "alpha widget" (Id 3) once lowercased.
        Assert.Equal(2, expected.Length);
    }

    [Fact]
    public async Task StringFn_ToUpper()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.ToUpperInvariant() == "GAMMA TOOL")
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=toupper(Name) eq 'GAMMA TOOL'");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_Trim()
    {
        // No fixture rows carry incidental whitespace, so trim(Name) is a structural
        // no-op here — this asserts the function is at least accepted and applied
        // (200, and equivalent to the un-trimmed comparison) rather than rejected.
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.Trim() == "Pi Part")
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=trim(Name) eq 'Pi Part'");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_Length()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.Length == 9)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=length(Name) eq 9");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_IndexOf()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.IndexOf("a", StringComparison.Ordinal) == 1)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=indexof(Name,'a') eq 1");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_Substring()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Name.Length >= 5 && x.Name.Substring(0, 5) == "Alpha")
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=substring(Name,0,5) eq 'Alpha'");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task StringFn_Concat()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => string.Concat(x.Name, x.Category ?? "") == "Pi PartParts")
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client,
            $"{Url}?$filter=concat(Name,Category) eq 'Pi PartParts'");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    // ── 4. Date/time functions ──────────────────────────────────────────────────

    [Fact]
    public async Task DateFn_Year()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.CreatedUtc.Year == 2024)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=year(CreatedUtc) eq 2024");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task DateFn_Month()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.CreatedUtc.Month == 1)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=month(CreatedUtc) eq 1");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task DateFn_Day()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.CreatedUtc.Day == 15)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=day(CreatedUtc) eq 15");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task DateFn_Hour()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.CreatedUtc.Hour == 23)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=hour(CreatedUtc) eq 23");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task DateFn_Minute()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.CreatedUtc.Minute == 30)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=minute(CreatedUtc) eq 30");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task DateFn_Date_OnDateTimeProperty()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.CreatedUtc.Date == new DateTime(2023, 6, 25))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=date(CreatedUtc) eq 2023-06-25");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task DateFn_Date_OnDateTimeOffsetProperty()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.UpdatedAt.Date == new DateTime(2024, 8, 23))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=date(UpdatedAt) eq 2024-08-23");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    // ── 5. Math functions ───────────────────────────────────────────────────────

    [Fact]
    public async Task MathFn_Round_Decimal()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => Math.Round(x.Price, MidpointRounding.AwayFromZero) == 22m)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=round(Price) eq 22");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task MathFn_Floor_Decimal()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => Math.Floor(x.Price) == 15m)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=floor(Price) eq 15");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task MathFn_Ceiling_Decimal()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => Math.Ceiling(x.Price) == 23m)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=ceiling(Price) eq 23");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task MathFn_Round_Double_UsesAwayFromZeroRoundingOnMidpoint()
    {
        // OData Part 2 §5.1.1.9 specifies round-half-away-from-zero for round(), which is now
        // the framework default: a double midpoint (2.5) rounds to 3, not 2 (banker's/.NET
        // Math.Round default) — so Weight 2.5 (Id 1) joins the eq-3 group.
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => Convert.ToInt32(Math.Round(x.Weight, MidpointRounding.AwayFromZero)) == 3)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=round(Weight) eq 3.0");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
        Assert.Contains(1, actual); // Weight 2.5 rounds to 3 (away-from-zero), not 2 (banker's)
    }

    // ── 5b. round() midpoint semantics (RoundingMode setting) ──────────────────────
    //
    // Uses the dedicated RoundingModeItem/RoundingModeData fixture (see
    // QueryOptionCoverageFixtures.cs) rather than QueryOptionData, since none of that dataset's
    // Price/Weight values land on a midpoint where away-from-zero and banker's rounding actually
    // disagree.

    private const string RoundingUrl = "/odata/RoundingModeItems";
    private const string RoundingBankersUrl = "/odata/RoundingModeBankersItems";

    private static async Task<TestFixture> BuildRoundingAsync() =>
        await TestHostBuilder.BuildAsync(o => o
            .AddProfile<RoundingModeProfile>()
            .AddProfile<RoundingModeBankersProfile>());

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -3)]
    [InlineData(4.5, 5)]
    [InlineData(-4.5, -5)]
    public async Task MathFn_Round_Double_SpecCompliantDefault_RoundsAwayFromZero(double value, int expectedRounded)
    {
        await using TestFixture fx = await BuildRoundingAsync();
        int[] expected = RoundingModeData.Items
            .Where(x => Math.Abs(x.Value - value) < 1e-9)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{RoundingUrl}?$filter=round(Value) eq {expectedRounded}");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -3)]
    [InlineData(4.5, 5)]
    [InlineData(-4.5, -5)]
    public async Task MathFn_Round_Decimal_SpecCompliantDefault_RoundsAwayFromZero(decimal value, int expectedRounded)
    {
        await using TestFixture fx = await BuildRoundingAsync();
        int[] expected = RoundingModeData.Items
            .Where(x => x.Amount == value)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{RoundingUrl}?$filter=round(Amount) eq {expectedRounded}");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Theory]
    [InlineData(2.5, 2)]
    [InlineData(-2.5, -2)]
    [InlineData(4.5, 4)]
    [InlineData(-4.5, -4)]
    public async Task MathFn_Round_Double_BankersRoundingOverride_RoundsToEven(double value, int expectedRounded)
    {
        // RoundingMode = BankersRounding opts a profile back into .NET's default (pre-fix)
        // Math.Round(double) semantics — round-half-to-even.
        await using TestFixture fx = await BuildRoundingAsync();
        int[] expected = RoundingModeData.Items
            .Where(x => Math.Abs(x.Value - value) < 1e-9)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{RoundingBankersUrl}?$filter=round(Value) eq {expectedRounded}");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Theory]
    [InlineData(2.5, 2)]
    [InlineData(-2.5, -2)]
    [InlineData(4.5, 4)]
    [InlineData(-4.5, -4)]
    public async Task MathFn_Round_Decimal_BankersRoundingOverride_RoundsToEven(decimal value, int expectedRounded)
    {
        await using TestFixture fx = await BuildRoundingAsync();
        int[] expected = RoundingModeData.Items
            .Where(x => x.Amount == value)
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{RoundingBankersUrl}?$filter=round(Amount) eq {expectedRounded}");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task MathFn_Round_Regression_FloorAndCeilingUnaffected()
    {
        // floor()/ceiling() are untouched by the RoundingMode rewrite (it only targets
        // single-argument Math.Round calls) - regression-check both still produce correct
        // results on the same GetQueryable path.
        await using TestFixture fx = await BuildAsync();
        int[] expectedFloor = QueryOptionData.Items
            .Where(x => Math.Floor(x.Price) == 15m)
            .Select(x => x.Id).ToArray();
        int[] actualFloor = await GetIdsAsync(fx.Client, $"{Url}?$filter=floor(Price) eq 15");
        AssertSameIds(expectedFloor, actualFloor);
        Assert.NotEmpty(expectedFloor);

        int[] expectedCeiling = QueryOptionData.Items
            .Where(x => Math.Ceiling(x.Price) == 23m)
            .Select(x => x.Id).ToArray();
        int[] actualCeiling = await GetIdsAsync(fx.Client, $"{Url}?$filter=ceiling(Price) eq 23");
        AssertSameIds(expectedCeiling, actualCeiling);
        Assert.NotEmpty(expectedCeiling);
    }

    // ── 6. any/all lambdas on a primitive collection navigation ────────────────
    //
    // QueryOptionItem.Tags is a List<string> structural (non-navigation) collection
    // property, populated on the in-memory queryable itself (not lazily loaded), so
    // it is expected to be usable with any/all on the GetQueryable path since ApplyTo
    // just compiles the lambda into the underlying LINQ-to-Objects query.

    [Fact]
    public async Task Lambda_Any_MatchesItemsWithTag()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Tags.Any(t => t == "red"))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Tags/any(t: t eq 'red')");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task Lambda_Any_OnEmptyCollection_IsFalse()
    {
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Tags.Any())
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Tags/any()");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
        Assert.True(expected.Length < QueryOptionData.Items.Count, "fixture must contain at least one empty Tags collection");
    }

    [Fact]
    public async Task Lambda_All_MatchesItemsWhereEveryTagContainsSubstring()
    {
        // all() over an empty collection is vacuously true — so this also picks up
        // every item with an empty Tags list, matching standard quantifier semantics.
        await using TestFixture fx = await BuildAsync();
        int[] expected = QueryOptionData.Items
            .Where(x => x.Tags.All(t => t.Contains("l", StringComparison.Ordinal)))
            .Select(x => x.Id).ToArray();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Tags/all(t: contains(t,'l'))");
        AssertSameIds(expected, actual);
        Assert.NotEmpty(expected);
    }

    // ── 8. Edge semantics (filter-specific) ─────────────────────────────────────

    [Fact]
    public async Task Filter_MatchingNothing_ReturnsEmptyValueArrayWithZeroCount()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage response = await fx.Client.GetAsync($"{Url}?$filter=Name eq 'DoesNotExist'&$count=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out _));
        Assert.Equal(0, json.GetProperty("value").GetArrayLength());
        Assert.Equal(0L, json.GetProperty("@odata.count").GetInt64());
    }

    [Fact]
    public async Task Filter_StringComparison_IsCaseSensitive()
    {
        // Documents actual semantics: `eq` on strings is ordinal/case-sensitive — "Alpha
        // Widget" (Id 1) does not match "alpha widget" (Id 3) or "ALPHA" substrings.
        await using TestFixture fx = await BuildAsync();
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=Name eq 'Alpha Widget'");
        Assert.Equal(new[] { 1 }, actual);
    }

    [Fact]
    public async Task Filter_ContainsIsCaseSensitive_DoesNotMatchDifferentCasing()
    {
        await using TestFixture fx = await BuildAsync();
        // "TOOL" (all-caps) only matches the all-caps fixture row ("GAMMA TOOL", Id 4);
        // it must not match "Tools" or "Xi Tool" (title case).
        int[] actual = await GetIdsAsync(fx.Client, $"{Url}?$filter=contains(Name,'TOOL')");
        Assert.Equal(new[] { 4 }, actual);
    }

    // ── 9. Casing contract ───────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_ResponseBody_IsCamelCase()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            $"{Url}?$filter=contains(Name,'Widget')");
        JsonElement first = json.GetProperty("value")[0];
        string[] expectedProps = { "id", "name", "category", "price", "weight", "quantity", "isActive", "createdUtc", "updatedAt", "rank", "tags" };
        foreach (string prop in expectedProps)
        {
            Assert.True(first.TryGetProperty(prop, out _), $"expected camelCase property '{prop}'");
        }
        // No PascalCase leakage.
        foreach (JsonProperty prop in first.EnumerateObject())
        {
            Assert.False(char.IsUpper(prop.Name[0]), $"property '{prop.Name}' leaked PascalCase casing");
        }
    }

    [Fact]
    public async Task Filter_WithSelect_ResponseBody_IsCamelCase()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            $"{Url}?$filter=IsActive eq true&$select=Name,Price");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.True(first.TryGetProperty("price", out _));
        Assert.False(first.TryGetProperty("Name", out _));
        Assert.False(first.TryGetProperty("Price", out _));
        // The filtered-on property (IsActive) was not selected, so it should be absent —
        // but it must still have been usable to filter (see next test for the assertion
        // that filtering still worked correctly).
        Assert.False(first.TryGetProperty("isActive", out _));
    }
}
