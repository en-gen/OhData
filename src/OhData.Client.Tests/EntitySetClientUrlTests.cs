using System;
using System.Net.Http;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>Tests URL construction without making any HTTP calls.</summary>
public class EntitySetClientUrlTests
{
    private sealed class Widget { public int Id { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; } }

    // Helper entity with a navigation property for C5/C7 validation tests
    private sealed class Product { public int Id { get; set; } public string Name { get; set; } = ""; public Category Category { get; set; } = new(); }
    private sealed class Category { public string Name { get; set; } = ""; public string Region { get; set; } = ""; }

    private static EntitySetClient<Product> ProductBuilder() =>
        new OhDataClient(new HttpClient { BaseAddress = new Uri("http://localhost/") })
            .For<Product>("Products");

    private static EntitySetClient<Widget> Builder() =>
        new OhDataClient(new HttpClient { BaseAddress = new Uri("http://localhost/") })
            .For<Widget>("Widgets");

    // ── Collection URL ──────────────────────────────────────────────────────────

    [Fact]
    public void NoOptions_JustEntitySetName() =>
        Assert.Equal("Widgets", Builder().BuildCollectionUrl());

    [Fact]
    public void Filter_AppendsFilterParam() =>
        Assert.Equal(
            "Widgets?$filter=price%20gt%2010",
            Builder().Filter(x => x.Price > 10).BuildCollectionUrl());

    [Fact]
    public void TopAndSkip() =>
        Assert.Equal(
            "Widgets?$top=20&$skip=40",
            Builder().Top(20).Skip(40).BuildCollectionUrl());

    [Fact]
    public void Select_Expression() =>
        Assert.Equal(
            "Widgets?$select=id%2Cname",
            Builder().Select(x => new { x.Id, x.Name }).BuildCollectionUrl());

    [Fact]
    public void Select_Strings() =>
        Assert.Equal(
            "Widgets?$select=Id%2CName",
            Builder().Select("Id", "Name").BuildCollectionUrl());

    [Fact]
    public void OrderBy_Ascending() =>
        Assert.Equal(
            "Widgets?$orderby=Name",
            Builder().OrderBy(x => x.Name).BuildCollectionUrl());

    [Fact]
    public void OrderBy_Descending() =>
        Assert.Equal(
            "Widgets?$orderby=Price%20desc",
            Builder().OrderByDescending(x => x.Price).BuildCollectionUrl());

    [Fact]
    public void ThenBy_AppendsToOrderBy() =>
        Assert.Equal(
            "Widgets?$orderby=Name%2CPrice%20desc",
            Builder().OrderBy(x => x.Name).ThenByDescending(x => x.Price).BuildCollectionUrl());

    [Fact]
    public void AllOptions_CorrectOrder()
    {
        string url = Builder()
            .Filter(x => x.Price > 5)
            .Select("Id", "Name")
            .OrderBy(x => x.Name)
            .Expand("Category")
            .Top(10)
            .Skip(20)
            .BuildCollectionUrl();

        // $filter, $select, $orderby, $expand, $top, $skip
        Assert.StartsWith("Widgets?", url);
        Assert.Contains("$filter=", url);
        Assert.Contains("$select=", url);
        Assert.Contains("$orderby=", url);
        Assert.Contains("$expand=", url);
        Assert.Contains("$top=10", url);
        Assert.Contains("$skip=20", url);
    }

    // ── Count URL ───────────────────────────────────────────────────────────────

    [Fact]
    public void CountUrl_NoFilter() =>
        Assert.Equal("Widgets/$count", Builder().BuildCountUrl());

    [Fact]
    public void CountUrl_WithFilter() =>
        Assert.Equal(
            "Widgets/$count?$filter=id%20eq%201",
            Builder().Filter(x => x.Id == 1).BuildCountUrl());

    // ── Keyed URL: all supported key types ──────────────────────────────────────

    [Fact]
    public void Key_Int() =>
        Assert.Equal("Widgets(42)", KeyUrl(42));

    [Fact]
    public void Key_Long() =>
        Assert.Equal("Widgets(9999999999)", KeyUrl(9_999_999_999L));

    [Fact]
    public void Key_Short() =>
        Assert.Equal("Widgets(32767)", KeyUrl((short)32767));

    [Fact]
    public void Key_Byte() =>
        Assert.Equal("Widgets(255)", KeyUrl((byte)255));

    [Fact]
    public void Key_UInt() =>
        Assert.Equal("Widgets(4294967295)", KeyUrl((uint)4_294_967_295));

    [Fact]
    public void Key_Bool_True() =>
        Assert.Equal("Widgets(true)", KeyUrl(true));

    [Fact]
    public void Key_Bool_False() =>
        Assert.Equal("Widgets(false)", KeyUrl(false));

