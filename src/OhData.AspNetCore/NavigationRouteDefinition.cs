using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Abstractions;

/// <summary>
/// Describes a navigation property route registered via
/// <c>HasOptional</c>, <c>HasRequired</c>, or <c>HasMany</c> on an
/// <see cref="EntitySetProfile{TKey,TModel}"/>. Built once at startup and cached.
/// </summary>
internal sealed record NavigationRouteDefinition
{
    /// <summary>The navigation property name as it appears in the entity type.</summary>
    public string PropertyName { get; init; } = "";

    /// <summary>
    /// <c>true</c> when the navigation exposes a collection
    /// (<c>GET /{EntitySet}({key})/{Property}</c> returns an array);
    /// <c>false</c> for a single-entity navigation.
    /// </summary>
    public bool IsCollection { get; init; }

    /// <summary>
    /// Handler that loads the related entity or collection for a given parent key.
    /// Signature: <c>(parentKey, cancellationToken) → object?</c> where the return value
    /// is the related entity or an <c>IEnumerable&lt;T&gt;</c> for collection navigations.
    /// </summary>
    public Func<object, CancellationToken, Task<object?>> Handler { get; init; } = null!;

    /// <summary>
    /// Handler for <c>POST /{EntitySet}({key})/{Property}/$ref</c> (add link,
    /// OData §11.4.6.1). The second parameter is the <c>@odata.id</c> string from the
    /// request body. <c>null</c> means the endpoint is not registered.
    /// </summary>
    public Func<object, object, CancellationToken, Task>? AddRef { get; init; }

    /// <summary>
    /// Handler for <c>DELETE /{EntitySet}({key})/{Property}/$ref</c> (remove link,
    /// OData §11.4.6.2). The second parameter is the related entity id. <c>null</c> means
    /// the endpoint is not registered.
    /// </summary>
    public Func<object, object, CancellationToken, Task>? RemoveRef { get; init; }

    /// <summary>
    /// Property name on the child entity to use as its key in <c>$ref</c> URLs.
    /// When non-null and <see cref="ChildEntitySetName"/> is also set, the GET <c>$ref</c>
    /// handler returns populated <c>@odata.id</c> references instead of an empty array.
    /// </summary>
    public string? ChildKeyPropertyName { get; init; }

    /// <summary>
    /// The entity-set name to use in <c>$ref</c> <c>@odata.id</c> URLs (e.g. <c>"Orders"</c>).
    /// Requires <see cref="ChildKeyPropertyName"/> to be set.
    /// </summary>
    public string? ChildEntitySetName { get; init; }

    /// <summary>
    /// The CLR type of the navigation target entity. Used to validate <c>$select</c>
    /// property names at request time. <c>null</c> for non-collection navigations where
    /// <c>$select</c> is not applied by the framework.
    /// </summary>
    public Type? NavItemType { get; init; }

    /// <summary>
    /// Optional batch loader used by <c>$expand</c> (OData §11.2.4.2). Given the full set of
    /// parent keys on the current page, returns a map keyed by parent key whose value has the
    /// SAME shape <see cref="Handler"/> would return: the related entity for single navigations,
    /// or an <c>IEnumerable&lt;T&gt;</c> for collection navigations. Missing keys are treated as
    /// null (single) / empty (collection). When <c>null</c>, the framework falls back to
    /// invoking <see cref="Handler"/> once per parent entity per expanded property.
    /// </summary>
    public Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyDictionary<object, object?>>>? BatchHandler { get; init; }
}
