using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// Which per-key check the serialize-path getter wrapper performs. The three values are the three
/// states #389 actually passed through, in commit order.
/// </summary>
internal enum SerializeKeyCheckArm
{
    /// <summary>
    /// <b>Arm A</b> — <c>e0edaac</c>. The getter wrapper exists and performs the declared-name
    /// shadow lookup, and nothing else. This is the floor: the cost of the loop the two later
    /// checks were added INTO, not the cost of having no wrapper.
    /// </summary>
    None,

    /// <summary>
    /// <b>Arm B</b> — <c>cab1de7</c>. Adds <c>string.IsNullOrWhiteSpace</c>, which short-circuits on
    /// the first non-whitespace character, so it reads exactly one character of an ordinary key.
    /// </summary>
    NullOrWhiteSpace,

    /// <summary>
    /// <b>Arm C</b> — <c>4a33b87</c>. Calls the SHIPPED
    /// <c>OpenTypeJsonOptions.IsValidDynamicPropertyNameCached</c> — not a copy of it — so the thing
    /// under measurement is the real validator, ASCII fast path, rune-walk fallback, bounded cache
    /// and all.
    /// </summary>
    FullGrammar,
}

/// <summary>
/// A faithful transcription of <c>OpenTypeJsonOptions.Build</c> +
/// <c>ThrowOnKeysThatCannotBeEmitted</c> + <c>ThrowIfAnyKeyCannotBeEmitted</c>, parameterised by
/// which per-key check the innermost loop runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, given the standing rule that a benchmark must measure shipped code.</b>
/// Arms A and B are not in the tree. They are <c>e0edaac</c> and <c>cab1de7</c> — two ancestors of
/// the commit under review — and the assembly can only carry one definition of a type, so the three
/// arms cannot all be the shipped <c>OpenTypeJsonOptions</c> in one process. Referencing three
/// builds of <c>OhData.AspNetCore.dll</c> is worse, not better: same assembly identity, so it needs
/// <c>AssemblyLoadContext</c> gymnastics that add their own per-call cost to exactly the thing being
/// measured.
/// </para>
/// <para>
/// <b>What keeps that honest.</b> Diffing the three commits shows the wrapper scaffolding — the
/// resolver modifier, the <c>HasSameMetadataDefinitionAs</c> member match, the ordinal
/// <c>declaredNames</c> set, the getter closure, the two-case bag switch, the <c>foreach</c> over
/// keys and the trailing <c>declaredNames.Contains</c> — is byte-identical across all three. The
/// ONLY textual difference is the branch at the top of the key loop. (The one other delta,
/// <c>e0edaac</c>'s <c>declaredNames.Count == 0</c> early bail, cannot fire here: the benchmark's
/// open complex type declares <c>Region</c> and <c>Tier</c>.) So this file reproduces the constant
/// part and varies the one thing under test.
/// </para>
/// <para>
/// <b>And it is verified, not asserted.</b> <c>OpenTypeKeyValidationBenchmarks</c> runs a FOURTH arm,
/// <c>C_Shipped</c>, which is the genuine <c>OpenTypeJsonOptions.Build</c> with no transcription
/// anywhere in it. <c>C_Replica</c> and <c>C_Shipped</c> do identical work by construction, so any
/// gap between their measured means IS the transcription bias, quantified in the same run and on the
/// same machine as the numbers it would distort. Read that gap first; every A/B/C conclusion is only
/// as good as it is small.
/// </para>
/// <para>
/// The arm is selected ONCE, at wrap time, and baked into the getter closure as a delegate — never
/// switched on inside the key loop. A per-key <c>switch</c> would charge arms A and B for a branch
/// their shipped originals never had, which would understate C's cost relative to B. The residual is
/// one delegate invocation per BAG (i.e. per row, 1 per 20 keys), identical in all three arms.
/// </para>
/// </remarks>
internal static class ArmedOpenTypeJsonOptions
{
    /// <summary>Transcription of <c>OpenTypeJsonOptions.Build</c>, arm-parameterised.</summary>
    internal static JsonSerializerOptions Build(
        JsonSerializerOptions baseOptions,
        OpenTypeJsonOptions.OpenComplexTypeContainers containers,
        SerializeKeyCheckArm arm)
    {
        // Reference-equal passthrough for a model with no open complex type — the shipped
        // behaviour, and what makes the control scenario compare four IDENTICAL options instances.
        if (containers.IsEmpty) return baseOptions;

        IReadOnlyDictionary<Type, PropertyInfo> byDeclaringType = containers.ByDeclaringType;
        JsonSerializerOptions derived = new JsonSerializerOptions(baseOptions);
        IJsonTypeInfoResolver resolver = derived.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        derived.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            if (!TryFindContainer(byDeclaringType, typeInfo.Type, out PropertyInfo? container)) return;
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                if (property.AttributeProvider is not PropertyInfo candidate ||
                    !candidate.HasSameMetadataDefinitionAs(container))
                {
                    continue;
                }
                property.IsExtensionData = true;
                WrapGetter(typeInfo, property, arm);
                break;
            }
        });
        return derived;
    }

    /// <summary>Transcription of <c>OpenTypeJsonOptions.ThrowOnKeysThatCannotBeEmitted</c>.</summary>
    private static void WrapGetter(JsonTypeInfo typeInfo, JsonPropertyInfo container, SerializeKeyCheckArm arm)
    {
        Func<object, object?>? get = container.Get;
        if (get is null) return;

        HashSet<string> declaredNames = new HashSet<string>(
            typeInfo.Properties.Where(p => !ReferenceEquals(p, container)).Select(p => p.Name),
            StringComparer.Ordinal);

        Type ownerType = typeInfo.Type;
        KeyCheck check = arm switch
        {
            SerializeKeyCheckArm.None => CheckKeysWithNoValidator,
            SerializeKeyCheckArm.NullOrWhiteSpace => CheckKeysWithIsNullOrWhiteSpace,
            _ => CheckKeysWithFullGrammar,
        };
        container.Get = MakeGetter(get, declaredNames, ownerType, check);
    }

    private delegate void KeyCheck(IEnumerable<string> keys, HashSet<string> declaredNames, Type ownerType);

    // Separate factory so the arm switch above is evaluated exactly once, at startup.
    private static Func<object, object?> MakeGetter(
        Func<object, object?> get, HashSet<string> declaredNames, Type ownerType, KeyCheck check) =>
        instance =>
        {
            object? bag = get(instance);
            switch (bag)
            {
                case IDictionary<string, object?> objectBag:
                    check(objectBag.Keys, declaredNames, ownerType);
                    break;
                case IDictionary<string, JsonElement> elementBag:
                    check(elementBag.Keys, declaredNames, ownerType);
                    break;
            }
            return bag;
        };

    // ── The three key loops ─────────────────────────────────────────────────────────────────────
    //
    // Everything below the leading branch is identical in all three, and identical to the shipped
    // ThrowIfAnyKeyCannotBeEmitted. The throw sites are unreachable in this benchmark by
    // construction — every generated key is a valid identifier and none collides with "Region" or
    // "Tier" — so the exception messages are shortened; the shipped ones build their strings inside
    // the throw, which costs nothing on the path not taken.

    // THE SHADOW LOOKUP IS SPELLED THE SAME WAY IN ALL THREE ARMS, and that spelling is
    //   bool shadowsDeclared = declaredNames.Contains(key);
    //   if (shadowsDeclared) throw Shadowed(key, ownerType);
    // rather than the shipped `if (!declaredNames.Contains(key)) continue; throw ...`. The two are
    // the same branch — verified, not assumed: the Release IL of all three CheckKeysWith* methods is
    // byte-identical before and after this rewrite (the named local is a stack temp the compiler
    // never emits), so the measured arm-to-arm deltas cannot have moved and the published numbers
    // stand without a re-run. What the rewrite buys is that arm A's loop body is no longer a bare
    // filter, which is what `.Where` would otherwise have to be folded into — and folding a LINQ
    // layer into ONE measured arm is the one thing this file must not do, since the arms are only
    // comparable while they are structurally identical.

    /// <summary>Arm A — <c>e0edaac</c>.</summary>
    private static void CheckKeysWithNoValidator(
        IEnumerable<string> keys, HashSet<string> declaredNames, Type ownerType)
    {
        foreach (string key in keys)
        {
            bool shadowsDeclared = declaredNames.Contains(key);
            if (shadowsDeclared) throw Shadowed(key, ownerType);
        }
    }

    /// <summary>Arm B — <c>cab1de7</c>.</summary>
    private static void CheckKeysWithIsNullOrWhiteSpace(
        IEnumerable<string> keys, HashSet<string> declaredNames, Type ownerType)
    {
        foreach (string key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) throw Nameless(ownerType);

            bool shadowsDeclared = declaredNames.Contains(key);
            if (shadowsDeclared) throw Shadowed(key, ownerType);
        }
    }

    /// <summary>
    /// Arm C — <c>4a33b87</c>. The call below is to the SHIPPED validator in
    /// <c>OhData.AspNetCore</c>, reached through <c>InternalsVisibleTo</c>. Nothing about the ASCII
    /// fast path, the rune walk or the bounded cache is reproduced here.
    /// </summary>
    private static void CheckKeysWithFullGrammar(
        IEnumerable<string> keys, HashSet<string> declaredNames, Type ownerType)
    {
        foreach (string key in keys)
        {
            if (key is null || !OpenTypeJsonOptions.IsValidDynamicPropertyNameCached(key))
            {
                throw NotAnIdentifier(ownerType);
            }

            bool shadowsDeclared = declaredNames.Contains(key);
            if (shadowsDeclared) throw Shadowed(key, ownerType);
        }
    }

    private static InvalidOperationException Shadowed(string key, Type ownerType) =>
        new($"OhData bench: dynamic key '{key}' shadows a declared property of '{ownerType.FullName}'.");

    private static InvalidOperationException Nameless(Type ownerType) =>
        new($"OhData bench: '{ownerType.FullName}' holds an empty or whitespace-only dynamic key.");

    private static InvalidOperationException NotAnIdentifier(Type ownerType) =>
        new($"OhData bench: '{ownerType.FullName}' holds a dynamic key that is not an odataIdentifier.");

    /// <summary>Transcription of <c>OpenTypeJsonOptions.TryFindContainer</c>.</summary>
    private static bool TryFindContainer(
        IReadOnlyDictionary<Type, PropertyInfo> containers,
        Type type,
        [NotNullWhen(true)] out PropertyInfo? container)
    {
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (containers.TryGetValue(t, out container)) return true;
        }
        container = null;
        return false;
    }
}
