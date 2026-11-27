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
    }
}
