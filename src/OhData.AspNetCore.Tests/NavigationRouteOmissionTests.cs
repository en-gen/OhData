using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Regression tests for issue #179. #176/#177 wired the un-expanded-navigation omission (OData JSON
/// Format v4.01 §4.5.1 / §11.2.4.2) into only the top-level reads (collection GET, GetById). Three
/// other serialization paths still emitted the full CLR graph, leaking un-expanded navigations and
/// making an entity's shape depend on which route returned it:
///   1. single-valued navigation GET      — GET /Set(key)/{nav}
///   2. navigation-collection GET items    — GET /Set(key)/{nav}  (collection)
///   3. bound-operation results            — bound function/action returning the set's own type
/// These tests assert each path now omits the nav element/target type's own un-expanded navigations,
/// that bound-op paths also inject @odata.etag (matching the normal paths), and — as an over-strip
/// guard — that structural data on those same responses survives.
/// </summary>
public class NavigationRouteOmissionTests
{
    // ── Path 1: single-valued navigation GET omits the target's own navigation ────

    [Fact]
    public async Task SingleValuedNavGet_OmitsTargetsOwnNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavLeakFilmProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavLeakFilms(1)/Studio");

        Assert.Equal("Skyline", json.GetProperty("name").GetString());
        Assert.False(json.TryGetProperty("films", out _),
            "the studio's own un-expanded nav 'films' must be omitted on the single-valued nav GET");
    }

    // ── Path 2: navigation-collection GET items omit each item's own navigation ───

    [Fact]
    public async Task NavCollectionGet_OmitsEachItemsOwnNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavLeakFilmProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavLeakFilms(1)/CoStudios");

        var value = json.GetProperty("value");
        Assert.Equal(2, value.GetArrayLength());
        foreach (var studio in value.EnumerateArray())
        {
            // Over-strip guard: structural data survives.
            Assert.False(string.IsNullOrEmpty(studio.GetProperty("name").GetString()));
            Assert.False(studio.TryGetProperty("films", out _),
                "each co-studio's own un-expanded nav 'films' must be omitted on the nav-collection GET");
        }
    }

    [Fact]
    public async Task NavCollectionGet_WithSelect_StillOmitsNavigationAndProjects()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavLeakFilmProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavLeakFilms(1)/CoStudios?$select=name");

        foreach (var studio in json.GetProperty("value").EnumerateArray())
        {
            Assert.True(studio.TryGetProperty("name", out _), "$select'd 'name' must be present");
            Assert.False(studio.TryGetProperty("id", out _), "unselected 'id' must be projected out");
            Assert.False(studio.TryGetProperty("films", out _), "un-expanded nav 'films' must be omitted");
        }
    }

    // ── Path 3: bound-operation collection result omits navs and injects @odata.etag ──

    [Fact]
    public async Task BoundFunction_CollectionResult_OmitsNavsAndIncludesETag()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavLeakFilmProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavLeakFilms/TopRated");

        var value = json.GetProperty("value");
        Assert.Equal(2, value.GetArrayLength());
        foreach (var film in value.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(film.GetProperty("title").GetString())); // over-strip guard
            Assert.False(film.TryGetProperty("studio", out _),
                "un-expanded single nav 'studio' must be omitted on the bound-op collection result");
            Assert.False(film.TryGetProperty("coStudios", out _),
                "un-expanded collection nav 'coStudios' must be omitted on the bound-op collection result");
            Assert.True(film.TryGetProperty("@odata.etag", out var etag),
                "@odata.etag must be injected on bound-op collection results (UseETag), matching the normal collection path");
            Assert.Equal(JsonValueKind.String, etag.ValueKind);
        }
    }

    // ── Path 4: bound-operation single result omits navs and injects @odata.etag ──

    [Fact]
    public async Task BoundFunction_SingleResult_OmitsNavsAndIncludesETag()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavLeakFilmProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavLeakFilms/GetFeatured");

        Assert.Equal("Ascent", json.GetProperty("title").GetString()); // over-strip guard
        Assert.False(json.TryGetProperty("studio", out _),
            "un-expanded single nav 'studio' must be omitted on the bound-op single result");
        Assert.False(json.TryGetProperty("coStudios", out _),
            "un-expanded collection nav 'coStudios' must be omitted on the bound-op single result");
        Assert.True(json.TryGetProperty("@odata.etag", out var etag),
            "@odata.etag must be injected on bound-op single results (UseETag), matching GetById");
        Assert.Equal(JsonValueKind.String, etag.ValueKind);
    }

    // ── Cross-route shape parity: the same entity looks identical everywhere ───────

    [Fact]
    public async Task SameEntity_HasIdenticalNavShape_AcrossTopLevelAndBoundOpReads()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavLeakFilmProfile>());

        var topLevel = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavLeakFilms(1)");
        var boundOp = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavLeakFilms/GetFeatured");

        // Neither carries the un-expanded navs; both carry the same structural data.
        foreach (var doc in new[] { topLevel, boundOp })
        {
            Assert.False(doc.TryGetProperty("studio", out _));
            Assert.False(doc.TryGetProperty("coStudios", out _));
            Assert.Equal("Ascent", doc.GetProperty("title").GetString());
        }
    }
}
