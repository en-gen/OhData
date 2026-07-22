using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.OData.Deltas;

namespace OhData;

/// <summary>
/// Expression-based, refactor-safe sugar over <c>Delta&lt;T&gt;</c>'s changed-property surface —
/// the core primitives that make the versioned-delta pattern cheap to hand-roll. Replaces
/// stringly-typed <c>GetChangedPropertyNames().Contains("Name")</c> / <c>TryGetPropertyValue</c>
/// calls with a strongly-typed selector.
/// </summary>
public static class DeltaExtensions
{
    /// <summary>
    /// Returns <c>true</c> when the selected property was present in the delta (i.e. the client
    /// sent it), e.g. <c>delta.IsChanged(x =&gt; x.Name)</c>.
    /// </summary>
    /// <param name="delta">The delta to inspect.</param>
    /// <param name="property">A direct property selector, e.g. <c>x =&gt; x.Name</c>.</param>
    public static bool IsChanged<T>(this Delta<T> delta, Expression<Func<T, object?>> property)
        where T : class
    {
        if (delta is null) throw new ArgumentNullException(nameof(delta));
        if (property is null) throw new ArgumentNullException(nameof(property));
        string name = DeltaExpressionHelper.GetMemberName(property, nameof(property));
        return delta.GetChangedPropertyNames().Contains(name);
    }

    /// <summary>
    /// Reads the selected property's value when it was present in the delta, e.g.
    /// <c>delta.TryGetChanged(x =&gt; x.Name, out string? name)</c>. Returns <c>false</c> (with
    /// <paramref name="value"/> set to <c>default</c>) when the property was not sent.
    /// </summary>
    /// <param name="delta">The delta to inspect.</param>
    /// <param name="property">A direct property selector, e.g. <c>x =&gt; x.Name</c>.</param>
    /// <param name="value">The changed value, when present.</param>
    public static bool TryGetChanged<T, TValue>(
        this Delta<T> delta, Expression<Func<T, TValue>> property, out TValue value)
        where T : class
    {
        if (delta is null) throw new ArgumentNullException(nameof(delta));
        if (property is null) throw new ArgumentNullException(nameof(property));
        string name = DeltaExpressionHelper.GetMemberName(property, nameof(property));
        if (delta.GetChangedPropertyNames().Contains(name) &&
            delta.TryGetPropertyValue(name, out object? boxed))
        {
            value = boxed is null ? default! : (TValue)boxed;
            return true;
        }

        value = default!;
        return false;
    }
}
