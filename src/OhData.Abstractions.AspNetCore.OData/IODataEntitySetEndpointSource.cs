using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;
using OhData.Abstractions;

namespace OhData.Abstractions.AspNetCore.OData;

internal interface IODataEntitySetEndpointSource : IEntitySetEndpointSource
{
    bool HasGetODataQueryable  { get; }
    bool HasPatchDelta { get; }
    Task<IQueryable<object>>  InvokeGetODataQueryableAsync (ODataQueryOptions options, CancellationToken ct);
    Task<object?> InvokePatchDeltaAsync(object key, Delta delta, CancellationToken ct);
}
