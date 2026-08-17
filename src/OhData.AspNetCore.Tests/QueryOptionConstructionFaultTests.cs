using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

internal sealed class QocWidget
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public List<QocChild>? Children { get; set; }
}

internal sealed class QocChild
{
    public int Id { get; set; }
    public string? Tag { get; set; }
}

/// <summary>Exercises every collection-read path plus <c>GetById</c> and <c>$count</c>, so the
/// #402 assertions cover all five <c>ODataQueryOptions</c> construction sites.</summary>
internal sealed class QocQueryableProfile : EntitySetProfile<int, QocWidget>
{
    private static readonly List<QocWidget> Store = new()
    {
        new QocWidget { Id = 1, Name = "a" },
    };

    public QocQueryableProfile() : base(x => x.Id)
    {
        EntitySetName = "QocQueryable";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        GetQueryable = ct => Task.FromResult(Store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));

        HasMany(
            navigation: x => x.Children!,
            getAll: (id, ct) => Task.FromResult<IEnumerable<QocChild>>(Array.Empty<QocChild>()));
    }
}

/// <summary>The simple <c>GetAll</c> collection path (no IQueryable).</summary>
internal sealed class QocGetAllProfile : EntitySetProfile<int, QocWidget>
{
    public QocGetAllProfile() : base(x => x.Id)
    {
        EntitySetName = "QocGetAll";
        GetAll = ct => Task.FromResult<IEnumerable<QocWidget>>(Array.Empty<QocWidget>());
    }
}

/// <summary>The Priority-1 path: the profile receives <see cref="ODataQueryOptions"/> directly, so
/// the framework still constructs them on its behalf.</summary>
internal sealed class QocPriority1Profile : ODataEntitySetProfile<int, QocWidget>
{
    public QocPriority1Profile() : base(x => x.Id)
    {
        EntitySetName = "QocPriority1";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        GetODataQueryable = (options, ct) => Task.FromResult(
            ODataQueryResult<QocWidget>.FromQueryable(Array.Empty<QocWidget>().AsQueryable()));
    }
}

/// <summary>
/// #402 — <c>GET /Set?$skiptoken=</c> returned <c>500</c>.
///
/// <para><b>What was measured.</b> Every system query option this framework accepts was probed
/// against a real <see cref="ODataQueryOptions{TEntity}"/> constructor with empty, malformed and
/// hostile values (<see cref="ConstructionThrowSet_IsExactlyODataExceptionAndArgumentException"/>
/// is that probe, kept as a live regression rather than a one-off script). Construction throws
/// exactly two types:</para>
/// <list type="bullet">
///   <item><description><c>Microsoft.OData.ODataException</c> — an empty value for <c>$filter</c>,
///   <c>$orderby</c>, <c>$top</c>, <c>$skip</c>, <c>$count</c>, <c>$search</c>, <c>$apply</c> or
///   <c>$compute</c>.</description></item>
///   <item><description><c>System.ArgumentException</c> — <c>$skiptoken=</c>, or a bare
///   <c>$skiptoken</c> with no value at all, from <c>SkipTokenQueryOption</c>'s own ctor. This is
///   the reported bug: the routes caught only <c>ODataException</c>, so it escaped to the
///   group-level exception filter and became a 500 on a request anyone can send.</description></item>
/// </list>
/// <para>Every other malformed value parses lazily and throws <c>ODataException</c> later, on the
/// first property access — which the routes' existing whole-body catch already handled.</para>
///
/// <para><b>Why the fix is a broad catch and not a longer catch list.</b> The measured set is a
/// measurement, not a contract. <c>ODataQueryOptions.BuildQueryOptions</c> constructs one option
/// object per recognized <c>$</c>-option, each with its own argument validation, and nothing in
/// Microsoft.AspNetCore.OData's public contract restricts those to <c>ODataException</c> — the
/// <c>$skiptoken</c> case is the proof, and a package bump can add another. Enumerating somebody
/// else's throw set is a mistake this file has made twice before. So the try is narrowed to
/// exactly the construction and the catch is widened to everything: inside that scope there is no
/// handler, no data source and no serializer, so any failure is a statement about the request URL
/// and 400 is right for the whole set, known and unknown.</para>
/// </summary>
public class QueryOptionConstructionFaultTests
{
    // ── The measurement itself ──────────────────────────────────────────────────────

