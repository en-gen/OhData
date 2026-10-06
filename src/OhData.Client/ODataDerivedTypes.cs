using System;
using System.Collections.Generic;

namespace OhData.Client;

/// <summary>
/// Explicit <c>@odata.type</c> to CLR type registrations, for a service whose EDM type names are not
/// the CLR <see cref="Type.FullName"/> (a renamed namespace). Reached through
/// <see cref="OhDataClientOptions.DerivedTypes"/>.
/// </summary>
/// <remarks>
/// Without a registration a derived type is still found by convention: a subclass of the declared type
/// in the declared type's assembly, matched by <c>#</c> + its full name. A registration takes precedence
/// over the convention. A response naming a type that neither resolves is read as the declared type.
/// </remarks>
public sealed class ODataDerivedTypes
{
    private readonly List<KeyValuePair<string, Type>> _entries = [];

    /// <summary>Registers <typeparamref name="TDerived"/> under the EDM type name <paramref name="edmTypeName"/>.</summary>
    /// <param name="edmTypeName">The namespace-qualified EDM name, with or without a leading <c>#</c>.</param>
    public ODataDerivedTypes Add<TDerived>(string edmTypeName) where TDerived : class =>
        Add(typeof(TDerived), edmTypeName);

    /// <summary>Registers <paramref name="derivedType"/> under the EDM type name <paramref name="edmTypeName"/>.</summary>
    public ODataDerivedTypes Add(Type derivedType, string edmTypeName)
    {
        ArgumentNullException.ThrowIfNull(derivedType);
        ArgumentException.ThrowIfNullOrWhiteSpace(edmTypeName);
        if (!derivedType.IsClass)
            throw new ArgumentException("Only classes can be registered as derived types.", nameof(derivedType));

        _entries.Add(new KeyValuePair<string, Type>(edmTypeName.TrimStart('#'), derivedType));
        return this;
    }

    internal KeyValuePair<string, Type>[] Snapshot() => [.. _entries];
}
