using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

// #707 review: @odata.type resolution at every depth of a self-referencing hierarchy, and the
// type-discovery rules (server EDM names, types that carry their own JSON handling, registrations).
public sealed class DerivedTypeNestingTests
{
    internal class Node
    {
        public int Id { get; set; }
        public Node? Parent { get; set; }
        public List<Node> Children { get; set; } = [];
    }

    internal class Folder : Node
    {
        public string Path { get; set; } = "";
    }

    internal class Emp
    {
        public int Id { get; set; }
        public Emp? Manager { get; set; }
    }

    internal class Mgr : Emp
    {
        public int Level { get; set; }
    }

    internal class Director : Mgr
    {
        public string Div { get; set; } = "";
    }

    internal static class Zoo
    {
        public class Animal
        {
            public int Id { get; set; }
        }

        public class Bird : Animal
        {
            public bool Flies { get; set; }
        }
    }

    [JsonConverter(typeof(ShapeConverter))]
    internal class Shape
    {
        public int Id { get; set; }
        public string Tag { get; set; } = "";
    }

    internal class Circle : Shape
    {
        public double R { get; set; }
    }

    internal sealed class ShapeConverter : JsonConverter<Shape>
    {
        public override Shape Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            using var d = JsonDocument.ParseValue(ref reader);
            return new Shape { Id = d.RootElement.GetProperty("Id").GetInt32(), Tag = "custom" };
        }

        public override void Write(Utf8JsonWriter w, Shape v, JsonSerializerOptions o) => throw new NotSupportedException();
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
    [JsonDerivedType(typeof(Square), "sq")]
    internal class Poly
    {
        public int Id { get; set; }
    }

    internal class Square : Poly
    {
        public int Side { get; set; }
    }

    internal abstract class AbstractRoot
    {
        public int Id { get; set; }
    }

    internal abstract class AbstractMid : AbstractRoot;

    internal class Leaf : AbstractMid
    {
        public int X { get; set; }
    }

    internal record Rec(int Id);

    internal record RecDerived(int Id, string Extra) : Rec(Id);

    internal class GenericBase
    {
        public int Id { get; set; }
    }

    internal class GenericDerived<T> : GenericBase
    {
        public T? Value { get; set; }
    }

    internal class Registered : Exception
    {
        public int Code { get; set; }
    }

