using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using OhData.Client;

namespace OhData.Client.Tests;

/// <summary>
/// Integration tests for M-7 (NextLink), M-8 (ETag/If-Match), L-10 (Prefer return=minimal),
/// and L-11 (SingleOrDefaultAsync).
/// Each test class manages its own fixture so ETag store state is isolated.
/// </summary>
public class ETagClientIntegrationTests : IAsyncDisposable
{
    private readonly ETagClientTestFixture _fixture;
    private OhDataClient Client => _fixture.Client;

    public ETagClientIntegrationTests()
    {
        _fixture = ETagClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    // ── M-8: ETag GET ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWithETagAsync_ReturnsETagHeader()
    {
        var (entity, etag) = await Client.For<Widget>("ETagWidgets").Key(1).GetWithETagAsync();
        Assert.NotNull(entity);
        Assert.NotNull(etag);
        Assert.NotEmpty(etag!);
    }

    [Fact]
    public async Task GetWithETagAsync_MissingKey_ReturnsNullPair()
    {
        var (entity, etag) = await Client.For<Widget>("ETagWidgets").Key(999).GetWithETagAsync();
        Assert.Null(entity);
        Assert.Null(etag);
    }

    // ── M-8: ETag PUT ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_WithCorrectETag_Succeeds()
    {
        var (_, etag) = await Client.For<Widget>("ETagWidgets").Key(1).GetWithETagAsync();
        Assert.NotNull(etag);

        var updated = await Client.For<Widget>("ETagWidgets").Key(1)
            .PutAsync(new Widget { Id = 1, Name = "Updated" }, ifMatch: etag);
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated!.Name);
    }

    [Fact]
    public async Task PutAsync_WithWrongETag_Throws412()
    {
        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => Client.For<Widget>("ETagWidgets").Key(1)
                .PutAsync(new Widget { Id = 1, Name = "BadUpdate" }, ifMatch: "wrongetag"));
        Assert.Equal(412, ex.StatusCode);
    }

    // ── M-8: ETag DELETE ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_WithCorrectETag_Succeeds()
    {
        // Insert a fresh widget so we don't interfere with other tests.
        var created = await Client.For<Widget>("ETagWidgets")
            .InsertAsync(new Widget { Name = "ToDeleteWithETag" });
        Assert.NotNull(created);

        var (_, etag) = await Client.For<Widget>("ETagWidgets").Key(created!.Id).GetWithETagAsync();
        Assert.NotNull(etag);

        await Client.For<Widget>("ETagWidgets").Key(created.Id).DeleteAsync(ifMatch: etag);

        var after = await Client.For<Widget>("ETagWidgets").Key(created.Id).GetAsync();
        Assert.Null(after);
    }

    [Fact]
    public async Task DeleteAsync_WithWrongETag_Throws412()
    {
        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => Client.For<Widget>("ETagWidgets").Key(1).DeleteAsync(ifMatch: "wrongetag"));
        Assert.Equal(412, ex.StatusCode);
    }

    // ── L-10: Prefer return=minimal ──────────────────────────────────────────────

    [Fact]
    public async Task InsertAsync_PreferMinimal_Returns204AndNull()
    {
        var result = await Client.For<Widget>("ETagWidgets")
            .InsertAsync(new Widget { Name = "MinimalInsert" }, preferMinimal: true);
        Assert.Null(result);
    }

    [Fact]
    public async Task PutAsync_PreferMinimal_Returns204AndNull()
    {
        var result = await Client.For<Widget>("ETagWidgets").Key(1)
            .PutAsync(new Widget { Id = 1, Name = "MinimalPut" }, preferMinimal: true);
        Assert.Null(result);
    }
}

public class NextLinkIntegrationTests : IAsyncDisposable
{
    private readonly PaginatedClientTestFixture _fixture;
    private OhDataClient Client => _fixture.Client;

