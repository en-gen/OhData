using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #786: a bound action's $top/$skip (including MaxTop) are validated BEFORE the handler runs, so a
// refused invocation mutates nothing (#359). Every refusal case asserts the handler was not reached.
public class Issue786BoundActionWindowValidationTests
{
    private const string BadTopMessageSuffix = "It must be a non-negative integer.";

    private static StringContent Json() => new("{}", Encoding.UTF8, "application/json");

    private static string Url(bool entity, string query) =>
        (entity ? "/odata/Za786Widgets(1)/ArchiveOne" : "/odata/Za786Widgets/ArchiveAll") + "?" + query;

    private static int Calls(bool entity) => entity ? Za786WidgetProfile.EntCalls : Za786WidgetProfile.CollCalls;

    private static async Task<TestFixture> BuildAsync()
    {
        Za786WidgetProfile.CollCalls = 0;
        Za786WidgetProfile.EntCalls = 0;
        return await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Za786WidgetProfile>());
    }

    [Theory]
    [InlineData(false, "$top=abc", "The value of '$top' ('abc') is invalid. " + BadTopMessageSuffix)]
    [InlineData(false, "$top=-1", "The value of '$top' ('-1') is invalid. " + BadTopMessageSuffix)]
    [InlineData(false, "$top=11", "The value of '$top' (11) exceeds the maximum allowed value (10).")]
    [InlineData(false, "$skip=abc", "The value of '$skip' ('abc') is invalid. " + BadTopMessageSuffix)]
    [InlineData(false, "$skip=-1", "The value of '$skip' ('-1') is invalid. " + BadTopMessageSuffix)]
    [InlineData(true, "$top=abc", "The value of '$top' ('abc') is invalid. " + BadTopMessageSuffix)]
    [InlineData(true, "$top=-1", "The value of '$top' ('-1') is invalid. " + BadTopMessageSuffix)]
    [InlineData(true, "$top=11", "The value of '$top' (11) exceeds the maximum allowed value (10).")]
    [InlineData(true, "$skip=abc", "The value of '$skip' ('abc') is invalid. " + BadTopMessageSuffix)]
    [InlineData(true, "$skip=-1", "The value of '$skip' ('-1') is invalid. " + BadTopMessageSuffix)]
    public async Task BadWindowValue_Returns400_AndDoesNotReachTheHandler(bool entity, string query, string message)
    {
        await using TestFixture fx = await BuildAsync();

        using HttpResponseMessage r = await fx.Client.PostAsync(Url(entity, query), Json());
        string body = await r.Content.ReadAsStringAsync();

        Assert.True(Calls(entity) == 0, $"{query} status={(int)r.StatusCode} counter={Calls(entity)}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("\"InvalidQueryOption\"", body);
        Assert.Contains(message, body.Replace("\u0027", "'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Top1_Control_ReachesTheHandlerOnce(bool entity)
    {
        await using TestFixture fx = await BuildAsync();

        using HttpResponseMessage r = await fx.Client.PostAsync(Url(entity, "$top=1"), Json());

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, Calls(entity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipAboveRowCount_IsNotRefused(bool entity)
    {
        await using TestFixture fx = await BuildAsync();

        using HttpResponseMessage r = await fx.Client.PostAsync(Url(entity, "$skip=500"), Json());

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, Calls(entity));
    }

    // C5: the window check runs after authorization. A caller the route refuses learns 401/403, never
    // a 400 about a query value, and the handler is not reached.
    [Theory]
    [InlineData(false, null, HttpStatusCode.Unauthorized)]
    [InlineData(true, null, HttpStatusCode.Unauthorized)]
    [InlineData(false, "u", HttpStatusCode.Forbidden)]
    [InlineData(true, "u", HttpStatusCode.Forbidden)]
    public async Task BadTop_OnAnAuthorizedAction_IsRefusedByAuthorizationFirst(
        bool entity, string? identity, HttpStatusCode expected)
    {
        Za786SecuredProfile.CollCalls = 0;
        Za786SecuredProfile.EntCalls = 0;
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Za786SecuredProfile>());

        string path = entity ? "/odata/Za786Secured(1)/ArchiveOne" : "/odata/Za786Secured/ArchiveAll";
        using var req = new HttpRequestMessage(HttpMethod.Post, path + "?$top=abc") { Content = Json() };
        if (identity is not null) req.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        using HttpResponseMessage r = await fx.Client.SendAsync(req);

        int calls = entity ? Za786SecuredProfile.EntCalls : Za786SecuredProfile.CollCalls;
        Assert.Equal(expected, r.StatusCode);
        Assert.Equal(0, calls);
    }

    // A repeated option: ?$top=1&$top=abc must not let the first valid value mask the invalid one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateTop_WithAnInvalidValue_IsRefused_AndDoesNotReachTheHandler(bool entity)
    {
        await using TestFixture fx = await BuildAsync();

        using HttpResponseMessage r = await fx.Client.PostAsync(Url(entity, "$top=1&$top=abc"), Json());

        Assert.True(Calls(entity) == 0, $"status={(int)r.StatusCode} counter={Calls(entity)}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // Precedence: the window refusal wins over a stale If-Match (which alone would be 412), because
    // it is checked before the precondition gate. Neither reaches the handler.
    [Fact]
    public async Task BadTop_WinsOverAStaleIfMatch_OnAnEntityAction()
    {
        Za786EtagProfile.EntCalls = 0;
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Za786EtagProfile>());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/odata/Za786Etags(1)/Touch?$top=abc") { Content = Json() };
        req.Headers.TryAddWithoutValidation("If-Match", "W/\"stale\"");
        using HttpResponseMessage r = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, Za786EtagProfile.EntCalls);

        // Control: with a valid window the same stale If-Match is the 412 it always was.
        using var ctl = new HttpRequestMessage(HttpMethod.Post, "/odata/Za786Etags(1)/Touch?$top=1") { Content = Json() };
        ctl.Headers.TryAddWithoutValidation("If-Match", "W/\"stale\"");
        using HttpResponseMessage c = await fx.Client.SendAsync(ctl);
        Assert.Equal(HttpStatusCode.PreconditionFailed, c.StatusCode);
        Assert.Equal(0, Za786EtagProfile.EntCalls);
    }

    // A window is only meaningful where the result can be a collection. A void or scalar action keeps
    // accepting $top/$skip as no-ops (docs/bound-operations.md), so the handler IS reached.
    [Theory]
    [InlineData("ArchiveVoid", "$top=11")]
    [InlineData("ArchiveVoid", "$top=abc")]
    [InlineData("ArchiveVoid", "$skip=-1")]
    [InlineData("CountAll", "$top=11")]
    [InlineData("CountAll", "$top=abc")]
    [InlineData("CountAll", "$skip=-1")]
    [InlineData("Name", "$top=11")]
    [InlineData("Name", "$top=abc")]
    [InlineData("Name", "$skip=-1")]
    public async Task NonCollectionAction_AcceptsTheWindowAsANoOp_AndReachesTheHandler(string action, string query)
    {
        Za786KindsProfile.Calls = 0;
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Za786KindsProfile>());

        string path = action == "Name" ? "/odata/Za786Kinds(1)/Name" : $"/odata/Za786Kinds/{action}";
        using HttpResponseMessage r = await fx.Client.PostAsync(path + "?" + query, Json());

        Assert.True((int)r.StatusCode is >= 200 and < 300, $"{action} {query} status={(int)r.StatusCode}");
        Assert.Equal(1, Za786KindsProfile.Calls);
    }

    // Task<object> can return a collection at runtime, so it stays pre-checked.
    [Theory]
    [InlineData("$top=abc")]
    [InlineData("$top=11")]
    [InlineData("$skip=-1")]
    public async Task ObjectReturningAction_IsStillPreChecked(string query)
    {
        Za786KindsProfile.Calls = 0;
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Za786KindsProfile>());

        using HttpResponseMessage r = await fx.Client.PostAsync("/odata/Za786Kinds/ListObject?" + query, Json());

        Assert.True(Za786KindsProfile.Calls == 0, $"{query} status={(int)r.StatusCode} counter={Za786KindsProfile.Calls}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}

internal sealed class Za786EtagProfile : EntitySetProfile<int, Za786Widget>
{
    internal static int EntCalls;

    public Za786EtagProfile() : base(x => x.Id)
    {
        EntitySetName = "Za786Etags";
        GetById = (id, _) => OhDataResult.Success<Za786Widget?>(new Za786Widget { Id = id, Name = "w" });
        UseETag(x => x.Name);
        BindEntityAction(Touch);
    }

    private Task<IEnumerable<Za786Widget>> Touch(int key)
    {
        EntCalls++;
        return Task.FromResult<IEnumerable<Za786Widget>>(new[] { new Za786Widget { Id = 1 } });
    }
}

internal sealed class Za786KindsProfile : EntitySetProfile<int, Za786Widget>
{
    internal static int Calls;

    public Za786KindsProfile() : base(x => x.Id)
    {
        EntitySetName = "Za786Kinds";
        MaxTop = 10;
        GetById = (id, _) => OhDataResult.Success<Za786Widget?>(new Za786Widget { Id = id, Name = "w" });
        BindAction(ArchiveVoid);
        BindAction(CountAll);
        BindAction(ListObject);
        BindEntityAction(Name);
    }

    private Task ArchiveVoid() { Calls++; return Task.CompletedTask; }
    private Task<int> CountAll() { Calls++; return Task.FromResult(3); }
    private Task<object> ListObject() { Calls++; return Task.FromResult<object>(new List<Za786Widget> { new() { Id = 1 } }); }
    private Task<Za786Widget> Name(int key) { Calls++; return Task.FromResult(new Za786Widget { Id = key, Name = "w" }); }
}

internal sealed class Za786Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class Za786WidgetProfile : EntitySetProfile<int, Za786Widget>
{
    internal static int CollCalls;
    internal static int EntCalls;

    public Za786WidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "Za786Widgets";
        MaxTop = 10;
        GetAll = _ => OhDataResult.Success<IEnumerable<Za786Widget>>(new[] { new Za786Widget { Id = 1, Name = "w" } });
        GetById = (id, _) => OhDataResult.Success<Za786Widget?>(new Za786Widget { Id = id, Name = "w" });
        BindAction(ArchiveAll);
        BindEntityAction(ArchiveOne);
    }

    private Task<IEnumerable<Za786Widget>> ArchiveAll()
    {
        CollCalls++;
        return Task.FromResult<IEnumerable<Za786Widget>>(new[] { new Za786Widget { Id = 1 }, new Za786Widget { Id = 2 } });
    }

    private Task<IEnumerable<Za786Widget>> ArchiveOne(int key)
    {
        EntCalls++;
        return Task.FromResult<IEnumerable<Za786Widget>>(new[] { new Za786Widget { Id = 1 }, new Za786Widget { Id = 2 } });
    }
}

internal sealed class Za786SecuredProfile : EntitySetProfile<int, Za786Widget>
{
    internal static int CollCalls;
    internal static int EntCalls;

    public Za786SecuredProfile() : base(x => x.Id)
    {
        EntitySetName = "Za786Secured";
        MaxTop = 10;
        GetAll = _ => OhDataResult.Success<IEnumerable<Za786Widget>>(new[] { new Za786Widget { Id = 1, Name = "w" } });
        GetById = (id, _) => OhDataResult.Success<Za786Widget?>(new Za786Widget { Id = id, Name = "w" });
        BindAction(ArchiveAll);
        BindEntityAction(ArchiveOne);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireRole("Admin")));
    }

    private Task<IEnumerable<Za786Widget>> ArchiveAll()
    {
        CollCalls++;
        return Task.FromResult<IEnumerable<Za786Widget>>(new[] { new Za786Widget { Id = 1 } });
    }

    private Task<IEnumerable<Za786Widget>> ArchiveOne(int key)
    {
        EntCalls++;
        return Task.FromResult<IEnumerable<Za786Widget>>(new[] { new Za786Widget { Id = 1 } });
    }
}
