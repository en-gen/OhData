using Microsoft.AspNetCore.OData.Query;
using OhData.Abstractions;

namespace OhData.Abstractions.AspNetCore.OData;

internal interface IODataEntitySetEndpointSource : IEntitySetEndpointSource
{
    bool HasGetODataQueryable  { get; }
    bool HasGetODataEnumerable { get; }
    Task<IQueryable<object>>  InvokeGetODataQueryableAsync (ODataQueryOptions options, CancellationToken ct);
    Task<IEnumerable<object>> InvokeGetODataEnumerableAsync(ODataQueryOptions options, CancellationToken ct);
}
