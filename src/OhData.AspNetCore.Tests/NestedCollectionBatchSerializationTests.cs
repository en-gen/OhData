using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #337: SerializeBounded's NESTED collection branch used to call JsonSerializer.SerializeToNode once
// per element; it now routes the whole homogeneous sibling set through SerializeBoundedCollection's
// single batched call (SerializeBoundedCollection's own fast path was gated on "the clause keeps NO
// navigation", which no $expand request can ever satisfy, so batching had never applied below the
// root page). This suite pins the shapes where a batched serialize could silently diverge from the
// per-element one it replaces:
//
//   * POLYMORPHIC elements — the highest-risk shape. The per-element call passed the element's
//     RUNTIME type explicitly. Batching must keep runtime-type dispatch (which it does by handing
//     System.Text.Json a List<object?>, whose `object` element type makes STJ resolve each element's
//     own JsonTypeInfo); handing STJ the concrete List<TBase> instead would serialize by DECLARED
//     type and silently drop every derived member.
//   * INDEX ALIGNMENT — the batched array is spliced by pairing batched[i] with values[i].
//   * NULL elements inside a collection navigation, and empty/single-element collections.
//   * [JsonPropertyName] renames and [JsonIgnore] on entities reached only through the nested level.

public class NcbTask
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

public class NcbPerson
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    [JsonIgnore] public string Secret { get; set; } = "must-never-serialize";
    public List<NcbTask> Tasks { get; set; } = new();
}

// Derived entity placed inside a base-typed collection navigation.
public sealed class NcbManager : NcbPerson
{
    public string Division { get; set; } = "";
    public int Reports { get; set; }
}

public class NcbNote
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
}

public class NcbCompany
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("staff")] public List<NcbPerson> Employees { get; set; } = new();
    public List<NcbNote> Notes { get; set; } = new();
}

public sealed class NcbCompanyProfile : EntitySetProfile<int, NcbCompany>
{
    private static readonly List<NcbCompany> Store = BuildStore();

    private static List<NcbCompany> BuildStore() => new()
    {
        new NcbCompany
        {
            Id = 1,
            Name = "Acme",
            Employees =
            {
                // Heterogeneous per-element task counts (1 / 0 / 2) so a misaligned splice shows up
                // as the wrong tasks on the wrong person rather than passing by coincidence.
                new NcbPerson { Id = 1, FullName = "Ann", Tasks = { new NcbTask { Id = 1, Title = "T1" } } },
                new NcbPerson { Id = 2, FullName = "Cyd" },
                new NcbManager
                {
                    Id = 3, FullName = "Bob", Division = "Ops", Reports = 4,
                    Tasks = { new NcbTask { Id = 2, Title = "T2" }, new NcbTask { Id = 3, Title = "T3" } },
                },
            },
            Notes = { new NcbNote { Id = 1, Body = "n1" } }, // single-element nested collection
        },
        new NcbCompany
        {
            Id = 2,
            Name = "Beta",
            // null element inside a collection navigation
            Employees = new List<NcbPerson> { null!, new NcbPerson { Id = 4, FullName = "Dee" } },
            Notes = new List<NcbNote>(),                    // empty nested collection
        },
    };

    public NcbCompanyProfile() : base(x => x.Id)
    {
        EntitySetName = "NcbCompanies";
        ExpandEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        GetQueryable = () => Store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(Store.FirstOrDefault(c => c.Id == id));
        HasMany(x => x.Employees!);
        HasMany(x => x.Notes!);
    }
}

public sealed class NestedCollectionBatchSerializationTests
{
    private static Task<TestFixture> BuildAsync() =>
        TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<NcbCompanyProfile>());

    private static async Task<JsonArray> GetValueAsync(TestFixture fx, string url)
    {
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonNode root = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return root["value"]!.AsArray();
    }

    [Fact]
    public async Task DerivedElementInBaseTypedNavCollection_KeepsItsOwnMembers()
    {
        await using TestFixture fx = await BuildAsync();
        JsonArray value = await GetValueAsync(fx, "/odata/NcbCompanies?$orderby=Id&$expand=staff");

        JsonArray staff = value[0]!["staff"]!.AsArray();
        JsonObject bob = staff[2]!.AsObject();

        // Batching by DECLARED element type (NcbPerson) would drop both of these.
        Assert.Equal("Ops", (string?)bob["Division"]);
        Assert.Equal(4, (int?)bob["Reports"]);
        Assert.Equal("Bob", (string?)bob["FullName"]);

        // ...and must not add them to the non-derived siblings.
        Assert.False(staff[0]!.AsObject().ContainsKey("Division"));
        Assert.False(staff[1]!.AsObject().ContainsKey("Division"));
    }

    [Fact]
    public async Task NestedCollectionSplice_IsIndexAligned()
    {
        await using TestFixture fx = await BuildAsync();
        JsonArray value = await GetValueAsync(fx, "/odata/NcbCompanies?$orderby=Id&$expand=staff($expand=Tasks)");

        JsonArray staff = value[0]!["staff"]!.AsArray();
        Assert.Equal("Ann", (string?)staff[0]!["FullName"]);
        Assert.Equal("Cyd", (string?)staff[1]!["FullName"]);
        Assert.Equal("Bob", (string?)staff[2]!["FullName"]);

        Assert.Equal(new[] { "T1" }, staff[0]!["Tasks"]!.AsArray().Select(t => (string?)t!["Title"]));
        Assert.Empty(staff[1]!["Tasks"]!.AsArray());
    }

    [Fact]
    public async Task NullElementInNavCollection_SerializesAsJsonNull()
    {
        await using TestFixture fx = await BuildAsync();
        JsonArray value = await GetValueAsync(fx, "/odata/NcbCompanies?$orderby=Id&$expand=staff");

        JsonArray staff = value[1]!["staff"]!.AsArray();
        Assert.Equal(2, staff.Count);
        Assert.Null(staff[0]);
        Assert.Equal("Dee", (string?)staff[1]!["FullName"]);
    }

    [Fact]
    public async Task EmptyAndSingleElementNestedCollections_KeepTheirShape()
    {
        await using TestFixture fx = await BuildAsync();
        JsonArray value = await GetValueAsync(fx, "/odata/NcbCompanies?$orderby=Id&$expand=Notes");

        Assert.Single(value[0]!["Notes"]!.AsArray());
        Assert.Empty(value[1]!["Notes"]!.AsArray());
    }

    [Fact]
    public async Task RenamedNavigation_AndIgnoredProperty_SurviveBatchedNestedSerialization()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NcbCompanies?$orderby=Id&$expand=staff($expand=Tasks)");
        string body = await resp.Content.ReadAsStringAsync();

        // [JsonPropertyName] rename is emitted verbatim; the CLR name never appears.
        Assert.Contains("\"staff\":", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Employees", body, System.StringComparison.Ordinal);
        // [JsonIgnore] on an entity reached ONLY through the nested (batched) level.
        Assert.DoesNotContain("must-never-serialize", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", body, System.StringComparison.Ordinal);
    }
}
