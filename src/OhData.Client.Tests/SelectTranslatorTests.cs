using System;
using System.Linq.Expressions;
using OhData.Client.Internal;
using Xunit;

namespace OhData.Client.Tests;

public class SelectTranslatorTests
{
    private sealed class Product
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public string Code { get; set; } = "";
        public Category? Category { get; set; }
    }

    private sealed class Category
    {
        public string Name { get; set; } = "";
        public Supplier? Supplier { get; set; }
    }

    private sealed class Supplier
    {
        public string Region { get; set; } = "";
    }

    private sealed class ProductDto
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public string CategoryName { get; set; } = "";
    }

    private static string S(Expression<Func<Product, object?>> expr)
        => SelectTranslator.Translate(expr);

    // ── Single property ─────────────────────────────────────────────────────────

    [Fact] public void SingleProperty() => Assert.Equal("Name", S(x => x.Name));
    [Fact] public void SingleProperty_Decimal() => Assert.Equal("Price", S(x => x.Price));

    // ── Anonymous type ──────────────────────────────────────────────────────────

    [Fact]
    public void AnonymousType_SingleProperty() =>
        Assert.Equal("Name", S(x => new { x.Name }));

    [Fact]
    public void AnonymousType_TwoProperties() =>
        Assert.Equal("Name,Price", S(x => new { x.Name, x.Price }));

    [Fact]
    public void AnonymousType_ThreeProperties() =>
        Assert.Equal("Name,Price,Code", S(x => new { x.Name, x.Price, x.Code }));

    // ── Navigation paths ────────────────────────────────────────────────────────

    [Fact]
    public void NavigationPath_OneLevelDeep() =>
        Assert.Equal("Category/Name", S(x => x.Category!.Name));

    [Fact]
    public void NavigationPath_TwoLevelsDeep() =>
        Assert.Equal("Category/Supplier/Region", S(x => x.Category!.Supplier!.Region));

    // ── Anonymous type mixing direct and nav properties ─────────────────────────

    [Fact]
    public void AnonymousType_MixedDirectAndNav() =>
        Assert.Equal("Name,Category/Name", S(x => new { x.Name, CategoryName = x.Category!.Name }));

    // ── MemberInitExpression ────────────────────────────────────────────────────

    [Fact]
    public void MemberInit_DirectProperties() =>
        Assert.Equal("Name,Price", SelectTranslator.Translate<Product>(
            x => new ProductDto { Name = x.Name, Price = x.Price }));

    [Fact]
    public void MemberInit_WithNavProperty() =>
        Assert.Equal("Name,Category/Name", SelectTranslator.Translate<Product>(
            x => new ProductDto { Name = x.Name, CategoryName = x.Category!.Name }));

    // ── Unsupported — should throw ──────────────────────────────────────────────

    [Fact]
    public void ConstantLiteral_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            SelectTranslator.Translate<Product>(x => "literal"));

    [Fact]
    public void MethodCall_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            SelectTranslator.Translate<Product>(x => x.Name.ToUpper()));
}
