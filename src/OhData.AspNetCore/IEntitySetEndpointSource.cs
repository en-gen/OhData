using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;

namespace OhData;

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

    /// <summary>
    /// Per-operation authorization rules declared via <c>ConfigureAuthorization(...)</c> (#199), or
    /// <c>null</c> when the profile uses the legacy profile-wide <see cref="Authorization"/> model.
    /// When non-null, the factory applies auth per-route rather than to a single all-operations group.
    /// </summary>
    IReadOnlyList<OperationAuthRule>? OperationAuthorization { get; }

    IReadOnlyList<NavigationRouteDefinition> NavigationRoutes { get; }
    IReadOnlyList<BoundOperationDefinition> BoundFunctions { get; }
    IReadOnlyList<BoundOperationDefinition> BoundActions { get; }

    int? MaxTop { get; }
    long? MaxRequestBodyBytes { get; }
    int MaxExpansionDepth { get; }
    int MaxFilterNodeCount { get; }
    int MaxOrderByNodeCount { get; }
    int MaxAnyAllExpressionDepth { get; }
    bool IdempotentDelete { get; }
    bool AllowUpsert { get; }
    bool HasSearch { get; }
    bool FilterEnabled { get; }
    bool OrderByEnabled { get; }
    bool SelectEnabled { get; }
    bool ExpandEnabled { get; }
    bool CountEnabled { get; }
    bool PropertyAccessEnabled { get; }
    bool PropertyRouteDocsEnabled { get; }

    /// <summary>
    /// Whether <c>$select</c> projection pushdown may apply on this set's <c>GetQueryable</c>
    /// path (#206). Resolved from the profile flag / <c>EntitySetDefaults</c> (default true).
    /// </summary>
    bool SelectPushdownEnabled { get; }

    /// <summary>
    /// Whether <c>$expand</c> Include pushdown may apply on this set's <c>GetQueryable</c> path
    /// (#206 phase 2). Resolved from the profile flag / <c>EntitySetDefaults</c>
    /// (default true). When true and a top-level <c>$expand</c> names a navigation declared
    /// <b>without</b> a delegate on an EF Core-backed source, that navigation is folded into the
    /// collection query's projection (one JOIN'd query), honoring the expand's nested
    /// <c>$filter</c>/<c>$orderby</c>/<c>$top</c>/<c>$skip</c>/<c>$count</c>/<c>$select</c>;
    /// delegate-backed navigations always take the delegate expansion path and are never pushed down.
    /// </summary>
    bool ExpandPushdownEnabled { get; }

    /// <summary>
    /// CLR property names participating in the <c>UseETag</c> hash, when every selector was a
    /// direct member access; <c>null</c> when ETags are unconfigured OR any selector was
    /// computed (names unknowable — #206 pushdown is then ineligible while <see cref="HasETag"/>).
    /// </summary>
    IReadOnlyCollection<string>? ETagPropertyNames { get; }
    RoundingMode RoundingMode { get; }
    IReadOnlyList<StructuralPropertyInfo> StructuralProperties { get; }

    /// <summary>
    /// When <c>true</c>, <c>POST /{EntitySet}</c> passes the full deserialized request graph
    /// (including nested navigation-property values) through to the <c>Post</c> handler, which
    /// is contractually responsible for persisting it atomically (OData §11.4.2.2 — deep insert).
    /// When <c>false</c> (the default), nested navigation-property values are stripped (set to
    /// <c>null</c>) from the deserialized model before <c>Post</c> is invoked.
    /// </summary>
    bool AllowDeepInsert { get; }

    /// <summary>
    /// Names of every CLR property declared as a navigation property via
    /// <c>HasOptional</c>/<c>HasRequired</c>/<c>HasMany</c> (any overload). Used by the POST
    /// pipeline to strip nested navigation values when <see cref="AllowDeepInsert"/> is
    /// <c>false</c>.
    /// </summary>
    IReadOnlyCollection<string> NavigationPropertyNames { get; }

    /// <summary>
    /// Names of CLR properties excluded from the OData surface via
    /// <c>EntitySetProfile.Ignore(...)</c> (#226). Empty when the profile ignores nothing.
    /// Drives structural-route exclusion, the registration-wide serializer-options derivation,
    /// and the PATCH delta-builder filter.
    /// </summary>
    IReadOnlyCollection<string> IgnoredPropertyNames { get; }

    string KeyPropertyName { get; }
    string InvokeGetKeyString(object model);

    /// <summary>
    /// Formats the model's key as a canonical, URL-safe OData key literal for embedding inside
    /// an entity-id URL segment (<c>{EntitySet}({key})</c>) -- e.g. quoted and percent-encoded
    /// for string keys (Part 2 §4.3.1). Distinct from <see cref="InvokeGetKeyString"/>, which
    /// returns the raw unquoted representation used for body-vs-URL key equality checks (PUT).
    /// </summary>
    string InvokeGetKeyForUrl(object model);
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
