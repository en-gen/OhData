using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;
using OhData.Abstractions;

namespace OhData.Abstractions.AspNetCore.OData;

internal interface IODataEntitySetEndpointSource : IEntitySetEndpointSource
{
    bool HasGetODataQueryable { get; }
    Task<ODataQueryResult<object>> InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct);
}
