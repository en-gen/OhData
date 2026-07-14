using System;

namespace OhData.Abstractions;

/// <summary>
/// Describes a structural (non-navigation) property of an entity's CLR model, used to register
/// individual property-access routes (<c>GET /{EntitySet}({key})/{Property}</c> and
/// <c>GET .../{Property}/$value</c>, OData §11.2.6 / Part 2 §4.6-4.7). Built once at startup from
/// <c>typeof(TModel)</c>'s public readable instance properties minus any property declared as a
/// navigation via <c>HasMany</c>/<c>HasOptional</c>/<c>HasRequired</c>, and cached on the profile.
/// </summary>
internal sealed record StructuralPropertyInfo
{
    /// <summary>The CLR property name, used as the route segment.</summary>
    public required string Name { get; init; }

    /// <summary>The CLR type of the property.</summary>
    public required Type ClrType { get; init; }

    /// <summary>
    /// <c>true</c> when this property is the entity's key property (the selector passed to the
    /// profile constructor). Reserved for the write-support PR (key properties are immutable);
    /// read routes treat key and non-key properties identically.
    /// </summary>
    public required bool IsKey { get; init; }

    /// <summary>
    /// <c>true</c> when the property's CLR type permits <c>null</c> (any reference type, or
    /// <c>Nullable&lt;T&gt;</c> for a value type). Reserved for the write-support PR
    /// (DELETE-to-null on a non-nullable property is a 400).
    /// </summary>
    public required bool IsNullable { get; init; }

    /// <summary>
    /// <c>true</c> when the property's type is not one of the OData primitive CLR types
    /// (string, numeric types, <see cref="Guid"/>, date/time types, <c>bool</c>, <c>byte[]</c>,
    /// enums). Complex-typed properties have no raw <c>/$value</c> representation — the read
    /// route in this PR returns <c>400 Bad Request</c> for <c>/$value</c> on these properties.
    /// </summary>
    public required bool IsComplex { get; init; }

    /// <summary>
    /// Compiled, type-erased accessor that reads the property's value from an entity instance.
    /// Compiled once via <see cref="System.Linq.Expressions.Expression"/> and cached — never
    /// invoked via per-request reflection.
    /// </summary>
    public required Func<object, object?> Accessor { get; init; }
}
