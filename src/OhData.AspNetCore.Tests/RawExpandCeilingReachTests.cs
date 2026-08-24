using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        // PR #477 review, F1: `.ThenInclude(b => b.Chapters)` puts a SECOND level into the graph the
        // handler returns. Chapters is not $expand'd unless a request asks for it, so every other
        // assertion in this file is unaffected (SerializeBounded never walks an un-expanded nav).
        GetAll = ct => Task.FromResult<IEnumerable<BeAuthor>>(
            db.Authors.Include(a => a.Books).ThenInclude(b => b.Chapters).ToList());
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

    // ── PR #477 review, F1: classification does NOT exempt rows below a ServeRaw parent ───────────
    //
    // #313 O6 exempts a delegate-backed navigation because its rows are the developer's own answer.
    // That justification is a statement about a delegate having RUN, and below a ServeRaw parent it
    // has not: ExpandLevelAsync's ServeRaw branch does not recurse, so at depth >= 2 nothing invoked
    // a delegate and nothing was blanked — every value present is the ROOT handler's raw graph,
    // whatever the profile over the child type declares. The first revision of this branch applied
    // the depth-1 Model B test at every level and cited O6 for the exemption; measured, cap = 2,
    // GetAll, `?$expand=Books($expand=Chapters)` served five chapters with the Chapters delegate
    // invoked ZERO times. The exemption was citing a delegate that never ran.
    //
    // Cap 5, not 2, deliberately: author 1's five books must sit INSIDE the ceiling so the depth-1
    // check passes and the depth-2 breach is what the response turns on. Book 1 carries six chapters
    // (the harness's two plus four seeded here).

    private static async Task<(TestFixture Fixture, SqliteConnection Connection, BeChapterDelegateCounter Counter)>
        BuildDepthTwoAsync(bool blankInsteadOfDelegate)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new BeChapterDelegateCounter();
        TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink: null,
            defaults: d => { d.MaxExpandTop = 5; d.ExpandPagingEnabled = true; },
            configureExtraServices: services => services.AddSingleton(counter),
            seedExtra: db => db.Chapters.AddRange(
                new BeChapter { Id = 20, BookId = 1, Heading = "C3" },
                new BeChapter { Id = 21, BookId = 1, Heading = "C4" },
                new BeChapter { Id = 22, BookId = 1, Heading = "C5" },
                new BeChapter { Id = 23, BookId = 1, Heading = "C6" }),
            configureExtraProfiles: b =>
            {
                b.AddEntitySetProfile<BeAuthorGetAllProfile>();
                b.AddEntitySetProfile<BeBookDelegateProfile>();
                // A second candidate over BeBook declaring Chapters delegate-LESS makes the two
                // disagree, so Model B answers Blank at depth 2 instead of RunDelegate.
                if (blankInsteadOfDelegate) b.AddEntitySetProfile<BeBookPlainProfile>();
            });
        return (fx, connection, counter);
    }

    // FAILS WITHOUT THE FIX: 200, six chapters served, ChapterDelegateCalls == 0.
    [Theory]
    [InlineData(false)] // Chapters resolves RunDelegate at depth 2
    [InlineData(true)]  // Chapters resolves Blank at depth 2
    public async Task DepthTwo_UnderAServeRawParent_IsBounded_WhateverTheClassification(bool blank)
    {
        (TestFixture fx, SqliteConnection conn, BeChapterDelegateCounter counter) =
            await BuildDepthTwoAsync(blank);
        await using (fx)
        {
            HttpResponseMessage resp = await fx.Client.GetAsync(
                "/odata/BeAuthorsAll?$expand=Books($expand=Chapters)");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string message = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            Assert.Contains("'Chapters'", message);
            Assert.Contains("maximum of 5 entities", message);

            // The measurement that makes the O6 exemption inapplicable here: no delegate ran, so
            // these were never "the developer's own answer" to begin with.
            Assert.Equal(0, counter.Calls);
        }
        conn.Dispose();
    }

    // The O6-legitimate exemption, unchanged and still pinned: at DEPTH 1 the delegate really does
    // run, and its six chapters are served whole under a ceiling of five.
    [Fact]
    public async Task DepthOne_DelegateBacked_StillRunsAndIsNotBounded()
    {
        (TestFixture fx, SqliteConnection conn, BeChapterDelegateCounter counter) =
            await BuildDepthTwoAsync(blankInsteadOfDelegate: false);
        await using (fx)
        {
            using JsonDocument doc = JsonDocument.Parse(
                await fx.Client.GetStringAsync("/odata/BeBooksDlg?$filter=Id eq 1&$expand=Chapters"));
            Assert.Equal(6, doc.RootElement.GetProperty("value")[0].GetProperty("Chapters").GetArrayLength());
            Assert.True(counter.Calls > 0, "the depth-1 delegate must actually run");
        }
        conn.Dispose();
    }

    // Under the ceiling at both levels, the depth-2 walk must leave the response alone.
    [Fact]
    public async Task ByteIdentical_DepthTwo_UnderCeiling_IsUntouched()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new BeChapterDelegateCounter();
        TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink: null,
            defaults: d => d.MaxExpandTop = 10,
            configureExtraServices: services => services.AddSingleton(counter),
            configureExtraProfiles: b =>
            {
                b.AddEntitySetProfile<BeAuthorGetAllProfile>();
                b.AddEntitySetProfile<BeBookDelegateProfile>();
            });
        await using (fx)
        {
            using JsonDocument doc = JsonDocument.Parse(
                await fx.Client.GetStringAsync("/odata/BeAuthorsAll?$expand=Books($expand=Chapters)"));
            JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
            Assert.Equal(5, books.GetArrayLength());
            Assert.Equal(2, books[0].GetProperty("Chapters").GetArrayLength());
            Assert.Equal(0, counter.Calls);
        }
        connection.Dispose();
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

