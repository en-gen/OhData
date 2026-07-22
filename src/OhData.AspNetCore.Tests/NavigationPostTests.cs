using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Tests for <c>POST /{EntitySet}({key})/{Property}</c> — creating a new related entity via a
/// collection navigation property (OData §11.4.2.1). Rides the new <c>post</c> parameter on
/// <c>HasMany</c>, wired through <see cref="NavigationRouteDefinition.PostChild"/>. Registered
/// only when a <c>post</c> handler is supplied (handler-presence-drives-routes).
/// </summary>
public class NavigationPostTests
{
    // ── Happy path: 201 + body + Location ───────────────────────────────────────

    [Fact]
    public async Task Post_CreatesChild_Returns201WithBodyAndLocation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostHappyProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/NavPostHappyParents(1)/Children", new { name = "NewChild" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("NavPostHappyChildren(500)", response.Headers.Location!.ToString());

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out _));
        Assert.True(json.TryGetProperty("@odata.id", out var odataId));
        Assert.Contains("NavPostHappyChildren(500)", odataId.GetString());
        Assert.Equal("NewChild", json.GetProperty("Name").GetString());
        Assert.Equal(1, json.GetProperty("ParentId").GetInt32());
    }

    [Fact]
    public async Task Post_NoRefTargetEntitySet_Returns201WithoutLocation()
    {
        // When refTargetEntitySet isn't configured, the framework cannot compute a child
        // @odata.id/Location — documented as omitted rather than guessed.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostNoRefProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/NavPostNoRefParents(1)/Children", new { name = "NewChild" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("@odata.id", out _));
        Assert.Equal("NewChild", json.GetProperty("Name").GetString());
    }

    // ── Prefer: return=minimal ───────────────────────────────────────────────────

    [Fact]
    public async Task Post_ReturnMinimal_Returns204WithODataEntityId()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostMinimalProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/odata/NavPostMinimalParents(1)/Children")
        {
            Content = JsonContent.Create(new { name = "NewChild" })
        };
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        var response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("OData-EntityId"));
        Assert.Contains("NavPostMinimalChildren(501)", response.Headers.GetValues("OData-EntityId").First());
        Assert.True(response.Headers.Contains("Preference-Applied"));
        Assert.Equal("return=minimal", response.Headers.GetValues("Preference-Applied").First());
        Assert.NotNull(response.Headers.Location);
    }

    // ── Parent not found ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_ParentNotFound_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostParentMissingProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/NavPostMissingParents(999)/Children", new { name = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("NotFound", err.GetProperty("code").GetString());
    }

    // ── Malformed body / content type ────────────────────────────────────────────

    [Fact]
    public async Task Post_MalformedBody_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostValidationProfile>());

        using var content = new StringContent("{ broken json", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/NavPostValidationParents(1)/Children", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("InvalidBody", err.GetProperty("code").GetString());

        // Connection must remain usable after a malformed request.
        var followUp = await fx.Client.GetAsync("/odata/NavPostValidationParents(1)/Children");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact]
    public async Task Post_EmptyBody_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostValidationProfile>());

        using var content = new StringContent("", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/NavPostValidationParents(1)/Children", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Post_WrongContentType_Returns415WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostValidationProfile>());

        using var content = new StringContent("{\"name\":\"X\"}", Encoding.UTF8, "text/plain");
        var response = await fx.Client.PostAsync("/odata/NavPostValidationParents(1)/Children", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("UnsupportedMediaType", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_BadKeyFormat_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostValidationProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/NavPostValidationParents(abc)/Children", new { name = "X" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BadRequest", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── No post handler configured → route not registered ───────────────────────

    [Fact]
    public async Task Post_NoHandlerConfigured_RouteNotRegistered()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostNoHandlerProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/NavPostNoHandlerParents(1)/Children", new { name = "X" });

        // No POST route was mapped for this template; the GET nav route occupies the same
        // template, so ASP.NET Core's endpoint routing reports either 405 (template matched,
        // method didn't) or 404, but never treats the request as handled.
        Assert.True(
            response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Expected 404 or 405 for an unregistered POST-to-nav route, got {(int)response.StatusCode}.");

        // The GET route must still work — confirms the profile itself is healthy.
        var getResponse = await fx.Client.GetAsync("/odata/NavPostNoHandlerParents(1)/Children");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    // ── Auth inherited from parent profile ───────────────────────────────────────

    [Fact]
    public async Task Post_AuthRequired_AnonymousReturns401()
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<NavPostAuthProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/NavPostAuthParents(1)/Children", new { name = "X" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_AuthRequired_AuthenticatedReturns201()
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<NavPostAuthProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/odata/NavPostAuthParents(1)/Children")
        {
            Content = JsonContent.Create(new { name = "X" })
        };
        request.Headers.TryAddWithoutValidation(HeaderAuthHandler.IdentityHeader, "alice");

        var response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── Coexists with batch nav handlers on the same profile ────────────────────

    [Fact]
    public async Task Post_CoexistsWithBatchNavHandlerOnSameProfile()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostWithBatchProfile>());

        var postResponse = await fx.Client.PostAsJsonAsync(
            "/odata/NavPostBatchParents(1)/Children", new { name = "NewChild" });
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);

        var notesResponse = await fx.Client.GetAsync("/odata/NavPostBatchParents(1)/Notes");
        Assert.Equal(HttpStatusCode.OK, notesResponse.StatusCode);
        var notesJson = await notesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, notesJson.GetProperty("value").ValueKind);
        Assert.True(notesJson.GetProperty("value").GetArrayLength() > 0);

        var expandResponse = await fx.Client.GetAsync("/odata/NavPostBatchParents?$expand=Notes");
        Assert.Equal(HttpStatusCode.OK, expandResponse.StatusCode);
    }

    // ── $metadata unchanged ───────────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_UnchangedByPostChildHandler()
    {
        await using var withPost = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostMetadataWithPostProfile>());
        await using var withoutPost = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavPostMetadataBaselineProfile>());

        string metaWithPost = await withPost.Client.GetStringAsync("/odata/$metadata");
        string metaWithoutPost = await withoutPost.Client.GetStringAsync("/odata/$metadata");

        Assert.Equal(metaWithoutPost, metaWithPost);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    private class NavPostChild
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public string Name { get; set; } = "";
    }

    private class NavPostParent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<NavPostChild> Children { get; set; } = new();
    }

    private class NavPostHappyProfile : EntitySetProfile<int, NavPostParent>
    {
        private readonly List<NavPostParent> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<NavPostChild> _children = new() { new() { Id = 100, ParentId = 1, Name = "Existing" } };

        public NavPostHappyProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostHappyParents";
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(_children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    if (_parents.All(p => p.Id != parentId)) return Task.FromResult<NavPostChild?>(null);
                    child.Id = 500;
                    child.ParentId = parentId;
                    _children.Add(child);
                    return Task.FromResult<NavPostChild?>(child);
                },
                refTargetEntitySet: "NavPostHappyChildren");
        }
    }

    private class NavPostNoRefProfile : EntitySetProfile<int, NavPostParent>
    {
        private readonly List<NavPostParent> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<NavPostChild> _children = new();

        public NavPostNoRefProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostNoRefParents";
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(_children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    if (_parents.All(p => p.Id != parentId)) return Task.FromResult<NavPostChild?>(null);
                    child.Id = 600;
                    child.ParentId = parentId;
                    _children.Add(child);
                    return Task.FromResult<NavPostChild?>(child);
                });
            // Note: no refTargetEntitySet — Location/@odata.id cannot be computed.
        }
    }

    private class NavPostMinimalProfile : EntitySetProfile<int, NavPostParent>
    {
        private readonly List<NavPostParent> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<NavPostChild> _children = new();

        public NavPostMinimalProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostMinimalParents";
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(_children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    if (_parents.All(p => p.Id != parentId)) return Task.FromResult<NavPostChild?>(null);
                    child.Id = 501;
                    child.ParentId = parentId;
                    _children.Add(child);
                    return Task.FromResult<NavPostChild?>(child);
                },
                refTargetEntitySet: "NavPostMinimalChildren");
        }
    }

    private class NavPostParentMissingProfile : EntitySetProfile<int, NavPostParent>
    {
        private readonly List<NavPostParent> _parents = new(); // intentionally empty — every key is "missing"

        public NavPostParentMissingProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostMissingParents";
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(Array.Empty<NavPostChild>()),
                post: (parentId, child, ct) =>
                {
                    if (_parents.All(p => p.Id != parentId)) return Task.FromResult<NavPostChild?>(null);
                    return Task.FromResult<NavPostChild?>(child);
                },
                refTargetEntitySet: "NavPostMissingChildren");
        }
    }

    private class NavPostValidationProfile : EntitySetProfile<int, NavPostParent>
    {
        private readonly List<NavPostParent> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<NavPostChild> _children = new();

        public NavPostValidationProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostValidationParents";
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(_children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    if (_parents.All(p => p.Id != parentId)) return Task.FromResult<NavPostChild?>(null);
                    child.Id = 700;
                    child.ParentId = parentId;
                    _children.Add(child);
                    return Task.FromResult<NavPostChild?>(child);
                });
        }
    }

    private class NavPostNoHandlerProfile : EntitySetProfile<int, NavPostParent>
    {
        private readonly List<NavPostParent> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<NavPostChild> _children = new() { new() { Id = 1, ParentId = 1, Name = "Existing" } };

        public NavPostNoHandlerProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostNoHandlerParents";
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            // getAll only — no post handler, so POST /{key}/Children must not be registered.
            HasMany(x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(_children.Where(c => c.ParentId == parentId)));
        }
    }

    private class NavPostAuthProfile : EntitySetProfile<int, NavPostParent>
    {
        private readonly List<NavPostParent> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<NavPostChild> _children = new();

        public NavPostAuthProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostAuthParents";
            RequireAuthorization();
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(_children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    if (_parents.All(p => p.Id != parentId)) return Task.FromResult<NavPostChild?>(null);
                    child.Id = 800;
                    child.ParentId = parentId;
                    _children.Add(child);
                    return Task.FromResult<NavPostChild?>(child);
                });
        }
    }

    private class NavPostNote
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public string Text { get; set; } = "";
    }

    private class NavPostParentEx
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<NavPostChild> Children { get; set; } = new();
        public List<NavPostNote> Notes { get; set; } = new();
    }

    private class NavPostWithBatchProfile : EntitySetProfile<int, NavPostParentEx>
    {
        private readonly List<NavPostParentEx> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<NavPostChild> _children = new();
        private readonly List<NavPostNote> _notes = new() { new() { Id = 1, ParentId = 1, Text = "Note1" } };

        public NavPostWithBatchProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostBatchParents";
            ExpandEnabled = true;
            GetAll = (ct) => Task.FromResult<IEnumerable<NavPostParentEx>>(_parents);
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(_children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    if (_parents.All(p => p.Id != parentId)) return Task.FromResult<NavPostChild?>(null);
                    child.Id = 900;
                    child.ParentId = parentId;
                    _children.Add(child);
                    return Task.FromResult<NavPostChild?>(child);
                });

            HasMany(x => x.Notes!, batchGetAll: (ids, ct) =>
                Task.FromResult(_notes.Where(n => ids.Contains(n.ParentId)).ToLookup(n => n.ParentId)));
        }
    }

    private class NavPostMetadataWithPostProfile : EntitySetProfile<int, NavPostParent>
    {
        public NavPostMetadataWithPostProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostMetadataParents";
            GetById = (id, ct) => Task.FromResult<NavPostParent?>(null);

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(Array.Empty<NavPostChild>()),
                post: (parentId, child, ct) => Task.FromResult<NavPostChild?>(child),
                refTargetEntitySet: "NavPostMetadataChildren");
        }
    }

    private class NavPostMetadataBaselineProfile : EntitySetProfile<int, NavPostParent>
    {
        public NavPostMetadataBaselineProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostMetadataParents";
            GetById = (id, ct) => Task.FromResult<NavPostParent?>(null);

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(Array.Empty<NavPostChild>()),
                refTargetEntitySet: "NavPostMetadataChildren");
        }
    }

    // ── Startup collision validation: POST /{Set}({key})/{segment} ──────────────

    /// <summary>
    /// A collection navigation with a <c>post</c> handler ("Children" — claims
    /// <c>POST /{key}/Children</c>) and an entity-level bound action deliberately named the same
    /// ("Children" — also claims <c>POST /{key}/Children</c>). Used to test the startup
    /// route-collision validation added alongside property routes: <c>app.MapOhData()</c> must
    /// throw <see cref="InvalidOperationException"/> rather than let ASP.NET Core register two
    /// handlers for the same (template, method) pair.
    /// </summary>
    private class NavPostActionCollisionProfile : EntitySetProfile<int, NavPostParent>
    {
        public NavPostActionCollisionProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPostActionCollisionParents";
            GetById = (id, ct) => Task.FromResult<NavPostParent?>(null);

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavPostChild>>(Array.Empty<NavPostChild>()),
                post: (parentId, child, ct) => Task.FromResult<NavPostChild?>(child));

            BindEntityAction(Children);
        }

        // Method name "Children" intentionally collides with the nav property's POST route.
        private void Children(int key) { }
    }

    [Fact]
    public void PostChildCollision_WithEntityLevelBoundAction_ThrowsAtMapOhData()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData(o => o.AddEntitySetProfile<NavPostActionCollisionProfile>());
        var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapOhData());
        Assert.Contains("Children", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NavPostActionCollisionParents", ex.Message, StringComparison.Ordinal);
    }
}
