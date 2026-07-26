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
//   1. An InvalidOperationException during materialization still 400s (the filter still catches what
//      it should) — the ONLY still-injectable EF Core failure of this kind, per the note below.
//   2. A simulated transient provider fault during materialization of an OTHERWISE-translatable shape
//      now 500s instead of being reclassified as a lying 400 (the bug this file fixes).
// Reuses the MlAuthor/MlBook/MlChapter fixtures, MultiLevelDbContext, and MlAuthorProfile from
// MultiLevelExpandPushdownSqliteTests.cs (that file itself stays untouched) — this file only adds its
// own DbCommandInterceptor-based fault injection on top.
//
// #304 update: this suite's branch-1 coverage originally used a genuinely-untranslatable request shape
// (Books($top=1;$expand=Chapters), the same SQLite APPLY/LATERAL reproducer as ExpandPushdownFailLoudTests)
// to prove InvalidOperationException → 400. #304 deferred that shape's SQL composition to the JSON pass
// (see OhDataEndpointFactory.ApplyNavShape/ShapePushedExpandsInJson), so it is now genuinely translatable
// and no longer trips a real EF Core translation failure — there is no known-untranslatable expand shape
// left reachable over HTTP on SQLite after #298/#300/#304. ThrowingReaderInterceptor was widened with an
// InvalidOperationFault mode (alongside its pre-existing transient-DbException mode) so this suite keeps
// proving the narrowed catch's InvalidOperationException branch without depending on a live untranslatable
// shape — see GenuinelyUntranslatableShape's replacement,
// InvalidOperationFault_DuringMaterialization_StillFailsLoud_400_NotMisclassifiedAs500, below.

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
/// Which exception <see cref="ThrowingReaderInterceptor"/> raises the next time it fires.
/// </summary>
internal enum ThrowingReaderInterceptorMode
{
    /// <summary>A <see cref="SimulatedTransientDbException"/> — stands in for a real infrastructure
    /// fault (connection drop / command timeout / deadlock). Must classify as 500, never 400.</summary>
    TransientDbFault,

    /// <summary>An <see cref="InvalidOperationException"/> carrying the same "...could not be
    /// translated..." message family EF Core itself throws for a genuinely untranslatable LINQ shape
    /// (empirically confirmed pre-#304 via the SQLite APPLY/LATERAL shape ExpandPushdownFailLoudTests
    /// used to reproduce). #304 deferred that specific shape to the JSON pass, so it no longer trips a
    /// real translation failure over HTTP — this mode proves the narrowed catch's OTHER branch
    /// (InvalidOperationException → 400) still works without depending on a live untranslatable shape.
    /// </summary>
    InvalidOperationFault,
}

/// <summary>
/// EF Core command interceptor that, once armed, throws the exception matching its
/// <see cref="ThrowingReaderInterceptorMode"/> the next time a reader is about to execute — simulating
/// a fault surfacing mid-materialization, exactly where <c>pushedQuery.ToArray()</c> would observe it.
/// </summary>
internal sealed class ThrowingReaderInterceptor : DbCommandInterceptor
{
    private int _armed;
    private ThrowingReaderInterceptorMode _mode = ThrowingReaderInterceptorMode.TransientDbFault;

    public void Arm(ThrowingReaderInterceptorMode mode = ThrowingReaderInterceptorMode.TransientDbFault)
    {
        _mode = mode;
        Interlocked.Exchange(ref _armed, 1);
    }

    public void Disarm() => Interlocked.Exchange(ref _armed, 0);

    private void ThrowIfArmed()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw _mode switch
            {
                ThrowingReaderInterceptorMode.InvalidOperationFault => new InvalidOperationException(
                    "simulated: the LINQ expression could not be translated. Simplify the query or write an equivalent."),
                _ => new SimulatedTransientDbException(
                    "simulated transient database fault (connection drop / timeout / deadlock)"),
            };
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
    public async Task InvalidOperationFault_DuringMaterialization_StillFailsLoud_400_NotMisclassifiedAs500()
    {
        // Coverage for the OTHER branch of the narrowed filter. Before #304, Books($top=1;$expand=Chapters)
        // itself tripped EF's own InvalidOperationException ("...could not be translated...") on the
        // SQLite APPLY/LATERAL shape ExpandPushdownFailLoudTests used to reproduce — #304 deferred that
        // shape's windowing to the JSON pass, so it is genuinely translatable now (see
        // ExpandPushdownFailLoudTests.NestedTopWithChildren_NowWorks_200_WindowedParentWithItsChildren)
        // and no longer trips a real translation failure. Rather than lose coverage of this catch branch,
        // the SAME interceptor used above to simulate a transient DbException is armed in its
        // InvalidOperationFault mode instead — proving InvalidOperationException is still classified 400
        // (not swallowed into the group-level 500 handler) without depending on a live untranslatable
        // shape. The request shape itself (Books($expand=Chapters), no nested $top/$skip/$count) is the
        // same genuinely-translatable one used by the control/transient-fault tests above.
        _interceptor.Arm(ThrowingReaderInterceptorMode.InvalidOperationFault);

        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters)");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);

        // S7: the raw injected exception's message must never leak to the client, on this 400 path
        // exactly as it already doesn't for a real EF translation failure.
        Assert.DoesNotContain("simulated:", body);
    }
}
