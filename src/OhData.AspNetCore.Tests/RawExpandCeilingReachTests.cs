using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #464: MaxExpandTop's XML doc said it bounds "every collection $expand level" once set. It bounded
// one — the EF-pushed one.
//
// MECHANISM. The collection-route ceiling and its #313 continuation link both live behind
// ShapePushedExpandsInJson, and that pass runs only when `engagedExpandNavs` is non-empty, which
// requires `ResolveEfCoreAssembly(filtered)` to have found EF Core. On a non-EF IQueryable, on
// GetAll, and on Priority-1 it returns null, so the pass never ran and the configured DoS bound
// silently did not exist. Measured on the pre-fix tree, cap = 2 with five books:
//
//   GET /BeAuthorsMem?$expand=Books          -> 200, all five   (the ceiling was never applied)
//   GET /BeAuthorsMem?$expand=Books($top=1)  -> 200, all five   (#413's confirmation cell: the
//                                                                in-ceiling $top is accepted and
//                                                                then not applied either)
//   GET /BeAuthorsAll?$expand=Books          -> 200, all five
//   GET /BeAuthorsP1?$expand=Books           -> 200, all five
//
// A knob whose whole purpose is to bound an unbounded fetch, absent on three of the five read paths,
// with a doc claiming otherwise. Now bounded by EnforceRawExpandCeiling — a 400, for the same reason
// #418 gave the single-entity read a 400: the framework composed neither side of the order here, so
// a $skip continuation link over these rows would silently skip and duplicate across the boundary.
//
// FIXTURE PROVENANCE: BeAuthor / BeBook / BeChapter / BareExpandDbContext / BareExpandSqliteHarness
// are #313 stage 2's, seeded exactly as they always were (author 1, five books). What is new is
// three profiles over the same model exposing the three unbounded paths, added through the harness's
// existing `configureExtraProfiles` hook.

/// <summary>#464: a GetQueryable whose IQueryable is NOT EF-backed — the pushdown planner is skipped.</summary>
public sealed class BeAuthorMemoryProfile : EntitySetProfile<int, BeAuthor>
{
    public BeAuthorMemoryProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeAuthorsMem";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(
            db.Authors.Include(a => a.Books).ToList().AsQueryable());
        HasMany(x => x.Books);
    }
}

/// <summary>#464: the GetAll path — an in-memory enumeration with no IQueryable at all.</summary>
public sealed class BeAuthorGetAllProfile : EntitySetProfile<int, BeAuthor>
{
    public BeAuthorGetAllProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeAuthorsAll";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetAll = ct => Task.FromResult<IEnumerable<BeAuthor>>(
            db.Authors.Include(a => a.Books).ToList());
        HasMany(x => x.Books);
    }
}

/// <summary>#464: the Priority-1 path — the profile owns query application.</summary>
public sealed class BeAuthorODataProfile : ODataEntitySetProfile<int, BeAuthor>
{
    public BeAuthorODataProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeAuthorsP1";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetODataQueryable = (options, ct) => Task.FromResult(
            new ODataQueryResult<BeAuthor> { Items = db.Authors.Include(a => a.Books).ToList().AsQueryable() });
        HasMany(x => x.Books);
    }
}

