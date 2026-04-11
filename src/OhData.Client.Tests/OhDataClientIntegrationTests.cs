using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// Integration tests: spins up a real OhData server via TestHost and exercises
/// the full round-trip through OhDataClient → OData HTTP → server → deserialisation.
/// </summary>
public class OhDataClientIntegrationTests : IAsyncDisposable
{
    private readonly ClientTestFixture _fixture;
    private          OhDataClient      Client => _fixture.Client;

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
        var count = await Client.For<Widget>().CountAsync();
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
        Assert.True(created.Id > 0);
        Assert.Equal("NewWidget", created.Name);
    }

    // ── PUT ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_ReplacesWidget()
    {
        var updated = await Client.For<Widget>().Key(1)
            .PutAsync(new Widget { Id = 1, Name = "Sprocket-Replaced" });
        Assert.Equal("Sprocket-Replaced", updated.Name);
    }

    // ── PATCH ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PatchAsync_PartialUpdate()
    {
        var patched = await Client.For<Widget>().Key(2)
            .PatchAsync(new { Name = "Cog-Patched" });
        Assert.Equal("Cog-Patched", patched.Name);
        Assert.Equal(2, patched.Id);
    }

    // ── DELETE ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingKey_Succeeds()
    {
        var created = await Client.For<Widget>()
            .InsertAsync(new Widget { Name = "ToDelete" });

        await Client.For<Widget>().Key(created.Id).DeleteAsync();

        var after = await Client.For<Widget>().Key(created.Id).GetAsync();
        Assert.Null(after);
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
}
