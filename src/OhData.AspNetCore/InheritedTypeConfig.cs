using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace OhData;

/// <summary>
/// The single place this assembly resolves per-CLR-type configuration for a <b>runtime</b> instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>#462/#343 — the defect class this type exists to make structurally impossible.</b> Per-type
/// configuration is declared against the type a profile names, but both serialization substrates
/// resolve the <i>runtime</i> type's contract: the batched collection path hands System.Text.Json an
/// <c>object</c>-declared element (so STJ resolves <c>element.GetType()</c>), and the single-entity
/// path calls <c>SerializeToNode(value, value.GetType(), ...)</c> outright. Returning a DERIVED
/// instance of the model type is an ordinary EF Core TPH shape, and every exact-type lookup —
/// <c>map.TryGetValue(typeInfo.Type, ...)</c> — therefore MISSES for such an instance and silently
/// applies no configuration at all.
/// </para>
/// <para>
/// That had already been fixed three separate times in three separate places before it was
/// recognised as one defect: #293 (delegate-backed navigation names), then <c>Ignore(...)</c>-style
/// property withholding (#462, a disclosure bug — the withheld property was SERVED on a plain GET),
/// then navigation suppression (#343, unrequested data plus a reachable 500 on a mutual reference).
/// <c>OpenTypeJsonOptions.TryFindContainer</c> had the correct walk all along and said so in a
/// comment; four other sites did not use it.
/// </para>
/// <para>
/// <b>Why the walk is over <c>Type.BaseType</c> and stops at <see cref="object"/>.</b> Same bound
/// <c>TryFindContainer</c> uses. Interfaces are deliberately NOT walked: every map routed through
/// here is keyed by an entity/complex CLR type a profile named or the EDM designated, which is
/// always a class, and an interface walk has no "nearest" ancestor to order by.
/// </para>
/// <para>
/// <b>The <c>ReflectedType</c> subtlety in <c>TryFindContainer</c>'s comment does NOT apply here,
/// and it is worth being explicit about why.</b> That comment is about comparing the resolved
/// <c>PropertyInfo</c> <i>value</i> against System.Text.Json's <c>AttributeProvider</c> — two
/// independent reflection walks that disagree on <c>ReflectedType</c>, which is why it uses
/// <c>HasSameMetadataDefinitionAs</c> rather than <c>==</c>. The base-chain <i>key</i> walk is the
/// half that generalises; every consumer here compares members by NAME (a string), so no
/// <c>PropertyInfo</c> identity question arises at all.
/// </para>
/// </remarks>
internal static class InheritedTypeConfig
{
    /// <summary>
    /// The nearest entry at or above <paramref name="runtimeType"/> in its CLR base chain —
    /// "most derived wins", i.e. a type that shadows its base's configuration gets its own.
    /// </summary>
    /// <remarks>
    /// This is the resolution policy for a <b>single-valued</b> configuration, where a derived
    /// declaration REPLACES the base's (<c>TryFindContainer</c>'s dynamic-property container is the
    /// canonical example: a derived type that shadows the container with <c>new</c> flattens its own
    /// member, not the base's). It is NOT the policy for a withheld-name set — see
    /// <see cref="InheritedNameSets"/>, which unions instead, and see that type's remarks for why
    /// the two must not be collapsed into one.
    /// </remarks>
    internal static bool TryResolveNearest<TValue>(
        IReadOnlyDictionary<Type, TValue> byType,
        Type runtimeType,
        [MaybeNullWhen(false)] out TValue value)
    {
        for (Type? t = runtimeType; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (byType.TryGetValue(t, out value)) return true;
        }
        value = default;
        return false;
    }
}

/// <summary>
/// A per-CLR-type map of name sets — withheld property names, withheld JSON names — resolved for a
/// <b>runtime</b> type by walking its CLR base chain and <b>unioning</b> every entry it finds.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type deliberately exposes no exact-type lookup.</b> That is the point of it: the four
/// sites #462 names all had the shape <c>map.TryGetValue(typeInfo.Type, out names)</c>, and as long
/// as the maps travelled as <see cref="IReadOnlyDictionary{TKey,TValue}"/> a fifth site could be
/// written the same way without anything noticing. Every consumer now receives an
/// <see cref="InheritedNameSets"/> and the only thing it can do with one is
/// <see cref="Resolve(Type)"/>, which always walks.
/// </para>
/// <para>
/// <b>Union, not nearest-wins — because these sets are a disclosure boundary.</b> If a base type's
/// profile withholds <c>Secret</c>, then <i>every</i> instance of that type withholds it, including
/// an instance whose runtime type is a derived entity set with an ignore set of its own. Taking only
/// the nearest entry would let a derived profile's unrelated <c>Ignore(...)</c> call silently
/// un-withhold its base's, which is the same leak this type exists to close, one level down.
/// (<see cref="InheritedTypeConfig.TryResolveNearest"/> is the other policy, for single-valued
/// configuration where a derived declaration genuinely replaces the base's.)
/// </para>
/// <para>
/// <b>The comparer is carried, never re-derived.</b> A withheld-name set must use the comparer the
/// <i>binder</i> matches body keys with (see
/// <c>IgnoredPropertyJsonOptions.WithheldNameComparer</c>) — an ordinal set is a containment bypass
/// (#398 review HIGH-1). The overwhelmingly common case — exactly one entry on the chain — returns
/// that set by reference, so its own comparer survives untouched; only a genuine multi-level union
/// allocates, and it allocates with the comparer supplied at construction.
/// </para>
/// <para>
/// Resolution is memoised per runtime type, so the walk runs once per type per registration. That
/// matters: two of the four sites are per-object-level lookups on the write path, i.e. per request.
/// </para>
/// </remarks>
internal sealed class InheritedNameSets
{
    /// <summary>The shared "nothing is configured" instance. Resolves to <c>null</c> for every type.</summary>
    internal static readonly InheritedNameSets Empty =
        new(new Dictionary<Type, IReadOnlySet<string>>(), StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<Type, IReadOnlySet<string>> _declared;
    private readonly StringComparer _unionComparer;
    private readonly ConcurrentDictionary<Type, IReadOnlySet<string>?> _resolved = new();

    internal InheritedNameSets(
        IReadOnlyDictionary<Type, IReadOnlySet<string>> declaredByType,
        StringComparer unionComparer)
    {
        _declared = declaredByType;
        _unionComparer = unionComparer;
    }

    /// <summary>True when no type declares anything, so every <see cref="Resolve"/> returns null.</summary>
    internal bool IsEmpty => _declared.Count == 0;

    /// <summary>
    /// The names configured for <paramref name="runtimeType"/> or any of its CLR base types, or
    /// <c>null</c> when nothing on the chain declares any.
    /// </summary>
    internal IReadOnlySet<string>? Resolve(Type runtimeType)
    {
        if (_declared.Count == 0) return null;
        return _resolved.GetOrAdd(runtimeType, static (type, self) => self.Walk(type), this);
    }

    private IReadOnlySet<string>? Walk(Type runtimeType)
    {
        IReadOnlySet<string>? first = null;
        HashSet<string>? union = null;
        for (Type? t = runtimeType; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (!_declared.TryGetValue(t, out IReadOnlySet<string>? names) || names.Count == 0) continue;
            if (first is null) { first = names; continue; }
            // Second and later contributors: only NOW is an allocation justified. `first` is kept
            // by reference in the single-contributor case precisely so its own comparer survives.
            union ??= new HashSet<string>(first, _unionComparer);
            union.UnionWith(names);
        }
        return union ?? first;
    }
}