    public NextLinkIntegrationTests()
    {
        _fixture = PaginatedClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    // ── M-7: NextLink ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToPageAsync_WithNextLink_NextLinkPopulated()
    {
        // PaginatedWidgetProfile has MaxTop=3 and 10 items.
        // Calling ToPageAsync() without $top should get 3 items and a nextLink.
        var page = await Client.For<Widget>("PaginatedWidgets").ToPageAsync();
        Assert.Equal(3, page.Items.Count);
        Assert.NotNull(page.NextLink);
        Assert.NotEmpty(page.NextLink!);
    }

    [Fact]
    public async Task ToPageAsync_LastPage_NextLinkIsNull()
    {
        // Skip past the last full page — fewer than MaxTop items remain, so no nextLink.
        var page = await Client.For<Widget>("PaginatedWidgets").Skip(9).ToPageAsync();
        Assert.Single(page.Items);
        Assert.Null(page.NextLink);
    }

    // ── ToAsyncEnumerable ────────────────────────────────────────────────────────

    [Fact]
    public async Task ToAsyncEnumerable_YieldsAllItemsAcrossPages()
    {
        // PaginatedWidgetProfile has MaxTop=3 and 10 items.
        // ToAsyncEnumerable should follow all nextLinks and yield all 10 items.
        var items = new List<Widget>();
        await foreach (Widget w in Client.For<Widget>("PaginatedWidgets").ToAsyncEnumerable())
            items.Add(w);

        Assert.Equal(10, items.Count);
    }

    [Fact]
    public async Task ToAsyncEnumerable_WithFilter_YieldsFilteredItems()
    {
        // Filter for widgets with Id <= 5 — should yield only 5 items across pages.
        var items = new List<Widget>();
        await foreach (Widget w in Client.For<Widget>("PaginatedWidgets")
            .Filter(x => x.Id <= 5)
            .ToAsyncEnumerable())
        {
            items.Add(w);
        }

        Assert.Equal(5, items.Count);
        Assert.All(items, w => Assert.True(w.Id <= 5));
    }

    [Fact]
    public async Task ToListAsync_YieldsAllPages()
    {
        // Regression: ToListAsync should follow nextLinks after the refactor to use ToAsyncEnumerable.
        var items = await Client.For<Widget>("PaginatedWidgets").ToListAsync();
        Assert.Equal(10, items.Count);
    }
}

public class SingleOrDefaultIntegrationTests : IAsyncDisposable
{
    private readonly ClientTestFixture _fixture;
    private OhDataClient Client => _fixture.Client;

    public SingleOrDefaultIntegrationTests()
    {
        _fixture = ClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    // ── L-11: SingleOrDefaultAsync ───────────────────────────────────────────────

    [Fact]
    public async Task SingleOrDefaultAsync_OneMatch_ReturnsEntity()
    {
        var widget = await Client.For<Widget>()
            .Filter(x => x.Name == "Sprocket")
            .SingleOrDefaultAsync();
        Assert.NotNull(widget);
        Assert.Equal("Sprocket", widget!.Name);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_NoMatch_ReturnsNull()
    {
        var widget = await Client.For<Widget>()
            .Filter(x => x.Name == "DoesNotExist")
            .SingleOrDefaultAsync();
        Assert.Null(widget);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_MultipleMatches_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client.For<Widget>().SingleOrDefaultAsync());
    }
}

/// <summary>
/// Integration tests: spins up a real OhData server via TestHost and exercises
/// the full round-trip through OhDataClient → OData HTTP → server → deserialisation.
/// </summary>
public class OhDataClientIntegrationTests : IAsyncDisposable
{
    private readonly ClientTestFixture _fixture;
    private OhDataClient Client => _fixture.Client;

