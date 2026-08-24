using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #418: MaxExpandTop's reach on the SINGLE-ENTITY read, GET /{Set}({key})?$expand=Nav.
//
// THE GAP THIS CLOSES, AS MEASURED ON THE PRE-FIX TREE (ee85a10, i.e. after #442 and #443):
//
//   MaxExpandTop = 2, ExpandPagingEnabled = true, author 1 holds five books
//   GET /BeAuthorsById?$filter=Id eq 1&$expand=Books  -> 200, two books + Books@odata.nextLink   (ok)
//   GET /BeAuthorsById(1)?$expand=Books               -> 200, ALL FIVE, no link, no 400          (the bug)
//
// The bare-$expand ceiling and its continuation link both live behind ShapePushedExpandsInJson, whose
// single call site is the GetQueryable collection route. GetById expands through
// ApplyCollectionPipelineAsync -> ExpandLevelAsync, whose ServeRaw branch is deliberately a no-op, so
// whatever the developer's GetById delegate already materialized went out unbounded and unannotated.
//
// WHY THE OUTCOME IS A 400 AND NOT A TRIM-AND-LINK — the M1 analysis, pinned by M1_* below.
// M1 ("no bound without either a continuation link or a 400") allows both. The link needs three
// things and only two are available on this route: the parent key (in the URL — easy) and a
// continuation route (already registered when both knobs are set). The third is a SHARED ORDER
// between page 1 and the continuation, and it does not exist here: the child rows arrive already
// materialized inside the TModel the GetById delegate returned, in that delegate's own order (a plain
// LEFT JOIN with no ORDER BY over the child, measured), while the continuation composes
// OrderBy(child key) IN THE DATABASE. Re-sorting the serialized JsonArray cannot reconcile them — a
// JSON sort is not the provider's collation (SQL Server's uniqueidentifier order, and any string
// column's collation, differ from an ordinal JSON compare). A link over a disagreeing order silently
// skips and duplicates rows across the page boundary, which is strictly worse than the 400 and is
// invisible to the client.
//
// FIXTURE PROVENANCE: BeAuthor / BeBook / BeChapter / BareExpandDbContext / BareExpandSqliteHarness
// were authored by #313 stage 2, not here. What is new is one profile that adds a GetById handler to
// that model — GetById is the route under test and BeAuthorProfile has never had one — plus, for the
// delegate-safety partition, one profile that declares Books WITH a delegate. Both are registered
// through the harness's existing additive `configureExtraProfiles` hook, so every pre-existing call
// site sees exactly the registration it did before.

/// <summary>
/// #418: a second entity set over the stage-2 BeAuthor model that DOES expose GetById. Its GetById
/// eagerly Includes Books, which is the shape that makes the gap observable at all — a GetById that
/// does not eager-load serves <c>"Books": []</c> and has nothing to bound.
/// </summary>
public sealed class BeAuthorByIdProfile : EntitySetProfile<int, BeAuthor>
{
    public BeAuthorByIdProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeAuthorsById";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Authors.AsQueryable());
        // #463: `.ThenInclude(b => b.Chapters)` is the one addition. #418's fixture eager-loaded ONE
        // level, which is exactly why its ceiling could pass while being enforced at one level: with
        // nothing materialized at depth 2 there was nothing for the missing check to have caught.
        // Chapters is not $expand'd unless the request asks for it, so every pre-existing byte-
        // identity assertion in this file is unaffected (SerializeBounded never walks an un-expanded
        // navigation).
        GetById = (id, ct) => Task.FromResult(
            db.Authors.Include(a => a.Books).ThenInclude(b => b.Chapters).FirstOrDefault(a => a.Id == id));
        HasMany(x => x.Books);
    }
}

