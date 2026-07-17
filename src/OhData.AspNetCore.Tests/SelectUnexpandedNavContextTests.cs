using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Issue #184, item 4 (documented decision: keep behavior). <c>GET Set(key)?$select=&lt;nav&gt;</c>
/// with no <c>$expand</c> selects the navigation's <em>link</em>, which the default
/// <c>odata.metadata=minimal</c> omits from the body when it is convention-computable (OData JSON
/// §4.5.9 / §11.2.4.1). The result is a spec-defensible content-less entity (only <c>@odata.*</c>
/// annotations), whose <c>@odata.context</c> still lists the selected nav in its projection — the
/// context URL MUST echo the client's select list (§10.8). We deliberately do NOT drop the
/// projection suffix (rejected option (a)): that would emit <c>#Set/$entity</c>, falsely claiming
/// the full entity was returned.
/// </summary>
public class SelectUnexpandedNavContextTests
{
    [Fact]
    public async Task GetById_SelectUnexpandedNav_ContextKeepsProjection_BodyHasNoContentMembers()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagExpandSelectProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ETagExpandSelectParents(1)?$select=Children");

        // Context URL keeps the (Children) projection (§10.8) — it faithfully reflects $select.
        string? context = json.GetProperty("@odata.context").GetString();
        Assert.NotNull(context);
        Assert.EndsWith("#ETagExpandSelectParents(Children)/$entity", context);

        // Body carries only @odata.* annotations: the un-expanded nav link is omitted under minimal
        // metadata, and $select excluded every structural property (id, name).
        foreach (var member in json.EnumerateObject())
        {
            Assert.StartsWith("@", member.Name);
        }
        Assert.False(json.TryGetProperty("children", out _), "un-expanded selected nav must not appear inline");
        Assert.False(json.TryGetProperty("name", out _), "unselected structural property must be stripped");
        Assert.False(json.TryGetProperty("id", out _), "unselected structural property must be stripped");
    }

    [Fact]
    public async Task GetById_SelectStructuralPlusUnexpandedNav_KeepsStructuralOmitsNav()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagExpandSelectProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ETagExpandSelectParents(1)?$select=Name,Children");

        // Both selected items appear in the projected context, in request order.
        string? context = json.GetProperty("@odata.context").GetString();
        Assert.EndsWith("#ETagExpandSelectParents(Name,Children)/$entity", context);

        // The selected structural property is present; the selected-but-un-expanded nav is not.
        Assert.Equal("P1", json.GetProperty("name").GetString());
        Assert.False(json.TryGetProperty("children", out _), "un-expanded selected nav must not appear inline");
    }
}
