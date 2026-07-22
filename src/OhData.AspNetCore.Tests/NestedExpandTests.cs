using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── Nested $expand / $select fixtures (issue #183, OData §11.2.4.2) ─────────────
//
// Two mutually-referencing entity sets, mirroring the issue's Movie/Studio shape:
// a Movie has one Studio (single-valued nav), a Studio has many Movies (collection nav).
// Both are registered as their own entity sets, so a nested $expand can resolve the
// target set's own navigation handlers one level deeper (and deeper still, cyclically,
// bounded by the depth the client actually writes in $expand).

internal class NestStudio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<NestMovie>? Movies { get; set; }
}

internal class NestMovie
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int StudioId { get; set; }
    public NestStudio? Studio { get; set; }
}

internal static class NestData
{
    public static readonly List<NestStudio> Studios = new()
    {
        new() { Id = 1, Name = "Studio A" },
        new() { Id = 2, Name = "Studio B" },
    };

    public static readonly List<NestMovie> Movies = new()
    {
        new() { Id = 1, Title = "M1", StudioId = 1 },
        new() { Id = 2, Title = "M2", StudioId = 1 },
        new() { Id = 3, Title = "M3", StudioId = 2 },
    };
}

internal class NestMovieProfile : EntitySetProfile<int, NestMovie>
{
    public NestMovieProfile() : base(x => x.Id)
    {
        EntitySetName = "NestMovies";
        ExpandEnabled = true;
        SelectEnabled = true;

        GetQueryable = (ct) => Task.FromResult(NestData.Movies.AsQueryable());
        GetById = (id, ct) => Task.FromResult(NestData.Movies.FirstOrDefault(m => m.Id == id));

        // Single-valued nav → Studios entity set.
        HasOptional(
            navigation: x => x.Studio!,
            get: (movieId, ct) =>
            {
                NestMovie? movie = NestData.Movies.FirstOrDefault(m => m.Id == movieId);
                NestStudio? studio = movie is null ? null : NestData.Studios.FirstOrDefault(s => s.Id == movie.StudioId);
                return Task.FromResult(studio);
            },
            refTargetEntitySet: null);
    }
}

internal class NestStudioProfile : EntitySetProfile<int, NestStudio>
{
    public NestStudioProfile() : base(x => x.Id)
    {
        EntitySetName = "NestStudios";
        ExpandEnabled = true;
        SelectEnabled = true;

        GetQueryable = (ct) => Task.FromResult(NestData.Studios.AsQueryable());
        GetById = (id, ct) => Task.FromResult(NestData.Studios.FirstOrDefault(s => s.Id == id));

        // Collection nav → Movies entity set.
        HasMany(
            navigation: x => x.Movies!,
            getAll: (studioId, ct) =>
                Task.FromResult<IEnumerable<NestMovie>>(NestData.Movies.Where(m => m.StudioId == studioId)));
    }
}

/// <summary>
/// Tests that nested <c>$expand</c> clauses are actually executed (issue #183). Before the fix,
/// <c>GET /NestMovies(1)?$expand=Studio($expand=Movies)</c> returned the studio with an empty
/// <c>movies</c> array; the second-level clause was parsed but never invoked against a handler.
/// </summary>
public class NestedExpandTests
{
    private static Task<TestFixture> BuildAsync() =>
        TestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<NestMovieProfile>();
            o.AddEntitySetProfile<NestStudioProfile>();
        });

    [Fact]
    public async Task NestedExpand_OnGetById_PopulatesGrandchildCollection()
    {
        await using var fx = await BuildAsync();

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NestMovies(1)?$expand=Studio($expand=Movies)");

        var studio = json.GetProperty("Studio");
        Assert.Equal("Studio A", studio.GetProperty("Name").GetString());

        // The grandchild collection — the studio's movies — must be loaded, not empty.
        var movies = studio.GetProperty("Movies");
        Assert.Equal(JsonValueKind.Array, movies.ValueKind);
        Assert.Equal(2, movies.GetArrayLength());
        string?[] titles = movies.EnumerateArray().Select(m => m.GetProperty("Title").GetString()).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "M1", "M2" }, titles);
    }

    [Fact]
    public async Task NestedExpand_OnCollectionGet_PopulatesGrandchildCollection()
    {
        await using var fx = await BuildAsync();

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NestMovies?$expand=Studio($expand=Movies)");

        var first = json.GetProperty("value").EnumerateArray()
            .First(m => m.GetProperty("Id").GetInt32() == 1);
        var movies = first.GetProperty("Studio").GetProperty("Movies");
        Assert.Equal(2, movies.GetArrayLength());
    }

    [Fact]
    public async Task ExpandParent_WithoutNestedExpand_OmitsGrandchildNav()
    {
        // Regression guard for #176/#179: expanding Studio (but not its Movies) must still OMIT
        // the un-expanded Movies navigation on the nested studio entirely — not emit it empty.
        await using var fx = await BuildAsync();

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NestMovies(1)?$expand=Studio");

        var studio = json.GetProperty("Studio");
        Assert.Equal("Studio A", studio.GetProperty("Name").GetString());
        Assert.False(studio.TryGetProperty("Movies", out _),
            "The nested studio's un-expanded 'movies' navigation must be omitted.");
    }

    [Fact]
    public async Task NestedSelect_InsideExpand_ProjectsRelatedEntities()
    {
        await using var fx = await BuildAsync();

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NestMovies(1)?$expand=Studio($expand=Movies($select=Title))");

        var movies = json.GetProperty("Studio").GetProperty("Movies");
        Assert.Equal(2, movies.GetArrayLength());
        foreach (var movie in movies.EnumerateArray())
        {
            Assert.True(movie.TryGetProperty("Title", out _));
            // Nested $select=Title must strip everything else on each related movie.
            Assert.False(movie.TryGetProperty("Id", out _));
            Assert.False(movie.TryGetProperty("StudioId", out _));
        }
    }

    [Fact]
    public async Task NestedSelect_OnExpandedSingleNav_ProjectsThatEntity()
    {
        await using var fx = await BuildAsync();

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NestMovies(1)?$expand=Studio($select=Name)");

        var studio = json.GetProperty("Studio");
        Assert.Equal("Studio A", studio.GetProperty("Name").GetString());
        // Only Name was selected on the expanded studio; Id must be stripped.
        Assert.False(studio.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task DeepNestedExpand_DepthThree_LoadsEveryLevel()
    {
        // Studio → Movies (depth 1) → Studio (depth 2) → Movies (depth 3). The chain is cyclic
        // in the model but finite in the request, so every requested level must be populated.
        await using var fx = await BuildAsync();

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NestStudios(1)?$expand=Movies($expand=Studio($expand=Movies))");

        var movies = json.GetProperty("Movies");
        Assert.Equal(2, movies.GetArrayLength());

        var firstMovie = movies.EnumerateArray().First();
        var depth2Studio = firstMovie.GetProperty("Studio");
        Assert.Equal("Studio A", depth2Studio.GetProperty("Name").GetString());

        var depth3Movies = depth2Studio.GetProperty("Movies");
        Assert.Equal(2, depth3Movies.GetArrayLength());
    }

    [Fact]
    public async Task NestedExpand_Returns200()
    {
        await using var fx = await BuildAsync();
        var response = await fx.Client.GetAsync("/odata/NestMovies(1)?$expand=Studio($expand=Movies)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
