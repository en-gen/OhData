using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;

namespace OhData.Abstractions;

/// <summary>
/// Holds per-profile authorization configuration. Applied by the factory in OhData.AspNetCore.
/// </summary>
public sealed record AuthorizationConfig(bool Required, string? Policy, IReadOnlyList<string>? Roles);

/// <summary>
/// Internal contract used by OhData.AspNetCore to interrogate a profile and invoke its handlers
/// without knowing the generic TKey/TModel types at compile time.
/// </summary>
internal interface IEntitySetEndpointSource
{
    string EntitySetName { get; }
    Type KeyType { get; }
    Type ModelType { get; }

    bool HasGetAll { get; }
    bool HasGetQueryable { get; }
    bool HasGetById { get; }
    bool HasPost { get; }
    bool HasPut { get; }
    bool HasPatch { get; }
    bool HasDelete { get; }

    bool HasETag { get; }

    AuthorizationConfig? Authorization { get; }

    IReadOnlyList<NavigationRouteDefinition> NavigationRoutes { get; }
    IReadOnlyList<BoundOperationDefinition> BoundFunctions { get; }
    IReadOnlyList<BoundOperationDefinition> BoundActions { get; }

    int? MaxTop { get; }
    bool IdempotentDelete { get; }
    bool AllowUpsert { get; }
    bool HasSearch { get; }
    bool FilterEnabled { get; }
    bool OrderByEnabled { get; }
    bool SelectEnabled { get; }
    bool ExpandEnabled { get; }
    bool CountEnabled { get; }
    bool PropertyAccessEnabled { get; }
    IReadOnlyList<StructuralPropertyInfo> StructuralProperties { get; }
    string KeyPropertyName { get; }
    string InvokeGetKeyString(object model);
    bool IsAdvancedConfigureOverridden { get; }

    Task<object?> InvokeGetAllAsync(CancellationToken ct);
    Task<IQueryable<object>> InvokeGetQueryableAsync(CancellationToken ct);
    Task<object?> InvokeGetByIdAsync(object key, CancellationToken ct);
    Task<object?> InvokePostAsync(object model, CancellationToken ct);
    Task<object?> InvokePutAsync(object key, object model, CancellationToken ct);
    Task<object?> InvokePatchAsync(object key, Delta delta, CancellationToken ct);
    Task<bool> InvokeDeleteAsync(object key, CancellationToken ct);
    Task<IEnumerable<object>> InvokeSearchAsync(string searchTerm, CancellationToken ct);
    string InvokeGetETag(object model);
}
