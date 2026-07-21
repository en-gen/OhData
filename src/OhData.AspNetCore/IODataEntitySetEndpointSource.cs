using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;

namespace OhData;

internal interface IODataEntitySetEndpointSource : IEntitySetEndpointSource
{
    bool HasGetODataQueryable { get; }
    Task<ODataQueryResult<object>> InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct);
}
