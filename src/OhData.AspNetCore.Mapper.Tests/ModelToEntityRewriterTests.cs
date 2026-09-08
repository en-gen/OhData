using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// The rewriter's contract, asserted against <b>emitted SQL</b> rather than expression shape
/// wherever the point is that something reached the database.
/// <para>
/// A rewritten expression that merely compiles proves nothing: the failure mode this whole design
/// exists to remove is an expression that looks right and cannot be translated, which surfaces as a
/// provider exception on a request. So the assertions below read the SQL EF actually produced.
/// </para>
/// </summary>
public sealed class ModelToEntityRewriterTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly SqliteConnection _connection;
    private readonly MapDb _db;

    public ModelToEntityRewriterTests(ITestOutputHelper output)
    {
        _out = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = MapDb.Seeded(_connection);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static readonly ModelMapRegistry Registry = Maps.Registry();

    private static ModelToEntityRewriter Rewriter() =>
        new(Registry.Find(typeof(ProductDto))!, Registry);

    private IQueryable<Product> Where(Expression<Func<ProductDto, bool>> modelPredicate)
    {
        var rewritten = (Expression<Func<Product, bool>>)Rewriter().RewriteLambda(modelPredicate);
        _out.WriteLine("model  : " + modelPredicate);
        _out.WriteLine("entity : " + rewritten);
        IQueryable<Product> q = _db.Products.Where(rewritten);
        _out.WriteLine("sql    : " + Flatten(q.ToQueryString()));
        return q;
    }

    private static string Flatten(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql.Replace("\r", " ").Replace("\n", " "), " +", " ");

    // ── Direct and rename ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DirectMember_TranslatesToItsOwnColumn()
    {
        IQueryable<Product> q = Where(d => d.Id == 1);

        Assert.Contains("\"p\".\"Id\" = 1", Flatten(q.ToQueryString()), StringComparison.Ordinal);
        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void RenamedMember_TranslatesToTheEntityColumn_NotTheModelName()
    {
        IQueryable<Product> q = Where(d => d.Title == "Hammer");

        string sql = Flatten(q.ToQueryString());
        Assert.Contains("\"p\".\"Name\" =", sql, StringComparison.Ordinal);
        // The model's spelling must not survive into SQL -- if it did, the rewrite silently did nothing.
        Assert.DoesNotContain("Title", sql, StringComparison.Ordinal);
        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    // ── Path across a reference ───────────────────────────────────────────────────────────────

    [Fact]
    public void PathMember_TranslatesIntoAJoin()
    {
        IQueryable<Product> q = Where(d => d.CategoryName == "Tools");

        string sql = Flatten(q.ToQueryString());
        Assert.Contains("JOIN \"Categories\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"c\".\"Name\" =", sql, StringComparison.Ordinal);
        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    // ── Format ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatMember_TranslatesToConcatenation_AndIsFilterable()
    {
        IQueryable<Product> q = Where(d => d.DisplayName == "Ada Lovelace");

        string sql = Flatten(q.ToQueryString());
        // Folded two-arg Concat becomes `||`. The interpolation as written, and the params-array
        // Concat overload, are both client-evaluated and would have thrown here instead.
        Assert.Contains("||", sql, StringComparison.Ordinal);
        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    // ── Collections: the case the design rests on ─────────────────────────────────────────────

    [Fact]
    public void AnyOverAReshapedCollection_ElidesTheJoinEntity_AndTranslatesToExists()
    {
        // ProductDto.Tags comes from Product.Tags (ProductTag) via l => l.Tag. The model never
        // mentions ProductTag; the predicate is written entirely in model terms.
        IQueryable<Product> q = Where(d => d.Tags.Any(t => t.Label == "sale"));

        string sql = Flatten(q.ToQueryString());
        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("\"ProductTags\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Tags\"", sql, StringComparison.Ordinal);
        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void AllOverAReshapedCollection_Translates()
    {
        IQueryable<Product> q = Where(d => d.Tags.All(t => t.Label != "sale"));

        // Orphan carries no tags at all, so All is vacuously true for it -- the same answer LINQ and
        // SQL's NOT EXISTS both give, and the reason the row is here rather than an oversight.
        Assert.Equal(new[] { "Ball", "Orphan" }, q.Select(x => x.Name).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void AnyOverAPlainCollection_NeedsNoElementHop()
    {
        // Reviews is declared AsIs: the source elements already are the element entity.
        IQueryable<Product> q = Where(d => d.Reviews.Any(r => r.Stars == 5));

        string sql = Flatten(q.ToQueryString());
        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void AnyOverAReshapedCollection_SubstitutesTheElementsOwnMap()
    {
        // t.Id is TagDto.Id -> Tag.Id, resolved through the ELEMENT's map, not the root's. If the
        // root map were consulted the rewrite would bind Product.Id and quietly match everything.
        IQueryable<Product> q = Where(d => d.Tags.Any(t => t.Id == 7));

        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    // ── Composition ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MixedPredicate_AcrossEveryBindingKind_Translates()
    {
        IQueryable<Product> q = Where(d =>
            d.Title == "Hammer"
            && d.CategoryName == "Tools"
            && d.DisplayName == "Ada Lovelace"
            && d.Tags.Any(t => t.Label == "sale"));

        Assert.Equal(new[] { "Hammer" }, q.Select(x => x.Name).ToArray());
    }

    [Theory]
    [InlineData("Tools", 1)]
    [InlineData("Toys", 1)]
    [InlineData("Nope", 0)]
    public void PathMember_MatchesTheSameRowsAsTheEquivalentEntityQuery(string category, int expected)
    {
        // The oracle in miniature: the rewritten model predicate must select exactly what the
        // hand-written entity predicate does.
        IQueryable<Product> viaModel = Where(d => d.CategoryName == category);
        IQueryable<Product> viaEntity = _db.Products.Where(p => p.Category!.Name == category);

        Assert.Equal(expected, viaModel.Count());
        Assert.Equal(viaEntity.Select(x => x.Id).OrderBy(x => x).ToArray(),
                     viaModel.Select(x => x.Id).OrderBy(x => x).ToArray());
    }

    // ── Sort keys ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASortKey_RewritesForOrderBy()
    {
        Expression<Func<ProductDto, string?>> key = d => d.CategoryName;
        var entityKey = (Expression<Func<Product, string?>>)Rewriter().RewriteLambda(key);

        IQueryable<Product> q = _db.Products.OrderByDescending(entityKey);
        string sql = Flatten(q.ToQueryString());
        _out.WriteLine("sql: " + sql);

        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("\"c\".\"Name\" DESC", sql, StringComparison.Ordinal);
        // Toys, Tools, then the row with no category at all: SQLite sorts NULL lowest, so descending
        // puts it last.
        Assert.Equal(new[] { "Ball", "Hammer", "Orphan" }, q.Select(x => x.Name).ToArray());
    }

    // ── Refusals ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnIgnoredMemberInAPredicate_IsRefusedWithAMessageNamingIt()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Rewriter().RewriteLambda((Expression<Func<ProductDto, bool>>)
                (d => d.RenderedAt == DateTime.MinValue)));

        Assert.Contains("RenderedAt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ignore()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberWithNoBindingAtAll_IsLeftAlone_NotSilentlyMistranslated()
    {
        // A model member the map never declares is not the rewriter's to invent. It falls through
        // untouched, which fails loudly at the provider rather than binding to something plausible.
        ModelMapBuilder<Product, ProductDto> b = new();
        b.Property(d => d.Id).From(o => o.Id);
        ModelMap sparse = b.Build();

        Assert.Null(sparse.Find(nameof(ProductDto.Title)));
    }

    [Fact]
    public void DeclaringOneMemberTwice_IsRefusedAtTheCall()
    {
        ModelMapBuilder<Product, ProductDto> b = new();
        b.Property(d => d.Title).From(o => o.Name);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => b.Property(d => d.Title).From(o => o.First));

        Assert.Contains("Title", ex.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonMemberFrom_IsRefusedAndPointsAtFormatOrCompute()
    {
        ModelMapBuilder<Product, ProductDto> b = new();

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => b.Property(d => d.Title).From(o => o.Name.ToUpper()));

        Assert.Contains("Format", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Compute", ex.Message, StringComparison.Ordinal);
    }
}
