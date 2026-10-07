using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

// #707: the client resolves @odata.type to a CLR type on every read path. These are unit-level
// (hand-written bodies) so they can cover what the bench model cannot: key order, a nested
// polymorphic collection, an unknown type, a renamed namespace and the write request body.

public sealed class DerivedTypeMaterializationTests
{
    internal class Pet
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    internal class Dog : Pet
    {
        public string Breed { get; set; } = "";
    }

    internal class Cat : Pet
    {
        public bool Indoor { get; set; }
    }

    internal class Household
    {
        public int Id { get; set; }
        public List<Pet> Pets { get; set; } = [];
        public Pet? Favourite { get; set; }
    }

    internal sealed class Box
    {
        public int Id { get; set; }
        public Plain? Item { get; set; }
    }

    internal sealed class Plain
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class Stub(string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    // The server's default EDM name: namespace + simple name, whatever the CLR nesting.
    private static string DogType => "#" + typeof(Dog).Namespace + "." + nameof(Dog);
    private static string CatType => "#" + typeof(Cat).Namespace + "." + nameof(Cat);

    private static (OhDataClient Client, Stub Handler) Make(string body, OhDataClientOptions? options = null)
    {
        var handler = new Stub(body);
        var http = new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost/odata/") };
        return (new OhDataClient(http, options), handler);
    }

    [Fact]
    public async Task Collection_ResolvesByConvention_WhateverTheKeyOrder()
    {
        // @odata.type last for the Dog, first for the Cat: STJ's built-in polymorphism would need it first.
        string body = $$"""
            {"value":[
              {"Id":1,"Name":"Rex","Breed":"Lab","@odata.type":"{{DogType}}"},
              {"@odata.type":"{{CatType}}","Id":2,"Name":"Tom","Indoor":true},
              {"Id":3,"Name":"Plain"}]}
            """;
        var (client, _) = Make(body);

        var rows = await client.For<Pet>("Pets").ToListAsync();

        Assert.Equal("Lab", Assert.IsType<Dog>(rows[0]).Breed);
        Assert.True(Assert.IsType<Cat>(rows[1]).Indoor);
        Assert.IsType<Pet>(rows[2]);
    }

    [Fact]
    public async Task LargeStreamedPage_ResolvesEveryRow()
    {
        // Bigger than System.Text.Json's stream buffer, so a row is read from a non-final block.
        var body = new StringBuilder("{\"value\":[");
        for (int i = 0; i < 2000; i++)
        {
            if (i > 0) body.Append(',');
            body.Append(i % 2 == 0
                ? $$"""{"Id":{{i}},"Name":"n{{i}}","Breed":"b{{i}}","@odata.type":"{{DogType}}"}"""
                : "{\"Id\":" + i + ",\"Name\":\"n" + i + "\",\"Tags\":{\"a\":[1,2,{\"b\":3}]}}");
        }

        body.Append("]}");
        var (client, _) = Make(body.ToString());

        var rows = await client.For<Pet>("Pets").ToListAsync();

        Assert.Equal(2000, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i % 2 == 0) Assert.Equal($"b{i}", Assert.IsType<Dog>(rows[i]).Breed);
            else Assert.IsType<Pet>(rows[i]);
        }
    }

    [Fact]
    public async Task TypeNameWithoutHash_IsAccepted()
    {
        var (client, _) = Make($$"""{"value":[{"Id":1,"Breed":"Lab","@odata.type":"{{DogType.TrimStart('#')}}"}]}""");

        var rows = await client.For<Pet>("Pets").ToListAsync();

        Assert.IsType<Dog>(rows[0]);
    }

    [Fact]
    public async Task Single_ByKey_ResolvesDerivedType()
    {
        var (client, _) = Make($$"""{"@odata.type":"{{DogType}}","Id":1,"Name":"Rex","Breed":"Lab"}""");

        Pet? pet = await client.For<Pet>("Pets").Key(1).GetAsync();

        Assert.Equal("Lab", Assert.IsType<Dog>(pet).Breed);
    }

    // #720: GetPropertyAsync reads the `value` envelope through the same polymorphism-aware options.
    [Fact]
    public async Task GetProperty_PolymorphicSingleValue_ResolvesDerivedType()
    {
        var (client, _) = Make($$$"""{"@odata.context":"x","value":{"Id":1,"Name":"Rex","Breed":"Lab","@odata.type":"{{{DogType}}}"}}""");

        Pet? pet = await client.For<Household>("Households").Key(1).GetPropertyAsync(h => h.Favourite);

        Assert.Equal("Lab", Assert.IsType<Dog>(pet).Breed);
    }

