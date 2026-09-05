using System;
using System.Collections.Generic;
using System.Linq;
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