/// <summary>Counts invocations of the depth-2 Chapters delegate (PR #477 review, F1).</summary>
public sealed class BeChapterDelegateCounter
{
    private int _calls;
    public int Calls => _calls;
    public void Count() => System.Threading.Interlocked.Increment(ref _calls);
}

/// <summary>
/// PR #477 review, F1: an entity set over <see cref="BeBook"/> that declares <c>Chapters</c> WITH a
/// delegate. It makes <c>Chapters</c> resolve to <c>RunDelegate</c> at depth 2 — while the request
/// reaches it through <c>BeAuthors*/Books</c>, a ServeRaw parent whose branch never recurses, so the
/// delegate never runs and the chapters in the payload are the AUTHOR handler's own graph.
/// </summary>
public sealed class BeBookDelegateProfile : EntitySetProfile<int, BeBook>
{
    public BeBookDelegateProfile(BareExpandDbContext db, BeChapterDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "BeBooksDlg";
        ExpandEnabled = true;
        FilterEnabled = true; // so the depth-1 O6 control can isolate book 1 deterministically
        GetQueryable = _ => Task.FromResult(db.Books.AsQueryable());
        HasMany(x => x.Chapters, getAll: (id, ct) =>
        {
            counter.Count();
            return Task.FromResult(db.Chapters.Where(c => c.BookId == id).AsEnumerable());
        });
    }
}

/// <summary>
/// PR #477 review, F1: a SECOND entity set over <see cref="BeBook"/> declaring <c>Chapters</c>
/// delegate-LESS. Registered alongside <see cref="BeBookDelegateProfile"/> it makes the two
/// candidates disagree, so <c>Chapters</c> resolves to <c>Blank</c> at depth 2 — the other
/// classification the reviewer flagged as reachable under a ServeRaw parent.
/// </summary>
public sealed class BeBookPlainProfile : EntitySetProfile<int, BeBook>
{
    public BeBookPlainProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeBooksPlain";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Books.AsQueryable());
        HasMany(x => x.Chapters);
    }
}
