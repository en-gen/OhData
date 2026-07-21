using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #253: a structural property carrying <c>[System.Text.Json.Serialization.JsonPropertyName]</c>
/// exposes that one name on every OData surface — <c>$metadata</c>, the response payload, and the
/// server-accepted <c>$select</c>/<c>$filter</c>/<c>$orderby</c> spellings. The headline case is the
/// confirmed silent data loss: <c>$select</c> of a renamed property used to drop it from the response
/// because the EDM accepted only the CLR name while the payload key was the rename.
/// </summary>
public class JsonPropertyNameEdmTests
{
    // ── $metadata advertises the renamed name (not the CLR name) ──────────────────

    [Fact]
    public async Task Metadata_UsesRenamedName_NotClrName()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        string metadata = await fx.Client.GetStringAsync("/odata/$metadata");

        Assert.Contains("Name=\"emailAddress\"", metadata);
        Assert.DoesNotContain("Name=\"Email\"", metadata);
        // An un-renamed sibling keeps its CLR/PascalCase EDM name.
        Assert.Contains("Name=\"Name\"", metadata);
    }

    // ── Response payload uses the renamed name (System.Text.Json) ─────────────────

    [Fact]
    public async Task Response_UsesRenamedName()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedStructCustomers");
        JsonElement first = json.GetProperty("value")[0];

        Assert.True(first.TryGetProperty("emailAddress", out _));
        Assert.False(first.TryGetProperty("Email", out _));
    }

    // ── THE DATA-LOSS REGRESSION: $select of a renamed property must survive ──────

    [Fact]
    public async Task Select_RenamedName_PropertySurvives()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedStructCustomers?$select=emailAddress");
        JsonElement first = json.GetProperty("value")[0];

        // Before the fix this was dropped: EDM name "Email" (the only accepted $select spelling)
        // ≠ payload key "emailAddress", so the OrdinalIgnoreCase strip removed it.
        Assert.True(first.TryGetProperty("emailAddress", out var email), "renamed $select'd property must be present");
        Assert.Equal("ada@example.com", email.GetString());
        // Only the selected property (Name/Id were not selected).
        Assert.False(first.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task GetById_Select_RenamedName_PropertySurvives()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var entity = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedStructCustomers(1)?$select=emailAddress");

        Assert.True(entity.TryGetProperty("emailAddress", out var email));
        Assert.Equal("ada@example.com", email.GetString());
    }

    // ── The old CLR name is now a genuinely-unknown property (part of the break) ──

    [Fact]
    public async Task Select_OldClrName_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/RenamedStructCustomers?$select=Email");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── $filter / $orderby accept the renamed name; reject the CLR name ───────────

    [Fact]
    public async Task Filter_RenamedName_Works()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/RenamedStructCustomers?$filter=emailAddress eq 'ben@example.com'");

        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
        Assert.Equal("Ben", json.GetProperty("value")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Filter_OldClrName_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/RenamedStructCustomers?$filter=Email eq 'ben@example.com'");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OrderBy_RenamedName_Works()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/RenamedStructCustomers?$orderby=emailAddress desc");
        var value = json.GetProperty("value");

        // Descending by emailAddress: "ben@..." before "ada@...".
        Assert.Equal("Ben", value[0].GetProperty("Name").GetString());
        Assert.Equal("Ada", value[1].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task OrderBy_OldClrName_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/RenamedStructCustomers?$orderby=Email");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Nested $select of a renamed child property survives (nested drop) ─────────

    [Fact]
    public async Task NestedSelect_RenamedChildProperty_Survives()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var entity = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/RenamedStructCustomers(1)?$expand=Orders($select=displayLabel)");

        JsonElement order = entity.GetProperty("Orders")[0];
        Assert.True(order.TryGetProperty("displayLabel", out var label), "renamed nested $select'd property must survive");
        Assert.Equal("First", label.GetString());
        Assert.False(order.TryGetProperty("Label", out _));
    }

    // ── Standalone navigation-collection GET $select/$orderby use the renamed name ─

    [Fact]
    public async Task NavCollectionGet_Select_RenamedChildProperty_Survives()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/RenamedStructCustomers(1)/Orders?$select=displayLabel");
        JsonElement order = json.GetProperty("value")[0];

        Assert.True(order.TryGetProperty("displayLabel", out var label));
        Assert.Equal("First", label.GetString());
        Assert.False(order.TryGetProperty("Label", out _));
    }

    [Fact]
    public async Task NavCollectionGet_Select_OldClrName_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/RenamedStructCustomers(1)/Orders?$select=Label");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NavCollectionGet_OrderBy_RenamedChildProperty_Works()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var response = await fx.Client.GetAsync(
            "/odata/RenamedStructCustomers(1)/Orders?$orderby=displayLabel");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Property route rides the renamed (EDM) name segment ───────────────────────

    [Fact]
    public async Task PropertyRoute_UsesRenamedSegment()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());

        var renamed = await fx.Client.GetAsync("/odata/RenamedStructCustomers(1)/emailAddress");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var body = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ada@example.com", body.GetProperty("value").GetString());

        // The CLR-name segment no longer exists.
        var clr = await fx.Client.GetAsync("/odata/RenamedStructCustomers(1)/Email");
        Assert.Equal(HttpStatusCode.NotFound, clr.StatusCode);
    }

    // ── PATCH binds the renamed property from its JSON body key ───────────────────

    [Fact]
    public async Task Patch_RenamedName_BindsAndPersists()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedStructCustomerProfile>());
        var patch = new HttpRequestMessage(new HttpMethod("PATCH"), "/odata/RenamedStructCustomers(2)")
        {
            Content = JsonContent.Create(new System.Collections.Generic.Dictionary<string, object?>
            {
                ["emailAddress"] = "ben.updated@example.com",
            }),
        };
        var resp = await fx.Client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var echo = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ben.updated@example.com", echo.GetProperty("emailAddress").GetString());
    }

    // ── Key property carrying [JsonPropertyName] ──────────────────────────────────

    [Fact]
    public async Task RenamedKey_MetadataAndRoutingAndPayload()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedKeyProfile>());

        string metadata = await fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains("Name=\"code\"", metadata);
        Assert.Contains("<PropertyRef Name=\"code\"", metadata);

        // Value-based key routing is name-independent — still resolves.
        var entity = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedKeyEntities('A1')");
        Assert.Equal("Alpha", entity.GetProperty("Name").GetString());
        Assert.Equal("A1", entity.GetProperty("code").GetString());
        Assert.False(entity.TryGetProperty("Key", out _));
    }

    // ── Interaction with Ignore() (#226) ──────────────────────────────────────────

    [Fact]
    public async Task RenameWithIgnore_IgnoredGone_OtherRenameExposed()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RenamedIgnoreProfile>());

        string metadata = await fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains("Name=\"publicEmail\"", metadata);
        Assert.DoesNotContain("secretNote", metadata);
        Assert.DoesNotContain("InternalNotes", metadata);

        var entity = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedIgnores(1)");
        Assert.True(entity.TryGetProperty("publicEmail", out _));
        Assert.False(entity.TryGetProperty("secretNote", out _));
        Assert.False(entity.TryGetProperty("InternalNotes", out _));
    }

    // ── A rename that collides with a sibling's OData name fails fast ─────────────

    [Fact]
    public async Task CollidingRename_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<CollidingRenameProfile>()));
        Assert.Contains("OData name", ex.Message);
        Assert.Contains("Name", ex.Message);
    }

    // ── camelCase opt-in: $metadata stays EDM names; only the payload flips ───────

    [Fact]
    public async Task CamelCaseOptIn_MetadataUnchanged_PayloadFlipsExceptRename()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
        {
            o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase);
            o.AddEntitySetProfile<RenamedStructCustomerProfile>();
        });

        // $metadata is unaffected by the response naming policy — it still advertises the EDM names
        // (PascalCase "Name", and the [JsonPropertyName] "emailAddress"), never a camelCase form.
        string metadata = await fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains("Name=\"emailAddress\"", metadata);
        Assert.Contains("Name=\"Name\"", metadata);
        Assert.DoesNotContain("Name=\"name\"", metadata);

        // The payload flips un-renamed props to camelCase, but a [JsonPropertyName] wins verbatim.
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/RenamedStructCustomers");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("name", out _));           // un-renamed → camelCase
        Assert.True(first.TryGetProperty("emailAddress", out _));   // rename verbatim (not "emailaddress")
    }
}