/// <summary>
/// #418 / #313 O6: the same model with Books declared WITH a delegate. Registered on its own (never
/// alongside <see cref="BeAuthorByIdProfile"/>), because a sibling declaring the same navigation
/// delegate-backed makes ResolveNavTreatment answer Blank for BOTH sets and would test nothing.
/// </summary>
public sealed class BeAuthorDelegateByIdProfile : EntitySetProfile<int, BeAuthor>
{
    public BeAuthorDelegateByIdProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeAuthorsDlg";
        ExpandEnabled = true;
        GetById = (id, ct) => Task.FromResult(db.Authors.FirstOrDefault(a => a.Id == id));
        HasMany(x => x.Books,
            getAll: (id, ct) => Task.FromResult(db.Books.Where(b => b.AuthorId == id).AsEnumerable()));
    }
}

public sealed class SingleEntityExpandCeilingTests
{
    private static async Task<(TestFixture Fixture, SqliteConnection Connection)> BuildAsync(
        int? cap, bool paging, bool withDelegateProfile = false)
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
            // #463: one author whose DEPTH-1 collection is inside every ceiling this file uses (one
            // book) and whose DEPTH-2 collection is not (five chapters). Author 1 cannot serve that
            // shape — its five books breach at depth 1, so the depth-2 hole is masked by the depth-1
            // check that already worked. Additive: every assertion in this file targets author 1.
            seedExtra: db =>
            {
                db.Authors.Add(new BeAuthor { Id = 2, Name = "Bea", PublisherId = 100 });
                db.Books.Add(new BeBook { Id = 10, AuthorId = 2, Title = "Deep" });
                db.Chapters.AddRange(
                    new BeChapter { Id = 10, BookId = 10, Heading = "C1" },
                    new BeChapter { Id = 11, BookId = 10, Heading = "C2" },
                    new BeChapter { Id = 12, BookId = 10, Heading = "C3" },
                    new BeChapter { Id = 13, BookId = 10, Heading = "C4" },
                    new BeChapter { Id = 14, BookId = 10, Heading = "C5" });
            },
            configureExtraProfiles: b =>
            {
                if (withDelegateProfile) b.AddEntitySetProfile<BeAuthorDelegateByIdProfile>();
                else b.AddEntitySetProfile<BeAuthorByIdProfile>();
            });
        return (fx, connection);
    }

    // ── The bug itself ───────────────────────────────────────────────────────────────────────────

    // FAILS WITHOUT THE FIX: returns 200 with all five books.
    [Fact]
    public async Task GetById_BareExpand_OverCeiling_Is400_NotAnUnboundedCollection()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthorsById(1)?$expand=Books");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement error = doc.RootElement.GetProperty("error");
            Assert.Equal("InvalidQueryOption", error.GetProperty("code").GetString());
            string message = error.GetProperty("message").GetString()!;
            Assert.Contains("maximum of 2 entities", message);
            // The remediation must name the route that CAN serve this, and must NOT repeat the
            // collection route's "Narrow it with a nested $filter" — a nested $filter is one of the
            // options this route silently ignores, so following that advice returns the same 400.
            Assert.Contains("/BeAuthorsById?$filter=", message);
            Assert.DoesNotContain("Narrow it with a nested $filter", message);
        }
        conn.Dispose();
    }

    // The ceiling covers EVERY nested shape, not only the bare one. GetById applies no nested option
    // at all to a delegate-less navigation (measured: $filter/$orderby/$select/$skip/$top/$count are
    // each silently ignored there while the collection route honours all six), so a ceiling that
    // fired only for the bare shape would be bypassable by appending any one of them.
    // FAILS WITHOUT THE FIX: every one of these returns 200 with all five books.
    [Theory]
    [InlineData("$expand=Books($select=Title)")]
    [InlineData("$expand=Books($filter=Id lt 3)")]
    [InlineData("$expand=Books($orderby=Id desc)")]
    [InlineData("$expand=Books($skip=2)")]
    [InlineData("$expand=Books($count=true)")]
    [InlineData("$expand=Books($top=2)")]
    [InlineData("$select=Name&$expand=Books")]
    public async Task GetById_OverCeiling_IsNotBypassableByAddingANestedOption(string query)
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/BeAuthorsById(1)?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        conn.Dispose();
    }

    // ── #463: the same ceiling, one level down ───────────────────────────────────────────────────
    //
    // #418 closed the OPTION axis on this route ("a ceiling that fired only for the bare shape would
    // be bypassable by appending any nested option") and left the DEPTH axis open: the enforcement
    // walked clause.SelectedItems without recursing into item.SelectAndExpand, and the nav set it
    // consulted was resolved once at startup from the ROOT profile. So with cap = 2 the depth-1 check
    // fired and the depth-2 collection went out whole.
    //
    // This is #454's pattern — a validation and its enforcement consulting different sets.
    // ValidateNestedTopCeiling walks the whole tree, so `Chapters($top=1000)` at depth 2 is rejected
    // (pinned below); the ceiling that bounds SERVED data checked depth 1. The option that would have
    // bounded the fetch was refused, and the shape that fetched everything was served.

    // FAILS WITHOUT THE FIX: 200 with all five chapters.
    [Fact]
    public async Task GetById_NestedExpand_AtDepthTwo_OverCeiling_Is400()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync(
                "/odata/BeAuthorsById(2)?$expand=Books($expand=Chapters)");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement error = doc.RootElement.GetProperty("error");
            Assert.Equal("InvalidQueryOption", error.GetProperty("code").GetString());
            string message = error.GetProperty("message").GetString()!;
            // The breach is named at the level it happened, not at the root.
            Assert.Contains("'Chapters'", message);
            Assert.Contains("maximum of 2 entities", message);
            // The remediation echoes the WHOLE path back, so the suggested collection-route request
            // is the request the client actually made rather than a truncation of it.
            Assert.Contains("$expand=Books($expand=Chapters)", message);
        }
        conn.Dispose();
    }

    // The depth-2 hole was not bare-shape-only either: exactly as at depth 1, no nested option is
    // applied to a raw-served collection, so any of them would otherwise reopen the bypass.
    // FAILS WITHOUT THE FIX: every one of these returns 200 with all five chapters.
    [Theory]
    [InlineData("$expand=Books($expand=Chapters($select=Heading))")]
    [InlineData("$expand=Books($expand=Chapters($filter=Id lt 12))")]
    [InlineData("$expand=Books($expand=Chapters($orderby=Id desc))")]
    [InlineData("$expand=Books($expand=Chapters($skip=3))")]
    [InlineData("$expand=Books($expand=Chapters($count=true))")]
    [InlineData("$expand=Books($expand=Chapters($top=2))")]
    public async Task GetById_DepthTwo_OverCeiling_IsNotBypassableByAddingANestedOption(string query)
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/BeAuthorsById(2)?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        conn.Dispose();
    }

    // The asymmetry that made #463 worth filing: the VALIDATION already walked the whole tree, so a
    // depth-2 $top above the ceiling was rejected while the shape that fetched everything passed.
    // Green before and after — recorded here so the two halves stay visibly paired.
    [Fact]
    public async Task GetById_DepthTwo_ExplicitTopAboveCeiling_WasAlreadyRejected()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync(
                "/odata/BeAuthorsById(2)?$expand=Books($expand=Chapters($top=1000))");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        conn.Dispose();
    }

    // Under the ceiling at BOTH levels the response is untouched — the recursion must not start
    // rejecting a request that fits.
    [Fact]
    public async Task ByteIdentical_DepthTwo_UnderCeiling_IsUntouched()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 10, paging: true);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync(
                "/odata/BeAuthorsById(2)?$expand=Books($expand=Chapters)");
            Assert.Equal(
                "{\"@odata.context\":\"http://localhost/odata/$metadata#BeAuthorsById/$entity\"," +
                "\"@odata.id\":\"http://localhost/odata/BeAuthorsById(2)\",\"Id\":2,\"Name\":\"Bea\"," +
                "\"PublisherId\":100,\"Books\":[" +
                "{\"Id\":10,\"AuthorId\":2,\"Title\":\"Deep\",\"Chapters\":[" +
                "{\"Id\":10,\"BookId\":10,\"Heading\":\"C1\"}," +
                "{\"Id\":11,\"BookId\":10,\"Heading\":\"C2\"}," +
                "{\"Id\":12,\"BookId\":10,\"Heading\":\"C3\"}," +
                "{\"Id\":13,\"BookId\":10,\"Heading\":\"C4\"}," +
                "{\"Id\":14,\"BookId\":10,\"Heading\":\"C5\"}]}]}",
                body);
        }
        conn.Dispose();
    }

    // MaxExpandTop = null is the shipping default; the recursion must be inert there too, or #463's
    // fix becomes a 200 -> 400 for every existing application with a two-level $expand.
    [Fact]
    public async Task ByteIdentical_DepthTwo_NoCeilingConfigured_IsUntouched()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: null, paging: false);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync(
                "/odata/BeAuthorsById(2)?$expand=Books($expand=Chapters)");
            Assert.Contains("\"Heading\":\"C5\"", body);
        }
        conn.Dispose();
    }

    // ── The M1 decision, pinned so a later change to it is deliberate ────────────────────────────

    // ExpandPagingEnabled buys a continuation link on the COLLECTION route and nothing at all here.
    // FAILS WITHOUT THE FIX: 200 with five books (the status assertion).
    [Fact]
    public async Task M1_GetById_EmitsNoNestedNextLink_EvenWithExpandPagingEnabled()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthorsById(1)?$expand=Books");
            string body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            // Never a bound without a link OR a 400 — and never a bound WITH a truncated body either.
            Assert.DoesNotContain("@odata.nextLink", body);
            Assert.DoesNotContain("Bk1", body);
        }
        conn.Dispose();
    }

    // The contrast anchor: the SAME registration, the SAME ceiling, the collection route — which does
    // compose both sides of the order and therefore can page. Green before and after the fix.
    [Fact]
    public async Task M1_CollectionRoute_OnTheSameRegistration_StillTrimsAndLinks()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 2, paging: true);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync(
                "/odata/BeAuthorsById?$filter=Id eq 1&$expand=Books");
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement parent = doc.RootElement.GetProperty("value")[0];

            Assert.Equal(2, parent.GetProperty("Books").GetArrayLength());
            Assert.Equal(
                "http://localhost/odata/BeAuthorsById(1)/Books?$skip=2",
                parent.GetProperty("Books@odata.nextLink").GetString());
        }
        conn.Dispose();
    }

    // ── Byte-identity: shapes that must not move ─────────────────────────────────────────────────
    //
    // Every string below was captured from the PRE-FIX build and passes on it unchanged.

    [Fact]
    public async Task ByteIdentical_UnderCeiling_IsUntouched()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 10, paging: true);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync("/odata/BeAuthorsById(1)?$expand=Books");
            Assert.Equal(
                "{\"@odata.context\":\"http://localhost/odata/$metadata#BeAuthorsById/$entity\"," +
                "\"@odata.id\":\"http://localhost/odata/BeAuthorsById(1)\",\"Id\":1,\"Name\":\"Ann\"," +
                "\"PublisherId\":100,\"Books\":[" +
                "{\"Id\":1,\"AuthorId\":1,\"Title\":\"Bk1\"}," +
                "{\"Id\":2,\"AuthorId\":1,\"Title\":\"Bk2\"}," +
                "{\"Id\":3,\"AuthorId\":1,\"Title\":\"Bk3\"}," +
                "{\"Id\":4,\"AuthorId\":1,\"Title\":\"Bk4\"}," +
                "{\"Id\":5,\"AuthorId\":1,\"Title\":\"Bk5\"}]}",
                body);
        }
        conn.Dispose();
    }

    // MaxExpandTop = null is the SHIPPING DEFAULT. The whole feature must stay inert there, or #418's
    // fix becomes a main-read-path 200 -> 400 for every existing application.
    [Fact]
    public async Task ByteIdentical_NoCeilingConfigured_IsUntouched()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: null, paging: false);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync("/odata/BeAuthorsById(1)?$expand=Books");
            Assert.Equal(
                "{\"@odata.context\":\"http://localhost/odata/$metadata#BeAuthorsById/$entity\"," +
                "\"@odata.id\":\"http://localhost/odata/BeAuthorsById(1)\",\"Id\":1,\"Name\":\"Ann\"," +
                "\"PublisherId\":100,\"Books\":[" +
                "{\"Id\":1,\"AuthorId\":1,\"Title\":\"Bk1\"}," +
                "{\"Id\":2,\"AuthorId\":1,\"Title\":\"Bk2\"}," +
                "{\"Id\":3,\"AuthorId\":1,\"Title\":\"Bk3\"}," +
                "{\"Id\":4,\"AuthorId\":1,\"Title\":\"Bk4\"}," +
                "{\"Id\":5,\"AuthorId\":1,\"Title\":\"Bk5\"}]}",
                body);
        }
        conn.Dispose();
    }

    // A single-valued navigation is at most one row; there is nothing to bound, and the ceiling must
    // not start rejecting it. The ceiling reads the SERIALIZED array and a single-valued nav has none,
    // so it is not in the ceiling map at all (the startup filter requires
    // IEdmNavigationProperty.Type.IsCollection()).
    //
    // Publisher is OMITTED rather than emitted as null, and that is #446's doing, not the ceiling's:
    // BeAuthorByIdProfile declares only HasMany(Books), so Publisher is a convention-discovered
    // navigation this profile never declared, and an undeclared nav is omitted (JSON Format v4.01
    // §8.3 - the inline form IS the expanded form, so "Publisher":null would positively assert an
    // empty relationship the server never evaluated). This assertion was authored against a tree
    // where #446 had not yet landed; the two PRs could not see each other, and develop went red on
    // the composition. Do NOT "restore" the null: that would mean an undeclared nav is being served
    // again. If this line ever needs to change back, the profile has started declaring Publisher.
    [Fact]
    public async Task ByteIdentical_SingleValuedNavigation_IsUntouched()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 1, paging: true);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync("/odata/BeAuthorsById(1)?$expand=Publisher");
            Assert.Equal(
                "{\"@odata.context\":\"http://localhost/odata/$metadata#BeAuthorsById/$entity\"," +
                "\"@odata.id\":\"http://localhost/odata/BeAuthorsById(1)\",\"Id\":1,\"Name\":\"Ann\"," +
                "\"PublisherId\":100}",
                body);
        }
        conn.Dispose();
    }

    // A GetById that does NOT eager-load serves an empty collection, whatever the ceiling says.
    // BeAuthorsDlg's GetById loads no Books; the delegate below is what fills them.
    [Fact]
    public async Task ByteIdentical_NoExpandAtAll_IsUntouched()
    {
        (TestFixture fx, SqliteConnection conn) = await BuildAsync(cap: 1, paging: true);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync("/odata/BeAuthorsById(1)");
            Assert.Equal(
                "{\"@odata.context\":\"http://localhost/odata/$metadata#BeAuthorsById/$entity\"," +
                "\"@odata.id\":\"http://localhost/odata/BeAuthorsById(1)\",\"Id\":1,\"Name\":\"Ann\"," +
                "\"PublisherId\":100}",
                body);
        }
        conn.Dispose();
    }

    // ── Delegate safety (#313 O6): the framework does not truncate a delegate's answer ────────────

    [Fact]
    public async Task ByteIdentical_DelegateBackedNavigation_IsNotCapped()
    {
        (TestFixture fx, SqliteConnection conn) =
            await BuildAsync(cap: 2, paging: true, withDelegateProfile: true);
        await using (fx)
        {
            string body = await fx.Client.GetStringAsync("/odata/BeAuthorsDlg(1)?$expand=Books");
            using JsonDocument doc = JsonDocument.Parse(body);
            // Five, not two: the rows came from the developer's own delegate and the framework does
            // not silently truncate those (#313 O6). A 400 here would be the same weakening by another
            // route.
            Assert.Equal(5, doc.RootElement.GetProperty("Books").GetArrayLength());
        }
        conn.Dispose();
    }
}
