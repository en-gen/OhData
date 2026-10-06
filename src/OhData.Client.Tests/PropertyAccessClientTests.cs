using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OhData.ClientTestBench;
using Xunit;

namespace OhData.Client.Tests;

public sealed class PropertyAccessClientTests : IClassFixture<BenchServerFixture>
{
    private readonly BenchServer _server;
    private readonly OhDataClient _client;

    public PropertyAccessClientTests(BenchServerFixture fixture)
    {
        _server = fixture.Server;
        _client = fixture.Client;
    }

    // -- against the bench server ---------------------------------------------------------

    [Fact]
    public async Task GetProperty_PresentValue_IsReturnedTyped()
    {
        Assert.Equal("note2", await _client.For<Gadget>("Gadgets").Key(2).GetPropertyAsync(g => g.Note));
        Assert.Equal(2, await _client.For<Gadget>("Gadgets").Key(2).GetPropertyAsync(g => g.Id));
    }

    [Fact]
    public async Task GetProperty_NullValue_IsDefault()
    {
        Assert.Null(await _client.For<Gadget>("Gadgets").Key(1).GetPropertyAsync(g => g.Note));
    }

    [Fact]
    public async Task GetRawValue_NullIs204ThenNull_PresentIsTheText()
    {
        using var absent = await _server.Http.GetAsync("Gadgets(1)/Note/$value");
        Assert.Equal(HttpStatusCode.NoContent, absent.StatusCode); // what the client is mapping

        Assert.Null(await _client.For<Gadget>("Gadgets").Key(1).GetRawValueAsync(g => g.Note));
        Assert.Equal("note2", await _client.For<Gadget>("Gadgets").Key(2).GetRawValueAsync(g => g.Note));
    }

    [Fact]
    public async Task MissingEntity_FollowsNotFoundBehavior_SoANonNullableZeroIsAmbiguous()
    {
        var keyed = _client.For<Gadget>("Gadgets").Key(99);

        // ReturnNull: a missing entity and a stored zero both read as 0 ...
        Assert.Equal(0, await keyed.GetPropertyAsync(g => g.Id));
        Assert.Null(await keyed.GetPropertyAsync(g => g.Note));
        Assert.Null(await keyed.GetRawValueAsync(g => g.Note));
    }

    [Fact]
    public async Task NullableTProp_DistinguishesAMissingEntityFromAZeroValue()
    {
        // ... so ask for a nullable TProp: the missing entity is null, a present value is not.
        Assert.Null(await _client.For<Gadget>("Gadgets").Key(99).GetPropertyAsync(g => (int?)g.Id));
        Assert.Equal(2, await _client.For<Gadget>("Gadgets").Key(2).GetPropertyAsync(g => (int?)g.Id));
    }

    [Fact]
    public async Task MissingEntity_Throws_WhenNotFoundBehaviorIsThrow()
    {
        using var client = new OhDataClient(_server.Http, new OhDataClientOptions { NotFoundBehavior = NotFoundBehavior.Throw });
        var keyed = client.For<Gadget>("Gadgets").Key(99);

        var ex1 = await Assert.ThrowsAsync<ODataClientException>(() => keyed.GetPropertyAsync(g => g.Note));
        var ex2 = await Assert.ThrowsAsync<ODataClientException>(() => keyed.GetRawValueAsync(g => g.Note));
        Assert.Equal(404, ex1.StatusCode);
        Assert.Equal(404, ex2.StatusCode);
    }

    // -- request shape --------------------------------------------------------------------

    private sealed class Doc
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        [JsonPropertyName("note_text")]
        public string? Note { get; set; }
        public byte[]? Data { get; set; }
    }

    [Fact]
    public async Task Urls_AreThePropertySegment_IgnoringSelectAndExpand_AndHonourNamingRules()
    {
        var handler = new RecordingHandler((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/$value", StringComparison.Ordinal)
                ? RecordingHandler.Text("raw")
                : RecordingHandler.Json("{\"value\":\"x\"}"));
        using var client = RecordingHandler.ClientFor(handler, o => o.JsonOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var keyed = client.For<Doc>("Docs").Select("Id").Expand("Foo").Key(7);

        await keyed.GetPropertyAsync(d => d.Title);
        await keyed.GetPropertyAsync(d => d.Note);
        Assert.Equal("raw", await keyed.GetRawValueAsync(d => d.Title));

        Assert.Equal(
            new[] { "http://localhost/odata/Docs(7)/title", "http://localhost/odata/Docs(7)/note_text", "http://localhost/odata/Docs(7)/title/$value" },
            handler.Requests.ConvertAll(r => r.Url));
    }

    [Fact]
    public async Task NonDirectMemberExpression_Throws()
    {
        using var client = RecordingHandler.ClientFor(new RecordingHandler((_, _) => RecordingHandler.Json("{}")));
        var keyed = client.For<Doc>("Docs").Key(1);

        await Assert.ThrowsAsync<ArgumentException>(() => keyed.GetPropertyAsync(d => d.Title!.Length));
        await Assert.ThrowsAsync<ArgumentException>(() => keyed.GetRawValueAsync(d => d.Title!.ToUpper()));
    }

    [Fact]
    public async Task EnvelopeWithNullValue_And204_AreBothDefault()
    {
        var nullEnvelope = new RecordingHandler((_, _) => RecordingHandler.Json("{\"value\":null}"));
        using var c1 = RecordingHandler.ClientFor(nullEnvelope);
        Assert.Null(await c1.For<Doc>("Docs").Key(1).GetPropertyAsync(d => d.Title));

        var noContent = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var c2 = RecordingHandler.ClientFor(noContent);
        Assert.Null(await c2.For<Doc>("Docs").Key(1).GetPropertyAsync(d => d.Title));
        Assert.Equal(0, await c2.For<Doc>("Docs").Key(1).GetPropertyAsync(d => d.Id));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"other\":1}")]
    public async Task ABodyThatIsNotAnObjectCarryingValue_Throws_NotDefault(string body)
    {
        var handler = new RecordingHandler((_, _) => RecordingHandler.Json(body));
        using var client = RecordingHandler.ClientFor(handler);

        await Assert.ThrowsAsync<JsonException>(
            () => client.For<Doc>("Docs").Key(1).GetPropertyAsync(d => d.Id));
    }

    [Fact]
    public async Task GetRawValue_OverABinaryMember_IsRefused_PointingAtGetProperty()
    {
        var handler = new RecordingHandler((_, _) => RecordingHandler.Text("x"));
        using var client = RecordingHandler.ClientFor(handler);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => client.For<Doc>("Docs").Key(1).GetRawValueAsync(d => d.Data));

        Assert.Contains("GetPropertyAsync", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetProperty_OverABinaryMember_DecodesBase64()
    {
        var handler = new RecordingHandler((_, _) => RecordingHandler.Json("{\"value\":\"AQID\"}"));
        using var client = RecordingHandler.ClientFor(handler);

        Assert.Equal(new byte[] { 1, 2, 3 }, await client.For<Doc>("Docs").Key(1).GetPropertyAsync(d => d.Data));
    }
}