    public OhDataClientIntegrationTests()
    {
        _fixture = ClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    // ── GET collection ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ToListAsync_ReturnsAllWidgets()
    {
        var widgets = await Client.For<Widget>().ToListAsync();
        Assert.Equal(2, widgets.Count);
        Assert.Contains(widgets, w => w.Name == "Sprocket");
        Assert.Contains(widgets, w => w.Name == "Cog");
    }

    [Fact]
    public async Task Filter_ByName_ReturnsMatchingWidget()
    {
        var widgets = await Client.For<Widget>()
            .Filter(x => x.Name == "Sprocket")
            .ToListAsync();
        Assert.Single(widgets);
        Assert.Equal("Sprocket", widgets[0].Name);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_ReturnsFirst()
    {
        var widget = await Client.For<Widget>().FirstOrDefaultAsync();
        Assert.NotNull(widget);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_NoMatch_ReturnsNull()
    {
        var widget = await Client.For<Widget>()
            .Filter(x => x.Name == "DoesNotExist")
            .FirstOrDefaultAsync();
        Assert.Null(widget);
    }

    // ── GET $count ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountAsync_ReturnsTotal()
    {
        long count = await Client.For<Widget>().CountAsync();
        Assert.Equal(2, count);
    }

    // ── GET single ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Key_GetAsync_ExistingKey_ReturnsWidget()
    {
        var widget = await Client.For<Widget>().Key(1).GetAsync();
        Assert.NotNull(widget);
        Assert.Equal(1, widget!.Id);
        Assert.Equal("Sprocket", widget.Name);
    }

    [Fact]
    public async Task Key_GetAsync_MissingKey_ReturnsNull()
    {
        var widget = await Client.For<Widget>().Key(999).GetAsync();
        Assert.Null(widget);
    }

    // ── POST ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAsync_ReturnsCreatedWidget()
    {
        var created = await Client.For<Widget>()
            .InsertAsync(new Widget { Name = "NewWidget" });
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Equal("NewWidget", created.Name);
    }

    // ── PUT ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_ReplacesWidget()
    {
        var updated = await Client.For<Widget>().Key(1)
            .PutAsync(new Widget { Id = 1, Name = "Sprocket-Replaced" });
        Assert.NotNull(updated);
        Assert.Equal("Sprocket-Replaced", updated!.Name);
    }

    // ── PATCH ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PatchAsync_PartialUpdate()
    {
        var patched = await Client.For<Widget>().Key(2)
            .PatchAsync(new { Name = "Cog-Patched" });
        Assert.NotNull(patched);
        Assert.Equal("Cog-Patched", patched!.Name);
        Assert.Equal(2, patched!.Id);
    }

    // ── DELETE ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingKey_Succeeds()
    {
        var created = await Client.For<Widget>()
            .InsertAsync(new Widget { Name = "ToDelete" });
        Assert.NotNull(created);

        await Client.For<Widget>().Key(created!.Id).DeleteAsync();

        var after = await Client.For<Widget>().Key(created.Id).GetAsync();
        Assert.Null(after);
    }

    // ── PUT/PATCH missing key ────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_MissingKey_ThrowsODataClientExceptionWith404()
    {
        var created = await Client.For<Widget>()
            .InsertAsync(new Widget { Name = "ToDelete" });
        Assert.NotNull(created);
        await Client.For<Widget>().Key(created!.Id).DeleteAsync();

        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => Client.For<Widget>().Key(created.Id)
                .PutAsync(new Widget { Id = created.Id, Name = "Ghost" }));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task PatchAsync_MissingKey_ThrowsODataClientExceptionWith404()
    {
        var created = await Client.For<Widget>()
            .InsertAsync(new Widget { Name = "ToDelete2" });
        Assert.NotNull(created);
        await Client.For<Widget>().Key(created!.Id).DeleteAsync();

        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => Client.For<Widget>().Key(created.Id)
                .PatchAsync(new { Name = "Ghost" }));
        Assert.Equal(404, ex.StatusCode);
    }

    // ── Error handling ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_MissingKey_ThrowsODataClientException()
    {
        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => Client.For<Widget>().Key(99999).DeleteAsync());
        Assert.Equal(404, ex.StatusCode);
    }

    // ── Entity set name convention ───────────────────────────────────────────────

    [Fact]
    public async Task For_UsesConventionalPluralisation()
    {
        // Widget → Widgets — server registers entity set as "Widgets"
        var widgets = await Client.For<Widget>().ToListAsync();
        Assert.Equal(2, widgets.Count);
    }

    // ── C6: ToPageAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ToPageAsync_ReturnsBothItemsAndTotalCount()
    {
        var page = await Client.For<Widget>().ToPageAsync();
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2L, page.TotalCount);
    }

