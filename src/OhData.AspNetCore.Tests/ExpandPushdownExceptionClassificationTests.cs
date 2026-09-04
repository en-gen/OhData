using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #494: a provider fault is classified by WHEN it was raised, not by what it was.
//
// The execution-time catch around the pushed-$expand materialization used to filter on
// `InvalidOperationException or NotSupportedException or ODataException`, on the premise that an IOE
// there could only be EF's translation failure. False, and the counterexamples matter under load:
// SqlClient reports pool exhaustion as a plain IOE from SqlConnection.Open at ENUMERATION,
// ObjectDisposedException DERIVES from IOE, and EF's "a second operation was started" is one too.
// Each answered 400 "simplify your query" -- telling client retry logic not to retry -- while the
// same request without $expand correctly 500'd.
//
// TranslateThenMaterialize splits the provider's TRANSLATION phase (the query factory and
// GetEnumerator, where EF compiles) from MATERIALIZATION (from the first MoveNext, where the
// connection opens). Only translation yields 400.
//
// Three classifications, over MultiLevelExpandPushdownSqliteTests' Author->Books->Chapters fixture:
// a genuine EF translation failure still 400s; an IOE at materialization now 500s; a transient fault
// during materialization of a translatable shape 500s.
//
// The injected IOE this file used to call a translation failure was injected at ReaderExecuting --
// the MATERIALIZATION phase -- which is exactly what made the suite assert the defect as correct
// behaviour. It is now what it always was, and real translation coverage comes from
// MlAuthorUntranslatableProfile.

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

    /// <summary>#494: a plain <see cref="InvalidOperationException"/> raised where the command
    /// executes. This is the shape SqlClient's connection-pool exhaustion takes (from
    /// <c>SqlConnection.Open</c>) and the shape EF's "a second operation was started on this context
    /// instance" takes. Must classify as 500, never 400 — a 400 tells client retry logic not to
    /// retry a fault that is entirely retryable.</summary>
    InvalidOperationFault,

    /// <summary>#494: an <see cref="ObjectDisposedException"/> — which DERIVES from
    /// <see cref="InvalidOperationException"/>, the disposed-<c>DbContext</c> shape, and so matched
    /// the old type-list filter. Must classify as 500, never 400.</summary>
    ObjectDisposedFault,
}

/// <summary>
/// #494: a profile whose queryable carries a predicate EF Core cannot translate, so a request
/// against it trips a REAL translation failure in the phase EF really raises one — out of
/// <c>IQueryable.GetEnumerator()</c>, before any command executes. Same MlAuthor/MlBook/MlChapter
/// fixture and the same delegate-less <c>HasMany(x =&gt; x.Books)</c> pushdown as
/// <c>MlAuthorProfile</c>; the only difference is the untranslatable <c>Where</c>.
/// </summary>
public sealed class MlAuthorUntranslatableProfile : EntitySetProfile<int, MlAuthor>
{
    internal const string Marker = "untranslatable-client-side-method";

    // A client-side method inside a Where predicate. EF Core permits client evaluation in a final
    // projection but never in a predicate, so this is the provider-independent way to make
    // translation — and only translation — fail.
    private static string ClientOnly(string s) => s + Marker;

    public MlAuthorUntranslatableProfile(MultiLevelDbContext db) : base(x => x.Id)
    {
        EntitySetName = "UntranslatableAuthors";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => db.Authors.Where(a => ClientOnly(a.Name) == Marker);
        HasMany(x => x.Books);
    }
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
                    "simulated: Timeout expired. The timeout period elapsed prior to obtaining a "
                    + "connection from the pool. This may have occurred because all pooled connections "
                    + "were in use and max pool size was reached."),
                ThrowingReaderInterceptorMode.ObjectDisposedFault => new ObjectDisposedException(
                    "MultiLevelDbContext", "simulated: the DbContext has been disposed."),
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

    /// <summary>
    /// #494. This test previously asserted <c>400</c> and was named
    /// <c>InvalidOperationFault_DuringMaterialization_StillFailsLoud_400_NotMisclassifiedAs500</c>.
    /// Its expectation was wrong, and it is what kept the defect pinned as correct behaviour: the
    /// interceptor injects at <c>ReaderExecuting</c>, i.e. while the command runs, which is exactly
    /// where SqlClient raises pool exhaustion and where EF raises "a second operation was started on
    /// this context instance" — and both of those are plain
    /// <see cref="InvalidOperationException"/>s. Nothing about the request is wrong in that
    /// situation, so 400 was actively harmful: it tells client retry logic not to retry a fault that
    /// is entirely retryable, and it did so only for requests carrying <c>$expand</c> (the same
    /// request without one correctly 500'd).
    /// </summary>
    [Fact]
    public async Task InvalidOperationFault_DuringMaterialization_Is500_NotMisclassifiedAs400()
    {
        // The request shape (Books($expand=Chapters), no nested $top/$skip/$count) is the same
        // genuinely-translatable one used by the control and transient-fault tests above, so the
        // only thing wrong here is the injected infrastructure fault.
        _interceptor.Arm(ThrowingReaderInterceptorMode.InvalidOperationFault);

        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters)");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InternalServerError", body);
        Assert.DoesNotContain("InvalidQueryOption", body);

        // S7: the raw injected exception's message must never leak to the client on the 500 path.
        Assert.DoesNotContain("simulated:", body);
        Assert.DoesNotContain("max pool size", body);
    }

    /// <summary>
    /// #494: <see cref="ObjectDisposedException"/> derives from
    /// <see cref="InvalidOperationException"/>, so a disposed <c>DbContext</c> matched the old
    /// type-list filter and came back 400. It is a server fault in any phase.
    /// </summary>
    [Fact]
    public async Task ObjectDisposedFault_DuringMaterialization_Is500_NotMisclassifiedAs400()
    {
        _interceptor.Arm(ThrowingReaderInterceptorMode.ObjectDisposedFault);

        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters)");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InternalServerError", body);
        Assert.DoesNotContain("InvalidQueryOption", body);
        Assert.DoesNotContain("simulated:", body);
    }

    /// <summary>
    /// #494, the other side of the split: a request the provider genuinely cannot translate is still
    /// a <c>400</c>, and now it is proved with a REAL EF Core translation failure raised where EF
    /// raises one — out of <c>GetEnumerator()</c>, before any command executes — rather than with an
    /// exception injected at command-execution time. Also pins the log level: the diagnostic used to
    /// be written at <c>Debug</c>, invisible at production log levels, which is what left an operator
    /// with a spike of 400s and no server-side signal at all.
    /// </summary>
    [Fact]
    public async Task GenuineTranslationFailure_Is400_AndIsLoggedAtWarning()
    {
        var logs = new CapturingLoggerProvider();
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<MlAuthorUntranslatableProfile>(),
            configureServices: services =>
            {
                services.AddDbContext<MultiLevelDbContext>(o => o.UseSqlite(_connection));
                services.AddLogging(b => b.AddProvider(logs));
            });

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/UntranslatableAuthors?$orderby=id&$expand=Books");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("could not be translated by the underlying data provider", body);

        // S7: EF's own message (which names the LINQ expression, and with it the model's shape)
        // must not reach the client.
        Assert.DoesNotContain(MlAuthorUntranslatableProfile.Marker, body);

        // The operator does get it, at a level that is on in production.
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("$expand pushdown query failed to translate", StringComparison.Ordinal) &&
            e.Exception is InvalidOperationException);
    }
}
