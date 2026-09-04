using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;

namespace OhData;

internal interface IODataEntitySetEndpointSource : IEntitySetEndpointSource
{
    bool HasGetODataQueryable { get; }

    /// <summary>#475: the system query options this profile declares its handler honours.</summary>
    OhDataSystemQueryOption HonouredQueryOptions { get; }
    Task<ODataQueryResult<object>> InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct);
}
