using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Tests for individual structural property write routes (#30/#31, OData §11.4.9.1/.2/.3):
/// <c>PUT</c>/<c>PATCH</c>/<c>DELETE /{Set}({key})/{Property}</c>. Rides the existing
/// <c>Patch</c> handler — property write routes are registered only when
/// <c>PropertyAccessEnabled</c> resolves <c>true</c> AND <c>Patch</c> is configured.
/// </summary>
public class PropertyWriteTests
{
    // ── PUT: happy paths + persistence ──────────────────────────────────────────

    [Fact]
    public async Task Put_StringProperty_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteItems(1)/Name", new { value = "Updated" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(1)/Name");
        Assert.Equal("Updated", json.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Put_IntProperty_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteItems(2)/Quantity", new { value = 42 });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(2)/Quantity");
        Assert.Equal(42, json.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Put_DecimalProperty_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteItems(3)/Price", new { value = 14.5m });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(3)/Price");
        Assert.Equal(14.5m, json.GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task Put_NullableIntProperty_SetValue_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteItems(4)/Stock", new { value = 99 });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(4)/Stock");
        Assert.Equal(99, json.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Put_NullableIntProperty_SetNullValue_Returns204AndPersists()
    {
        // PUT with an explicit JSON null (as opposed to DELETE) also clears a nullable property.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        using var content = new StringContent("{\"value\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/PropertyWriteItems(5)/Stock", content);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var followUp = await fx.Client.GetAsync("/odata/PropertyWriteItems(5)/Stock");
        Assert.Equal(HttpStatusCode.NoContent, followUp.StatusCode); // §11.2.6: null property → 204
    }

    [Fact]
    public async Task Put_ComplexProperty_FullReplace_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync(
            "/odata/PropertyWriteItems(6)/Size", new { value = new { width = 7.5m, height = 8.25m } });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // #252: the complex property read echoes nested member names in PascalCase (owned options).
        // The request body above uses lowercase keys, proving request binding stays case-insensitive.
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(6)/Size");
        var size = json.GetProperty("value");
        Assert.Equal(7.5m, size.GetProperty("Width").GetDecimal());
        Assert.Equal(8.25m, size.GetProperty("Height").GetDecimal());
    }

    // ── PATCH: primitive happy paths (semantically identical to PUT) ───────────

    [Fact]
    public async Task Patch_StringProperty_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PatchAsync("/odata/PropertyWriteItems(7)/Name",
            JsonContent.Create(new { value = "PatchedName" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(7)/Name");
        Assert.Equal("PatchedName", json.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Patch_IntProperty_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PatchAsync("/odata/PropertyWriteItems(8)/Quantity",
            JsonContent.Create(new { value = 7 }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(8)/Quantity");
        Assert.Equal(7, json.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Patch_NullableIntProperty_Returns204AndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PatchAsync("/odata/PropertyWriteItems(9)/Stock",
            JsonContent.Create(new { value = 3 }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(9)/Stock");
        Assert.Equal(3, json.GetProperty("value").GetInt32());
    }

    // ── PATCH on a complex property: documented non-support ────────────────────

    [Fact]
    public async Task Patch_ComplexProperty_Returns400NotSupported()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PatchAsync("/odata/PropertyWriteItems(10)/Size",
            JsonContent.Create(new { value = new { width = 1m, height = 1m } }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NotSupported", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── DELETE: happy paths (nullable → set to null) ────────────────────────────

    [Fact]
    public async Task Delete_NullableStringProperty_SetsNull_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/PropertyWriteItems(11)/Description");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var followUp = await fx.Client.GetAsync("/odata/PropertyWriteItems(11)/Description");
        Assert.Equal(HttpStatusCode.NoContent, followUp.StatusCode); // null property → 204 on read
    }

    [Fact]
    public async Task Delete_NullableIntProperty_SetsNull_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/PropertyWriteItems(12)/Stock");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var followUp = await fx.Client.GetAsync("/odata/PropertyWriteItems(12)/Stock");
        Assert.Equal(HttpStatusCode.NoContent, followUp.StatusCode);
    }

    [Fact]
    public async Task Delete_ComplexNullableProperty_SetsNull_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/PropertyWriteItems(13)/Size");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var followUp = await fx.Client.GetAsync("/odata/PropertyWriteItems(13)/Size");
        Assert.Equal(HttpStatusCode.NoContent, followUp.StatusCode);
    }

    // ── DELETE on a non-nullable property → 400 ─────────────────────────────────

    [Fact]
    public async Task Delete_NonNullableProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/PropertyWriteItems(14)/Price");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));

        // The property must be untouched.
        var followUp = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyWriteItems(14)/Price");
        Assert.Equal(9.99m, followUp.GetProperty("value").GetDecimal());
    }

    // ── Key property is immutable → 400 on all three verbs ──────────────────────

    [Fact]
    public async Task Put_KeyProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteItems(15)/Id", new { value = 999 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Id", json.GetProperty("error").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Patch_KeyProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PatchAsync("/odata/PropertyWriteItems(16)/Id",
            JsonContent.Create(new { value = 999 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_KeyProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/PropertyWriteItems(17)/Id");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Unknown property name → 404 (no route registered for that segment) ─────

    [Fact]
    public async Task Put_UnknownProperty_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteItems(18)/NotAProperty", new { value = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_UnknownProperty_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PatchAsync("/odata/PropertyWriteItems(19)/NotAProperty",
            JsonContent.Create(new { value = 1 }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownProperty_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/PropertyWriteItems(20)/NotAProperty");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Entity not found → 404 ───────────────────────────────────────────────────

    [Fact]
    public async Task Put_EntityMissing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteItems(999)/Name", new { value = "X" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_EntityMissing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/PropertyWriteItems(999)/Stock");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Malformed bodies ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_MissingValueMember_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        using var content = new StringContent("{\"notValue\":1}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/PropertyWriteItems(21)/Name", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Put_NonObjectBody_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        using var content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/PropertyWriteItems(22)/Name", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_InvalidJson_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        using var content = new StringContent("{ broken json", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/PropertyWriteItems(23)/Name", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_WrongTypedValue_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        using var content = new StringContent("{\"value\":\"not-a-decimal\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/PropertyWriteItems(24)/Price", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Wrong content type → 415 ─────────────────────────────────────────────────

    [Fact]
    public async Task Put_TextPlainContentType_Returns415()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        using var content = new StringContent("{\"value\":\"X\"}", Encoding.UTF8, "text/plain");
        var response = await fx.Client.PutAsync("/odata/PropertyWriteItems(25)/Name", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Patch_TextPlainContentType_Returns415()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteProfile>());
        using var content = new StringContent("{\"value\":\"X\"}", Encoding.UTF8, "text/plain");
        var response = await fx.Client.PatchAsync("/odata/PropertyWriteItems(26)/Name", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ── PropertyAccessEnabled = false → routes absent ────────────────────────────

    [Fact]
    public async Task Put_PropertyAccessDisabled_RouteAbsent_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyWriteDisabledProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyWriteDisabledItems(1)/Name", new { value = "X" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── No Patch handler → write routes absent (read routes still work) ─────────

    [Fact]
    public async Task Put_NoPatchHandler_RouteAbsent_Returns405()
    {
        // PropertyAccessProfile (see PropertyAccessTests) configures GetById but no Patch, so the
        // GET route for this template is registered but PUT/PATCH/DELETE are not. Since the
        // template itself still matches (only the read route), ASP.NET routing reports this as
        // 405 Method Not Allowed rather than 404 Not Found -- the segment exists, the verb doesn't.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyAccessProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/PropertyAccessItems(1)/Name", new { value = "X" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ── ETag / If-Match ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_IfMatch_Matches_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        var getResp = await fx.Client.GetAsync("/odata/ETagWidgets(1)");
        string etag = getResp.Headers.ETag!.Tag;

        using var request = new HttpRequestMessage(HttpMethod.Put, "/odata/ETagWidgets(1)/Name")
        {
            Content = JsonContent.Create(new { value = "Updated" })
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Put_IfMatch_Mismatch_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Put, "/odata/ETagWidgets(1)/Name")
        {
            Content = JsonContent.Create(new { value = "Updated" })
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale-etag\"");
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    // ── Authorization inherited from the entity set ─────────────────────────────

    [Fact]
    public async Task Put_Unauthenticated_Returns401()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<AuthorizedWidgetProfile>(),
            addAuth: true);
        var response = await fx.Client.PutAsJsonAsync("/odata/AuthorizedWidgets(1)/Name", new { value = "X" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    internal class PropertyWriteItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public decimal Price { get; set; }
        public int Quantity { get; set; }
        public int? Stock { get; set; }
        public Dimensions? Size { get; set; }
    }

    /// <summary>
    /// GetById + Patch, so both property-read and property-write routes register. Each test
    /// operates on its own dedicated <c>Id</c> so tests remain independent regardless of
    /// execution order (xUnit does not guarantee ordering of [Fact]s within a class, and the
    /// backing store is a shared static list so mutations persist across the scoped-DI instances
    /// created per HTTP request — see WidgetProfile/PropertyAccessProfile for the same pattern).
    /// </summary>
    internal class PropertyWriteProfile : EntitySetProfile<int, PropertyWriteItem>
    {
        internal static readonly List<PropertyWriteItem> Store =
            Enumerable.Range(1, 30).Select(i => new PropertyWriteItem
            {
                Id = i,
                Name = $"Item{i}",
                Description = $"Desc{i}",
                Price = 9.99m,
                Quantity = 5,
                Stock = 10,
                Size = new Dimensions { Width = 1m, Height = 2m },
            }).ToList();

        public PropertyWriteProfile() : base(x => x.Id)
        {
            EntitySetName = "PropertyWriteItems";
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(x => x.Id == id));
            Patch = (id, delta, ct) =>
            {
                var existing = Store.FirstOrDefault(x => x.Id == id);
                if (existing is null) return Task.FromResult<PropertyWriteItem?>(null);
                delta.Patch(existing);
                return Task.FromResult<PropertyWriteItem?>(existing);
            };
        }
    }

    /// <summary>
    /// Same shape as <see cref="PropertyWriteProfile"/> but with property access opted out —
    /// configures <c>Patch</c> too, to prove absence is driven by the flag, not a missing handler.
    /// </summary>
    internal class PropertyWriteDisabledProfile : EntitySetProfile<int, PropertyWriteItem>
    {
        private static readonly List<PropertyWriteItem> Store = new()
        {
            new() { Id = 1, Name = "X", Price = 1m, Quantity = 1 },
        };

        public PropertyWriteDisabledProfile() : base(x => x.Id)
        {
            EntitySetName = "PropertyWriteDisabledItems";
            PropertyAccessEnabled = false;
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(x => x.Id == id));
            Patch = (id, delta, ct) =>
            {
                var existing = Store.FirstOrDefault(x => x.Id == id);
                if (existing is null) return Task.FromResult<PropertyWriteItem?>(null);
                delta.Patch(existing);
                return Task.FromResult<PropertyWriteItem?>(existing);
            };
        }
    }
}
