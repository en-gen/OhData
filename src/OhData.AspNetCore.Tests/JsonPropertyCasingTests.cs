using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #252: OhData owns its response JSON property casing. The default is PascalCase — the CLR names
/// declared in <c>$metadata</c> (EDM) — so payload identifiers match <c>$metadata</c> (OData §4.4).
/// The host's <c>HttpJsonOptions.PropertyNamingPolicy</c> is intentionally not inherited; camelCase
/// is an explicit opt-in via <see cref="OhDataBuilder.WithJsonPropertyNamingPolicy"/>.
/// </summary>
public class JsonPropertyCasingTests
{
    [Fact]
    public async Task Default_Collection_UsesPascalCase()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("Id", out _));
        Assert.True(first.TryGetProperty("Name", out _));
        Assert.False(first.TryGetProperty("id", out _));
        Assert.False(first.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Default_GetById_UsesPascalCase()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var entity = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets(1)");
        Assert.True(entity.TryGetProperty("Id", out _));
        Assert.True(entity.TryGetProperty("Name", out _));
        Assert.False(entity.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Default_PayloadCasing_MatchesMetadata()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        // $metadata declares the structural properties in PascalCase (the CLR names).
        string metadata = await fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains("Name=\"Name\"", metadata);
        Assert.Contains("Name=\"Id\"", metadata);

        // The response payload uses the same identifiers — casing agrees with $metadata (§4.4).
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("Name", out _));
        Assert.True(first.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task CamelCaseOptIn_ProducesCamelCasePayload()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
        {
            o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase);
            o.AddEntitySetProfile<WidgetProfile>();
        });
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("name", out _));
        Assert.False(first.TryGetProperty("Id", out _));
        Assert.False(first.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task HostCamelCaseHttpJsonOptions_DoesNotOverride_OhDataDefault()
    {
        // The host explicitly configures ASP.NET Core minimal-API JSON to camelCase. OhData owns
        // its own response casing, so this must NOT change the payload — it stays PascalCase.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WidgetProfile>(),
            configureServices: services => services.ConfigureHttpJsonOptions(
                j => j.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("Name", out _));
        Assert.True(first.TryGetProperty("Id", out _));
        Assert.False(first.TryGetProperty("name", out _));
        Assert.False(first.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Default_WriteEcho_UsesPascalCase()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PostAsJsonAsync("/odata/Widgets", new { Name = "Flywheel" });
        resp.EnsureSuccessStatusCode();
        var echo = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(echo.TryGetProperty("Id", out _));
        Assert.True(echo.TryGetProperty("Name", out _));
        Assert.False(echo.TryGetProperty("name", out _));
    }
}
