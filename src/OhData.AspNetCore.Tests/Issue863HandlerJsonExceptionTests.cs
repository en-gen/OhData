using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #863: a JsonException thrown by a user handler is a server fault (500 generic envelope, as on the
// collection POST); only reading/deserializing the request body maps to 400 InvalidBody.

internal sealed class Issue863Item
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

// GetById throws, so the ETag and If-None-Match checks that run before the write handler hit it.
internal sealed class Issue863EtagProfile : EntitySetProfile<int, Issue863Item>
{
    public const string GetByIdMessage = "getbyid payload bad";
    public static readonly int[] Calls = new int[2]; // [0] = Put, [1] = Patch

    public Issue863EtagProfile() : base(x => x.Id)
    {
        EntitySetName = "Issue863EtagItems";
        AllowUpsert = true;
        UseETag(x => x.Name);
        GetById = (id, ct) => throw new JsonException(GetByIdMessage);
        Put = (id, m, ct) => { Interlocked.Increment(ref Calls[0]); return OhDataResult.Success<Issue863Item?>(m); };
        Patch = (id, delta, ct) => { Interlocked.Increment(ref Calls[1]); return OhDataResult.Success<Issue863Item?>(null!); };
    }
}

internal sealed class Issue863Profile : EntitySetProfile<int, Issue863Item>
{
    public const string PostMessage = "downstream payload bad";
    public const string PutMessage = "put handler bad";
    public const string PatchMessage = "patch handler bad";

    // [0] = Post calls, [1] = Put calls, [2] = Patch calls
    public static readonly int[] Calls = new int[3];

    public Issue863Profile() : base(x => x.Id)
    {
        EntitySetName = "Issue863Items";
        AllowUpsert = true;
        Post = (m, ct) =>
        {
            Interlocked.Increment(ref Calls[0]);
            throw new JsonException(PostMessage);
        };
        Put = (id, m, ct) =>
        {
            Interlocked.Increment(ref Calls[1]);
            if (id == 1) throw new JsonException(PutMessage);
            return OhDataResult.Success<Issue863Item?>(null!);
        };
        Patch = (id, delta, ct) =>
        {
            Interlocked.Increment(ref Calls[2]);
            throw new JsonException(PatchMessage);
        };
    }
}

