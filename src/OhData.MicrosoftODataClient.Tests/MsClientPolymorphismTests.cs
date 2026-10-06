using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.OData.Client;
using Microsoft.OData.ModelBuilder;
using OhData.ClientTestBench;
using Xunit;

namespace OhData.MicrosoftODataClient.Tests.MsClient;

// #652 / #628: Microsoft.OData.Client resolves a row's CLR type from its @odata.type annotation.
// OhData omitted the annotation for derived rows until #628, which is a §4.5.3 MUST; this is the
// conformance claim #628 rests on, asserted by the industry-standard client rather than by byte
// comparison. The CLR types are the shared bench's own (BenchModels.cs, linked).

public sealed class MsClientPolymorphismTests : IAsyncLifetime
{
    private BenchServer _server = null!;
    private DataServiceContext _context = null!;

    public async Task InitializeAsync()
    {
        _server = await BenchServer.BuildAsync();

        var modelBuilder = new ODataConventionModelBuilder();
        modelBuilder.EntitySet<Award>("Awards");
        modelBuilder.EntityType<AcademyAward>();
        modelBuilder.EntityType<FestivalAward>();
        var model = modelBuilder.GetEdmModel();

        _context = new DataServiceContext(_server.Http.BaseAddress!);
        _context.Configurations.RequestPipeline.OnMessageCreating =
            args => new TestServerRequestMessage(args, _server.Http);
        _context.Format.UseJson(model);
        _context.ResolveType = name => name switch
        {
            "OhData.ClientTestBench.Award" => typeof(Award),
            "OhData.ClientTestBench.AcademyAward" => typeof(AcademyAward),
            "OhData.ClientTestBench.FestivalAward" => typeof(FestivalAward),
            "OhData.ClientTestBench.AwardNomination" => typeof(AwardNomination),
            _ => null,
        };
        _context.ResolveName = type => type.FullName;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Collection_MaterializesEachRowAsItsODataType_WithDerivedMembers()
    {
        List<Award> rows = (await _context.CreateQuery<Award>("Awards")
            .AddQueryOption("$orderby", "Id").ExecuteAsync()).ToList();

        Assert.Equal(3, rows.Count);
        var academy = Assert.IsType<AcademyAward>(rows[0]);
        Assert.Equal("67th Academy Awards", academy.Ceremony);
        Assert.True(academy.IsWinner);
        var festival = Assert.IsType<FestivalAward>(rows[1]);
        Assert.Equal("Cannes", festival.Festival);
        Assert.Equal("Clint Eastwood", festival.Jury);
        Assert.IsType<Award>(rows[2]); // declared type == runtime type: no annotation needed
    }

    [Fact]
    public async Task DerivedRows_UnderExpand_KeepTheirTypeAndMembers()
    {
        // #529: $expand takes the Include path; the derived members must not vanish with it.
        List<Award> rows = (await _context.CreateQuery<Award>("Awards")
            .AddQueryOption("$orderby", "Id")
            .AddQueryOption("$expand", "Nominations").ExecuteAsync()).ToList();

        var academy = Assert.IsType<AcademyAward>(rows[0]);
        Assert.Equal("67th Academy Awards", academy.Ceremony);
        Assert.Equal(3, academy.Nominations.Count);
        Assert.IsType<FestivalAward>(rows[1]);
    }
}
