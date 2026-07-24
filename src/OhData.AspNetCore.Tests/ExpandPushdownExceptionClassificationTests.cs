using System;
using System.Data.Common;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// Adversarial-review MEDIUM (folded into fix/expand-pushdown-fail-loud): the execution-time catch in
// OhDataEndpointFactory (around the `pushedQuery.ToArray()` materialization of a pushed $expand) was
// widened by this branch to `catch (Exception ex) when (ex is not OperationCanceledException)` so a
// genuinely UNTRANSLATABLE query shape fails loud with 400 instead of silently degrading to a lying 200
// (see ExpandPushdownFailLoudTests.cs). But that filter is too wide: it also swallowed INFRASTRUCTURE /
// TRANSIENT provider faults — DB command timeouts, connection drops, deadlocks (DbException subclasses
// like SqliteException/SqlException, TimeoutException) — and relabeled them 400 "simplify your query",
// which is wrong (a transient fault is a 500, and is retryable; a 400 tells client retry logic NOT to
// retry). The catch is now narrowed to `ex is InvalidOperationException or NotSupportedException or
// Microsoft.OData.ODataException` — the exact family EF Core / Microsoft's OData binder throw for a
// genuine translation failure (empirically confirmed via ExpandPushdownFailLoudTests: EF Core raises
// System.InvalidOperationException with message "...could not be translated..." for an untranslatable
// SQLite APPLY/LATERAL shape). Anything else — in particular a provider/transient fault — now propagates
// past this catch to the group-level exception filter (OhDataEndpointFactory.MapAll) and comes back as
// 500, never leaking the underlying exception's message/stack trace (S7).
//
// This suite proves BOTH branches of the narrowed filter using ONE harness/scenario pair reused from
// MultiLevelExpandPushdownSqliteTests.cs (Author → Books → Chapters, a delegate-less two-level chain):
//   1. A genuinely untranslatable nested shape still 400s (the filter still catches what it should).
//   2. A simulated transient provider fault during materialization of an OTHERWISE-translatable shape
//      now 500s instead of being reclassified as a lying 400 (the bug this file fixes).
// Reuses the MlAuthor/MlBook/MlChapter fixtures, MultiLevelDbContext, and MlAuthorProfile from
// MultiLevelExpandPushdownSqliteTests.cs (that file itself stays untouched) — this file only adds its
// own DbCommandInterceptor-based fault injection on top.

/// <summary>
/// A minimal, constructible <see cref="DbException"/> subclass standing in for a real transient
/// provider fault (e.g. <c>Microsoft.Data.Sqlite.SqliteException</c> / <c>System.Data.SqlClient.SqlException</c>)
/// without depending on provider-internal constructors. It IS a <see cref="DbException"/>, so it
/// exercises exactly the class of exception the narrowed catch must NOT reclassify as 400.
/// </summary>
internal sealed class SimulatedTransientDbException : DbException
{
    public SimulatedTransientDbException(string message) : base(message) { }
}

/// <summary>
/// EF Core command interceptor that, once armed, throws a <see cref="SimulatedTransientDbException"/>
/// the next time a reader is about to execute — simulating a connection drop / command timeout /
/// deadlock surfacing mid-materialization, exactly where <c>pushedQuery.ToArray()</c> would observe it.
/// </summary>
internal sealed class ThrowingReaderInterceptor : DbCommandInterceptor
{
    private int _armed;

    public void Arm() => Interlocked.Exchange(ref _armed, 1);
    public void Disarm() => Interlocked.Exchange(ref _armed, 0);

    private void ThrowIfArmed()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new SimulatedTransientDbException(
                "simulated transient database fault (connection drop / timeout / deadlock)");
        }
    }

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        ThrowIfArmed();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmed();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class ExpandPushdownExceptionClassificationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ThrowingReaderInterceptor _interceptor = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _interceptor = new ThrowingReaderInterceptor();

        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<MlAuthorProfile>(),
            configureServices: services =>
            {
                services.AddDbContext<MultiLevelDbContext>(o =>
                {
                    o.UseSqlite(_connection);
                    o.AddInterceptors(_interceptor);
                });
            });

        using IServiceScope scope = _fx.App.Services.CreateScope();
        MultiLevelDbContext db = scope.ServiceProvider.GetRequiredService<MultiLevelDbContext>();
        db.Database.EnsureCreated();

        // Same shape as MultiLevelExpandPushdownSqliteTests.TwoLevel_NestedExpand_PushesThenIncludeInOneQuery
        // (a confirmed-translatable two-level pushed $expand: Author -> Books -> Chapters).
        db.Authors.Add(new MlAuthor { Id = 1, Name = "Ann" });
        db.Books.Add(new MlBook { Id = 10, AuthorId = 1, Title = "B1", Year = 2001 });
        db.Chapters.AddRange(
            new MlChapter { Id = 100, BookId = 10, Heading = "Zeta", Ordinal = 2 },
            new MlChapter { Id = 101, BookId = 10, Heading = "Alpha", Ordinal = 1 });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task TransientProviderFault_DuringMaterialization_Is500_NotReclassifiedAs400()
    {
        // The shape below (Books($expand=Chapters), no nested $top/$skip/$count) is genuinely
        // translatable on SQLite — see TwoLevel_NestedExpand_PushesThenIncludeInOneQuery, which asserts
        // 200 for this exact request against the same fixture shape. Arming the interceptor injects a
        // DbException (simulating a dropped connection / command timeout / deadlock) exactly at
        // materialization time (pushedQuery.ToArray()), with NOTHING wrong with the query itself.
        _interceptor.Arm();

        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters)");

        // Before this fix: the wide `catch (Exception ex) when (ex is not OperationCanceledException)`
        // caught the DbException too and relabeled it 400 "could not be translated... simplify your
        // query" — actively misleading for a transient infrastructure fault, and telling client retry
        // logic not to retry. After the fix: a DbException is not in the narrowed allowlist, so it
        // propagates to the group-level exception filter and comes back 500 (retryable).
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InternalServerError", body);
        Assert.DoesNotContain("InvalidQueryOption", body); // must NOT be relabeled a translation failure

        // S7: the raw provider/DB exception message/type must never leak to the client, on the 500 path
        // exactly as it already doesn't on the 400 path.
        Assert.DoesNotContain("SimulatedTransientDbException", body);
        Assert.DoesNotContain("simulated transient database fault", body);
        Assert.DoesNotContain("connection drop", body);
    }

    [Fact]
    public async Task SameShape_WithoutFault_StillSucceeds_ProvingTheFaultAloneCausedThe500()
    {
        // Control: same request, interceptor left disarmed — proves the 500 above is caused solely by
        // the injected fault, not by the request shape itself being untranslatable.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GenuinelyUntranslatableShape_StillFailsLoud_400_NotMisclassifiedAs500()
    {
        // Coverage for the OTHER branch of the narrowed filter, on this same harness: a nested $top
        // alongside a nested $expand (Books($top=1;$expand=Chapters)) still trips EF's own
        // InvalidOperationException ("could not be translated" — the SQLite APPLY/LATERAL shape from
        // ExpandPushdownFailLoudTests), which IS in the narrowed allowlist, so it still fails loud 400 —
        // narrowing the filter must not have accidentally started letting genuine translation failures
        // through to the group-level 500 handler either.
        _interceptor.Disarm();

        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($top=1;$expand=Chapters)");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
    }
}
