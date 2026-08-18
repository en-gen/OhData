using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// #313 stage 4. The client used to drop every OData annotation attached to an entity: the
/// envelope binds four members by <c>[JsonPropertyName]</c> and nothing else, the caller's own
/// <c>T</c> owns no <c>[JsonExtensionData]</c> bag the client could reach, and
/// <c>UnmappedMemberHandling</c> sits at its <c>Skip</c> default — so
/// <c>{Nav}@odata.nextLink</c> and <c>{Nav}@odata.count</c> matched no member (the <c>@</c>
/// guarantees it) and vanished. A server-side-paged expansion therefore arrived as a truncated
/// collection that looked complete.
/// <para>
/// The bar these tests hold is that a caller can <em>see</em> both annotations, not that
/// deserialization does not error — it never did.
/// </para>
/// <para>
/// The nested <c>nextLink</c> half is exercised against canned bytes because no OhData server
/// emits one yet (that is stage 5); the nested <c>count</c> half is exercised against a live
/// in-process server, because the server has emitted <c>{Nav}@odata.count</c> all along and it
/// has been equally unreadable — a hole that exists today, independent of #313.
/// </para>
/// </summary>
public class NestedAnnotationTests
{
    // ── Canned-response harness ─────────────────────────────────────────────────

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public CannedHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (HttpResponseMessage response in _responses) response.Dispose();
                _responses.Clear();
            }
            base.Dispose(disposing);
        }
    }

    private static (OhDataClient Client, CannedHandler Handler) BuildClient(
        string body, OhDataClientOptions? options = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CannedHandler(body, status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://server.example/odata/") };
        return (new OhDataClient(http, options), handler);
    }

    // A page of TaggedItems whose expanded Tags collection was server-side paged: two of the
    // fifty-seven related tags are inline, and the server said so twice — once with the full count
    // and once with the continuation link. Read without annotations, this is a two-tag item.
    private const string PagedExpansionBody = """
        {
          "@odata.context": "https://server.example/odata/$metadata#TaggedItems",
          "value": [
            {
              "Id": 1,
              "Name": "Foo",
              "Tags": [ { "Id": 1, "Name": "Red" }, { "Id": 2, "Name": "Blue" } ],
              "Tags@odata.count": 57,
              "Tags@odata.nextLink": "https://server.example/odata/TaggedItems(1)/Tags?$skip=2"
            }
          ]
        }
        """;

    // ── The gate, in the issue's own words ──────────────────────────────────────

    // Deliberately the Authors/Books shape from OhData.AspNetCore.Tests' MultiLevelSqliteHarness,
    // whose MaxExpandTopTests.NestedCount_ChildCountEqualToCeiling_Succeeds_WithExactCount asserts
    // the server writes the literal bytes "Books@odata.count":2. The canned body below reproduces
    // that key verbatim and adds the Books@odata.nextLink #313 stage 5 will emit beside it, so the
    // two suites are auditable against each other: the server test pins the emission, this one pins
    // that a caller can read it.
    private sealed class Author
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Book> Books { get; set; } = [];
    }

    private sealed class Book
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
    }

    [Fact]
    public async Task ACallerCanSee_BooksNextLink_AndBooksCount()
    {
        const string body = """
            {
              "@odata.context": "https://server.example/odata/$metadata#Authors",
              "value": [
                {
                  "Id": 1, "Name": "A1",
                  "Books": [ { "Id": 10, "Title": "B1" }, { "Id": 11, "Title": "B2" } ],
                  "Books@odata.count": 2,
                  "Books@odata.nextLink": "https://server.example/odata/Authors(1)/Books?$skip=2"
                }
              ]
            }
            """;
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            ODataAnnotatedPage<Author> page = await client.For<Author>("Authors")
                .Expand(x => x.Books)
                .ToAnnotatedPageAsync();

            ODataAnnotatedEntity<Author> author = Assert.Single(page.Entries);
            Assert.Equal(2L, author.CountFor(x => x.Books));
            Assert.Equal(
                new Uri("https://server.example/odata/Authors(1)/Books?$skip=2"),
                author.NextLinkFor(x => x.Books));
        }
    }

    // ── The same, on the brownfield model ───────────────────────────────────────

    [Fact]
    public async Task AnnotatedPage_SurfacesNestedNextLinkAndCount()
    {
        (OhDataClient client, CannedHandler handler) = BuildClient(PagedExpansionBody);
        using (client)
        using (handler)
        {
            ODataAnnotatedPage<TaggedItem> page = await client.For<TaggedItem>("TaggedItems")
                .Expand(x => x.Tags)
                .ToAnnotatedPageAsync();

            ODataAnnotatedEntity<TaggedItem> entry = Assert.Single(page.Entries);

            Assert.Equal(
                new Uri("https://server.example/odata/TaggedItems(1)/Tags?$skip=2"),
                entry.Annotations.NextLinkFor("Tags"));
            Assert.Equal(57L, entry.Annotations.CountFor("Tags"));

            // and typed, off the entity's own member rather than a string
            Assert.Equal(
                new Uri("https://server.example/odata/TaggedItems(1)/Tags?$skip=2"),
                entry.NextLinkFor(x => x.Tags));
            Assert.Equal(57L, entry.CountFor(x => x.Tags));
        }
    }

    [Fact]
    public async Task PlainRead_ShowsATruncatedCollectionAsThoughItWereComplete()
    {
        // The defect, stated as an assertion. From the SAME bytes the plain path yields an item
        // carrying two tags and no signal whatsoever that fifty-five more exist; ODataPage<T> has
        // no member that could carry one. Only the annotated read can tell the difference.
        (OhDataClient plain, CannedHandler plainHandler) = BuildClient(PagedExpansionBody);
        using (plain)
        using (plainHandler)
        {
            ODataPage<TaggedItem> page = await plain.For<TaggedItem>("TaggedItems")
                .Expand(x => x.Tags)
                .ToPageAsync();

            TaggedItem item = Assert.Single(page.Items);
            Assert.Equal(2, item.Tags.Count);
            Assert.Null(page.NextLink);   // the ENVELOPE was not paged; the expansion was
        }

        (OhDataClient annotated, CannedHandler annotatedHandler) = BuildClient(PagedExpansionBody);
        using (annotated)
        using (annotatedHandler)
        {
            ODataAnnotatedPage<TaggedItem> page = await annotated.For<TaggedItem>("TaggedItems")
                .Expand(x => x.Tags)
                .ToAnnotatedPageAsync();

            ODataAnnotatedEntity<TaggedItem> entry = Assert.Single(page.Entries);
            Assert.Equal(2, entry.Entity.Tags.Count);
            Assert.Equal(57L, entry.CountFor(x => x.Tags));
            Assert.NotNull(entry.NextLinkFor(x => x.Tags));
        }
    }

    // ── Annotation vocabulary ───────────────────────────────────────────────────

    [Fact]
    public async Task NextLink_MayBeRelative()
    {
        const string body = """
            {"value":[{"Id":1,"Name":"Foo","Tags":[],"Tags@odata.nextLink":"TaggedItems(1)/Tags?$skip=2"}]}
            """;
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            var page = await client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync();
            Uri? link = Assert.Single(page.Entries).NextLinkFor(x => x.Tags);
            Assert.NotNull(link);
            Assert.False(link!.IsAbsoluteUri);
            Assert.Equal("TaggedItems(1)/Tags?$skip=2", link.ToString());
        }
    }

    [Fact]
    public async Task ShortFormAnnotations_AreAccepted()
    {
        // OData 4.01 JSON Format lets a producer drop the "odata." qualifier. OhData servers emit
        // the 4.0 qualified form, but this client is not OhData-only.
        const string body = """
            {"value":[{"Id":1,"Name":"Foo","Tags":[],"Tags@count":9,"Tags@nextLink":"/x?$skip=1"}]}
            """;
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            var entry = Assert.Single((await client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync()).Entries);
            Assert.Equal(9L, entry.CountFor(x => x.Tags));
            Assert.Equal("/x?$skip=1", entry.NextLinkFor(x => x.Tags)!.ToString());
        }
    }

    [Fact]
    public async Task InstanceAnnotations_AreReachableRaw()
    {
        const string body = """
            {"value":[{"@odata.id":"TaggedItems(1)","@odata.etag":"W/\"7\"","Id":1,"Name":"Foo","Tags":[]}]}
            """;
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            var entry = Assert.Single((await client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync()).Entries);
            Assert.True(entry.Annotations.TryGetValue("@odata.etag", out JsonElement etag));
            Assert.Equal("W/\"7\"", etag.GetString());
            Assert.Equal(2, entry.Annotations.Values.Count);
        }
    }

    [Fact]
    public async Task NoAnnotations_YieldsTheEmptySingleton_AndNullAccessors()
    {
        const string body = """{"value":[{"Id":1,"Name":"Foo","Tags":[]}]}""";
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            var entry = Assert.Single((await client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync()).Entries);
            Assert.True(entry.Annotations.IsEmpty);
            Assert.Same(ODataEntityAnnotations.Empty, entry.Annotations);
            Assert.Null(entry.NextLinkFor(x => x.Tags));
            Assert.Null(entry.CountFor(x => x.Tags));
            Assert.Null(entry.Annotations.CountFor("Nope"));
        }
    }

    [Fact]
    public async Task NonNumericCount_AndNonStringNextLink_AreNotCoerced()
    {
        const string body = """
            {"value":[{"Id":1,"Name":"Foo","Tags":[],"Tags@odata.count":"57","Tags@odata.nextLink":42}]}
            """;
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            var entry = Assert.Single((await client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync()).Entries);
            Assert.Null(entry.CountFor(x => x.Tags));
            Assert.Null(entry.NextLinkFor(x => x.Tags));
            // …but the raw value is still there, so nothing is silently lost.
            Assert.True(entry.Annotations.TryGetValue("Tags@odata.count", out JsonElement raw));
            Assert.Equal("57", raw.GetString());
        }
    }

    // ── Envelope-level annotations ──────────────────────────────────────────────

    [Fact]
    public async Task EnvelopeAnnotations_AreCaptured_AndCountNextLinkStayTyped()
    {
        const string body = """
            {
              "@odata.context": "https://server.example/odata/$metadata#TaggedItems",
              "@odata.count": 3,
              "@odata.deltaLink": "https://server.example/odata/TaggedItems?$deltatoken=abc",
              "value": [ { "Id": 1, "Name": "Foo", "Tags": [] } ]
            }
            """;
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            var page = await client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync();
            Assert.Equal(3L, page.TotalCount);
            Assert.Null(page.NextLink);
            Assert.True(page.Annotations.TryGetValue("@odata.deltaLink", out JsonElement delta));
            Assert.Equal("https://server.example/odata/TaggedItems?$deltatoken=abc", delta.GetString());
        }
    }

    // ── Naming ──────────────────────────────────────────────────────────────────

    private sealed class RenamedItem
    {
        public int Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("labels")]
        public List<ItemTag> Tags { get; set; } = [];
    }

    [Fact]
    public async Task ExpressionAccessor_HonoursJsonPropertyName()
    {
        const string body = """{"value":[{"Id":1,"labels":[],"labels@odata.count":4}]}""";
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            var entry = Assert.Single((await client.For<RenamedItem>("RenamedItems").ToAnnotatedPageAsync()).Entries);
            Assert.Equal(4L, entry.CountFor(x => x.Tags));
        }
    }

    [Fact]
    public async Task ExpressionAccessor_HonoursNamingPolicy()
    {
        const string body = """{"value":[{"id":1,"name":"Foo","tags":[],"tags@odata.count":4}]}""";
        var options = new OhDataClientOptions
        {
            JsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            },
        };
        (OhDataClient client, CannedHandler handler) = BuildClient(body, options);
        using (client)
        using (handler)
        {
            var entry = Assert.Single((await client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync()).Entries);
            Assert.Equal(4L, entry.CountFor(x => x.Tags));
        }
    }

    [Fact]
    public async Task AnnotationLookup_UsesTheBindersComparer()
    {
        // A camelCase server against a PascalCase model: PropertyNameCaseInsensitive (the client
        // default) binds "tags" to Tags, so the annotation on "tags" must resolve too. The two must
        // not disagree — a case-differing spelling is exactly how the annotation would be lost again.
        const string body = """{"value":[{"Id":1,"Name":"Foo","tags":[],"tags@odata.count":4}]}""";

        (OhDataClient lenient, CannedHandler lenientHandler) = BuildClient(body);
        using (lenient)
        using (lenientHandler)
        {
            var entry = Assert.Single((await lenient.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync()).Entries);
            Assert.Equal(4L, entry.CountFor(x => x.Tags));
        }

        var strict = new OhDataClientOptions
        {
            JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = false },
        };
        (OhDataClient exact, CannedHandler exactHandler) = BuildClient(body, strict);
        using (exact)
        using (exactHandler)
        {
            var entry = Assert.Single((await exact.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync()).Entries);
            Assert.Null(entry.CountFor(x => x.Tags));
            Assert.Equal(4L, entry.Annotations.CountFor("tags"));
        }
    }

    // ── Request shape ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ToAnnotatedPageAsync_DoesNotForceCount_UnlikeToPageAsync()
    {
        const string body = """{"value":[]}""";

        (OhDataClient bare, CannedHandler bareHandler) = BuildClient(body);
        using (bare)
        using (bareHandler)
        {
            await bare.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync();
            Assert.DoesNotContain("$count", bareHandler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
        }

        (OhDataClient counted, CannedHandler countedHandler) = BuildClient(body);
        using (counted)
        using (countedHandler)
        {
            await counted.For<TaggedItem>("TaggedItems").IncludeCount().ToAnnotatedPageAsync();
            Assert.Contains("$count=true", countedHandler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
        }
    }

    // ── Single entity ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAnnotatedAsync_SurfacesNestedAnnotations()
    {
        const string body = """
            {
              "Id": 1, "Name": "Foo",
              "Tags": [ { "Id": 1, "Name": "Red" } ],
              "Tags@odata.count": 57,
              "Tags@odata.nextLink": "TaggedItems(1)/Tags?$skip=1"
            }
            """;
        (OhDataClient client, CannedHandler handler) = BuildClient(body);
        using (client)
        using (handler)
        {
            ODataAnnotatedEntity<TaggedItem>? entry = await client.For<TaggedItem>("TaggedItems")
                .Expand(x => x.Tags)
                .Key(1)
                .GetAnnotatedAsync();

            Assert.NotNull(entry);
            Assert.Equal("Foo", entry!.Entity.Name);
            Assert.Equal(57L, entry.CountFor(x => x.Tags));
            Assert.Equal("TaggedItems(1)/Tags?$skip=1", entry.NextLinkFor(x => x.Tags)!.ToString());
        }
    }

    [Fact]
    public async Task GetAnnotatedAsync_NotFound_FollowsNotFoundBehavior()
    {
        (OhDataClient lenient, CannedHandler lenientHandler) =
            BuildClient("""{"error":{"code":"NotFound","message":"no"}}""", status: HttpStatusCode.NotFound);
        using (lenient)
        using (lenientHandler)
        {
            Assert.Null(await lenient.For<TaggedItem>("TaggedItems").Key(9).GetAnnotatedAsync());
        }

        var throwing = new OhDataClientOptions { NotFoundBehavior = NotFoundBehavior.Throw };
        (OhDataClient strict, CannedHandler strictHandler) =
            BuildClient("""{"error":{"code":"NotFound","message":"no"}}""", throwing, HttpStatusCode.NotFound);
        using (strict)
        using (strictHandler)
        {
            await Assert.ThrowsAsync<ODataClientException>(
                () => strict.For<TaggedItem>("TaggedItems").Key(9).GetAnnotatedAsync());
        }
    }

    // ── Errors ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnnotatedRead_PropagatesServerErrors()
    {
        (OhDataClient client, CannedHandler handler) = BuildClient(
            """{"error":{"code":"UnsupportedQueryOption","message":"nope"}}""",
            status: HttpStatusCode.BadRequest);
        using (client)
        using (handler)
        {
            var ex = await Assert.ThrowsAsync<ODataClientException>(
                () => client.For<TaggedItem>("TaggedItems").ToAnnotatedPageAsync());
            Assert.Equal(400, ex.StatusCode);
        }
    }
}

/// <summary>
/// #313 stage 4, against a live in-process OhData server rather than canned bytes: the client now
/// surfaces the annotations a real server really writes, and the second read it performs to do so
/// does not change how the first one binds. The fixture is the pre-existing NEW-1 one
/// (<see cref="TaggedItemProfile"/>), not a model authored alongside the behaviour it pins.
/// <para>
/// A live <c>{Nav}@odata.count</c> is deliberately NOT asserted here. Measured on this branch: a
/// profile whose <c>GetQueryable</c> returns <c>List&lt;T&gt;.AsQueryable()</c> — the only shape
/// this suite has, which references no EF Core provider — never engages the pushed-expand shaping
/// pass at all, so <c>$expand=Tags($count=true)</c>, <c>($select=…)</c> and <c>($top=…)</c> are all
/// answered <c>200</c> with the nested option silently unapplied. The emission itself is pinned
/// where it can be: <c>MaxExpandTopTests.NestedCount_ChildCountEqualToCeiling_Succeeds_WithExactCount</c>
/// asserts the literal <c>"Books@odata.count":2</c> over the EF/SQLite harness, and the canned-byte
/// gate above reads exactly those bytes back.
/// </para>
/// </summary>
public class NestedAnnotationLiveServerTests : IAsyncDisposable
{
    private readonly TaggedItemClientTestFixture _fixture;
    private OhDataClient Client => _fixture.Client;

    public NestedAnnotationLiveServerTests()
    {
        _fixture = TaggedItemClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EnvelopeAnnotations_FromARealServer_AreVisible()
    {
        ODataAnnotatedPage<TaggedItem> page = await Client.For<TaggedItem>("TaggedItems")
            .Expand(x => x.Tags)
            .ToAnnotatedPageAsync();

        Assert.Equal(2, page.Entries.Count);
        Assert.True(page.Annotations.TryGetValue("@odata.context", out JsonElement context));
        Assert.EndsWith("$metadata#TaggedItems", context.GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnnotatedRead_BindsEntitiesIdenticallyToThePlainRead()
    {
        // The annotated path adds a second read of the SAME bytes; it must not change how the first
        // one binds. Same URL, same options, same entities.
        List<TaggedItem> plain = await Client.For<TaggedItem>("TaggedItems")
            .Expand(x => x.Tags)
            .ToListAsync();

        ODataAnnotatedPage<TaggedItem> annotated = await Client.For<TaggedItem>("TaggedItems")
            .Expand(x => x.Tags)
            .ToAnnotatedPageAsync();

        Assert.Equal(plain.Count, annotated.Items.Count);
        Assert.Equal(
            plain.Select(i => $"{i.Id}|{i.Name}|{string.Join(',', i.Tags.Select(t => $"{t.Id}:{t.Name}"))}"),
            annotated.Items.Select(i => $"{i.Id}|{i.Name}|{string.Join(',', i.Tags.Select(t => $"{t.Id}:{t.Name}"))}"));
    }

}

/// <summary>
/// The single-entity half, live: an OhData server writes <c>@odata.id</c> on every
/// <c>GET /{EntitySet}(key)</c> response, and this client discarded it — the same drop as the
/// nested case, on a route that has nothing to do with <c>$expand</c>.
/// </summary>
public class AnnotatedSingleEntityLiveServerTests : IAsyncDisposable
{
    private readonly ClientTestFixture _fixture;

    public AnnotatedSingleEntityLiveServerTests()
    {
        _fixture = ClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAnnotatedAsync_SurfacesTheEntityIdTheServerWrites()
    {
        ODataAnnotatedEntity<Widget>? entry =
            await _fixture.Client.For<Widget>("Widgets").Key(1).GetAnnotatedAsync();

        Assert.NotNull(entry);
        Assert.Equal("Sprocket", entry!.Entity.Name);
        Assert.True(entry.Annotations.TryGetValue("@odata.id", out JsonElement id));
        Assert.EndsWith("Widgets(1)", id.GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAnnotatedAsync_BindsIdenticallyToGetAsync()
    {
        Widget? plain = await _fixture.Client.For<Widget>("Widgets").Key(1).GetAsync();
        ODataAnnotatedEntity<Widget>? annotated =
            await _fixture.Client.For<Widget>("Widgets").Key(1).GetAnnotatedAsync();

        Assert.NotNull(plain);
        Assert.NotNull(annotated);
        Assert.Equal(plain!.Id, annotated!.Entity.Id);
        Assert.Equal(plain.Name, annotated.Entity.Name);
    }

    [Fact]
    public async Task GetAnnotatedAsync_MissingKey_ReturnsNull()
    {
        Assert.Null(await _fixture.Client.For<Widget>("Widgets").Key(999).GetAnnotatedAsync());
    }
}

/// <summary>
/// The annotated walker follows the collection's own envelope <c>@odata.nextLink</c> exactly as
/// <see cref="EntitySetClient{T}.ToAsyncEnumerable"/> does — server-driven paging of the
/// collection itself is orthogonal to annotations on the entities inside it.
/// </summary>
public class AnnotatedPagingTests : IAsyncDisposable
{
    private readonly PaginatedClientTestFixture _fixture;

    public AnnotatedPagingTests()
    {
        _fixture = PaginatedClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ToAnnotatedAsyncEnumerable_FollowsEnvelopeNextLinks()
    {
        var ids = new List<int>();
        await foreach (ODataAnnotatedEntity<Widget> entry in
            _fixture.Client.For<Widget>("PaginatedWidgets").ToAnnotatedAsyncEnumerable())
        {
            ids.Add(entry.Entity.Id);
            Assert.True(entry.Annotations.IsEmpty);
        }

        // MaxTop = 3 over a 10-item store: four pages, every entity served exactly once, in order.
        Assert.Equal(Enumerable.Range(1, 10), ids);
    }

    [Fact]
    public async Task ToAnnotatedPageAsync_SurfacesTheEnvelopeNextLink()
    {
        ODataAnnotatedPage<Widget> page =
            await _fixture.Client.For<Widget>("PaginatedWidgets").ToAnnotatedPageAsync();

        Assert.Equal(3, page.Entries.Count);
        Assert.NotNull(page.NextLink);
        Assert.True(page.Annotations.TryGetValue("@odata.nextLink", out JsonElement raw));
        // NextLink is a Uri (the annotation surface represents every link that way); OriginalString is
        // what pins it byte-for-byte to the raw annotation the server sent.
        Assert.Equal(raw.GetString(), page.NextLink!.OriginalString);
    }
}
