using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace OhData.AspNetCore.Mapper.Tests;

// ── Entities: a shape that exercises every binding kind at once ────────────────────────────────
//
// Deliberately NOT a green-field model built around the mapper. It carries the things that break
// naive mapping: a member the API must hide, a value reached through an OPTIONAL reference (so the
// null path is real rather than hypothetical), and a many-to-many whose join entity must never
// appear on the wire.

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Product> Products { get; set; } = new();
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    public int Rank { get; set; }

    /// <summary>Must never reach the wire.</summary>
    public decimal InternalCost { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public List<ProductTag> Tags { get; set; } = new();
    public List<Review> Reviews { get; set; } = new();
}

/// <summary>The many-to-many join entity. The API model never mentions it.</summary>
public sealed class ProductTag
{
    public int ProductId { get; set; }
    public int TagId { get; set; }
    public Product Product { get; set; } = null!;
    public Tag Tag { get; set; } = null!;
}

public sealed class Tag
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>An ordinary one-to-many, so the element-less collection path is covered too.</summary>
public sealed class Review
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int Stars { get; set; }
    public string Body { get; set; } = "";
}

// ── API models ────────────────────────────────────────────────────────────────────────────────

public sealed class ProductDto
{
    public int Id { get; set; }                           // Direct
    public string Title { get; set; } = "";               // Rename    <- Name
    public string? CategoryName { get; set; }             // Path      <- Category.Name
    public string DisplayName { get; set; } = "";         // Format    <- $"{First} {Last}"
    public int Rank { get; set; }                         // Direct
    public CategoryDto? Category { get; set; }            // Reference <- Category
    public List<TagDto> Tags { get; set; } = new();       // Collection, join elided
    public List<ReviewDto> Reviews { get; set; } = new(); // Collection, no element hop
    public DateTime RenderedAt { get; set; }              // Ignored
}

/// <summary>
/// A model deliberately declared wider than the columns behind it: <c>long</c> over an <c>int</c>,
/// <c>int?</c> over a non-nullable column, and a non-nullable <c>int</c> reached through an OPTIONAL
/// reference. All three are ordinary API-contract shapes and all three used to break.
/// </summary>
public sealed class WideDto
{
    public int Id { get; set; }
    public long BigRank { get; set; }
    public int? MaybeRank { get; set; }
    public int CatId { get; set; }
}

public sealed class CategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class TagDto
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class ReviewDto
{
    public int Id { get; set; }
    public int Stars { get; set; }
}

public sealed class MapDb : DbContext
{
    public MapDb(DbContextOptions<MapDb> options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ProductTag> ProductTags => Set<ProductTag>();
    public DbSet<Review> Reviews => Set<Review>();

    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<ProductTag>().HasKey(x => new { x.ProductId, x.TagId });

    public static MapDb Seeded(Microsoft.Data.Sqlite.SqliteConnection connection, int filler = 0)
    {
        MapDb db = new(new DbContextOptionsBuilder<MapDb>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        if (db.Products.Any()) return db;

        db.Categories.AddRange(
            new Category { Id = 1, Name = "Tools" },
            new Category { Id = 2, Name = "Toys" });

        db.Products.AddRange(
            new Product { Id = 1, Name = "Hammer", First = "Ada", Last = "Lovelace", Rank = 3, InternalCost = 3m, CategoryId = 1 },
            new Product { Id = 2, Name = "Ball", First = "Alan", Last = "Turing", Rank = 1, InternalCost = 4m, CategoryId = 2 },
            // No category at all: the optional reference, so every path binding has a null row to
            // render and the null-guard is exercised on the ordinary read path rather than by a test
            // written for it.
            new Product { Id = 3, Name = "Orphan", First = "Grace", Last = "Hopper", Rank = 2, InternalCost = 9m, CategoryId = null });

        db.Tags.AddRange(
            new Tag { Id = 7, Label = "sale" },
            new Tag { Id = 8, Label = "new" });

        db.ProductTags.AddRange(
            new ProductTag { ProductId = 1, TagId = 7 },
            new ProductTag { ProductId = 1, TagId = 8 },
            new ProductTag { ProductId = 2, TagId = 8 });

        db.Reviews.AddRange(
            new Review { Id = 100, ProductId = 1, Stars = 5, Body = "great" },
            new Review { Id = 101, ProductId = 1, Stars = 2, Body = "meh" });

        for (int i = 0; i < filler; i++)
        {
            db.Products.Add(new Product
            {
                Id = 1000 + i,
                Name = "Filler" + i.ToString("D3"),
                First = "F" + i,
                Last = "L" + i,
                Rank = 100 + i,
                CategoryId = (i % 2) + 1,
            });
        }

        db.SaveChanges();
        return db;
    }
}

public static class Maps
{
    /// <summary>The map every unit test uses, exercising all binding kinds.</summary>
    public static ModelMap Product()
    {
        ModelMapBuilder<Product, ProductDto> m = new();
        Declare(m);
        return m.Build();
    }

    /// <summary>The root declaration, shared with the end-to-end profile so the two cannot drift.</summary>
    public static void Declare(ModelMapBuilder<Product, ProductDto> m)
    {
        m.Property(d => d.Id).From(o => o.Id);
        m.Property(d => d.Title).From(o => o.Name);
        m.Property(d => d.CategoryName).From(o => o.Category!.Name);
        m.Property(d => d.DisplayName).Format(o => $"{o.First} {o.Last}");
        m.Property(d => d.Rank).From(o => o.Rank);
        m.Reference(d => d.Category, o => o.Category);
        m.Collection(d => d.Tags).From(o => o.Tags).Element(l => l.Tag);
        m.Collection(d => d.Reviews).From(o => o.Reviews).AsIs();
        m.Ignore(d => d.RenderedAt);
    }

    public static void DeclareWide(ModelMapBuilder<Product, WideDto> m)
    {
        m.Property(d => d.Id).From(o => o.Id);
        m.Property(d => d.BigRank).From(o => o.Rank);
        m.Property(d => d.MaybeRank).From(o => o.Rank);
        m.Property(d => d.CatId).From(o => o.Category!.Id);
    }

    public static void DeclareCategory(ModelMapBuilder<Category, CategoryDto> m)
    {
        m.Property(d => d.Id).From(o => o.Id);
        m.Property(d => d.Name).From(o => o.Name);
    }

    public static void DeclareTag(ModelMapBuilder<Tag, TagDto> m)
    {
        m.Property(d => d.Id).From(o => o.Id);
        m.Property(d => d.Label).From(o => o.Label);
    }

    public static void DeclareReview(ModelMapBuilder<Review, ReviewDto> m)
    {
        m.Property(d => d.Id).From(o => o.Id);
        m.Property(d => d.Stars).From(o => o.Stars);
    }

    public static ModelMap Category()
    {
        ModelMapBuilder<Category, CategoryDto> m = new();
        DeclareCategory(m);
        return m.Build();
    }

    public static ModelMap Tag()
    {
        ModelMapBuilder<Tag, TagDto> m = new();
        DeclareTag(m);
        return m.Build();
    }

    public static ModelMap Review()
    {
        ModelMapBuilder<Review, ReviewDto> m = new();
        DeclareReview(m);
        return m.Build();
    }

    /// <summary>Every map, as the registry the rewriter and the loaders resolve through.</summary>
    public static ModelMapRegistry Registry() => new ModelMapRegistry()
        .Add(Product())
        .Add(Category())
        .Add(Tag())
        .Add(Review());

}