    [Fact]
    public void Key_String_SingleQuoted()
    {
        // Strings are wrapped in OData single-quote syntax; the quotes are percent-encoded
        // because single-quote is a reserved URI character in path segments.
        Assert.Equal("Widgets(%27foo%27)", KeyUrl("foo"));
    }

    [Fact]
    public void Key_String_ApostropheEscaped()
    {
        // An apostrophe inside a string key is doubled (OData escaping), then percent-encoded.
        Assert.Equal("Widgets(%27it%27%27s%27)", KeyUrl("it's"));
    }

    [Fact]
    public void Key_Guid()
    {
        var g = Guid.Parse("12345678-1234-1234-1234-123456789012");
        Assert.Equal($"Widgets({g})", KeyUrl(g));
    }

    [Fact]
    public void Key_DateTime_Utc()
    {
        // DateTime colons are percent-encoded for URL path safety.
        var dt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Widgets(2024-06-01T12%3A00%3A00Z)", KeyUrl(dt));
    }

    [Fact]
    public void Key_DateTime_Unspecified()
    {
        var dt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal("Widgets(2024-06-01T12%3A00%3A00)", KeyUrl(dt));
    }

    [Fact]
    public void Key_DateTimeOffset_Utc()
    {
        var dto = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Widgets(2024-06-01T12%3A00%3A00Z)", KeyUrl(dto));
    }

    [Fact]
    public void Key_DateTimeOffset_WithOffset()
    {
        // The + sign is percent-encoded as %2B; colons as %3A.
        var dto = new DateTimeOffset(2024, 6, 1, 12, 0, 0, new TimeSpan(5, 30, 0));
        Assert.Equal("Widgets(2024-06-01T12%3A00%3A00%2B05%3A30)", KeyUrl(dto));
    }

    [Fact]
    public void Key_DateOnly()
    {
        // H-2 fix: DateOnly keys must NOT be wrapped in single quotes.
        // '2024-01-15' would cause DateOnly.Parse to throw on the server.
        Assert.Equal("Widgets(2024-01-15)", KeyUrl(new DateOnly(2024, 1, 15)));
    }

    [Fact]
    public void Key_TimeOnly()
    {
        // H-2 fix: TimeOnly keys must NOT be wrapped in single quotes.
        // Colons are percent-encoded; ASP.NET Core route binding decodes them before parsing.
        Assert.Equal("Widgets(08%3A30%3A00)", KeyUrl(new TimeOnly(8, 30, 0)));
    }

    // ── Immutability ─────────────────────────────────────────────────────────────

    [Fact]
    public void Builder_IsImmutable()
    {
        var base_ = Builder().Filter(x => x.Price > 10);
        var page1 = base_.Top(10).Skip(0);
        var page2 = base_.Top(10).Skip(10);

        // base_ is unmodified
        Assert.DoesNotContain("$top", base_.BuildCollectionUrl());
        Assert.DoesNotContain("$skip", base_.BuildCollectionUrl());

        Assert.Contains("$skip=0", page1.BuildCollectionUrl());
        Assert.Contains("$skip=10", page2.BuildCollectionUrl());
    }

    // ── M-8: OrderBy with navigation path ────────────────────────────────────────

    [Fact]
    public void OrderBy_NavigationPath_ProducesSlashSeparatedPath() =>
        Assert.Contains(
            "$orderby=Category%2FName",
            ProductBuilder().OrderBy(x => x.Category.Name).BuildCollectionUrl());

    [Fact]
    public void OrderByDescending_NavigationPath() =>
        Assert.Contains(
            "$orderby=Category%2FName%20desc",
            ProductBuilder().OrderByDescending(x => x.Category.Name).BuildCollectionUrl());

    [Fact]
    public void ThenBy_NavigationPath() =>
        Assert.Contains(
            "$orderby=Name%2CCategory%2FName",
            ProductBuilder().OrderBy(x => x.Name).ThenBy(x => x.Category.Name).BuildCollectionUrl());

    // ── M-9: Select with navigation path ────────────────────────────────────────

    [Fact]
    public void Select_SingleExpr_NavigationPath_ProducesSlashSeparatedPath() =>
        Assert.Contains(
            "$select=category%2Fname",
            ProductBuilder().Select(x => x.Category.Name).BuildCollectionUrl());

    [Fact]
    public void Select_SingleExpr_DeepNavigationPath() =>
        Assert.Contains(
            "$select=category%2Fregion",
            ProductBuilder().Select(x => x.Category.Region).BuildCollectionUrl());

