using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OhData.Client.Internal;
using Xunit;

namespace OhData.Client.Tests;

public class FilterTranslatorTests
{
    private sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public bool IsActive { get; set; }
        public Sub? Sub { get; set; }
        public DateOnly Date { get; set; }
        public TimeOnly Time { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public double Score { get; set; }
        public float Rating { get; set; }
        public long Serial { get; set; }
        public char Initial { get; set; }
        public ItemStatus Status { get; set; }
        public string Status2 { get; set; } = "";
        public int? NullableId { get; set; }
        public decimal? NullablePrice { get; set; }
        public List<Tag> Tags { get; set; } = new();
    }

    private sealed class Tag
    {
        public string Name { get; set; } = "";
        public bool Active { get; set; }
        public Item? Owner { get; set; }
    }

    private sealed class Sub { public string Code { get; set; } = ""; }

    private enum ItemStatus { Active = 1, Inactive = 2 }

    private static string F<T>(System.Linq.Expressions.Expression<Func<T, bool>> expr)
        => FilterTranslator.Translate(expr);

    // ── Comparison operators ────────────────────────────────────────────────────

    [Fact] public void Equal_Int() => Assert.Equal("Id eq 1", F<Item>(x => x.Id == 1));
    [Fact] public void NotEqual_Int() => Assert.Equal("Id ne 1", F<Item>(x => x.Id != 1));
    [Fact] public void GreaterThan() => Assert.Equal("Price gt 10", F<Item>(x => x.Price > 10));
    [Fact] public void GreaterOrEqual() => Assert.Equal("Price ge 10", F<Item>(x => x.Price >= 10));
    [Fact] public void LessThan() => Assert.Equal("Price lt 10", F<Item>(x => x.Price < 10));
    [Fact] public void LessOrEqual() => Assert.Equal("Price le 10", F<Item>(x => x.Price <= 10));

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

    // ── Precedence: comparison operators around explicit and/or grouping ───────
    //
    // OData binds "eq"/"ne"/"gt"/"ge"/"lt"/"le" tighter than "and"/"or" (Part 2 §5.1.1.1).
    // When the LINQ expression tree explicitly groups an and/or expression as the *operand*
    // of a comparison (e.g. `b == (x || y)`), that grouping must be preserved with an
    // explicit extra pair of parens around the whole sub-expression — otherwise
    // "b eq (x) or (y)" (each operand of "or" separately wrapped, but not "x or y" as a
    // unit) is emitted, which a server parses as "(b eq (x)) or (y)": silently wrong.

    [Fact]
    public void Precedence_EqualityAroundOrElse_WrapsWholeRightOperand() =>
        Assert.Equal(
            "IsActive eq ((Price gt 5) or (Id eq 1))",
            F<Item>(x => x.IsActive == (x.Price > 5 || x.Id == 1)));

    [Fact]
    public void Precedence_EqualityAroundAndAlso_WrapsWholeLeftOperand() =>
        Assert.Equal(
            "((Price gt 5) and (IsActive eq true)) eq IsActive",
            F<Item>(x => (x.Price > 5 && x.IsActive == true) == x.IsActive));

    [Fact]
    public void Precedence_NotOfAndAlso_OrElse_NestedGroupingPreserved() =>
        // !(a || b) && c  →  (not ((a) or (b))) and (c)
        Assert.Equal(
            "(not ((IsActive) or (Id eq 1))) and (Price gt 5)",
            F<Item>(x => !(x.IsActive || x.Id == 1) && x.Price > 5));

    [Fact]
    public void Precedence_OrOfEqualityOperands_NoExtraWrapNeeded() =>
        // `a eq b or c`: eq already binds tighter than or, so no *extra* wrap is required
        // beyond the blanket and/or operand parens the translator already applies.
        Assert.Equal(
            "(Id eq 1) or (IsActive)",
            F<Item>(x => x.Id == 1 || x.IsActive));

    // ── String methods ──────────────────────────────────────────────────────────

