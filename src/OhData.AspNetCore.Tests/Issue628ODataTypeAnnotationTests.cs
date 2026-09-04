using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #628 — a derived-type instance carries <c>@odata.type</c>.
/// <para>
/// JSON Format §4.5.3: <i>"The odata.type annotation MUST appear in minimal or full metadata if the
/// type cannot be heuristically determined … and one of the following is true: The type is derived
/// from the type specified for the (collection of) entities …"</i>. Identical in 4.0 and 4.01 — both
/// were checked, because #372 was exactly a 4.0-vs-4.01 difference.
/// </para>
/// <para>
/// Measured on the live demo before the fix: <c>GET /v2/Awards</c> served
/// <c>{"Ceremony":"67th Academy Awards","IsWinner":true,"Id":1,…}</c> with no annotation, while
/// <c>$metadata</c> correctly declared <c>AcademyAward BaseType="…Award"</c> — the server knew and
/// did not say, so a conforming client deserializes into the base and drops the derived members.
/// </para>
/// <para>
/// Nothing pinned the absence, and nothing could: <c>@odata.type</c> is required only when runtime
/// type != declared type, and no fixture in the repo had a polymorphic entity set until #618. The
/// whole suite stayed green through the defect.
/// </para>
/// </summary>
public sealed class Issue628ODataTypeAnnotationTests
{
    private const string DerivedType = "#OhData.AspNetCore.Tests.P529Derived";

    private static async Task<TestFixture> BuildAsync(SqliteConnection connection)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<P529BaseProfile>(),
            configureServices: s => s.AddDbContext<P529DbContext>(o => o.UseSqlite(connection)));

        using IServiceScope scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<P529DbContext>();
        db.Database.EnsureCreated();
        db.Things.Add(new P529Base { Id = 1, Name = "base" });
        db.Things.Add(new P529Derived { Id = 2, Name = "derived", Extra = "EXTRA", Rank = 7 });
        db.Children.Add(new P529Child { Id = 20, BaseId = 2, Body = "c2" });
        db.SaveChanges();
        return fx;
    }

    private static async Task<JsonElement> GetAsync(TestFixture fx, string url)
    {
        var response = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Row(JsonElement body, int id) =>
        body.GetProperty("value").EnumerateArray().Single(e => e.GetProperty("Id").GetInt32() == id);

    [Fact]
    public async Task ADerivedRowInACollectionCarriesTheAnnotation()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement body = await GetAsync(fx, "/odata/P529Things");

        Assert.Equal(DerivedType, Row(body, 2).GetProperty("@odata.type").GetString());
    }

    [Fact]
    public async Task ABaseRowDoesNot()
    {
        // The other half of §4.5.3: the annotation is required when the type IS derived from the
        // declared one. Emitting it unconditionally would be noise on every row of every payload.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement body = await GetAsync(fx, "/odata/P529Things");

        Assert.False(Row(body, 1).TryGetProperty("@odata.type", out _));
    }

    [Fact]
    public async Task TheSingleEntityReadCarriesItToo()
    {
        // GetById serializes through a different path (SerializeBounded directly, not the batched
        // collection one), so it needs its own assertion rather than riding on the collection case.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement entity = await GetAsync(fx, "/odata/P529Things(2)");

        Assert.Equal(DerivedType, entity.GetProperty("@odata.type").GetString());
    }

    [Fact]
    public async Task ItSurvivesAnExpand()
    {
        // $expand routes a polymorphic root through the Include path (#529), which is a different
        // query shape again -- and the collection annotation loop deliberately runs BEFORE the
        // "nothing to splice" fast path, so it cannot become $expand-only or $expand-never.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement body = await GetAsync(fx, "/odata/P529Things?$expand=Children");

        Assert.Equal(DerivedType, Row(body, 2).GetProperty("@odata.type").GetString());
    }

    [Fact]
    public async Task ItSurvivesASelectProjection()
    {
        // $select trims PROPERTIES. @odata.type is control information, not a property, so a
        // projection must not strip it -- otherwise the annotation would be present exactly when the
        // client did not narrow the payload, which is backwards.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement body = await GetAsync(fx, "/odata/P529Things?$select=Id");

        Assert.Equal(DerivedType, Row(body, 2).GetProperty("@odata.type").GetString());
        Assert.False(Row(body, 1).TryGetProperty("@odata.type", out _));
    }

    [Fact]
    public async Task ANonPolymorphicSetIsUntouched()
    {
        // The control, and the cost argument: a model with no derived types must emit no annotation
        // anywhere. The detection piggy-backs the existing distinct-runtime-type walk, so this case
        // pays one cached EDM lookup for the single type present and nothing per element.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        string body = await (await fx.Client.GetAsync("/odata/Widgets")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("@odata.type", body, StringComparison.Ordinal);
    }
}