    private sealed class Stub(string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static OhDataClient Make(string body, OhDataClientOptions? options = null) =>
        new(new HttpClient(new Stub(body)) { BaseAddress = new Uri("http://localhost/odata/") }, options);

    private static string T<TType>() => "#" + typeof(TType).Namespace + "." + typeof(TType).Name;

    [Fact]
    public async Task BaseRoot_DerivedChild_KeepsTheDerivedMembers()
    {
        var client = Make($$$"""{"value":[{"Id":1,"Children":[{"@odata.type":"{{{T<Folder>()}}}","Id":2,"Path":"/a"}]}]}""");

        var rows = await client.For<Node>("N").ToListAsync();

        Assert.Equal("/a", Assert.IsType<Folder>(rows[0].Children[0]).Path);
    }

    [Fact]
    public async Task DerivedRoot_DerivedChild_KeepsTheDerivedMembers()
    {
        var client = Make($$$"""
            {"value":[{"@odata.type":"{{{T<Folder>()}}}","Id":1,
              "Children":[{"@odata.type":"{{{T<Folder>()}}}","Id":2,"Path":"/a"}]}]}
            """);

        var rows = await client.For<Node>("N").ToListAsync();

        Assert.Equal("/a", Assert.IsType<Folder>(rows[0].Children[0]).Path);
    }

    [Fact]
    public async Task DerivedRoot_BaseMember_StaysBase_AndItsOwnChildrenResolve()
    {
        var client = Make($$$"""
            {"value":[{"@odata.type":"{{{T<Folder>()}}}","Id":1,
              "Parent":{"Id":9,"Children":[{"@odata.type":"{{{T<Folder>()}}}","Id":3,"Path":"/p"}]}}]}
            """);

        var rows = await client.For<Node>("N").ToListAsync();

        Node parent = Assert.IsType<Node>(rows[0].Parent);
        Assert.Equal("/p", Assert.IsType<Folder>(parent.Children[0]).Path);
    }

    [Fact]
    public async Task Employee_BaseRow_ManagerIsDerived()
    {
        var client = Make($$$"""{"value":[{"Id":1,"Manager":{"@odata.type":"{{{T<Mgr>()}}}","Id":2,"Level":3}}]}""");

        var rows = await client.For<Emp>("E").ToListAsync();

        Assert.Equal(3, Assert.IsType<Mgr>(rows[0].Manager).Level);
    }

    [Fact]
    public async Task MidLevelRow_ManagerResolvesToTheLeaf()
    {
        var client = Make($$$"""
            {"value":[{"@odata.type":"{{{T<Mgr>()}}}","Id":1,"Level":1,
              "Manager":{"@odata.type":"{{{T<Director>()}}}","Id":2,"Div":"x"}}]}
            """);

        var rows = await client.For<Emp>("E").ToListAsync();

        Assert.Equal("x", Assert.IsType<Director>(Assert.IsType<Mgr>(rows[0]).Manager).Div);
    }

    [Fact]
    public async Task GrandchildRow_ReadAsRootOrAsMidLevel()
    {
        string body = $$$"""{"value":[{"@odata.type":"{{{T<Director>()}}}","Id":1,"Div":"d"}]}""";

        Assert.Equal("d", Assert.IsType<Director>((await Make(body).For<Emp>("E").ToListAsync())[0]).Div);
        Assert.IsType<Director>((await Make(body).For<Mgr>("E").ToListAsync())[0]);
    }

    [Fact]
    public async Task NestedClass_IsMatchedByTheServersEdmName_NamespaceAndSimpleName()
    {
        var client = Make("""{"value":[{"@odata.type":"#OhData.Client.Tests.Bird","Id":1,"Flies":true}]}""");

        var rows = await client.For<Zoo.Animal>("A").ToListAsync();

        Assert.True(Assert.IsType<Zoo.Bird>(rows[0]).Flies);
    }

    [Fact]
    public async Task TypeWithItsOwnConverter_IsLeftToThatConverter()
    {
        var client = Make($$$"""{"value":[{"@odata.type":"{{{T<Circle>()}}}","Id":1,"Tag":"server","R":2}]}""");

        var rows = await client.For<Shape>("S").ToListAsync();

        Assert.Equal("custom", rows[0].Tag);
    }

    [Fact]
    public async Task TypeWithJsonPolymorphic_IsLeftToSystemTextJson()
    {
        var client = Make($$$"""{"value":[{"$kind":"sq","Id":1,"Side":4,"@odata.type":"{{{T<Square>()}}}"}]}""");

        var rows = await client.For<Poly>("P").ToListAsync();

        Assert.Equal(4, Assert.IsType<Square>(rows[0]).Side);
    }

    [Fact]
    public async Task AbstractIntermediate_ResolvesTheConcreteLeaf()
    {
        var client = Make($$$"""{"value":[{"@odata.type":"{{{T<Leaf>()}}}","Id":1,"X":5}]}""");

        var rows = await client.For<AbstractRoot>("S").ToListAsync();

        Assert.Equal(5, Assert.IsType<Leaf>(rows[0]).X);
    }

    [Fact]
    public async Task Records_ResolveTheDerivedRecord()
    {
        var client = Make($$$"""{"value":[{"Id":1,"Extra":"e","@odata.type":"{{{T<RecDerived>()}}}"}]}""");

        var rows = await client.For<Rec>("S").ToListAsync();

        Assert.Equal("e", Assert.IsType<RecDerived>(rows[0]).Extra);
    }

    [Fact]
    public async Task ShortForm401Annotation_IsRecognised()
    {
        var client = Make($$$"""{"value":[{"@type":"{{{T<Mgr>()}}}","Id":1,"Level":2}]}""");

        var rows = await client.For<Emp>("E").ToListAsync();

        Assert.Equal(2, Assert.IsType<Mgr>(rows[0]).Level);
    }

    [Fact]
    public async Task NonStringODataType_ReadsAsTheDeclaredType()
    {
        var client = Make("""{"value":[{"@odata.type":5,"Id":1}]}""");

        var rows = await client.For<Emp>("E").ToListAsync();

        Assert.IsType<Emp>(rows[0]);
    }

    [Fact]
    public async Task RegistrationsAddedAfterConstruction_AreNotSeen()
    {
        var options = new OhDataClientOptions();
        var client = Make("""{"value":[{"@odata.type":"#My.Ns.M","Id":1,"Level":2}]}""", options);
        options.DerivedTypes.Add<Mgr>("My.Ns.M");

        var rows = await client.For<Emp>("E").ToListAsync();

        Assert.IsType<Emp>(rows[0]);
    }

    [Fact]
    public void Add_RejectsTypesThatCannotBeMaterialized()
    {
        var types = new OhDataClientOptions().DerivedTypes;

        Assert.Throws<ArgumentException>(() => types.Add(typeof(AbstractMid), "My.Ns.Mid"));
        Assert.Throws<ArgumentException>(() => types.Add(typeof(GenericDerived<>), "My.Ns.G"));
        Assert.Throws<ArgumentException>(() => types.Add(typeof(int), "My.Ns.I"));
    }

    [Fact]
    public async Task ClosedGenericSubclass_IsReachableThroughARegistration()
    {
        var options = new OhDataClientOptions();
        options.DerivedTypes.Add<GenericDerived<int>>("My.Ns.GInt");
        var client = Make("""{"value":[{"@odata.type":"#My.Ns.GInt","Id":1,"Value":7}]}""", options);

        var rows = await client.For<GenericBase>("G").ToListAsync();

        Assert.Equal(7, Assert.IsType<GenericDerived<int>>(rows[0]).Value);
    }

    [Fact]
    public async Task ExplicitRegistration_UnderAFrameworkBaseType_IsHonoured()
    {
        var options = new OhDataClientOptions();
        options.DerivedTypes.Add<Registered>("My.Ns.Registered");
        var client = Make("""{"value":[{"@odata.type":"#My.Ns.Registered","Code":5}]}""", options);

        var rows = await client.For<Exception>("X").ToListAsync();

        Assert.Equal(5, Assert.IsType<Registered>(rows[0]).Code);
    }
}