    [Fact] public void Contains() => Assert.Equal("contains(Name,'foo')", F<Item>(x => x.Name.Contains("foo")));
    [Fact] public void StartsWith() => Assert.Equal("startswith(Name,'W')", F<Item>(x => x.Name.StartsWith("W")));
    [Fact] public void EndsWith() => Assert.Equal("endswith(Name,'t')", F<Item>(x => x.Name.EndsWith("t")));
    [Fact] public void ToLower() => Assert.Equal("tolower(Name) eq 'foo'", F<Item>(x => x.Name.ToLower() == "foo"));
    [Fact] public void ToUpper() => Assert.Equal("toupper(Name) eq 'FOO'", F<Item>(x => x.Name.ToUpper() == "FOO"));
    [Fact] public void Trim() => Assert.Equal("trim(Name) eq 'foo'", F<Item>(x => x.Name.Trim() == "foo"));

    // ── String literal escaping ─────────────────────────────────────────────────

    [Fact]
    public void String_SingleQuoteEscaped() =>
        Assert.Equal("Name eq 'it''s'", F<Item>(x => x.Name == "it's"));

    // ── Null literals ───────────────────────────────────────────────────────────

    [Fact] public void Equal_Null() => Assert.Equal("Name eq null", F<Item>(x => x.Name == null));
    [Fact] public void NotEqual_Null() => Assert.Equal("Name ne null", F<Item>(x => x.Name != null));

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
        string name = "Widget";
        Assert.Equal("Name eq 'Widget'", F<Item>(x => x.Name == name));
    }

    // M-9 regression: captured-variable evaluation now goes through a reflection fast
    // path (FilterTranslator.TryEvaluateAsObject) instead of unconditionally compiling a
    // fresh lambda per variable. Two variables captured from different lexical scopes are
    // merged by the C# compiler into a chained closure (DisplayClass -> DisplayClass), which
    // exercises the "nested closure: recurse" branch of that fast path — verify it still
    // resolves to the correct values.
    [Fact]
    public void CapturedVariables_FromNestedClosureScopes_ResolveCorrectly()
    {
        string outerName = "Sprocket";

        System.Linq.Expressions.Expression<Func<Item, bool>> BuildPredicate()
        {
            decimal innerThreshold = 5m;
            return x => x.Name == outerName && x.Price > innerThreshold;
        }

        var predicate = BuildPredicate();
        Assert.Equal("(Name eq 'Sprocket') and (Price gt 5)", FilterTranslator.Translate(predicate));
    }

    // M-9 regression: a captured variable that is itself a field on a captured object
    // (rather than a plain local) must still resolve via the reflection fast path.
    [Fact]
    public void CapturedVariable_FieldOnCapturedObject_ResolvesCorrectly()
    {
        var box = new StrongBox<decimal>(42m);
        Assert.Equal("Price eq 42", F<Item>(x => x.Price == box.Value));
    }

    private sealed class StrongBox<T>
    {
        public readonly T Value;
        public StrongBox(T value) => Value = value;
    }

    // ── String.IsNullOrEmpty ────────────────────────────────────────────────────

    [Fact]
    public void IsNullOrEmpty_ProducesCorrectFilter() =>
        Assert.Equal("(Name eq null or Name eq '')", F<Item>(x => string.IsNullOrEmpty(x.Name)));

    [Fact]
    public void IsNullOrEmpty_Negated_ProducesCorrectFilter() =>
        Assert.Equal("not ((Name eq null or Name eq ''))", F<Item>(x => !string.IsNullOrEmpty(x.Name)));

    // ── String.IsNullOrWhiteSpace ───────────────────────────────────────────────

    [Fact]
    public void IsNullOrWhiteSpace_ProducesCorrectFilter() =>
        Assert.Equal("(Name eq null or trim(Name) eq '')", F<Item>(x => string.IsNullOrWhiteSpace(x.Name)));

    // ── Arithmetic parenthesization ─────────────────────────────────────────────

    [Fact]
    public void Arithmetic_Add() =>
        Assert.Equal("(Price) add (1) eq 1", F<Item>(x => x.Price + 1 == 1));

    [Fact]
    public void Arithmetic_NestedAddInMultiply()
    {
        // (A + B) * C should not become A add B mul C (wrong precedence)
        // It should become ((A) add (B)) mul (C) which equals ((Price) add (1)) mul (2)
        string filter = F<Item>(x => (x.Price + 1) * 2 == 10);
        Assert.Contains("add", filter);
        Assert.Contains("mul", filter);
        // Ensure add operands are parenthesized — the add must appear inside parens before mul
        Assert.Contains("(Price) add (1)", filter);
    }

    // ── FormatLiteral: bool / null ─────────────────────────────────────────────

    [Fact] public void FormatLiteral_Null() => Assert.Equal("null", FilterTranslator.FormatLiteral(null));
    [Fact] public void FormatLiteral_True() => Assert.Equal("true", FilterTranslator.FormatLiteral(true));
    [Fact] public void FormatLiteral_False() => Assert.Equal("false", FilterTranslator.FormatLiteral(false));

    // ── FormatLiteral: string / char ───────────────────────────────────────────

    [Fact] public void FormatLiteral_String() => Assert.Equal("'hello'", FilterTranslator.FormatLiteral("hello"));
    [Fact] public void FormatLiteral_String_SingleQuoteEscaped() => Assert.Equal("'it''s'", FilterTranslator.FormatLiteral("it's"));
    [Fact] public void FormatLiteral_String_Empty() => Assert.Equal("''", FilterTranslator.FormatLiteral(""));
    [Fact] public void FormatLiteral_Char() => Assert.Equal("'A'", FilterTranslator.FormatLiteral('A'));

    // ── FormatLiteral: integers ────────────────────────────────────────────────

    [Fact] public void FormatLiteral_Int() => Assert.Equal("42", FilterTranslator.FormatLiteral(42));
    [Fact] public void FormatLiteral_Long() => Assert.Equal("9999999999", FilterTranslator.FormatLiteral(9_999_999_999L));
    [Fact] public void FormatLiteral_Short() => Assert.Equal("32767", FilterTranslator.FormatLiteral((short)32767));
    [Fact] public void FormatLiteral_Byte() => Assert.Equal("255", FilterTranslator.FormatLiteral((byte)255));
    [Fact] public void FormatLiteral_SByte() => Assert.Equal("-128", FilterTranslator.FormatLiteral((sbyte)-128));
    [Fact] public void FormatLiteral_UInt() => Assert.Equal("4294967295", FilterTranslator.FormatLiteral((uint)4_294_967_295));
    [Fact] public void FormatLiteral_ULong() => Assert.Equal("9999999999", FilterTranslator.FormatLiteral((ulong)9_999_999_999));
    [Fact] public void FormatLiteral_UShort() => Assert.Equal("65535", FilterTranslator.FormatLiteral((ushort)65535));

    // ── FormatLiteral: floating-point ──────────────────────────────────────────

    [Fact] public void FormatLiteral_Decimal() => Assert.Equal("9.99", FilterTranslator.FormatLiteral(9.99m));
    [Fact] public void FormatLiteral_Float() => Assert.Equal("1.5", FilterTranslator.FormatLiteral(1.5f));
    [Fact] public void FormatLiteral_Double() => Assert.Equal("1.5", FilterTranslator.FormatLiteral(1.5));

    // ── FormatLiteral: Guid ────────────────────────────────────────────────────

    [Fact]
    public void FormatLiteral_Guid()
    {
        var g = Guid.Parse("12345678-1234-1234-1234-123456789012");
        Assert.Equal("12345678-1234-1234-1234-123456789012", FilterTranslator.FormatLiteral(g));
    }

    // ── FormatLiteral: DateTime / DateTimeOffset ───────────────────────────────

    [Fact]
    public void FormatLiteral_DateTimeUtc()
    {
        // OData 4.0: DateTimeOffset literals are NOT quoted.
        var dt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2024-06-01T12:00:00Z", FilterTranslator.FormatLiteral(dt));
    }

    // B3: DateTimeKind.Unspecified is treated as UTC (not left offset-less). An offset-less
    // literal violates the OData ABNF (dateTimeOffsetValue always requires "Z" or a numeric
    // offset) and the Microsoft URI parser — and therefore OhData's own server — rejects it
    // with 400. See FilterTranslator.FormatDateTime for the full rationale.
    [Fact]
    public void FormatLiteral_DateTimeUnspecified_TreatedAsUtc()
    {
        var dt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal("2024-06-01T12:00:00Z", FilterTranslator.FormatLiteral(dt));
    }

    // B3: DateTimeKind.Local is converted to its UTC-equivalent instant before formatting,
    // rather than emitting the wall-clock value with no offset (which is both spec-invalid
    // and, if it were "fixed" by attaching the local machine's offset instead, ambiguous once
    // the value leaves the machine that produced it / drifts under DST).
    [Fact]
    public void FormatLiteral_DateTimeLocal_ConvertsToUtc()
    {
        var local = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);
        DateTime expectedUtc = local.ToUniversalTime();
        string expectedLiteral = expectedUtc.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

        string literal = FilterTranslator.FormatLiteral(local);

        // Computed independently of the test runner's local timezone offset: whatever that
        // offset is, FormatLiteral must land on the same UTC instant DateTime.ToUniversalTime()
        // would compute for it.
        Assert.Equal(expectedLiteral, literal);
        Assert.EndsWith("Z", literal);
    }

    [Fact]
    public void FormatLiteral_DateTimeOffsetUtc()
    {
        var dto = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("2024-06-01T12:00:00Z", FilterTranslator.FormatLiteral(dto));
    }

    [Fact]
    public void FormatLiteral_DateTimeOffsetWithOffset()
    {
        var dto = new DateTimeOffset(2024, 6, 1, 12, 0, 0, new TimeSpan(5, 30, 0));
        Assert.Equal("2024-06-01T12:00:00+05:30", FilterTranslator.FormatLiteral(dto));
    }

    // ── FormatLiteral: DateOnly / TimeOnly ─────────────────────────────────────

    [Fact]
    public void FormatLiteral_DateOnly_Unquoted()
    {
        // OData 4.0: Edm.Date literals are NOT wrapped in single quotes.
        // Single-quoted '2024-01-15' would be interpreted as an Edm.String by the server.
        Assert.Equal("2024-01-15", FilterTranslator.FormatLiteral(new DateOnly(2024, 1, 15)));
    }

    [Fact]
    public void FormatLiteral_TimeOnly_Unquoted()
    {
        // OData 4.0: Edm.TimeOfDay literals are NOT wrapped in single quotes.
        Assert.Equal("08:30:00", FilterTranslator.FormatLiteral(new TimeOnly(8, 30, 0)));
    }

    // ── FormatLiteral: enum ────────────────────────────────────────────────────

    [Fact]
    public void FormatLiteral_Enum_UsesMemberName() =>
        Assert.Equal("'Active'", FilterTranslator.FormatLiteral(ItemStatus.Active));

    // ── Filter expressions: date and time types ────────────────────────────────

    [Fact]
    public void Filter_DateOnly_Equality()
    {
        // H-3: DateOnly in $filter must produce an unquoted Edm.Date literal.
        Assert.Equal("Date eq 2024-01-15", F<Item>(x => x.Date == new DateOnly(2024, 1, 15)));
    }

    [Fact]
    public void Filter_DateOnly_GreaterThan() =>
        Assert.Equal("Date gt 2024-01-15", F<Item>(x => x.Date > new DateOnly(2024, 1, 15)));

    [Fact]
    public void Filter_TimeOnly_Equality()
    {
        // H-3: TimeOnly in $filter must produce an unquoted Edm.TimeOfDay literal.
        Assert.Equal("Time eq 08:30:00", F<Item>(x => x.Time == new TimeOnly(8, 30, 0)));
    }

    [Fact]
    public void Filter_DateTime_Utc() =>
        Assert.Equal(
            "CreatedAt eq 2024-06-01T12:00:00Z",
            F<Item>(x => x.CreatedAt == new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc)));

    // B3: Kind=Unspecified must still produce a spec-valid ("Z"-suffixed) literal, not the
    // offset-less string the server previously rejected with 400.
    [Fact]
    public void Filter_DateTime_Unspecified_EmitsZSuffix()
    {
        string filter = F<Item>(x => x.CreatedAt == new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal("CreatedAt eq 2024-06-01T12:00:00Z", filter);
    }

    // B3: Kind=Local (the Kind of DateTime.Now) must convert to UTC rather than emit a
    // wall-clock value with no offset. This is the "bread-and-butter" repro from the review:
    // x => x.CreatedAt > DateTime.Now used to produce an offset-less literal.
    [Fact]
    public void Filter_DateTime_Local_ConvertsToUtc()
    {
        var local = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);
        string expected = $"CreatedAt eq {local.ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z";

        string filter = F<Item>(x => x.CreatedAt == local);

        Assert.Equal(expected, filter);
    }

    [Fact]
    public void Filter_DateTimeOffset_Utc() =>
        Assert.Equal(
            "UpdatedAt gt 2024-06-01T12:00:00Z",
            F<Item>(x => x.UpdatedAt > new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero)));

    // B3: DateTimeOffset already carries its own offset and must pass through unchanged
    // (this path was already correct; guarding against regression from the DateTime-Kind fix).
    [Fact]
    public void Filter_DateTimeOffset_NonZeroOffset_PassesThroughUnchanged() =>
        Assert.Equal(
            "UpdatedAt eq 2024-06-01T12:00:00+05:30",
            F<Item>(x => x.UpdatedAt == new DateTimeOffset(2024, 6, 1, 12, 0, 0, new TimeSpan(5, 30, 0))));

    // ── Date/time component accessors and string.Length → canonical functions ─
    //
    // x.CreatedAt.Year used to translate to the nonsensical nested-property path
    // "CreatedAt/Year" instead of the OData canonical function year(CreatedAt), producing a
    // confusing 400 from any real server. Same for Month/Day/Hour/Minute/Second and
    // string.Length vs. length().

    [Fact]
    public void Filter_DateTimeYear_TranslatesToYearFunction() =>
        Assert.Equal("year(CreatedAt) eq 2024", F<Item>(x => x.CreatedAt.Year == 2024));

    [Fact]
    public void Filter_DateTimeMonth_TranslatesToMonthFunction() =>
        Assert.Equal("month(CreatedAt) eq 6", F<Item>(x => x.CreatedAt.Month == 6));

    [Fact]
    public void Filter_DateTimeDay_TranslatesToDayFunction() =>
        Assert.Equal("day(CreatedAt) eq 1", F<Item>(x => x.CreatedAt.Day == 1));

    [Fact]
    public void Filter_DateTimeHour_TranslatesToHourFunction() =>
        Assert.Equal("hour(CreatedAt) eq 12", F<Item>(x => x.CreatedAt.Hour == 12));

    [Fact]
    public void Filter_DateTimeMinute_TranslatesToMinuteFunction() =>
        Assert.Equal("minute(CreatedAt) eq 30", F<Item>(x => x.CreatedAt.Minute == 30));

    [Fact]
    public void Filter_DateTimeSecond_TranslatesToSecondFunction() =>
        Assert.Equal("second(CreatedAt) eq 15", F<Item>(x => x.CreatedAt.Second == 15));

    [Fact]
    public void Filter_DateTimeOffsetYear_TranslatesToYearFunction() =>
        Assert.Equal("year(UpdatedAt) eq 2024", F<Item>(x => x.UpdatedAt.Year == 2024));

    [Fact]
    public void Filter_DateOnlyYear_TranslatesToYearFunction() =>
        Assert.Equal("year(Date) eq 2024", F<Item>(x => x.Date.Year == 2024));

    [Fact]
    public void Filter_TimeOnlyHour_TranslatesToHourFunction() =>
        Assert.Equal("hour(Time) eq 8", F<Item>(x => x.Time.Hour == 8));

    [Fact]
    public void Filter_StringLength_TranslatesToLengthFunction() =>
        Assert.Equal("length(Name) eq 5", F<Item>(x => x.Name.Length == 5));

    [Fact]
    public void Filter_StringLength_NavigationPath_TranslatesToLengthFunction() =>
        Assert.Equal("length(Sub/Code) gt 3", F<Item>(x => x.Sub!.Code.Length > 3));

    // ── Filter expressions: numeric types ─────────────────────────────────────

    [Fact]
    public void Filter_Long_Equality() =>
        Assert.Equal("Serial eq 9999999999", F<Item>(x => x.Serial == 9_999_999_999L));

    [Fact]
    public void Filter_Double_Comparison() =>
        Assert.Equal("Score gt 4.5", F<Item>(x => x.Score > 4.5));

    [Fact]
    public void Filter_Float_Comparison() =>
        Assert.Equal("Rating le 4.5", F<Item>(x => x.Rating <= 4.5f));

    // ── Filter expressions: other primitive types ──────────────────────────────

    [Fact]
    public void Filter_Bool_Equality() =>
        Assert.Equal("IsActive eq true", F<Item>(x => x.IsActive == true));

    [Fact]
    public void Filter_Char_Equality()
    {
        // char literals in lambdas are promoted to int by the C# compiler.
        // Use a captured variable so FormatLiteral receives the char value.
        char initial = 'A';
        Assert.Equal("Initial eq 'A'", F<Item>(x => x.Initial == initial));
    }

    [Fact]
    public void Filter_Enum_UsesMemberName() =>
        Assert.Equal("Status eq 'Active'", F<Item>(x => x.Status == ItemStatus.Active));

    [Fact]
    public void Filter_Guid_Equality()
    {
        var g = Guid.Parse("12345678-1234-1234-1234-123456789012");
        Assert.Equal($"Id eq {g}", F<GuidItem>(x => x.Id == g));
    }

    // ── Unsupported — should throw ──────────────────────────────────────────────

    [Fact]
    public void UnsupportedMethod_Throws() =>
        Assert.Throws<NotSupportedException>(() =>
            F<Item>(x => string.Compare(x.Name, "foo", StringComparison.Ordinal) == 0));

    // ── M-10: any/all collection lambda support ────────────────────────────────

    [Fact]
    public void Filter_Any_TranslatesToODataAny() =>
        Assert.Equal(
            "Tags/any(t: t/Name eq 'sale')",
            F<Item>(x => x.Tags.Any(t => t.Name == "sale")));

    [Fact]
    public void Filter_All_TranslatesToODataAll() =>
        Assert.Equal(
            "Tags/all(t: t/Active eq true)",
            F<Item>(x => x.Tags.All(t => t.Active == true)));

    [Fact]
    public void Filter_Any_WithNoArg_Throws() =>
        Assert.Throws<NotSupportedException>(() =>
            F<Item>(x => x.Tags.Any()));

    // ── B4: outer-lambda references inside Any/All ─────────────────────────────
    //
    // Referencing the outer range variable (or a property of it) from inside a nested
    // any()/all() predicate used to fall through to a silent "null" literal (TryEvaluateAsObject
    // swallowing the "unbound parameter" exception from trying to compile the outer parameter
    // as a constant). That produced a syntactically valid but semantically wrong filter —
    // silently wrong query results, the worst failure class. The outer parameter is addressed
    // via the OData implicit iteration variable "$it" (Part 2 §5.1.1.10.1) since the range
    // variable inside the lambda shadows the unqualified top-level reference.

    [Fact]
    public void Filter_Any_OuterProperty_TranslatesToItPath() =>
        Assert.Equal(
            "Tags/any(t: t/Name eq $it/Name)",
            F<Item>(x => x.Tags.Any(t => t.Name == x.Name)));

    [Fact]
    public void Filter_All_OuterProperty_TranslatesToItPath() =>
        Assert.Equal(
            "Tags/all(t: t/Name eq $it/Name)",
            F<Item>(x => x.Tags.All(t => t.Name == x.Name)));

    [Fact]
    public void Filter_Any_OuterScalarPropertyComparedAgainstConstant_StillWorks() =>
        // Regression guard: an outer-scope reference combined with an ordinary constant
        // comparison in the same predicate must not disturb either side's translation.
        Assert.Equal(
            "Tags/any(t: (t/Active eq true) and (t/Name eq $it/Name))",
            F<Item>(x => x.Tags.Any(t => t.Active == true && t.Name == x.Name)));

    [Fact]
    public void Filter_Any_OuterRangeVariableItself_TranslatesToIt()
    {
        // The bare outer parameter (not one of its properties) referenced from inside the
        // nested lambda — e.g. comparing a nav property back to the whole outer instance.
        // FilterTranslator has no VisitParameter override prior to this fix, so a bare
        // ParameterExpression silently produced no output at all.
        string filter = F<Item>(x => x.Tags.Any(t => t.Owner == x));
        Assert.Equal("Tags/any(t: t/Owner eq $it)", filter);
    }

    [Fact]
    public void Filter_Any_GenuinelyUntranslatableOuterReference_Throws()
    {
        // A member access on a conditional expression that itself reads an outer-scope
        // member: there is no OData path that can express a ternary, so this must fail loudly
        // (NotSupportedException) rather than silently degrade to a "null" literal.
        var ex = Assert.Throws<NotSupportedException>(() =>
            F<Item>(x => x.Tags.Any(t => t.Name == (x.IsActive ? x.Sub : x.Sub)!.Code)));
        Assert.Contains("cannot be translated", ex.Message);
    }

    // ── M-11: Enumerable.Contains / in operator ────────────────────────────────

    [Fact]
    public void Filter_EnumerableContains_TranslatesToInOperator()
    {
        string[] statuses = new[] { "Active", "Pending" };
        Assert.Equal(
            "Status2 in ('Active','Pending')",
            F<Item>(x => statuses.Contains(x.Status2)));
    }

    [Fact]
    public void Filter_EnumerableContains_IntValues()
    {
        int[] ids = new[] { 1, 2, 3 };
        Assert.Equal(
            "Id in (1,2,3)",
            F<Item>(x => ids.Contains(x.Id)));
    }

    // ── M-12: Property names respect JsonNamingPolicy ──────────────────────────

    [Fact]
    public void Filter_WithCamelCasePolicy_EmitsCamelCasePropertyNames() =>
        Assert.Equal(
            "name eq 'foo'",
            FilterTranslator.Translate<Item>(x => x.Name == "foo", JsonNamingPolicy.CamelCase));

    [Fact]
    public void Filter_WithNullPolicy_EmitsPascalCase() =>
        Assert.Equal(
            "Name eq 'foo'",
            FilterTranslator.Translate<Item>(x => x.Name == "foo", null));

    // ── L-15: Nullable<T> property patterns ───────────────────────────────────

    [Fact]
    public void Filter_NullableHasValue_TranslatesToNeNull() =>
        Assert.Equal(
            "NullablePrice ne null",
            F<Item>(x => x.NullablePrice.HasValue));

    [Fact]
    public void Filter_NullableValue_StripsValueAccessor() =>
        Assert.Equal(
            "NullablePrice gt 10",
            F<Item>(x => x.NullablePrice!.Value > 10));

    [Fact]
    public void Filter_NullableEquals_EqNull() =>
        Assert.Equal(
            "NullablePrice eq null",
            F<Item>(x => x.NullablePrice == null));

    // ── M-15: StringComparison overloads better error ─────────────────────────

    [Fact]
    public void Filter_ContainsWithStringComparison_ThrowsNotSupportedException()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            F<Item>(x => x.Name.Contains("foo", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("StringComparison", ex.Message);
    }

    [Fact]
    public void Filter_StartsWithStringComparison_ThrowsNotSupportedException()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            F<Item>(x => x.Name.StartsWith("foo", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("StringComparison", ex.Message);
    }

    // ── L-16: DateTime/DateTimeOffset filter literals sub-second precision ─────

    [Fact]
    public void FormatLiteral_DateTime_WithSubSecond_IncludesFractional()
    {
        var dt = new DateTime(2024, 1, 15, 10, 30, 0, 123, DateTimeKind.Utc);
        Assert.Equal("2024-01-15T10:30:00.123Z", FilterTranslator.FormatLiteral(dt));
    }

    [Fact]
    public void FormatLiteral_DateTime_NoSubSecond_OmitsFractional()
    {
        var dt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        Assert.Equal("2024-01-15T10:30:00Z", FilterTranslator.FormatLiteral(dt));
    }

    [Fact]
    public void FormatLiteral_DateTimeOffset_WithSubSecond_IncludesFractional()
    {
        var dto = new DateTimeOffset(2024, 1, 15, 10, 30, 0, 123, new TimeSpan(5, 0, 0));
        Assert.Equal("2024-01-15T10:30:00.123+05:00", FilterTranslator.FormatLiteral(dto));
    }

    // ── Helper entity for Guid key tests ────────────────────────────────────────

    private sealed class GuidItem { public Guid Id { get; set; } }
}