    /// <summary>
    /// Probes <see cref="ODataQueryOptions{TEntity}"/> construction directly (no host, no routing)
    /// across every system query option the framework accepts, and asserts the CONSTRUCTION-time
    /// throw set is exactly {ODataException, ArgumentException} — with $skiptoken as the sole
    /// producer of the latter.
    ///
    /// This is deliberately an assertion about Microsoft's behaviour rather than OhData's. If a
    /// package bump changes it, this test fails and the change is visible; the shipped fix does not
    /// depend on the set being what it is today, only this documentation of it does.
    /// </summary>
    [Fact]
    public void ConstructionThrowSet_IsExactlyODataExceptionAndArgumentException()
    {
        var mb = new ODataConventionModelBuilder();
        mb.EntitySet<QocWidget>("QocWidgets");
        IEdmModel model = mb.GetEdmModel();
        var context = new ODataQueryContext(model, typeof(QocWidget), path: null);

        // Empty value for each option whose ctor parses eagerly.
        string[] odataExceptionCases =
        {
            "$filter=", "$orderby=", "$top=", "$skip=", "$count=", "$search=", "$apply=", "$compute=",
        };
        // The #402 case, in both spellings a client can produce.
        string[] argumentExceptionCases = { "$skiptoken=", "$skiptoken" };
        // Malformed-but-not-empty values: construction succeeds, the throw is deferred to first
        // property access (where the routes' whole-body ODataException catch already covers it).
        string[] lazyCases =
        {
            "$filter=Name eq", "$orderby=Name asc desc", "$select=,", "$expand=Children(",
            "$top=x", "$top=-1", "$skip=-1", "$count=yes", "$search=(((",
            "$expand=Children($levels=abc)", "$expand=Children($count=)",
        };
        // No system option is recognized at all -> nothing to parse, nothing to throw.
        string[] noThrowCases =
        {
            "$select=", "$expand=", "$skiptoken=abc", "$deltatoken=", "$levels=x", "$index=x",
            "$bogus=1", "$=1", "$id=",
        };

        foreach (string q in odataExceptionCases)
        {
            Assert.Throws<Microsoft.OData.ODataException>(() => Construct(context, q));
        }
        foreach (string q in argumentExceptionCases)
        {
            // ArgumentException, and specifically NOT an ODataException — that is the whole defect.
            var ex = Assert.ThrowsAny<ArgumentException>(() => Construct(context, q));
            Assert.IsNotType<Microsoft.OData.ODataException>(ex);
        }
        foreach (ODataQueryOptions<QocWidget> options in lazyCases.Select(q => Construct(context, q)))
        {
            // Constructing inside the enumeration is the assertion: these cases must survive
            // construction and throw only on first property access.
            Assert.Throws<Microsoft.OData.ODataException>(() => TouchEveryOption(options));
        }
        foreach (string q in noThrowCases)
        {
            TouchEveryOption(Construct(context, q));
        }
    }

