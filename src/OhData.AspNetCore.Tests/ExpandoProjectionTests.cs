using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── EF Core InMemory test model ───────────────────────────────────────────────

internal class Item
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

internal class ItemDbContext : DbContext
{
    public ItemDbContext(DbContextOptions<ItemDbContext> options) : base(options) { }
    public DbSet<Item> Items { get; set; } = null!;
}

// ── Helpers that mirror what OhDataEndpointFactory does ──────────────────────

internal static class ExpandoProjectionHelper
{
    /// <summary>
    /// POST-MATERIALIZATION approach: materialize the IQueryable to IEnumerable first,
    /// then project to ExpandoObject via LINQ-to-Objects. This is the approach implemented
    /// in OhDataEndpointFactory for the ExpandoObject experiment.
    /// Works with any IQueryable provider including EF Core.
    /// </summary>
    public static ExpandoObject[] PostMaterializationProjection<TModel>(
        IQueryable<TModel> source,
        IEnumerable<string> selectedProperties) where TModel : class
    {
        var props = selectedProperties
            .Select(name => typeof(TModel).GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase))
            .Where(p => p is not null)
            .ToArray();

        return source
            .AsEnumerable()   // materialize: LINQ-to-Objects after this point
            .Select(item =>
            {
                var expando = new ExpandoObject();
                var dict = (IDictionary<string, object?>)expando;
                foreach (var prop in props)
                    dict[prop!.Name] = prop.GetValue(item);
                return expando;
            })
            .ToArray();
    }

    /// <summary>
    /// EXPRESSION TREE approach: tries to build an Expression&lt;Func&lt;TModel, ExpandoObject&gt;&gt;
    /// that EF Core can translate (spoiler: it cannot).
    ///
    /// The challenge: ExpandoObject has no public parameterless constructor accessible via
    /// Expression.New, and even if it did, EF Core's LINQ translator has no knowledge of
    /// ExpandoObject or IDictionary&lt;string,object?&gt; operations. This method demonstrates
    /// the failure mode for documentation purposes.
    /// </summary>
    public static (bool succeeded, string? error, ExpandoObject[]? results)
        TryExpressionTreeProjection<TModel>(
            IQueryable<TModel> source,
            IEnumerable<string> selectedProperties) where TModel : class
    {
        try
        {
            var props = selectedProperties
                .Select(name => typeof(TModel).GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase))
                .Where(p => p is not null)
                .ToList();

            // Build: x => ProjectToExpando(x, propGetters)
            // We use a helper method call in the expression — EF Core can't translate this.
            var param = Expression.Parameter(typeof(TModel), "x");
            var propInfosConst = Expression.Constant(props.ToArray());
            var helperMethod = typeof(ExpandoProjectionHelper)
                .GetMethod(nameof(ProjectToExpando), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(TModel));

            var body = Expression.Call(helperMethod, param, propInfosConst);
            var lambda = Expression.Lambda<Func<TModel, ExpandoObject>>(body, param);

            // This will throw InvalidOperationException when EF Core InMemory tries to translate it
            var results = source.Select(lambda).ToArray();
            return (true, null, results);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    private static ExpandoObject ProjectToExpando<TModel>(TModel item, PropertyInfo[] props)
    {
        var expando = new ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        foreach (var prop in props)
            dict[prop.Name] = prop.GetValue(item);
        return expando;
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class ExpandoProjectionTests
{
    private static ItemDbContext CreateDbContext()
    {
        var opts = new DbContextOptionsBuilder<ItemDbContext>()
            .UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
            .Options;
        var ctx = new ItemDbContext(opts);
        ctx.Items.AddRange(
            new Item { Id = 1, Name = "Widget", Price = 9.99m },
            new Item { Id = 2, Name = "Gadget", Price = 24.99m }
        );
        ctx.SaveChanges();
        return ctx;
    }

    // ── Post-materialization approach ─────────────────────────────────────────

    [Fact]
    public void PostMaterialization_WithEfCoreInMemory_SelectsCorrectProperties()
    {
        using var ctx = CreateDbContext();
        var results = ExpandoProjectionHelper.PostMaterializationProjection(
            ctx.Items.AsQueryable(),
            new[] { "Name", "Price" });

        Assert.Equal(2, results.Length);
        var dict = (IDictionary<string, object?>)results[0];
        Assert.True(dict.ContainsKey("Name"));
        Assert.True(dict.ContainsKey("Price"));
        Assert.False(dict.ContainsKey("Id"));
    }

    [Fact]
    public void PostMaterialization_ExcludesNonSelectedProperties()
    {
        using var ctx = CreateDbContext();
        var results = ExpandoProjectionHelper.PostMaterializationProjection(
            ctx.Items.AsQueryable(),
            new[] { "Name" });

        var dict = (IDictionary<string, object?>)results[0];
        Assert.True(dict.ContainsKey("Name"));
        Assert.False(dict.ContainsKey("Id"));
        Assert.False(dict.ContainsKey("Price"));
    }

    [Fact]
    public void PostMaterialization_PreservesValues()
    {
        using var ctx = CreateDbContext();
        var results = ExpandoProjectionHelper.PostMaterializationProjection(
            ctx.Items.AsQueryable().OrderBy(x => x.Id),
            new[] { "Name", "Price" });

        var first = (IDictionary<string, object?>)results[0];
        Assert.Equal("Widget", first["Name"]);
        Assert.Equal(9.99m, first["Price"]);
    }

    [Fact]
    public void PostMaterialization_CaseInsensitivePropertyLookup()
    {
        using var ctx = CreateDbContext();
        // Requesting "name" (lowercase) should match C# property "Name"
        var results = ExpandoProjectionHelper.PostMaterializationProjection(
            ctx.Items.AsQueryable(),
            new[] { "name", "price" });

        var dict = (IDictionary<string, object?>)results[0];
        // The ExpandoObject key uses the C# property name casing, not the requested name
        Assert.True(dict.ContainsKey("Name"), "Expected key 'Name' (C# casing preserved)");
        Assert.True(dict.ContainsKey("Price"), "Expected key 'Price' (C# casing preserved)");
    }

    [Fact]
    public void PostMaterialization_UnknownPropertiesAreIgnored()
    {
        using var ctx = CreateDbContext();
        var results = ExpandoProjectionHelper.PostMaterializationProjection(
            ctx.Items.AsQueryable(),
            new[] { "Name", "DoesNotExist" });

        var dict = (IDictionary<string, object?>)results[0];
        Assert.True(dict.ContainsKey("Name"));
        Assert.False(dict.ContainsKey("DoesNotExist"));
    }

    // ── ExpandoObject serialization with System.Text.Json ────────────────────

    [Fact]
    public void ExpandoObject_SerializesAsJsonObject_NotArray()
    {
        var expando = new ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        dict["Name"] = "Widget";
        dict["Price"] = 9.99m;

        string json = JsonSerializer.Serialize(expando);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
    }

    [Fact]
    public void ExpandoObject_SerializesKeys_PreservingDictionaryKeyCasing()
    {
        // By default (no camelCase options), ExpandoObject dictionary keys are preserved as-is
        var expando = new ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        dict["Name"] = "Widget";
        dict["Price"] = 9.99m;

        string json = JsonSerializer.Serialize(expando);
        var element = JsonDocument.Parse(json).RootElement;

        // Keys appear exactly as set in the dictionary (PascalCase here)
        Assert.True(element.TryGetProperty("Name", out _), "Expected PascalCase 'Name' key");
        Assert.True(element.TryGetProperty("Price", out _), "Expected PascalCase 'Price' key");
        // camelCase variants should NOT be present
        Assert.False(element.TryGetProperty("name", out _));
        Assert.False(element.TryGetProperty("price", out _));
    }

    [Fact]
    public void ExpandoObject_SerializesNullValues_AsJsonNull()
    {
        var expando = new ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        dict["Name"] = null;

        string json = JsonSerializer.Serialize(expando);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal(JsonValueKind.Null, element.GetProperty("Name").ValueKind);
    }

    [Fact]
    public void ExpandoObject_ArrayOfExpandos_SerializesAsJsonArray()
    {
        var items = new[]
        {
            BuildExpando(("Name", (object?)"Widget"), ("Price", (object?)9.99m)),
            BuildExpando(("Name", (object?)"Gadget"), ("Price", (object?)24.99m)),
        };

        string json = JsonSerializer.Serialize(items);
        var array = JsonDocument.Parse(json).RootElement;

        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(2, array.GetArrayLength());
        Assert.Equal("Widget", array[0].GetProperty("Name").GetString());
        Assert.Equal("Gadget", array[1].GetProperty("Name").GetString());
    }

    // ── Expression tree approach — documents the EF Core failure ─────────────

    [Fact]
    public void ExpressionTree_WithEfCoreInMemory_SucceedsUnexpectedly()
    {
        // KEY FINDING: EF Core InMemory provider DOES NOT attempt SQL translation.
        // It evaluates the expression tree client-side (in-process) so a helper method call
        // that would fail against a real SQL database (SQL Server, PostgreSQL, etc.) succeeds here.
        //
        // This means: the expression tree approach would work in tests using EF Core InMemory but
        // would throw "could not be translated" at runtime against a real SQL database provider.
        // The post-materialization approach is the safe choice for production use.
        using var ctx = CreateDbContext();

        var (succeeded, error, results) = ExpandoProjectionHelper.TryExpressionTreeProjection(
            ctx.Items.AsQueryable(),
            new[] { "Name" });

        Assert.True(succeeded,
            $"EF Core InMemory evaluated the expression tree client-side and succeeded. Error: {error}");
        Assert.NotNull(results);
        Assert.Equal(2, results!.Length);
        var dict = (IDictionary<string, object?>)results[0];
        Assert.True(dict.ContainsKey("Name"));
    }

    [Fact]
    public void ExpressionTree_WithLinqToObjects_Succeeds()
    {
        // The same expression tree approach DOES work on LINQ-to-Objects (plain List<T>.AsQueryable())
        var store = new List<Item>
        {
            new() { Id = 1, Name = "Widget", Price = 9.99m },
        }.AsQueryable();

        var (succeeded, error, results) = ExpandoProjectionHelper.TryExpressionTreeProjection(
            store,
            new[] { "Name" });

        Assert.True(succeeded, $"Expected LINQ-to-Objects to succeed, but got: {error}");
        Assert.NotNull(results);
        Assert.Single(results!);
        var dict = (IDictionary<string, object?>)results![0];
        Assert.True(dict.ContainsKey("Name"));
    }

    private static ExpandoObject BuildExpando(params (string key, object? value)[] pairs)
    {
        var e = new ExpandoObject();
        var d = (IDictionary<string, object?>)e;
        foreach (var (key, val) in pairs) d[key] = val;
        return e;
    }
}
