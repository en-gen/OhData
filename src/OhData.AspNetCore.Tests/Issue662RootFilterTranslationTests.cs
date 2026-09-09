using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #662 — a root <c>$filter</c> the provider cannot translate answered <c>500</c>, where the same
/// condition on the <c>$expand</c> pushdown path has answered <c>400</c> since #494.
/// </summary>
/// <remarks>
/// <para>
/// #494 settled the rule: <i>a provider fault is classified by WHEN it was raised, not by what it
/// was</i>. A translation failure happens before any command executes, so it is the client's query
/// shape that is at fault and <c>400</c> is the honest answer; a <c>500</c> tells retry logic the
/// server is broken and the request is worth repeating, when it will fail identically forever.
/// <c>TranslateThenMaterialize</c> was applied to the three <c>$expand</c> execution sites and never
/// to the root collection read.
/// </para>
/// <para>
/// The fixture isolates the client's contribution deliberately. Its source is perfectly
/// translatable and a bare read succeeds; only a <c>$filter</c>/<c>$orderby</c> over the computed
/// member cannot translate. That is what makes the <b>blame</b> observable — see
/// <see cref="ATranslationFailureWithNoClientQueryOption_StaysAServerFault"/>, which pins the other
/// direction.
/// </para>
/// </remarks>
public sealed class Issue662RootFilterTranslationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private SqliteConnection _connection = null!;

    public Issue662RootFilterTranslationTests(ITestOutputHelper output) => _out = output;

    public Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var db = new N662Db(new DbContextOptionsBuilder<N662Db>().UseSqlite(_connection).Options);
        db.Database.EnsureCreated();
        db.Products.AddRange(
            new N662Product { Id = 1, Name = "Hammer" },
            new N662Product { Id = 2, Name = "Ball" });
        db.SaveChanges();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private Task<TestFixture> HostAsync() => TestHostBuilder.BuildAsync(
        o =>
        {
            o.AddEntitySetProfile<N662ComputedProfile>();
            o.AddEntitySetProfile<N662UnprojectedNavProfile>();
            o.AddEntitySetProfile<N662BrokenSourceProfile>();
            o.AddEntitySetProfile<N662PriorityOneProfile>();
            o.AddEntitySetProfile<N662EagerProviderProfile>();
        },
        configureServices: s => s.AddDbContext<N662Db>(
            b => b.UseSqlite(_connection), ServiceLifetime.Scoped));

    // ── The defect ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("N662Computed", "$filter=Label eq 'x'")]
    [InlineData("N662Computed", "$orderby=Label")]
    [InlineData("N662Computed", "$filter=Label eq 'x'&$select=Id")]
    [InlineData("N662Computed", "$filter=Label eq 'x'&$count=true")]
    // The /$count route reaches the same condition through a scalar aggregate, which has no
    // enumeration seam -- so leaving it out would have put this URL at 500 beside the sibling
    // collection read at 400, for one untranslatable expression.
    [InlineData("N662Computed/$count", "$filter=Label eq 'x'")]
    public async Task AnUntranslatableRootFilter_Is400_NotAServerFault(string set, string query)
    {
        await using TestFixture fixture = await HostAsync();

        HttpResponseMessage response = await fixture.Client.GetAsync($"/odata/{set}?{query}");
        string body = await response.Content.ReadAsStringAsync();
        _out.WriteLine($"{(int)response.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidQueryOption", body, StringComparison.Ordinal);

        // The same wording the $expand pushdown has used since #494 — one condition, one envelope,
        // whichever path reached it.
        Assert.Contains("could not be translated by the underlying data provider", body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape #662 measured: a DTO whose navigation is served by <c>batchGetAll</c> and so is
    /// absent from the projected queryable, which is what #651/#661 recommends.
    /// </summary>
    [Fact]
    public async Task AFilterThroughANavigationOutsideTheProjection_Is400()
    {
        await using TestFixture fixture = await HostAsync();

        HttpResponseMessage response = await fixture.Client.GetAsync(
            "/odata/N662Batch?$filter=Tags/any(t: t/Label eq 'sale')");
        string body = await response.Content.ReadAsStringAsync();
        _out.WriteLine($"{(int)response.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("could not be translated by the underlying data provider", body,
            StringComparison.Ordinal);
    }

    // ── The other direction: blame ────────────────────────────────────────────────────────────

    /// <summary>
    /// A source the profile itself made untranslatable is a SERVER fault and must stay <c>500</c>.
    /// </summary>
    /// <remarks>
    /// The gate is the same one <c>EvaluateQueryWithArithmeticFaultGuard</c> already applies to a
    /// divide-by-zero: reclassify only when the request actually carried the option being blamed.
    /// Without it this fix would relabel every misconfigured profile as the client's fault, which is
    /// #494's own inversion running the other way.
    /// </remarks>
    [Fact]
    public async Task ATranslationFailureWithNoClientQueryOption_StaysAServerFault()
    {
        await using TestFixture fixture = await HostAsync();

        HttpResponseMessage response = await fixture.Client.GetAsync("/odata/N662Broken");
        string body = await response.Content.ReadAsStringAsync();
        _out.WriteLine($"{(int)response.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("InternalServerError", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A profile whose OWN source cannot translate stays a server fault even when the request
    /// carried an option — presence is not attribution.
    /// </summary>
    /// <remarks>
    /// The first cut of this fix gated on the request having merely CARRIED <c>$filter</c>, which
    /// blamed the client for a permanent server misconfiguration and told them to simplify a
    /// predicate that is not the problem: <c>Name eq 'Hammer'</c> is a translatable comparison over
    /// a real column, while the untranslatable clause is the profile's own <c>Where</c>. The gate
    /// now also requires the unmodified source to compile.
    /// </remarks>
    [Theory]
    [InlineData("/odata/N662Broken?$filter=Name eq 'Hammer'")]
    [InlineData("/odata/N662Broken?$orderby=Name")]
    [InlineData("/odata/N662Broken/$count?$filter=Name eq 'Hammer'")]
    public async Task ABrokenSourceIsNotBlamedOnTheClient_EvenWithAnOptionPresent(string url)
    {
        await using TestFixture fixture = await HostAsync();

        HttpResponseMessage response = await fixture.Client.GetAsync(url);
        _out.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A Priority-1 profile composed the query itself, so the framework has nothing to attribute a
    /// translation failure to and does not reclassify.
    /// </summary>
    /// <remarks>
    /// Documented rather than fixed: attribution needs to know what the framework added, and on this
    /// route it added nothing. Blaming the client on option PRESENCE alone is the defect the test
    /// above pins.
    /// </remarks>
    [Theory]
    [InlineData("/odata/N662P1?$filter=Label eq 'x'")]
    [InlineData("/odata/N662P1?$orderby=Label")]
    [InlineData("/odata/N662P1/$count?$filter=Label eq 'x'")]
    public async Task APriorityOneProfile_IsNotReclassified(string url)
    {
        await using TestFixture fixture = await HostAsync();

        HttpResponseMessage response = await fixture.Client.GetAsync(url);
        _out.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A provider that executes on <c>GetEnumerator()</c> keeps its <c>500</c>, because the phase
    /// split's premise does not hold for it.
    /// </summary>
    /// <remarks>
    /// The whole split rests on <c>GetEnumerator()</c> compiling without doing I/O, which is an EF
    /// Core property rather than a contract of <see cref="IQueryable"/>. <c>GetQueryable</c> accepts
    /// any queryable, and the first cut of this fix reported a transient, retryable remote fault as
    /// <c>400</c> "simplify the expression" — #494's own inversion, reintroduced — and re-ran the
    /// failing query against the degraded backend to do it.
    /// </remarks>
    [Theory]
    [InlineData("/odata/N662Eager?$filter=Name eq 'Hammer'")]
    [InlineData("/odata/N662Eager/$count?$filter=Name eq 'Hammer'")]
    public async Task ANonEfProviderKeepsItsServerFault(string url)
    {
        await using TestFixture fixture = await HostAsync();
        EagerFaultingProvider.Reset();

        HttpResponseMessage response = await fixture.Client.GetAsync(url);
        _out.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Exactly one. The probe must not re-run a query against an already-degraded backend, which
        // is the second harm the EF gate removes.
        Assert.Equal(1, EagerFaultingProvider.Executions);
    }

    /// <summary>
    /// A count whose query compiles but whose EXECUTION fails keeps its <c>500</c>.
    /// </summary>
    /// <remarks>
    /// This is the whole reason <c>CountRootQuery</c> probes rather than stopping at the source
    /// check: the fault is raised by a perfectly translatable query, from the phase that belongs to
    /// the server. Injected at <c>ReaderExecuting</c>, which is the materialization window, using
    /// the same interceptor shape <c>ExpandPushdownExceptionClassificationTests</c> uses.
    /// </remarks>
    [Fact]
    public async Task ACountThatCompilesButFailsToExecute_StaysAServerFault()
    {
        await using TestFixture fixture = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N662ComputedProfile>(),
            configureServices: s => s.AddDbContext<N662Db>(
                b => b.UseSqlite(_connection).AddInterceptors(new N662FaultingReaderInterceptor()),
                ServiceLifetime.Scoped));

        HttpResponseMessage response = await fixture.Client.GetAsync(
            "/odata/N662Computed/$count?$filter=Name eq 'Hammer'");
        _out.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── Control: nothing about the working path moved ─────────────────────────────────────────

    [Theory]
    [InlineData("/odata/N662Computed")]
    [InlineData("/odata/N662Computed?$filter=Name eq 'Hammer'")]
    [InlineData("/odata/N662Computed?$orderby=Name desc")]
    [InlineData("/odata/N662Computed?$count=true")]
    [InlineData("/odata/N662Computed/$count")]
    public async Task ATranslatableQuery_IsUnaffected(string url)
    {
        await using TestFixture fixture = await HostAsync();

        HttpResponseMessage response = await fixture.Client.GetAsync(url);
        _out.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────────────────────

public sealed class N662Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<N662Tag> Tags { get; set; } = new();
}

public sealed class N662Tag
{
    public int Id { get; set; }
    public int N662ProductId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class N662Db : DbContext
{
    public N662Db(DbContextOptions<N662Db> options) : base(options) { }

    public DbSet<N662Product> Products => Set<N662Product>();
    public DbSet<N662Tag> Tags => Set<N662Tag>();
}

public sealed class N662Dto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Computed by a client-side method, so a predicate over it cannot translate.</summary>
    public string Label { get; set; } = "";
}

public sealed class N662NavDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Declared in the EDM and served by <c>batchGetAll</c>, never projected.</summary>
    public List<N662TagDto> Tags { get; set; } = new();
}

public sealed class N662TagDto
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// A translatable source with one client-computed member. A bare read succeeds — EF evaluates the
/// final projection on the client — so only a <c>$filter</c>/<c>$orderby</c> over that member fails,
/// which is what isolates the client's contribution.
/// </summary>
public sealed class N662ComputedProfile : EntitySetProfile<int, N662Dto>
{
    private static string Decorate(string s) => "<" + s + ">";

    public N662ComputedProfile(N662Db db) : base(x => x.Id)
    {
        EntitySetName = "N662Computed";
        FilterEnabled = OrderByEnabled = SelectEnabled = CountEnabled = true;

        GetQueryable = () => db.Products.Select(p => new N662Dto
        {
            Id = p.Id,
            Name = p.Name,
            Label = Decorate(p.Name),
        });
    }
}

/// <summary>
/// #662's measured shape: the navigation is declared and served by a batch delegate, so it is not
/// in the projected queryable and a predicate through it cannot translate.
/// </summary>
public sealed class N662UnprojectedNavProfile : EntitySetProfile<int, N662NavDto>
{
    public N662UnprojectedNavProfile(N662Db db) : base(x => x.Id)
    {
        EntitySetName = "N662Batch";
        FilterEnabled = OrderByEnabled = SelectEnabled = ExpandEnabled = CountEnabled = true;

        GetQueryable = () => db.Products.Select(p => new N662NavDto { Id = p.Id, Name = p.Name });

        HasMany<N662TagDto>(
            x => x.Tags,
            batchGetAll: (keys, ct) => Task.FromResult(
                db.Tags.Where(t => keys.Contains(t.N662ProductId))
                    .Select(t => new { t.N662ProductId, Dto = new N662TagDto { Id = t.Id, Label = t.Label } })
                    .ToLookup(x => x.N662ProductId, x => x.Dto)));
    }
}

/// <summary>
/// The source itself cannot translate, so there is no client query option to blame and the fault
/// stays the server's.
/// </summary>
public sealed class N662BrokenSourceProfile : EntitySetProfile<int, N662Dto>
{
    private const string Marker = "n662-client-side";

    private static string ClientOnly(string s) => s + Marker;

    public N662BrokenSourceProfile(N662Db db) : base(x => x.Id)
    {
        EntitySetName = "N662Broken";
        FilterEnabled = OrderByEnabled = SelectEnabled = CountEnabled = true;

        GetQueryable = () => db.Products
            .Where(p => ClientOnly(p.Name) == Marker)
            .Select(p => new N662Dto { Id = p.Id, Name = p.Name, Label = p.Name });
    }
}

/// <summary>
/// The same untranslatable member, reached through a Priority-1 profile that applies the options
/// itself.
/// </summary>
public sealed class N662PriorityOneProfile : ODataEntitySetProfile<int, N662Dto>
{
    private static string Decorate(string s) => "<" + s + ">";

    public N662PriorityOneProfile(N662Db db) : base(x => x.Id)
    {
        EntitySetName = "N662P1";
        FilterEnabled = OrderByEnabled = SelectEnabled = CountEnabled = true;

        GetODataQueryable = (options, ct) =>
        {
            IQueryable<N662Dto> q = db.Products.Select(p => new N662Dto
            {
                Id = p.Id,
                Name = p.Name,
                Label = Decorate(p.Name),
            });

            return Task.FromResult(new ODataQueryResult<N662Dto>
            {
                Items = (IQueryable<N662Dto>)options.ApplyTo(q),
            });
        };
    }
}

/// <summary>
/// A provider that does its work on <c>GetEnumerator()</c> — the shape every non-EF LINQ provider
/// is free to take — and fails persistently, standing in for a transient remote fault.
/// </summary>
public sealed class EagerFaultingProvider : IQueryProvider
{
    public static int Executions;

    /// <summary>Reset by the test, never by queryable resolution — the count is of provider calls
    /// per request, and resolving the source is not one of them.</summary>
    public static void Reset() => Executions = 0;

    public IQueryable CreateQuery(System.Linq.Expressions.Expression expression) =>
        new EagerFaultingQueryable<N662Dto>(expression);

    public IQueryable<TElement> CreateQuery<TElement>(System.Linq.Expressions.Expression expression) =>
        new EagerFaultingQueryable<TElement>(expression);

    public object Execute(System.Linq.Expressions.Expression expression) => Execute<object>(expression);

    public TResult Execute<TResult>(System.Linq.Expressions.Expression expression)
    {
        Executions++;
        throw new InvalidOperationException("simulated: transient remote provider fault (retryable)");
    }
}

public sealed class EagerFaultingQueryable<T> : IOrderedQueryable<T>
{
    public EagerFaultingQueryable(System.Linq.Expressions.Expression? expression = null)
    {
        Expression = expression ?? System.Linq.Expressions.Expression.Constant(this);
        Provider = new EagerFaultingProvider();
    }

    public Type ElementType => typeof(T);

    public System.Linq.Expressions.Expression Expression { get; }

    public IQueryProvider Provider { get; }

    public IEnumerator<T> GetEnumerator() => Provider.Execute<IEnumerator<T>>(Expression);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A profile over the eager, faulting provider.</summary>
public sealed class N662EagerProviderProfile : EntitySetProfile<int, N662Dto>
{
    public N662EagerProviderProfile() : base(x => x.Id)
    {
        EntitySetName = "N662Eager";
        FilterEnabled = OrderByEnabled = SelectEnabled = CountEnabled = true;

        GetQueryable = () => new EagerFaultingQueryable<N662Dto>();
    }
}

/// <summary>
/// Faults where the command EXECUTES — the materialization window — leaving the query itself
/// perfectly translatable. The shape #494 names: SqlClient reports pool exhaustion as a plain
/// <see cref="InvalidOperationException"/> from here, and it must never be read as the client's
/// bad query.
/// </summary>
public sealed class N662FaultingReaderInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command,
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result) =>
        throw new InvalidOperationException(
            "simulated: Timeout expired. The timeout period elapsed prior to obtaining a connection "
            + "from the pool.");
}