    private static ODataQueryOptions<QocWidget> Construct(ODataQueryContext context, string queryString)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?" + queryString);
        return new ODataQueryOptions<QocWidget>(context, http.Request);
    }

    private static void TouchEveryOption(ODataQueryOptions<QocWidget> options)
    {
        _ = options.Filter?.FilterClause;
        _ = options.OrderBy?.OrderByClause;
        _ = options.SelectExpand?.SelectExpandClause;
        _ = options.Top?.Value;
        _ = options.Skip?.Value;
        _ = options.Count?.Value;
        _ = options.Search?.SearchClause;
        _ = options.Apply?.ApplyClause;
        _ = options.SkipToken?.RawValue;
    }

    // ── The reported defect, over the wire ──────────────────────────────────────────

    // Every route that constructs ODataQueryOptions. GetById and /$count are included because both
    // build options of their own (GetById only when $select or $expand is present, hence the
    // companion $select here) — the fix had to reach all five sites, not just the one in the
    // reported stack trace.
    public static TheoryData<string> SkipTokenRoutes() => new()
    {
        "/odata/QocQueryable?$skiptoken=",            // Priority-2 GetQueryable collection
        "/odata/QocQueryable/$count?$skiptoken=",     // collection $count
        "/odata/QocQueryable(1)?$select=Name&$skiptoken=", // GetById (options built only with $select/$expand)
        "/odata/QocGetAll?$skiptoken=",               // GetAll collection
        "/odata/QocPriority1?$skiptoken=",            // Priority-1 collection
    };

    [Theory]
    [MemberData(nameof(SkipTokenRoutes))]
    public async Task EmptySkipToken_Returns400WithODataErrorEnvelope(string url)
    {
        await using var fx = await BuildAsync();
        var response = await fx.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertODataErrorEnvelope(response, "InvalidQueryOption");
    }

    /// <summary>A bare <c>$skiptoken</c> with no <c>=</c> at all reaches the same constructor with
    /// an empty raw value, so it must land on the same 400 rather than the pre-fix 500.</summary>
    [Fact]
    public async Task ValuelessSkipToken_Returns400WithODataErrorEnvelope()
    {
        await using var fx = await BuildAsync();
        var response = await fx.Client.GetAsync("/odata/QocQueryable?$skiptoken");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertODataErrorEnvelope(response, "InvalidQueryOption");
    }

    /// <summary>The reported reproduction combined the empty $skiptoken with an otherwise valid
    /// query, which is how it shows up in a real client's paging loop.</summary>
    [Fact]
    public async Task EmptySkipTokenAlongsideValidFilter_Returns400WithODataErrorEnvelope()
    {
        await using var fx = await BuildAsync();
        var response = await fx.Client.GetAsync("/odata/QocQueryable?$filter=Name eq 'a'&$skiptoken=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertODataErrorEnvelope(response, "InvalidQueryOption");
    }

    /// <summary>The other measured construction-time throw type. These already returned 400 before
    /// the fix (they are ODataExceptions), and the point of pinning them is that narrowing the try
    /// scope did not change their status OR their message — the wording is Microsoft's and is still
    /// passed through verbatim.</summary>
    [Theory]
    [InlineData("$filter=", "$filter")]
    [InlineData("$orderby=", "$orderby")]
    [InlineData("$top=", "$top")]
    [InlineData("$skip=", "$skip")]
    [InlineData("$count=", "$count")]
    [InlineData("$search=", "$search")]
    public async Task EmptyOptionValue_Returns400WithMicrosoftsOwnMessage(string query, string optionName)
    {
        await using var fx = await BuildAsync();
        var response = await fx.Client.GetAsync("/odata/QocQueryable?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement error = await AssertODataErrorEnvelope(response, "InvalidQueryOption");
        Assert.Equal($"The value for OData query '{optionName}' cannot be empty.",
            error.GetProperty("message").GetString());
    }

    /// <summary>The broad catch must not swallow a genuine handler fault. Narrowing the try to the
    /// construction is what keeps this a 500 — a broad catch over the old whole-handler try would
    /// have relabelled a database outage as a client error.</summary>
    [Fact]
    public async Task HandlerFault_IsStill500NotRelabelled400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<QocThrowingProfile>());
        var response = await fx.Client.GetAsync("/odata/QocThrowing");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertODataErrorEnvelope(response, "InternalServerError");
    }

    private static Task<TestFixture> BuildAsync() => TestHostBuilder.BuildAsync(o =>
    {
        o.AddEntitySetProfile<QocQueryableProfile>();
        o.AddEntitySetProfile<QocGetAllProfile>();
        o.AddEntitySetProfile<QocPriority1Profile>();
    });

    /// <summary>Asserts the full OData error-envelope shape (§9.4), not merely the status code:
    /// a JSON object with an "error" member carrying non-empty "code" and "message" strings.</summary>
    private static async Task<JsonElement> AssertODataErrorEnvelope(HttpResponseMessage response, string code)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.True(body.TryGetProperty("error", out JsonElement error));
        Assert.Equal(JsonValueKind.Object, error.ValueKind);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        return error;
    }
}

internal sealed class QocThrowingProfile : EntitySetProfile<int, QocWidget>
{
    public QocThrowingProfile() : base(x => x.Id)
    {
        EntitySetName = "QocThrowing";
        GetQueryable = ct => throw new InvalidOperationException("simulated data-source outage");
    }
}
