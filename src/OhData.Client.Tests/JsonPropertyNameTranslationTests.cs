using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using OhData.Client.Internal;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// #253: the client's $filter/$select/$orderby translators emit a property's
/// <c>[System.Text.Json.Serialization.JsonPropertyName]</c> name (verbatim, ahead of the naming
/// policy) so query-option property names match the server's EDM/$metadata name.
/// </summary>
public class JsonPropertyNameTranslationTests
{
    private sealed class Customer
    {
        [JsonPropertyName("emailAddress")]
        public string Email { get; set; } = "";

        public string Name { get; set; } = "";
    }

    // ── $select ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Select_RenamedProperty_EmitsJsonName()
    {
        string result = SelectTranslator.Translate<Customer>(x => x.Email);
        Assert.Equal("emailAddress", result);
    }

    [Fact]
    public void Select_RenamedProperty_JsonNameWinsOverCamelCasePolicy()
    {
        // [JsonPropertyName] is emitted verbatim — the naming policy must NOT re-case it.
        string result = SelectTranslator.Translate<Customer>(x => x.Email, JsonNamingPolicy.CamelCase);
        Assert.Equal("emailAddress", result);
    }

    [Fact]
    public void Select_UnrenamedProperty_UsesPolicy()
    {
        Assert.Equal("Name", SelectTranslator.Translate<Customer>(x => x.Name));
        Assert.Equal("name", SelectTranslator.Translate<Customer>(x => x.Name, JsonNamingPolicy.CamelCase));
    }

    // ── $filter ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_RenamedProperty_EmitsJsonName()
    {
        Expression<Func<Customer, bool>> predicate = x => x.Email == "ada@example.com";
        string result = FilterTranslator.Translate(predicate);
        Assert.Equal("emailAddress eq 'ada@example.com'", result);
    }

    [Fact]
    public void Filter_RenamedProperty_JsonNameWinsOverCamelCasePolicy()
    {
        Expression<Func<Customer, bool>> predicate = x => x.Email == "ada@example.com";
        string result = FilterTranslator.Translate(predicate, JsonNamingPolicy.CamelCase);
        Assert.Equal("emailAddress eq 'ada@example.com'", result);
    }

    // ── #184 residual: NAVIGATION identifiers keep their CLR name ───────────────────
    //
    // The server intentionally does NOT apply the [JsonPropertyName] rename to navigation
    // identifiers ($expand/$filter/$orderby hop) — only the nav's JSON payload key is renamed. The
    // client must therefore emit the CLR name for a nav segment, or a substantively-renamed
    // navigation 400s at the server. A structural leaf, by contrast, keeps its rename.

    private sealed class Category
    {
        [JsonPropertyName("categoryName")]
        public string Name { get; set; } = "";
    }

    private sealed class Tag
    {
        public int Id { get; set; }
    }

    private sealed class Order
    {
        public int Id { get; set; }

        // Substantively-renamed to-one navigation (not merely a camelCase difference, which
        // case-insensitive server resolution would forgive).
        [JsonPropertyName("cat")]
        public Category Category { get; set; } = new();

        // Substantively-renamed to-many navigation.
        [JsonPropertyName("tagList")]
        public List<Tag> Tags { get; set; } = new();
    }

    private static EntitySetClient<Order> OrderBuilder() =>
        new OhDataClient(new HttpClient { BaseAddress = new Uri("http://localhost/") })
            .For<Order>("Orders");

    [Fact]
    public void Expand_RenamedToOneNavigation_EmitsClrIdentifier()
    {
        // $expand=Category, NOT $expand=cat — the nav identifier is not renamed server-side.
        string url = OrderBuilder().Expand(x => x.Category).BuildCollectionUrl();
        Assert.Equal("Orders?$expand=Category", url);
    }

    [Fact]
    public void Expand_RenamedToManyNavigation_EmitsClrIdentifier()
    {
        string url = OrderBuilder().Expand(x => x.Tags).BuildCollectionUrl();
        Assert.Equal("Orders?$expand=Tags", url);
    }

    [Fact]
    public void Filter_RenamedNavigationHop_KeepsClrNav_RenamesStructuralLeaf()
    {
        // Nav hop "Category" stays CLR; the structural leaf "Name" keeps its rename ("categoryName").
        Expression<Func<Order, bool>> predicate = x => x.Category.Name == "Books";
        string result = FilterTranslator.Translate(predicate);
        Assert.Equal("Category/categoryName eq 'Books'", result);
    }

    [Fact]
    public void OrderBy_RenamedNavigationHop_KeepsClrNav_RenamesStructuralLeaf()
    {
        string url = OrderBuilder().OrderBy(x => x.Category.Name).BuildCollectionUrl();
        Assert.Equal("Orders?$orderby=Category%2FcategoryName", url);
    }
}