    [Fact]
    public async Task GetProperty_PolymorphicCollectionValue_ResolvesDerivedTypes()
    {
        var (client, _) = Make($$"""
            {"value":[{"@odata.type":"{{CatType}}","Id":2,"Name":"Tom","Indoor":true},
                      {"Id":1,"Name":"Rex","Breed":"Lab","@odata.type":"{{DogType}}"},
                      {"Id":3,"Name":"Plain"}]}
            """);

        List<Pet>? pets = await client.For<Household>("Households").Key(1).GetPropertyAsync(h => h.Pets);

        Assert.NotNull(pets);
        Assert.True(Assert.IsType<Cat>(pets[0]).Indoor);
        Assert.Equal("Lab", Assert.IsType<Dog>(pets[1]).Breed);
        Assert.IsType<Pet>(pets[2]);
    }

    [Fact]
    public async Task GetProperty_NonPolymorphicValue_IsUnchanged()
    {
        var (client, _) = Make("""{"value":{"Id":5,"Name":"x"}}""");

        Plain? plain = await client.For<Box>("Boxes").Key(1).GetPropertyAsync(b => b.Item);

        Assert.Equal(5, plain!.Id);
    }

    [Fact]
    public async Task NestedCollectionAndSingle_ResolveDerivedTypes()
    {
        string body = $$$"""
            {"value":[{"Id":1,
              "Pets":[{"Id":1,"Breed":"Lab","@odata.type":"{{{DogType}}}"},{"Id":2,"Name":"x"}],
              "Favourite":{"@odata.type":"{{{CatType}}}","Id":9,"Indoor":true}}]}
            """;
        var (client, _) = Make(body);

        var rows = await client.For<Household>("Households").ToListAsync();

        Assert.IsType<Dog>(rows[0].Pets[0]);
        Assert.IsType<Pet>(rows[0].Pets[1]);
        Assert.IsType<Cat>(rows[0].Favourite);
    }

    [Fact]
    public async Task AnnotatedPage_ResolvesDerivedTypesAndKeepsAnnotations()
    {
        var (client, _) = Make($$"""{"value":[{"@odata.type":"{{DogType}}","Id":1,"Breed":"Lab"}]}""");

        var page = await client.For<Pet>("Pets").ToAnnotatedPageAsync();

        Assert.IsType<Dog>(page.Entries[0].Entity);
        Assert.True(page.Entries[0].Annotations.TryGetValue("@odata.type", out _));
    }

    [Fact]
    public async Task UnknownODataType_FallsBackToTheBaseType()
    {
        var (client, _) = Make("""{"value":[{"Id":1,"Name":"x","@odata.type":"#Other.Ns.Hamster"}]}""");

        var rows = await client.For<Pet>("Pets").ToListAsync();

        Assert.IsType<Pet>(rows[0]);
        Assert.Equal("x", rows[0].Name);
    }

    [Fact]
    public async Task RenamedNamespace_ResolvesThroughTheExplicitRegistry()
    {
        string body = """{"value":[{"Id":1,"Breed":"Lab","@odata.type":"#My.Ns.Hound"}]}""";

        var (unregistered, _) = Make(body);
        Assert.IsType<Pet>((await unregistered.For<Pet>("Pets").ToListAsync())[0]);

        var options = new OhDataClientOptions();
        options.DerivedTypes.Add<Dog>("My.Ns.Hound");
        var (registered, _) = Make(body, options);
        Assert.Equal("Lab", Assert.IsType<Dog>((await registered.For<Pet>("Pets").ToListAsync())[0]).Breed);
    }

    [Fact]
    public async Task UserJsonOptions_NamingPolicyAndConverters_StillApply()
    {
        var options = new OhDataClientOptions
        {
            JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        };
        var (client, _) = Make($$"""{"value":[{"@odata.type":"{{DogType}}","id":1,"breed":"Lab"}]}""", options);

        var rows = await client.For<Pet>("Pets").ToListAsync();

        Assert.Equal("Lab", Assert.IsType<Dog>(rows[0]).Breed);
        Assert.Same(JsonNamingPolicy.CamelCase, options.JsonOptions.PropertyNamingPolicy);
    }

    [Fact]
    public async Task Write_SerializesAsTheDeclaredTypeWithoutODataType_AndEchoIsDerived()
    {
        var (client, handler) = Make($$"""{"@odata.type":"{{DogType}}","Id":5,"Name":"Rex","Breed":"Lab"}""");

        Pet? echo = await client.For<Pet>("Pets").InsertAsync(new Dog { Id = 5, Name = "Rex", Breed = "Lab" });

        Assert.Equal("""{"Id":5,"Name":"Rex"}""", handler.LastRequestBody);
        Assert.IsType<Dog>(echo);
    }

    [Fact]
    public async Task NonPolymorphicType_IsUntouched()
    {
        var (client, _) = Make("""{"value":[{"Id":1,"Name":"w"}]}""");

        var rows = await client.For<Plain>("Plains").ToListAsync();

        Assert.Equal("w", rows[0].Name);
    }
}