public class Issue863HandlerJsonExceptionTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static void Reset() => Array.Clear(Issue863Profile.Calls);

    private static async Task<string> AssertServerFault(HttpResponseMessage response, string handlerMessage)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("InternalServerError", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(handlerMessage, body);
        return body;
    }

    public static TheoryData<string?> Policies => new() { null, "snake", "kebab" };

    private static Task<TestFixture> BuildAsync(string? policy) =>
        TestHostBuilder.BuildAsync(o =>
        {
            if (policy == "snake") o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.SnakeCaseLower);
            if (policy == "kebab") o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.KebabCaseLower);
            o.AddEntitySetProfile<Issue863Profile>();
        });

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task Put_ExistingKey_HandlerJsonException_Is500(string? policy)
    {
        await using var fx = await BuildAsync(policy);
        Reset();

        var response = await fx.Client.PutAsync("/odata/Issue863Items(1)", Json("{\"id\":1,\"name\":\"x\"}"));

        await AssertServerFault(response, Issue863Profile.PutMessage);
        Assert.Equal(1, Issue863Profile.Calls[1]);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task Put_MissingKey_UpsertCreateHandlerJsonException_Is500(string? policy)
    {
        await using var fx = await BuildAsync(policy);
        Reset();

        var response = await fx.Client.PutAsync("/odata/Issue863Items(99)", Json("{\"id\":99,\"name\":\"x\"}"));

        await AssertServerFault(response, Issue863Profile.PostMessage);
        Assert.Equal(1, Issue863Profile.Calls[0]);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task Patch_HandlerJsonException_Is500(string? policy)
    {
        await using var fx = await BuildAsync(policy);
        Reset();

        var response = await fx.Client.PatchAsync("/odata/Issue863Items(1)", Json("{\"name\":\"x\"}"));

        await AssertServerFault(response, Issue863Profile.PatchMessage);
        Assert.Equal(1, Issue863Profile.Calls[2]);
    }

    [Fact]
    public async Task Put_And_Patch_MatchCollectionPost_StatusAndBody()
    {
        await using var fx = await BuildAsync(null);
        Reset();

        var post = await fx.Client.PostAsync("/odata/Issue863Items", Json("{\"id\":5,\"name\":\"x\"}"));
        var putCreate = await fx.Client.PutAsync("/odata/Issue863Items(99)", Json("{\"id\":99,\"name\":\"x\"}"));
        var put = await fx.Client.PutAsync("/odata/Issue863Items(1)", Json("{\"id\":1,\"name\":\"x\"}"));
        var patch = await fx.Client.PatchAsync("/odata/Issue863Items(1)", Json("{\"name\":\"x\"}"));

        string postBody = await AssertServerFault(post, Issue863Profile.PostMessage);
        Assert.Equal(2, Issue863Profile.Calls[0]); // collection POST and the upsert create
        Assert.Equal(postBody, await AssertServerFault(putCreate, Issue863Profile.PostMessage));
        Assert.Equal(postBody, await AssertServerFault(put, Issue863Profile.PutMessage));
        Assert.Equal(postBody, await AssertServerFault(patch, Issue863Profile.PatchMessage));
    }

    [Theory]
    [InlineData("PUT", "{\"id\":1,\"name\":")]
    [InlineData("PATCH", "{\"id\":1,\"name\":")]
    [InlineData("PUT", "{\"Id\":1,\"Count\":\"abc\"}")]
    [InlineData("PATCH", "{\"Count\":\"abc\"}")]
    [InlineData("PATCH", "{\"Count\":1e400}")]
    public async Task MalformedBody_Is400InvalidBody_HandlerNotReached(string method, string payload)
    {
        await using var fx = await BuildAsync(null);
        Reset();

        var request = new HttpRequestMessage(new HttpMethod(method), "/odata/Issue863Items(1)") { Content = Json(payload) };
        var response = await fx.Client.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("InvalidBody", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(new[] { 0, 0, 0 }, Issue863Profile.Calls);
    }

    [Theory]
    [InlineData("PUT", "If-Match", "\"abc\"", "{\"id\":1,\"name\":\"x\"}")]
    [InlineData("PATCH", "If-Match", "\"abc\"", "{\"name\":\"x\"}")]
    [InlineData("PUT", "If-None-Match", "*", "{\"id\":1,\"name\":\"x\"}")]
    public async Task GetByIdJsonException_BeforeTheWriteHandler_Is500(string method, string header, string value, string payload)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Issue863EtagProfile>());
        Array.Clear(Issue863EtagProfile.Calls);

        var request = new HttpRequestMessage(new HttpMethod(method), "/odata/Issue863EtagItems(1)") { Content = Json(payload) };
        if (header.Length > 0) request.Headers.TryAddWithoutValidation(header, value);
        var response = await fx.Client.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("InternalServerError", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(Issue863EtagProfile.GetByIdMessage, body);
        Assert.Equal(new[] { 0, 0 }, Issue863EtagProfile.Calls);
    }

    private static async Task<TestFixture> BuildFaultHostAsync()
    {
        var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Issue863FaultProfile>());
        Issue863Faults.Reset();
        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/Issue863Faults")).StatusCode); // builds the registration
        return fx;
    }

    private static HttpRequestMessage Write(string method, string key, string? ifMatch = null)
    {
        string payload = method == "PUT" ? "{\"id\":" + key + ",\"name\":\"x\"}" : "{\"name\":\"x\"}";
        var request = new HttpRequestMessage(new HttpMethod(method), "/odata/Issue863Faults(" + key + ")") { Content = Json(payload) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }

    private static async Task AssertGenericServerFault(HttpResponseMessage response, string message)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("InternalServerError", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(message, body);
    }

    [Fact]
    public async Task ProfileConstructorJsonException_OnPutAndPatch_Is500_LikeThePost()
    {
        await using var fx = await BuildFaultHostAsync();
        try
        {
            Issue863Faults.Ctor = true;
            var post = await fx.Client.PostAsync("/odata/Issue863Faults", Json("{\"id\":5,\"name\":\"x\"}"));
            var put = await fx.Client.SendAsync(Write("PUT", "1"));
            var patch = await fx.Client.SendAsync(Write("PATCH", "1"));

            await AssertGenericServerFault(post, Issue863Faults.CtorMessage);
            await AssertGenericServerFault(put, Issue863Faults.CtorMessage);
            await AssertGenericServerFault(patch, Issue863Faults.CtorMessage);
            Assert.Equal(await post.Content.ReadAsStringAsync(), await put.Content.ReadAsStringAsync());
            Assert.Equal(await post.Content.ReadAsStringAsync(), await patch.Content.ReadAsStringAsync());
        }
        finally { Issue863Faults.Reset(); }
    }

    [Fact]
    public async Task KeyGetterJsonException_AfterBinding_OnPut_Is500()
    {
        await using var fx = await BuildFaultHostAsync();
        try
        {
            Issue863Faults.Key = true;
            await AssertGenericServerFault(await fx.Client.SendAsync(Write("PUT", "1")), Issue863Faults.GetterMessage);
        }
        finally { Issue863Faults.Reset(); }
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task RequiredPropertyGetterJsonException_DuringNullabilityValidation_Is500(string method)
    {
        await using var fx = await BuildFaultHostAsync();
        try
        {
            Issue863Faults.Name = true;
            await AssertGenericServerFault(await fx.Client.SendAsync(Write(method, "1")), Issue863Faults.GetterMessage);
        }
        finally { Issue863Faults.Reset(); }
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task ResponseSerializationJsonException_Is500(string method)
    {
        await using var fx = await BuildFaultHostAsync();
        try
        {
            Issue863Faults.Serialize = true;
            await AssertGenericServerFault(await fx.Client.SendAsync(Write(method, "1")), Issue863Faults.GetterMessage);
        }
        finally { Issue863Faults.Reset(); }
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task EtagSelectorJsonException_Is500(string method)
    {
        await using var fx = await BuildFaultHostAsync();
        try
        {
            Issue863Faults.Tag = true;
            await AssertGenericServerFault(await fx.Client.SendAsync(Write(method, "1", "\"abc\"")), Issue863Faults.GetterMessage);
        }
        finally { Issue863Faults.Reset(); }
    }
}

internal static class Issue863Faults
{
    public const string CtorMessage = "profile ctor payload bad";
    public const string GetterMessage = "getter payload bad";
    public static volatile bool Ctor, Key, Tag, Serialize, Name;
    public static void Reset() { Ctor = false; Key = false; Tag = false; Serialize = false; Name = false; }
}

internal sealed class Issue863FaultItem
{
    private int _id;
    public int Id { get => Issue863Faults.Key ? throw new JsonException(Issue863Faults.GetterMessage) : _id; set => _id = value; }
    private string _name = "";
    public string Name { get => Issue863Faults.Name ? throw new JsonException(Issue863Faults.GetterMessage) : _name; set => _name = value; }
    public int Tag => Issue863Faults.Tag ? throw new JsonException(Issue863Faults.GetterMessage) : 1;
    public int Extra => Issue863Faults.Serialize ? throw new JsonException(Issue863Faults.GetterMessage) : 1;
}

internal sealed class Issue863FaultProfile : EntitySetProfile<int, Issue863FaultItem>
{
    public Issue863FaultProfile() : base(x => x.Id)
    {
        if (Issue863Faults.Ctor) throw new JsonException(Issue863Faults.CtorMessage);
        EntitySetName = "Issue863Faults";
        UseETag(x => x.Tag);
        GetAll = ct => OhDataResult.Success<System.Collections.Generic.IEnumerable<Issue863FaultItem>>(Array.Empty<Issue863FaultItem>());
        GetById = (id, ct) => OhDataResult.Success<Issue863FaultItem?>(new Issue863FaultItem { Name = "n" });
        Post = (m, ct) => OhDataResult.Success(m);
        Put = (id, m, ct) => OhDataResult.Success<Issue863FaultItem?>(new Issue863FaultItem { Name = "n" });
        Patch = (id, d, ct) => OhDataResult.Success<Issue863FaultItem?>(new Issue863FaultItem { Name = "n" });
    }
}
