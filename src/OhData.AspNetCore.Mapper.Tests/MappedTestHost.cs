using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// A running OhData host over a real SQLite database, with the mapped profile and its
/// <b>control</b> registered side by side.
/// </summary>
/// <remarks>
/// The control is what makes the conformance assertions meaningful. It serves the same model type
/// from an ordinary <c>GetQueryable</c> profile over the same rows, so the framework's own pipeline
/// answers each request without the mapper in it. Any request the two answer differently is a
/// mapper defect, and the comparison covers constructs no hand-written expectation would think to
/// check.
/// </remarks>
internal sealed class MappedTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SqliteConnection _connection;

    /// <summary>The entity set the mapped profile serves.</summary>
    public const string Mapped = "Products";

    /// <summary>The entity set the control profile serves, over the same rows.</summary>
    public const string Control = "ControlProducts";

    /// <summary>The same map with a two-row page, so paging is observable on a small fixture.</summary>
    public const string Paged = "PagedProducts";

    private MappedTestHost(WebApplication app, SqliteConnection connection)
    {
        _app = app;
        _connection = connection;
        Client = ((IHost)app).GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<MappedTestHost> StartAsync(Action<MapDb>? seed = null)
    {
        SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();

        using (MapDb db = MapDb.Seeded(connection))
        {
            seed?.Invoke(db);
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.Services.AddDbContext<MapDb>(
            o => o.UseSqlite(connection), ServiceLifetime.Scoped);

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            o.AddEntitySetProfile<MappedProductProfile>();
            o.AddEntitySetProfile<ControlProductProfile>();
            o.AddEntitySetProfile<PagedProductProfile>();
        });

        WebApplication app = builder.Build();
        app.MapOhData();
        await app.StartAsync();

        return new MappedTestHost(app, connection);
    }

    /// <summary>GETs a URL -- absolute or relative -- and parses the envelope.</summary>
    public async Task<JsonObject> GetJsonAsync(string url)
    {
        HttpResponseMessage response = await Client.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} for {url}: {body}");

        return JsonNode.Parse(body)!.AsObject();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        _connection.Dispose();
    }
}

/// <summary>
/// The same correspondence with a deliberately tiny page, so continuation behaviour is observable
/// without seeding thousands of rows.
/// </summary>
internal sealed class PagedProductProfile : MappedEntitySetProfile<int, ProductDto, Product>
{
    public PagedProductProfile(MapDb db) : base(d => d.Id)
    {
        EntitySetName = MappedTestHost.Paged;
        MappedPageSize = 2;
        MaxTop = 10;

        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        UseMap(() => db.Products.AsNoTracking(), m => m
            .Root(Maps.Declare)
            .Nested<Category, CategoryDto>(Maps.DeclareCategory)
            .Nested<Tag, TagDto>(Maps.DeclareTag)
            .Nested<Review, ReviewDto>(Maps.DeclareReview));
    }
}

/// <summary>The profile under test: <c>ProductDto</c> served from <c>Product</c>.</summary>
internal sealed class MappedProductProfile : MappedEntitySetProfile<int, ProductDto, Product>
{
    public MappedProductProfile(MapDb db) : base(d => d.Id)
    {
        EntitySetName = MappedTestHost.Mapped;

        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        UseMap(() => db.Products.AsNoTracking(), m => m
            .Root(Maps.Declare)
            .Nested<Category, CategoryDto>(Maps.DeclareCategory)
            .Nested<Tag, TagDto>(Maps.DeclareTag)
            .Nested<Review, ReviewDto>(Maps.DeclareReview));
    }
}

/// <summary>
/// The oracle: the same model, the same rows, served by the framework's ordinary pipeline.
/// </summary>
/// <remarks>
/// It materialises the projection eagerly and hands the framework a LINQ-to-objects queryable, which
/// is exactly the strategy this package exists to replace — and exactly why it is the right control.
/// It has no mapper in it at all, so where the two responses agree the mapper has reproduced the
/// framework's own semantics, and where they differ one of them is wrong.
/// </remarks>
internal sealed class ControlProductProfile : EntitySetProfile<int, ProductDto>
{
    public ControlProductProfile(MapDb db) : base(d => d.Id)
    {
        EntitySetName = MappedTestHost.Control;

        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        List<ProductDto> rows = Project(db);

        GetQueryable = () => rows.AsQueryable();
        GetById = (key, _) => Task.FromResult(
            OhDataResult.Success<ProductDto?>(rows.FirstOrDefault(r => r.Id == key)));

        HasOptional<CategoryDto>(
            d => d.Category!,
            (keys, _) => Task.FromResult<IReadOnlyDictionary<int, CategoryDto?>>(rows
                .Where(r => keys.Contains(r.Id) && r.Category is not null)
                .ToDictionary(r => r.Id, r => r.Category)));

        HasMany<TagDto>(
            d => d.Tags,
            (keys, _) => Task.FromResult(rows
                .Where(r => keys.Contains(r.Id))
                .SelectMany(r => r.Tags.Select(t => (r.Id, Tag: t)))
                .ToLookup(x => x.Id, x => x.Tag)));

        HasMany<ReviewDto>(
            d => d.Reviews,
            (keys, _) => Task.FromResult(rows
                .Where(r => keys.Contains(r.Id))
                .SelectMany(r => r.Reviews.Select(v => (r.Id, Review: v)))
                .ToLookup(x => x.Id, x => x.Review)));
    }

    private static List<ProductDto> Project(MapDb db) => db.Products
        .AsNoTracking()
        .Include(p => p.Category)
        .Include(p => p.Tags).ThenInclude(l => l.Tag)
        .Include(p => p.Reviews)
        .ToList()
        .Select(p => new ProductDto
        {
            Id = p.Id,
            Title = p.Name,
            CategoryName = p.Category?.Name,
            DisplayName = p.First + " " + p.Last,
            Rank = p.Rank,
            Category = p.Category is null
                ? null
                : new CategoryDto { Id = p.Category.Id, Name = p.Category.Name },
            Tags = p.Tags.Select(l => new TagDto { Id = l.Tag.Id, Label = l.Tag.Label }).ToList(),
            Reviews = p.Reviews.Select(v => new ReviewDto { Id = v.Id, Stars = v.Stars }).ToList(),
        })
        .ToList();
}