    [Fact]
    public void Select_ParamsExpr_NavigationPath_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            ProductBuilder().Select(x => x.Category.Name, x => x.Id));

    // ── C5: Select params expression overload ───────────────────────────────────

    [Fact]
    public void Select_TwoDirectMemberExpressions_ProducesCorrectUrl() =>
        Assert.Equal(
            "Widgets?$select=Id%2CName",
            Builder().Select(x => x.Id, x => x.Name).BuildCollectionUrl());

    [Fact]
    public void Select_ThreeDirectMemberExpressions_ProducesCorrectUrl() =>
        Assert.Equal(
            "Widgets?$select=Id%2CName%2CPrice",
            Builder().Select(x => x.Id, x => x.Name, x => x.Price).BuildCollectionUrl());

    [Fact]
    public void Select_NavigationPathInParams_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            ProductBuilder().Select(x => x.Id, x => x.Category.Name));

    // ── C7: Expand params expression overload ───────────────────────────────────

    [Fact]
    public void Expand_TwoDirectMemberExpressions_ProducesCorrectUrl()
    {
        // Product has Category; add a second nav property class for the test
        string url = ProductBuilder().Expand(x => x.Category).BuildCollectionUrl();
        Assert.Equal("Products?$expand=Category", url);
    }

    [Fact]
    public void Expand_NavigationChainInParams_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            ProductBuilder().Expand(x => x.Id, x => x.Category.Name));

    // ── C6: IncludeCount and ToPageAsync URL ────────────────────────────────────

    [Fact]
    public void IncludeCount_AppendsCountParam() =>
        Assert.Contains("$count=true", Builder().IncludeCount().BuildCollectionUrl());

    [Fact]
    public void IncludeCount_WithFilterAndTop_AllParamsPresent()
    {
        string url = Builder().Filter(x => x.Price > 5).Top(10).IncludeCount().BuildCollectionUrl();
        Assert.Contains("$filter=", url);
        Assert.Contains("$top=10", url);
        Assert.Contains("$count=true", url);
    }

    [Fact]
    public void IncludeCount_DoesNotAffectBaseBuilder()
    {
        var base_ = Builder().Filter(x => x.Price > 10);
        var withCount = base_.IncludeCount();

        Assert.DoesNotContain("$count", base_.BuildCollectionUrl());
        Assert.Contains("$count=true", withCount.BuildCollectionUrl());
    }

    // ── M-13: Filter/Select/Expand compose instead of replace ───────────────────

    [Fact]
    public void Filter_CalledTwice_ComposesWithAnd()
    {
        string url = Builder().Filter(x => x.Price > 5).Filter(x => x.Price < 20).BuildCollectionUrl();
        Assert.Contains("$filter=", url);
        Assert.Contains("%20and%20", url);
    }

    [Fact]
    public void Filter_RawTwice_ComposesWithAnd()
    {
        string url = Builder().Filter("a eq 1").Filter("b eq 2").BuildCollectionUrl();
        Assert.Contains("and", url);
    }

    [Fact]
    public void Select_CalledTwice_AccumulatesFields()
    {
        string url = Builder().Select("Id").Select("Name").BuildCollectionUrl();
        Assert.Contains("$select=Id%2CName", url);
    }

    [Fact]
    public void Expand_CalledTwice_AccumulatesNavigations()
    {
        string url = ProductBuilder().Expand("Category").Expand("Tags").BuildCollectionUrl();
        Assert.Contains("$expand=Category%2CTags", url);
    }

    // ── L-14: Single-expression Expand rejects chained navigation ────────────

    [Fact]
    public void Expand_SingleExpr_ChainedPath_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            ProductBuilder().Expand(x => x.Category.Name));


    // ── M-14: Key carries  and  ────────────────────────────────────

    [Fact]
    public void Key_AfterSelect_SelectCarriedThrough() =>
        Assert.Contains(
            "=Id%2CName",
            Builder().Select("Id", "Name").Key(1).BuildEntityUrl());

    [Fact]
    public void Key_AfterExpand_ExpandCarriedThrough() =>
        Assert.Contains(
            "=Category",
            Builder().Expand("Category").Key(1).BuildEntityUrl());

    [Fact]
    public void Key_WithoutSelectOrExpand_NoQueryString() =>
        Assert.Equal("Widgets(1)", Builder().Key(1).BuildEntityUrl());

    // ── L-9: Generic Key<TKey> overload ─────────────────────────────────────────

    [Fact]
    public void Key_GenericOverload_FormatsCorrectly() =>
        Assert.Equal("Widgets(42)", Builder().Key<int>(42).BuildEntityUrl());

    [Fact]
    public void Key_GenericOverload_StringKey() =>
        Assert.Equal(
            Builder().Key("foo").BuildEntityUrl(),
            Builder().Key<string>("foo").BuildEntityUrl());

    // ── Helper ──────────────────────────────────────────────────────────────────

    private static string KeyUrl(object key) =>
        Builder().Key(key).BuildEntityUrl();
}