    [Fact]
    public async Task ToPageAsync_WithTop_ItemsSubsetButCountIsTotal()
    {
        var page = await Client.For<Widget>().Top(1).ToPageAsync();
        Assert.Single(page.Items);
        // Total count reflects all matches, not the page size
        Assert.Equal(2L, page.TotalCount);
    }

    [Fact]
    public async Task IncludeCount_ToListAsync_DoesNotBreak()
    {
        // IncludeCount() affects URL but ToListAsync() only reads the value array —
        // the presence of @odata.count in the response should not cause issues.
        var items = await Client.For<Widget>().IncludeCount().ToListAsync();
        Assert.Equal(2, items.Count);
    }

    // ── AnyAsync ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnyAsync_NonEmptyCollection_ReturnsTrue()
    {
        bool any = await Client.For<Widget>().AnyAsync();
        Assert.True(any);
    }

    [Fact]
    public async Task AnyAsync_NoMatch_ReturnsFalse()
    {
        bool any = await Client.For<Widget>()
            .Filter(x => x.Name == "DoesNotExist")
            .AnyAsync();
        Assert.False(any);
    }

    // ── $orderby execution ───────────────────────────────────────────────────────

    [Fact]
    public async Task OrderBy_Name_Ascending_ReturnsCogFirst()
    {
        var widgets = await Client.For<Widget>().OrderBy(x => x.Name).ToListAsync();
        Assert.Equal(2, widgets.Count);
        Assert.Equal("Cog", widgets[0].Name);
        Assert.Equal("Sprocket", widgets[1].Name);
    }

    [Fact]
    public async Task OrderByDescending_Name_ReturnsSprocketFirst()
    {
        var widgets = await Client.For<Widget>().OrderByDescending(x => x.Name).ToListAsync();
        Assert.Equal(2, widgets.Count);
        Assert.Equal("Sprocket", widgets[0].Name);
        Assert.Equal("Cog", widgets[1].Name);
    }

    // ── $select execution ────────────────────────────────────────────────────────

    [Fact]
    public async Task Select_SingleProperty_NonSelectedFieldIsDefault()
    {
        // $select=Name: server returns only the name field; Id deserialises as 0 (default).
        var widgets = await Client.For<Widget>().Select(x => x.Name).ToListAsync();
        Assert.Equal(2, widgets.Count);
        Assert.All(widgets, w => Assert.Equal(0, w.Id));
        Assert.Contains(widgets, w => w.Name == "Sprocket");
        Assert.Contains(widgets, w => w.Name == "Cog");
    }

    [Fact]
    public async Task Select_AllProperties_ReturnsFullEntities()
    {
        var widgets = await Client.For<Widget>().Select(x => new { x.Id, x.Name }).ToListAsync();
        Assert.Equal(2, widgets.Count);
        Assert.All(widgets, w => Assert.True(w.Id > 0));
    }

    // ── Argument validation ───────────────────────────────────────────────────────

    [Fact]
    public void OhDataClient_NullBaseAddress_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OhDataClient((string)null!));
    }

    [Fact]
    public void OhDataClient_EmptyBaseAddress_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new OhDataClient(""));
    }

    [Fact]
    public void Top_Negative_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Client.For<Widget>().Top(-1));
    }

    [Fact]
    public void Skip_Negative_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Client.For<Widget>().Skip(-1));
    }
}
