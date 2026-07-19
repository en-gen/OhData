using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Regression tests for issue #240. #176/#179 made READ responses omit un-expanded navigation
/// properties, but write-path response bodies (POST 201 echo, PUT/PATCH return-representation) still
/// serialized them as explicit <c>null</c>/<c>[]</c> — so <c>POST /X</c> echoed <c>"studio": null</c>
/// while <c>GET /X(id)</c> omitted the member entirely. A write response takes no <c>$expand</c>, so
/// every declared navigation must be omitted, exactly as an un-expanded read.
/// </summary>
public class WriteResponseNavOmissionTests
{
    /// <summary>Write-enabled profile over the OmitMovie model (Studio single nav + Cast collection
    /// nav). Every handler returns an entity with BOTH navigations populated, so an un-omitted
    /// response would leak them.</summary>
    private sealed class WriteNavMovieProfile : EntitySetProfile<int, OmitMovie>
    {
        private static OmitMovie Populated(int id, string title) => new()
        {
            Id = id,
            Title = title,
            Studio = new OmitStudio { Id = 7, Name = "Skyline" },
            Cast = new List<OmitActor> { new() { Id = 100, Name = "Ada" } },
        };

        public WriteNavMovieProfile() : base(x => x.Id)
        {
            EntitySetName = "WriteNavMovies";

            GetById = (id, _) => Task.FromResult<OmitMovie?>(Populated(id, "Ascent"));
            Post = (movie, _) => Task.FromResult<OmitMovie?>(Populated(movie.Id == 0 ? 1 : movie.Id, movie.Title));
            Put = (id, movie, _) => Task.FromResult<OmitMovie>(Populated(id, movie.Title));
            Patch = (id, delta, _) =>
            {
                OmitMovie entity = Populated(id, "Original");
                delta.Patch(entity);
                return Task.FromResult<OmitMovie?>(entity);
            };

            HasOptional(
                navigation: x => x.Studio!,
                get: (movieId, _) => Task.FromResult<OmitStudio?>(null),
                refTargetEntitySet: null);
            HasMany(
                navigation: x => x.Cast!,
                getAll: (movieId, _) => Task.FromResult<IEnumerable<OmitActor>>(new List<OmitActor>()));
        }
    }

    /// <summary>Upsert-enabled profile: a PUT to a missing key returns null from Put, which — with
    /// AllowUpsert — delegates to Post and yields a 201 create echo carrying populated navs.</summary>
    private sealed class UpsertNavMovieProfile : EntitySetProfile<int, OmitMovie>
    {
        private static OmitMovie Populated(int id, string title) => new()
        {
            Id = id,
            Title = title,
            Studio = new OmitStudio { Id = 7, Name = "Skyline" },
            Cast = new List<OmitActor> { new() { Id = 100, Name = "Ada" } },
        };

        public UpsertNavMovieProfile() : base(x => x.Id)
        {
            EntitySetName = "UpsertNavMovies";
            AllowUpsert = true;

            GetById = (id, _) => Task.FromResult<OmitMovie?>(null);
            Put = (id, movie, _) => Task.FromResult<OmitMovie>(null!); // null → triggers upsert-create
            Post = (movie, _) => Task.FromResult<OmitMovie?>(Populated(movie.Id == 0 ? 9 : movie.Id, movie.Title));

            HasOptional(
                navigation: x => x.Studio!,
                get: (movieId, _) => Task.FromResult<OmitStudio?>(null),
                refTargetEntitySet: null);
            HasMany(
                navigation: x => x.Cast!,
                getAll: (movieId, _) => Task.FromResult<IEnumerable<OmitActor>>(new List<OmitActor>()));
        }
    }

    private static void AssertNavsOmittedButScalarsPresent(JsonElement body, string expectedTitle)
    {
        Assert.False(body.TryGetProperty("studio", out _), "write response must omit un-expanded single nav 'studio'");
        Assert.False(body.TryGetProperty("cast", out _), "write response must omit un-expanded collection nav 'cast'");
        Assert.Equal(expectedTitle, body.GetProperty("title").GetString()); // scalars still present (no over-strip)
    }

    [Fact]
    public async Task Post_201Echo_OmitsUnexpandedNavigations()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WriteNavMovieProfile>());
        var resp = await fx.Client.PostAsJsonAsync("/odata/WriteNavMovies", new { title = "Ascent" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        AssertNavsOmittedButScalarsPresent(doc.RootElement, "Ascent");
    }

    [Fact]
    public async Task Put_ResponseBody_OmitsUnexpandedNavigations()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WriteNavMovieProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/WriteNavMovies(1)", new { id = 1, title = "Ballad" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        AssertNavsOmittedButScalarsPresent(doc.RootElement, "Ballad");
    }

    [Fact]
    public async Task Patch_ResponseBody_OmitsUnexpandedNavigations()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WriteNavMovieProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/odata/WriteNavMovies(1)")
        {
            Content = JsonContent.Create(new { title = "Crest" }),
        };
        var resp = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        AssertNavsOmittedButScalarsPresent(doc.RootElement, "Crest");
    }

    [Fact]
    public async Task PutUpsertCreate_201Echo_OmitsUnexpandedNavigations()
    {
        // The 201 upsert-create branch of PUT (a distinct response path from the 200 update) must
        // also omit navigations.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<UpsertNavMovieProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/UpsertNavMovies(9)", new { id = 9, title = "Dune" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        AssertNavsOmittedButScalarsPresent(doc.RootElement, "Dune");
    }

    [Fact]
    public async Task Post_Echo_AndGetById_ExposeTheIdenticalMemberSet()
    {
        // The whole point of #240: the POST echo and a subsequent GET of the same type expose the
        // identical member set (neither carries the navigations).
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WriteNavMovieProfile>());

        using var postDoc = JsonDocument.Parse(await (await fx.Client.PostAsJsonAsync(
            "/odata/WriteNavMovies", new { title = "Ascent" })).Content.ReadAsStringAsync());
        using var getDoc = JsonDocument.Parse(await (await fx.Client.GetAsync(
            "/odata/WriteNavMovies(1)")).Content.ReadAsStringAsync());

        foreach (string nav in new[] { "studio", "cast" })
        {
            Assert.False(postDoc.RootElement.TryGetProperty(nav, out _), $"POST echo must omit '{nav}'");
            Assert.False(getDoc.RootElement.TryGetProperty(nav, out _), $"GET must omit '{nav}'");
        }
    }
}
