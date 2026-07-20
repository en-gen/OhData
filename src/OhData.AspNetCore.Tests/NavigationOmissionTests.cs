using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Regression tests for issue #176. Per OData JSON Format v4.01 §4.5.1 / §11.2.4.2 a navigation
/// property that was not requested via <c>$expand</c> must be OMITTED from the payload — never
/// serialised inline as an empty array (collection nav) or <c>null</c> (single-valued nav).
/// System.Text.Json serialises the whole CLR graph, so before the fix every declared navigation
/// leaked into read responses. These tests cover all three faces of the bug plus the guard that
/// an expanded navigation is still populated (no over-stripping).
/// </summary>
public class NavigationOmissionTests
{
    // ── Face 1: un-expanded collection navigation is omitted ──────────────────────

    [Fact]
    public async Task GetById_NoExpand_OmitsCollectionNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies(1)");

        Assert.False(json.TryGetProperty("cast", out _), "un-expanded collection nav 'cast' must be omitted");
        Assert.Equal("Ascent", json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetAll_NoExpand_OmitsCollectionNavigationOnEveryItem()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies");
        var value = json.GetProperty("value");
        Assert.Equal(3, value.GetArrayLength());

        foreach (var item in value.EnumerateArray())
        {
            Assert.False(item.TryGetProperty("cast", out _), "un-expanded collection nav 'cast' must be omitted");
        }
    }

    // ── Face 2: un-expanded single-valued navigation is omitted ───────────────────

    [Fact]
    public async Task GetById_NoExpand_OmitsSingleValuedNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies(1)");

        Assert.False(json.TryGetProperty("studio", out _), "un-expanded single nav 'studio' must be omitted");
    }

    [Fact]
    public async Task GetAll_NoExpand_OmitsSingleValuedNavigationOnEveryItem()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies");

        foreach (var item in json.GetProperty("value").EnumerateArray())
        {
            Assert.False(item.TryGetProperty("studio", out _), "un-expanded single nav 'studio' must be omitted");
        }
    }

    // ── Guard: an EXPANDED navigation is still present and populated ──────────────

    [Fact]
    public async Task GetById_ExpandCollection_NavigationPresentAndPopulated()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies(1)?$expand=Cast");

        Assert.True(json.TryGetProperty("cast", out var cast), "expanded 'cast' must be present");
        Assert.Equal(JsonValueKind.Array, cast.ValueKind);
        Assert.Equal(2, cast.GetArrayLength());
        Assert.Equal("Ada", cast[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetById_ExpandSingle_NavigationPresentAndPopulated()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies(1)?$expand=Studio");

        Assert.True(json.TryGetProperty("studio", out var studio), "expanded 'studio' must be present");
        Assert.Equal(JsonValueKind.Object, studio.ValueKind);
        Assert.Equal("Skyline", studio.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetAll_ExpandCollection_NavigationPresentAndPopulated()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies?$expand=Cast");

        var movie1 = json.GetProperty("value").EnumerateArray().First(m => m.GetProperty("id").GetInt32() == 1);
        Assert.True(movie1.TryGetProperty("cast", out var cast), "expanded 'cast' must be present");
        Assert.Equal(2, cast.GetArrayLength());
    }

    // ── Face 3a: expanding one nav does not bring a sibling nav ───────────────────

    [Fact]
    public async Task GetById_ExpandOneNav_DoesNotIncludeSiblingNav()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies(1)?$expand=Studio");

        Assert.True(json.TryGetProperty("studio", out _), "expanded 'studio' must be present");
        // 'cast' was NOT expanded and must not ride along.
        Assert.False(json.TryGetProperty("cast", out _), "un-expanded sibling nav 'cast' must be omitted");
    }

    [Fact]
    public async Task GetAll_ExpandOneNav_DoesNotIncludeSiblingNav()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies?$expand=Cast");

        foreach (var item in json.GetProperty("value").EnumerateArray())
        {
            Assert.True(item.TryGetProperty("cast", out _), "expanded 'cast' must be present");
            Assert.False(item.TryGetProperty("studio", out _), "un-expanded sibling nav 'studio' must be omitted");
        }
    }

    // ── Face 3b: an expanded entity does not carry its own un-expanded nav ────────

    [Fact]
    public async Task GetById_ExpandSingle_ExpandedEntityOmitsItsOwnNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies(1)?$expand=Studio");

        var studio = json.GetProperty("studio");
        Assert.Equal("Skyline", studio.GetProperty("name").GetString());
        // The studio carries a populated Movies back-reference on the CLR object, but it was not
        // expanded at this level and must be omitted from the expanded studio (issue #176 face 3).
        Assert.False(studio.TryGetProperty("movies", out _), "expanded studio's own un-expanded nav 'movies' must be omitted");
    }

    [Fact]
    public async Task GetAll_ExpandSingle_ExpandedEntityOmitsItsOwnNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies?$expand=Studio");

        foreach (var studio in json.GetProperty("value").EnumerateArray()
            .Select(item => item.GetProperty("studio"))
            .Where(studio => studio.ValueKind != JsonValueKind.Null))
        {
            Assert.False(studio.TryGetProperty("movies", out _), "expanded studio's own un-expanded nav 'movies' must be omitted");
        }
    }

    // ── Expanded single-valued navigation with no related entity ──────────────────

    [Fact]
    public async Task GetById_ExpandSingle_NullRelated_YieldsNullWithoutThrowing()
    {
        // Movie 3 has no studio. Expanding it must produce "studio": null (an expanded single nav
        // with no related entity is null, not omitted) and the recursive omission pass must no-op
        // on the null node rather than throw (issue #176 — covers the null-node recursion path).
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<OmitNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/OmitNavMovies(3)?$expand=Studio");

        Assert.True(json.TryGetProperty("studio", out var studio), "expanded single nav must be present even when null");
        Assert.Equal(JsonValueKind.Null, studio.ValueKind);
    }
}