public sealed class RawExpandCeilingReachTests
{
    private static async Task<(TestFixture Fixture, SqliteConnection Connection)> BuildAsync(
        int? cap, bool paging)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink: null,
            defaults: d =>
            {
                d.MaxExpandTop = cap;
                d.ExpandPagingEnabled = paging;
            },
            configureExtraProfiles: b =>
            {
                b.AddEntitySetProfile<BeAuthorMemoryProfile>();
                b.AddEntitySetProfile<BeAuthorGetAllProfile>();
                b.AddEntitySetProfile<BeAuthorODataProfile>();
            });
        return (fx, connection);
    }

    // ── The bug itself ───────────────────────────────────────────────────────────────────────────

    // FAILS WITHOUT THE FIX: 200 with all five books on every one of the three sets.
    [Theory]
    [InlineData("BeAuthorsMem")] // non-EF GetQueryable
    [InlineData("BeAuthorsAll")] // GetAll
    [InlineData("BeAuthorsP1")]  // Priority-1
    public async Task BareExpand_OverCeiling_Is400_OnEveryNonPushdownCollectionPath(string set)
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/{set}?$expand=Books");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement error = doc.RootElement.GetProperty("error");
            Assert.Equal("InvalidQueryOption", error.GetProperty("code").GetString());
            string message = error.GetProperty("message").GetString()!;
            Assert.Contains("'Books'", message);
            Assert.Contains("maximum of 2 entities", message);
            // The remediation must not repeat the pushed path's "Narrow it with a nested $filter":
            // a nested $filter is one of the options this substrate silently ignores, so following
            // that advice returns the same 400.
            Assert.DoesNotContain("Narrow it with a nested $filter", message);
            Assert.Contains("not pushed down", message);
        }
        conn.Dispose();
    }

    // #413's confirmation cell, now settled in the same direction as the bare shape: an IN-CEILING
    // nested $top was accepted and then not applied, so the response was the whole collection under
    // a 200. It is now the same 400 — the option axis cannot be used to walk around the bound.
    // FAILS WITHOUT THE FIX: 200 with all five books.
    [Theory]
    [InlineData("BeAuthorsMem", "$expand=Books($top=1)")]
    [InlineData("BeAuthorsAll", "$expand=Books($top=1)")]
    [InlineData("BeAuthorsP1", "$expand=Books($top=1)")]
    [InlineData("BeAuthorsMem", "$expand=Books($select=Title)")]
    [InlineData("BeAuthorsAll", "$expand=Books($count=true)")]
    [InlineData("BeAuthorsP1", "$select=Name&$expand=Books")]
    public async Task OverCeiling_IsNotBypassableByAddingANestedOption(string set, string query)
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/{set}?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        conn.Dispose();
    }

    // ── The contrast anchor: the EF path is untouched ────────────────────────────────────────────

    // The SAME registration, the SAME ceiling, the pushed path — which composes both sides of the
    // order and therefore really can page. Green before and after: EnforceRawExpandCeiling skips
    // every navigation the pushdown engaged, precisely so it cannot 400 a request that was about to
    // be handed a continuation link.
    [Fact]
    public async Task EfPushdownPath_StillTrimsAndLinks()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync("/odata/BeAuthors?$filter=Id eq 1&$expand=Books");
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement parent = doc.RootElement.GetProperty("value")[0];

            Assert.Equal(2, parent.GetProperty("Books").GetArrayLength());
            Assert.Equal(
                "http://localhost/odata/BeAuthors(1)/Books?$skip=2",
                parent.GetProperty("Books@odata.nextLink").GetString());
        }
        conn.Dispose();
    }

    // With paging off, the pushed path takes its own 400 with its own message — proving the two
    // ceilings stay distinct rather than one shadowing the other.
    [Fact]
    public async Task EfPushdownPath_WithoutPaging_KeepsItsOwnMessage()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: false);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$expand=Books");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Contains(
                "Narrow it with a nested $filter",
                doc.RootElement.GetProperty("error").GetProperty("message").GetString()!);
        }
        conn.Dispose();
    }

    // ── Byte-identity: shapes that must not move ─────────────────────────────────────────────────

    // MaxExpandTop = null is the SHIPPING DEFAULT. The whole pass must be inert there, or this fix
    // becomes a main-read-path 200 -> 400 for every existing application.
    [Theory]
    [InlineData("BeAuthorsMem")]
    [InlineData("BeAuthorsAll")]
    [InlineData("BeAuthorsP1")]
    public async Task ByteIdentical_NoCeilingConfigured_IsUntouched(string set)
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: null, paging: false);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync($"/odata/{set}?$expand=Books");
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement author = doc.RootElement.GetProperty("value")[0];
            Assert.Equal(5, author.GetProperty("Books").GetArrayLength());
            Assert.DoesNotContain("@odata.nextLink", body);
        }
        conn.Dispose();
    }

    // Under the ceiling nothing moves either.
    [Theory]
    [InlineData("BeAuthorsMem")]
    [InlineData("BeAuthorsAll")]
    [InlineData("BeAuthorsP1")]
    public async Task ByteIdentical_UnderCeiling_IsUntouched(string set)
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 10, paging: true);
        await using (fx)
        {
            using JsonDocument doc = JsonDocument.Parse(
                await fx.Client.GetStringAsync($"/odata/{set}?$expand=Books"));
            Assert.Equal(5, doc.RootElement.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        }
        conn.Dispose();
    }

    // A single-valued navigation holds at most one related entity; the ceiling must never reject it.
    [Theory]
    [InlineData("BeAuthorsMem")]
    [InlineData("BeAuthorsAll")]
    [InlineData("BeAuthorsP1")]
    public async Task SingleValuedNavigation_IsNeverBounded(string set)
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 1, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/{set}?$expand=Publisher");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        conn.Dispose();
    }

    // ── Delegate safety (#313 O6): the framework does not truncate — or reject — a delegate's answer
    //
    // The ceiling reaches the RAW substrate, which is exactly the substrate the framework is
    // responsible for. A delegate-backed navigation's rows are the developer's own answer, and a 400
    // here would be the same weakening #313 O6 refused, arriving by another route.
    [Fact]
    public async Task DelegateBackedNavigation_IsNotBounded_OnANonPushdownPath()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink: null,
            defaults: d => { d.MaxExpandTop = 2; d.ExpandPagingEnabled = true; },
            configureExtraProfiles: b => b.AddEntitySetProfile<BeAuthorDelegateGetAllProfile>());
        await using (fx)
        {
            using JsonDocument doc = JsonDocument.Parse(
                await fx.Client.GetStringAsync("/odata/BeAuthorsDlgAll?$expand=Books"));
            Assert.Equal(5, doc.RootElement.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        }
        connection.Dispose();
    }
}

/// <summary>
/// #464 / #313 O6: GetAll with Books declared WITH a delegate. Registered on its own — a sibling
/// declaring the same navigation delegate-less would make ResolveNavTreatment answer Blank for both
/// and the test would prove nothing.
/// </summary>
public sealed class BeAuthorDelegateGetAllProfile : EntitySetProfile<int, BeAuthor>
{
    public BeAuthorDelegateGetAllProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeAuthorsDlgAll";
        ExpandEnabled = true;
        GetAll = ct => Task.FromResult<IEnumerable<BeAuthor>>(db.Authors.ToList());
        HasMany(x => x.Books,
            getAll: (id, ct) => Task.FromResult(db.Books.Where(b => b.AuthorId == id).AsEnumerable()));
    }
}
