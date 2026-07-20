using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Issue #184, item 1. A navigation property renamed with
/// <c>[System.Text.Json.Serialization.JsonPropertyName]</c> serialises under the attribute's exact
/// key (never run through <c>PropertyNamingPolicy</c>). Omission (OData JSON §4.5.1 / §11.2.4.2) and
/// the <c>$expand</c> injection must both key off that renamed name: otherwise an un-expanded renamed
/// nav leaks inline (omission looked for the policy-cased key and missed it), and an expanded one gets
/// written under a second, differently-cased key.
/// </summary>
public class RenamedNavigationTests
{
    // ── Omission: un-expanded renamed navigations are absent under their JSON key ──

    [Fact]
    public async Task GetById_NoExpand_OmitsRenamedCollectionNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedNavMovies(1)");

        Assert.False(json.TryGetProperty("starring", out _), "un-expanded renamed collection nav 'starring' must be omitted");
        Assert.False(json.TryGetProperty("cast", out _), "no policy-cased leak of the renamed nav");
        Assert.Equal("Ascent", json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetById_NoExpand_OmitsRenamedSingleValuedNavigation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedNavMovies(1)");

        Assert.False(json.TryGetProperty("producedBy", out _), "un-expanded renamed single nav 'producedBy' must be omitted");
        Assert.False(json.TryGetProperty("studio", out _), "no policy-cased leak of the renamed nav");
    }

    [Fact]
    public async Task GetAll_NoExpand_OmitsRenamedNavigationsOnEveryItem()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedNavMovies");

        foreach (var item in json.GetProperty("value").EnumerateArray())
        {
            Assert.False(item.TryGetProperty("starring", out _), "renamed collection nav must be omitted");
            Assert.False(item.TryGetProperty("producedBy", out _), "renamed single nav must be omitted");
        }
    }

    // ── Expansion: expanded renamed navigations are present under their JSON key ───

    [Fact]
    public async Task GetById_ExpandRenamedCollection_PresentUnderRenamedKeyOnly()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedNavMovieProfile>());
        // $expand uses the EDM/CLR property name (Cast), while the JSON key is the rename (starring).
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedNavMovies(1)?$expand=Cast");

        Assert.True(json.TryGetProperty("starring", out var starring), "expanded nav must be present under renamed key 'starring'");
        Assert.Equal(JsonValueKind.Array, starring.ValueKind);
        Assert.Equal(2, starring.GetArrayLength());
        Assert.Equal("Ada", starring[0].GetProperty("name").GetString());
        // No second, differently-cased key from the naming policy.
        Assert.False(json.TryGetProperty("cast", out _), "no duplicate policy-cased expand key");
    }

    [Fact]
    public async Task GetById_ExpandRenamedSingle_PresentUnderRenamedKeyOnly()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedNavMovies(1)?$expand=Studio");

        Assert.True(json.TryGetProperty("producedBy", out var producedBy), "expanded nav must be present under renamed key 'producedBy'");
        Assert.Equal(JsonValueKind.Object, producedBy.ValueKind);
        Assert.Equal("Skyline", producedBy.GetProperty("name").GetString());
        Assert.False(json.TryGetProperty("studio", out _), "no duplicate policy-cased expand key");
    }

    [Fact]
    public async Task GetAll_ExpandRenamedCollection_PresentUnderRenamedKey()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedNavMovieProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedNavMovies?$expand=Cast");

        var movie1 = json.GetProperty("value").EnumerateArray().First(m => m.GetProperty("id").GetInt32() == 1);
        Assert.True(movie1.TryGetProperty("starring", out var starring), "expanded nav present under renamed key");
        Assert.Equal(2, starring.GetArrayLength());
        Assert.False(movie1.TryGetProperty("cast", out _), "no duplicate policy-cased expand key");
    }
}
