using System;
using System.Linq.Expressions;
using System.Text.Json;
using OhData.Client.Internal;

namespace OhData.Client;

/// <summary>
/// One entity together with the OData control information the server attached to it.
/// Produced by <see cref="EntitySetClient{T}.ToAnnotatedPageAsync"/>,
/// <see cref="EntitySetClient{T}.ToAnnotatedAsyncEnumerable"/> and
/// <see cref="KeyedEntitySetClient{T}.GetAnnotatedAsync"/>.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class ODataAnnotatedEntity<T> where T : class
{
    private readonly JsonNamingPolicy? _namingPolicy;

    /// <summary>Creates an annotated entity.</summary>
    /// <param name="entity">The deserialized entity.</param>
    /// <param name="annotations">The entity's annotations, or <see cref="ODataEntityAnnotations.Empty"/>.</param>
    /// <param name="namingPolicy">
    /// The naming policy the expression-based accessors use to turn a CLR member into the name the
    /// server spells on the wire. Pass the same value as
    /// <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> on the client's options
    /// (<see langword="null"/> — the client default — meaning the CLR names verbatim).
    /// A <c>[JsonPropertyName]</c> on the member wins over the policy, exactly as it does for the
    /// query options the client emits.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="entity"/> or <paramref name="annotations"/> is null.
    /// </exception>
    public ODataAnnotatedEntity(T entity, ODataEntityAnnotations annotations, JsonNamingPolicy? namingPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(annotations);
        Entity = entity;
        Annotations = annotations;
        _namingPolicy = namingPolicy;
    }

    /// <summary>The deserialized entity.</summary>
    public T Entity { get; }

    /// <summary>The control information the server attached to <see cref="Entity"/>.</summary>
    public ODataEntityAnnotations Annotations { get; }

    /// <summary>
    /// The <c>@odata.nextLink</c> for an expanded collection navigation property, or
    /// <see langword="null"/> when the expansion was not server-side paged.
    /// A non-null result means <c>Entity</c>'s copy of that collection is a <em>prefix</em>.
    /// </summary>
    /// <param name="navigationProperty">
    /// A direct member access on <typeparamref name="T"/>, e.g. <c>x =&gt; x.Books</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the expression is not a direct member access on <typeparamref name="T"/>.
    /// </exception>
    public Uri? NextLinkFor(Expression<Func<T, object?>> navigationProperty) =>
        Annotations.NextLinkFor(ResolveName(navigationProperty));

    /// <summary>
    /// The <c>@odata.count</c> for an expanded collection navigation property — the size of the
    /// full related collection — or <see langword="null"/> when the server did not emit it.
    /// </summary>
    /// <param name="navigationProperty">
    /// A direct member access on <typeparamref name="T"/>, e.g. <c>x =&gt; x.Books</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the expression is not a direct member access on <typeparamref name="T"/>.
    /// </exception>
    public long? CountFor(Expression<Func<T, object?>> navigationProperty) =>
        Annotations.CountFor(ResolveName(navigationProperty));

    private string ResolveName(Expression<Func<T, object?>> navigationProperty) =>
        ODataMemberName.ResolveDirectMember(
            navigationProperty,
            _namingPolicy,
            "Annotations are addressed by a direct navigation property of the entity (e.g. x => x.Books); " +
            "chained paths are not supported — use the string overload on Annotations.");
}
