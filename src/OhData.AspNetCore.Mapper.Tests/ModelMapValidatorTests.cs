using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// Startup validation. Every case here produces a silently wrong <c>200</c> if it is allowed
/// through — an undeclared member serialises as its default, which no client can distinguish from a
/// genuinely empty value — which is why the checks are unconditional rather than opt-in.
/// </summary>
public sealed class ModelMapValidatorTests
{
    private sealed class PartialDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
    }

    private sealed class NoCtorDto
    {
        public NoCtorDto(int id) => Id = id;

        public int Id { get; set; }
    }

    private sealed class ComputedDto
    {
        public int Id { get; set; }
        public string Untranslatable { get; set; } = "";
    }

    [Fact]
    public void AnUndeclaredMember_IsRefused_NamingItAndTheRemedy()
    {
        ModelMapBuilder<Product, PartialDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ModelMapValidator.Validate(m.Build(), new ModelMapRegistry().Add(m.Build())));

        Assert.Contains("PartialDto.Title", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ignore(d => d.Title)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitlyIgnoredMember_Satisfies_TheCompletenessCheck()
    {
        ModelMapBuilder<Product, PartialDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);
        m.Ignore(d => d.Title);

        ModelMap map = m.Build();
        ModelMapValidator.Validate(map, new ModelMapRegistry().Add(map));
    }

    [Fact]
    public void AModelWithNoParameterlessConstructor_IsRefused()
    {
        ModelMapBuilder<Product, NoCtorDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);

        ModelMap map = m.Build();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ModelMapValidator.Validate(map, new ModelMapRegistry().Add(map)));

        Assert.Contains("NoCtorDto", ex.Message, StringComparison.Ordinal);
        Assert.Contains("parameterless constructor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANavigationWhoseTargetHasNoMap_IsRefused_NamingTheDeclaration()
    {
        ModelMap root = Maps.Product();

        // Registered alone: Tags, Reviews and Category all reach model types with no map.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ModelMapValidator.Validate(root, new ModelMapRegistry().Add(root)));

        Assert.Contains("ProductDto.Tags", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nested<Tag, TagDto>(...)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompleteRegistry_Validates()
    {
        ModelMapRegistry registry = Maps.Registry();
        ModelMapValidator.Validate(registry.Find(typeof(ProductDto))!, registry);
    }

    [Fact]
    public void ANavigationWhoseMapComesFromAnotherEntity_IsRefused()
    {
        // TagDto declared from Review rather than Tag: the shapes happen to be compatible enough to
        // compile, and the resulting query would read the wrong table.
        ModelMapBuilder<Review, TagDto> wrong = new();
        wrong.Property(d => d.Id).From(o => o.Id);
        wrong.Property(d => d.Label).From(o => o.Body);

        ModelMapRegistry registry = new ModelMapRegistry()
            .Add(Maps.Product())
            .Add(Maps.Category())
            .Add(wrong.Build())
            .Add(Maps.Review());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ModelMapValidator.Validate(registry.Find(typeof(ProductDto))!, registry));

        Assert.Contains("ProductDto.Tags", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Review", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoMapsForOneModelType_AreRefusedAtRegistration()
    {
        ModelMapBuilder<Review, TagDto> other = new();
        other.Property(d => d.Id).From(o => o.Id);
        other.Property(d => d.Label).From(o => o.Body);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => new ModelMapRegistry().Add(Maps.Tag()).Add(other.Build()));

        Assert.Contains("TagDto", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one entity type", ex.Message, StringComparison.Ordinal);
    }

    // ── Format ────────────────────────────────────────────────────────────────────────────────

    private sealed class FormattedDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    [Fact]
    public void AFormatSpecifier_IsRefusedAtStartup() =>
        AssertFormatRefused(m => m.Property(d => d.Label).Format(o => $"{o.Rank:N2}"));

    [Fact]
    public void AFormatAlignment_IsRefusedAtStartup() =>
        AssertFormatRefused(m => m.Property(d => d.Label).Format(o => $"{o.Rank,10}"));

    [Fact]
    public void AFormatAlignmentAndSpecifierTogether_IsRefusedAtStartup() =>
        AssertFormatRefused(m => m.Property(d => d.Label).Format(o => $"{o.Rank,-10:C}"));

    /// <summary>
    /// Refused rather than emitted: SQL has no equivalent, and the previous pattern matched neither
    /// shape — so the placeholder reached the wire as literal text (<c>"{0:N2}"</c>) and the value
    /// was dropped, on every row, under a <c>200</c>.
    /// </summary>
    private static void AssertFormatRefused(Action<ModelMapBuilder<Product, FormattedDto>> declare)
    {
        ModelMapBuilder<Product, FormattedDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);
        declare(m);

        ModelMap map = m.Build();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ModelMapValidator.Validate(map, new ModelMapRegistry().Add(map)));

        Assert.Contains("FormattedDto.Label", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be translated to SQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEscapedBrace_IsUnescaped_NotDoubled()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using MapDb db = MapDb.Seeded(connection);

        ModelMapBuilder<Product, FormattedDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);
        m.Property(d => d.Label).Format(o => $"{{{o.First}}}");

        ModelMap map = m.Build();
        ModelMapRegistry registry = new ModelMapRegistry().Add(map);
        ModelMapValidator.Validate(map, registry);

        var projection = (Expression<Func<Product, FormattedDto>>)
            ModelProjection.BuildLambda(map, registry);

        FormattedDto first = db.Products.OrderBy(p => p.Id).Select(projection).First();
        Assert.Equal("{Ada}", first.Label);
    }

    // ── Guards that are reachable from a declaration ──────────────────────────────────────────

    [Fact]
    public void AFormatThatIsNotAnInterpolation_IsRefusedAtStartup()
    {
        ModelMapBuilder<Product, FormattedDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);

        // Compiles -- FormattableString is the declared parameter type -- but it is a method call,
        // not an interpolation, so there is no format string to decompose.
        m.Property(d => d.Label).Format(o => Bracket(o.Name));

        ModelMap map = m.Build();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ModelMapValidator.Validate(map, new ModelMapRegistry().Add(map)));

        Assert.Contains("FormattedDto.Label", ex.Message, StringComparison.Ordinal);
        Assert.Contains("expects a string interpolation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APublicFieldOnTheModel_IsMappedLikeAProperty()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using MapDb db = MapDb.Seeded(connection);

        ModelMapBuilder<Product, FieldDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);
        m.Property(d => d.Name).From(o => o.Name);

        ModelMap map = m.Build();
        ModelMapRegistry registry = new ModelMapRegistry().Add(map);

        var projection = (Expression<Func<Product, FieldDto>>)ModelProjection.BuildLambda(map, registry);
        Assert.Equal("Hammer", db.Products.OrderBy(p => p.Id).Select(projection).First().Name);
    }

    private static FormattableString Bracket(string value) => $"<{value}>";

    private sealed class FieldDto
    {
        public int Id { get; set; }

        /// <summary>
        /// A field rather than a property, which the member reader has to handle. Deliberately
        /// mutable: this stands in for an adopter's DTO, and a DTO field is a data carrier.
        /// <c>readonly</c> would in fact bind and compile — measured, an init-only field is
        /// writable through <c>Expression.Bind</c> — but it would make the fixture unrepresentative.
        /// </summary>
        public string Name = "";
    }

    // ── The translatability probe ─────────────────────────────────────────────────────────────

    [Fact]
    public void AComputeTheProviderCannotTranslate_IsReported_NamingTheMember()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using MapDb db = MapDb.Seeded(connection);

        ModelMapBuilder<Product, ComputedDto> m = new();
        m.Property(d => d.Id).From(o => o.Id);

        // A client-side method: EF Core has no translation for it, and since 3.0 it will not silently
        // evaluate it in a Where or an OrderBy either -- it throws.
        m.Property(d => d.Untranslatable).Compute(o => Describe(o.Name));

        ModelMap map = m.Build();
        ModelMapRegistry registry = new ModelMapRegistry().Add(map);

        IReadOnlyList<(string Member, string Reason)> failures =
            ModelMapValidator.ProbeTranslatability(
                map, registry, db.Products.AsNoTracking(), q => q.ToQueryString());

        (string Member, string Reason) failure = Assert.Single(failures);
        Assert.Equal("ComputedDto.Untranslatable", failure.Member);
        Assert.NotEmpty(failure.Reason);
    }

    [Fact]
    public void EveryBindingOfTheShippedMap_Translates()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using MapDb db = MapDb.Seeded(connection);

        ModelMapRegistry registry = Maps.Registry();

        // The claim the Format binding rests on: an interpolation decomposed into folded
        // string.Concat really does reach SQL, so a $filter over DisplayName is not a runtime 500.
        Assert.Empty(ModelMapValidator.ProbeTranslatability(
            registry.Find(typeof(ProductDto))!, registry, db.Products.AsNoTracking(),
            q => q.ToQueryString()));
    }

    private static string Describe(string value) => value.Normalize() + "!";
}
