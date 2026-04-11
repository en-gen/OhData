using OhData.Client.Internal;
using Xunit;

namespace OhData.Client.Tests;

public class FilterTranslatorTests
{
    private sealed class Item
    {
        public int     Id       { get; set; }
        public string  Name     { get; set; } = "";
        public decimal Price    { get; set; }
        public bool    IsActive { get; set; }
        public Sub?    Sub      { get; set; }
    }

    private sealed class Sub { public string Code { get; set; } = ""; }

    private static string F<T>(System.Linq.Expressions.Expression<Func<T, bool>> expr)
        => FilterTranslator.Translate(expr);

    // ── Comparison operators ────────────────────────────────────────────────────

    [Fact] public void Equal_Int()     => Assert.Equal("Id eq 1",     F<Item>(x => x.Id == 1));
    [Fact] public void NotEqual_Int()  => Assert.Equal("Id ne 1",     F<Item>(x => x.Id != 1));
    [Fact] public void GreaterThan()   => Assert.Equal("Price gt 10", F<Item>(x => x.Price > 10));
    [Fact] public void GreaterOrEqual()=> Assert.Equal("Price ge 10", F<Item>(x => x.Price >= 10));
    [Fact] public void LessThan()      => Assert.Equal("Price lt 10", F<Item>(x => x.Price < 10));
    [Fact] public void LessOrEqual()   => Assert.Equal("Price le 10", F<Item>(x => x.Price <= 10));

    // ── Logical operators ───────────────────────────────────────────────────────

    [Fact]
    public void And() =>
        Assert.Equal("(Price gt 10) and (Id eq 1)", F<Item>(x => x.Price > 10 && x.Id == 1));

    [Fact]
    public void Or() =>
        Assert.Equal("(Price lt 5) or (Id eq 99)", F<Item>(x => x.Price < 5 || x.Id == 99));

    [Fact]
    public void Not() =>
        Assert.Equal("not (IsActive)", F<Item>(x => !x.IsActive));

    // ── String methods ──────────────────────────────────────────────────────────

    [Fact] public void Contains()   => Assert.Equal("contains(Name,'foo')",      F<Item>(x => x.Name.Contains("foo")));
    [Fact] public void StartsWith() => Assert.Equal("startswith(Name,'W')",      F<Item>(x => x.Name.StartsWith("W")));
    [Fact] public void EndsWith()   => Assert.Equal("endswith(Name,'t')",        F<Item>(x => x.Name.EndsWith("t")));
    [Fact] public void ToLower()    => Assert.Equal("tolower(Name) eq 'foo'",    F<Item>(x => x.Name.ToLower() == "foo"));
    [Fact] public void ToUpper()    => Assert.Equal("toupper(Name) eq 'FOO'",    F<Item>(x => x.Name.ToUpper() == "FOO"));
    [Fact] public void Trim()       => Assert.Equal("trim(Name) eq 'foo'",       F<Item>(x => x.Name.Trim() == "foo"));

    // ── String literal escaping ─────────────────────────────────────────────────

    [Fact]
    public void String_SingleQuoteEscaped() =>
        Assert.Equal("Name eq 'it''s'", F<Item>(x => x.Name == "it's"));

    // ── Null literals ───────────────────────────────────────────────────────────

    [Fact] public void Equal_Null()    => Assert.Equal("Name eq null",  F<Item>(x => x.Name == null));
    [Fact] public void NotEqual_Null() => Assert.Equal("Name ne null",  F<Item>(x => x.Name != null));

    // ── Navigation paths ────────────────────────────────────────────────────────

    [Fact]
    public void NavigationProperty() =>
        Assert.Equal("Sub/Code eq 'ABC'", F<Item>(x => x.Sub!.Code == "ABC"));

    // ── Captured variables ──────────────────────────────────────────────────────

    [Fact]
    public void CapturedVariable()
    {
        decimal threshold = 99.9m;
        Assert.Equal("Price gt 99.9", F<Item>(x => x.Price > threshold));
    }

    [Fact]
    public void CapturedString()
    {
        var name = "Widget";
        Assert.Equal("Name eq 'Widget'", F<Item>(x => x.Name == name));
    }

    // ── Literal formatting ──────────────────────────────────────────────────────

    [Fact] public void FormatLiteral_Null()    => Assert.Equal("null",  FilterTranslator.FormatLiteral(null));
    [Fact] public void FormatLiteral_True()    => Assert.Equal("true",  FilterTranslator.FormatLiteral(true));
    [Fact] public void FormatLiteral_False()   => Assert.Equal("false", FilterTranslator.FormatLiteral(false));
    [Fact] public void FormatLiteral_Int()     => Assert.Equal("42",    FilterTranslator.FormatLiteral(42));
    [Fact] public void FormatLiteral_Decimal() => Assert.Equal("9.99",  FilterTranslator.FormatLiteral(9.99m));

    [Fact]
    public void FormatLiteral_Guid()
    {
        var g = Guid.Parse("12345678-1234-1234-1234-123456789012");
        Assert.Equal("12345678-1234-1234-1234-123456789012", FilterTranslator.FormatLiteral(g));
    }

    [Fact]
    public void FormatLiteral_DateTimeUtc()
    {
        var dt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("'2024-06-01T12:00:00Z'", FilterTranslator.FormatLiteral(dt));
    }

    // ── Unsupported — should throw ──────────────────────────────────────────────

    [Fact]
    public void UnsupportedMethod_Throws() =>
        Assert.Throws<NotSupportedException>(() =>
            F<Item>(x => string.Compare(x.Name, "foo", StringComparison.Ordinal) == 0));
}
