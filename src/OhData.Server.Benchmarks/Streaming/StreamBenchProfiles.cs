using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OhData;

namespace OhData.Server.Benchmarks.Streaming;

// SPIKE #867 -- four sets over the same 10,000 rows: {in-memory, EF SQLite} x {GetQueryable, GetODataQueryable}.
// Row count per request is chosen with $top (the host clears MaxTop).

internal sealed class StreamMemProfile : EntitySetProfile<int, StreamBenchRow>
{
    private static readonly List<StreamBenchRow> Store = StreamBenchData.Create();

    public StreamMemProfile() : base(x => x.Id)
    {
        EntitySetName = "StreamMem";
        SelectEnabled = FilterEnabled = OrderByEnabled = CountEnabled = true;
        GetQueryable = () => Store.AsQueryable();
    }
}

internal sealed class StreamMemP1Profile : ODataEntitySetProfile<int, StreamBenchRow>
{
    private static readonly List<StreamBenchRow> Store = StreamBenchData.Create();

    public StreamMemP1Profile() : base(x => x.Id)
    {
        EntitySetName = "StreamMemP1";
        SelectEnabled = FilterEnabled = OrderByEnabled = CountEnabled = true;
        GetODataQueryable = (options, ct) => Task.FromResult(new ODataQueryResult<StreamBenchRow>
        {
            Items = (IQueryable<StreamBenchRow>)options.ApplyTo(Store.OrderBy(r => r.Id).AsQueryable()),
        });
    }
}

internal sealed class StreamEfProfile : EntitySetProfile<int, StreamBenchRow>
{
    public StreamEfProfile(StreamBenchDbContext db) : base(x => x.Id)
    {
        EntitySetName = "StreamEf";
        SelectEnabled = FilterEnabled = OrderByEnabled = CountEnabled = true;
        GetQueryable = () => db.Rows.AsNoTracking();
    }
}

internal sealed class StreamEfP1Profile : ODataEntitySetProfile<int, StreamBenchRow>
{
    public StreamEfP1Profile(StreamBenchDbContext db) : base(x => x.Id)
    {
        EntitySetName = "StreamEfP1";
        SelectEnabled = FilterEnabled = OrderByEnabled = CountEnabled = true;
        GetODataQueryable = (options, ct) => Task.FromResult(new ODataQueryResult<StreamBenchRow>
        {
            Items = (IQueryable<StreamBenchRow>)options.ApplyTo(db.Rows.AsNoTracking().OrderBy(r => r.Id)),
        });
    }
}
