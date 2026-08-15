using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder.Annotations;

namespace OhData;

/// <summary>
/// Builds the registration-wide <see cref="JsonSerializerOptions"/> that make an OData
/// <b>open complex type</b>'s dynamic-property container serialize and bind <b>flat</b> — dynamic
/// keys as siblings of the declared properties, never nested under the container property's own
/// name (#389).
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> Nothing in this file runs unless the registration called
/// <c>OhDataBuilder.WithOpenTypes()</c>. That is not a style choice: flattening a container
/// <i>re-binds</i> a body an existing adopter is already sending. Once the member is extension
/// data it is no longer a <i>declared</i> property, so <c>{"Meta":{"Bag":{"a":1}}}</c> stops
/// meaning "the Bag property" and starts meaning "a dynamic key literally named Bag" — and because
/// the echo of that mis-bound value is byte-identical to the correct one, the corruption is
/// invisible from the wire. Defaulting off keeps every pre-#389 registration byte-identical.
/// </para>
/// <para>
/// <b>Driven by the EDM, never by attributes and never by a name convention.</b>
/// <c>ODataConventionModelBuilder</c> already infers a dynamic-property container from an
/// <see cref="IDictionary{TKey,TValue}"/> member, marks the containing type
/// <c>OpenType="true"</c> in the CSDL, omits the container from the declared properties, and
/// records the backing <see cref="PropertyInfo"/> as a
/// <c>DynamicPropertyDictionaryAnnotation</c>. That annotation — read back through
/// <c>EdmAnnotationExtensions.GetDynamicPropertyDictionary</c> — is the single source of truth
/// here, so the consumer's model needs no <c>[JsonExtensionData]</c> (or any other) attribute:
/// the exact same registration that produces the CSDL produces the wire shape.
/// </para>
/// <para>
/// Mechanism: the same <c>TypeInfoResolver</c>-modifier hook
/// <see cref="IgnoredPropertyJsonOptions"/> and <c>OhDataEndpointFactory</c>'s nav-suppression
/// state already use — except this one <i>mutates</i> a member
/// (<see cref="JsonPropertyInfo.IsExtensionData"/>) rather than removing one.
/// <c>WithAddedModifier</c> chains, so this modifier runs alongside both of those. It is added
/// after the ignored-property modifier and before the per-request nav-suppression modifier, which
/// derives from these options. The three never contend for a member: nav suppression only removes
/// EDM navigations, of which an open <b>complex</b> type has none, and the ignored-property
/// modifier is keyed by <c>profile.ModelType</c> — an <i>entity</i> type — which a container's
/// declaring complex type can never be, so the two modifiers never see the same
/// <see cref="JsonTypeInfo"/> at all.
/// </para>
/// <para>
/// <b>Scope: complex types only.</b> Entity-root dynamic containers are deliberately not handled
/// (see <c>docs/open-types.md</c>): the PATCH delta loop resolves body members through
/// <c>FindClrPropertyByEdmName</c> and skips what it cannot resolve, so a root-level undeclared
/// key would be silently dropped on write — a half-working feature is worse than an absent one.
/// </para>
/// <para>
/// <b>Clause-bounded serialization (#325/#326) is not widened.</b> The values inside the bag
/// already reached <c>System.Text.Json</c> before this change — they were simply written one
/// level deeper, nested under the container property's name. This modifier changes only where
/// the keys are placed in the emitted JSON; it adds no new object to the graph the serializer
/// walks, and it never touches an entity type or a navigation property.
/// </para>
/// </remarks>
internal static class OpenTypeJsonOptions
{
    /// <summary>
    /// The open complex types of one EDM, resolved to CLR reflection.
    /// </summary>
    /// <param name="ByDeclaringType">
    /// Each CLR type that <i>declares</i> a dynamic-property container, mapped to that container's
    /// <see cref="PropertyInfo"/>. Keyed by <see cref="MemberInfo.DeclaringType"/> rather than by
    /// the EDM type's CLR type because a derived open complex type normally reports the
    /// <i>base</i> type's container <see cref="PropertyInfo"/>, so one entry covers a whole
    /// inheritance chain and the modifier resolves it with a short base-type walk.
    /// </param>
    /// <param name="OpenClrTypes">
    /// One CLR type per open complex type in the EDM — including derived types that share a base's
    /// container, which <paramref name="ByDeclaringType"/> collapses away. Each of these gets its
    /// own <see cref="JsonTypeInfo"/> contract, so each has to be probed separately by
    /// <see cref="ValidateOrThrow"/>. Taken from the annotation's
    /// <see cref="MemberInfo.ReflectedType"/>, which the model builder sets to the EDM type the
    /// annotation was written for.
    /// </param>
    internal sealed record OpenComplexTypeContainers(
        IReadOnlyDictionary<Type, PropertyInfo> ByDeclaringType,
        IReadOnlyList<Type> OpenClrTypes)
    {
        internal static readonly OpenComplexTypeContainers Empty =
            new(new Dictionary<Type, PropertyInfo>(), Array.Empty<Type>());

        internal bool IsEmpty => ByDeclaringType.Count == 0;
    }

    /// <summary>
    /// Resolves every <b>open complex type</b> in <paramref name="model"/> to the CLR member
    /// backing its dynamic properties.
    /// </summary>
    /// <remarks>
    /// There is no public EDM-to-CLR <i>type</i> accessor on <c>EdmAnnotationExtensions</c> — only
    /// a setter — but none is needed: both the declaring and the reflected type come straight off
    /// the annotation, so no name-based or convention-based mapping is involved anywhere in this
    /// file.
    /// <para>
    /// Throws when the EDM marks a type open but <c>System.Text.Json</c> cannot use the designated
    /// member as extension data. The caller asked for open types explicitly, and the alternative
    /// outcomes are both worse than a startup failure: silently skipping leaves the CSDL saying
    /// <c>OpenType="true"</c> with the container omitted while the wire still nests it under its
    /// own name (measured — that is exactly the EDM/wire mismatch this feature declines to ship
    /// for entity roots), and marking it anyway silently <i>drops</i> every incoming dynamic key
    /// on write (also measured: a getter-only container binds nothing).
    /// </para>
    /// </remarks>
    internal static OpenComplexTypeContainers BuildOpenComplexTypeContainerMap(IEdmModel model)
    {
        var byDeclaringType = new Dictionary<Type, PropertyInfo>();
        var openClrTypes = new List<Type>();
        foreach (IEdmComplexType complexType in model.SchemaElements.OfType<IEdmComplexType>())
        {
            if (!complexType.IsOpen) continue;
            PropertyInfo? container = model.GetDynamicPropertyDictionary(complexType);
            if (container?.DeclaringType is null) continue;
            ThrowIfUnusableAsExtensionData(container, complexType);
            byDeclaringType[container.DeclaringType] = container;
            Type openClrType = container.ReflectedType ?? container.DeclaringType;
            if (!openClrTypes.Contains(openClrType)) openClrTypes.Add(openClrType);
        }
        return byDeclaringType.Count == 0
            ? OpenComplexTypeContainers.Empty
            : new OpenComplexTypeContainers(byDeclaringType, openClrTypes);
    }

    /// <summary>
    /// Returns <paramref name="baseOptions"/> unchanged (reference-equal) when
    /// <paramref name="containers"/> is empty — zero delta for a model with no open complex
    /// types. Otherwise returns one derived options instance whose resolver modifier marks each
    /// mapped container as <see cref="JsonPropertyInfo.IsExtensionData"/>.
    /// </summary>
    internal static JsonSerializerOptions Build(
        JsonSerializerOptions baseOptions,
        OpenComplexTypeContainers containers,
        ILogger? logger = null)
    {
        if (containers.IsEmpty) return baseOptions;

        IReadOnlyDictionary<Type, PropertyInfo> byDeclaringType = containers.ByDeclaringType;
        var derived = new JsonSerializerOptions(baseOptions);
        IJsonTypeInfoResolver resolver = derived.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        derived.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            if (!TryFindContainer(byDeclaringType, typeInfo.Type, out PropertyInfo? container)) return;
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                // Identity match on the CLR member the EDM designated, via
                // HasSameMetadataDefinitionAs (module + metadata token) rather than `==`:
                // PropertyInfo equality also compares ReflectedType, and the two PropertyInfo
                // instances here come from independent reflection walks that disagree on it. The
                // model builder discovers a complex type's DERIVED types too, and the annotation it
                // stores can carry the derived type as ReflectedType while declaring the member on
                // the base — measured, not assumed: with `ExternalReferenceMetadataV2 :
                // ExternalReferenceMetadata` present in the assembly, the annotation's
                // ReflectedType is V2 while System.Text.Json's AttributeProvider for the base
                // contract reports the base. Same DeclaringType, same token, `==` false.
                //
                // HasSameMetadataDefinitionAs is NOT a whole-member identity check: it also
                // returns true across different instantiations of the same generic type — measured,
                // `typeof(GBag<int>).GetProperty("Bag").HasSameMetadataDefinitionAs(
                // typeof(GBag<string>).GetProperty("Bag"))` is true. The invariant that makes it
                // safe here is the lookup, not the comparison: `container` is whatever
                // TryFindContainer resolved by walking typeInfo.Type's own CLR BASE chain against a
                // map keyed by DeclaringType, so the candidate and the container are always members
                // of the same closed type or of one of its base types — never of a generic sibling.
                // A refactor that flattened this map, keyed it by anything other than the declaring
                // type, or resolved the container by any route other than that base walk would
                // silently start converting a DECLARED property into an extension-data bag.
                if (property.AttributeProvider is not PropertyInfo candidate ||
                    !candidate.HasSameMetadataDefinitionAs(container))
                {
                    continue;
                }
                // Idempotent: a member already carrying [JsonExtensionData] is simply reaffirmed.
                property.IsExtensionData = true;
                SuppressKeysShadowingADeclaredProperty(typeInfo, property, logger);
                break;
            }
        });
        return derived;
    }

    // A bag key equal to a DECLARED property's JSON name would otherwise emit that name twice in
    // one JSON object — measured: `{"Region":"declared","Region":"fromBag"}`, which is invalid
    // OData, is what Microsoft's ODataWriter runs an explicit duplicate-property-name check to
    // prevent, and which every .NET reader tested resolves in the BAG's favour, making the
    // declared value unreachable. This is reachable from ordinary server-side data (a handler that
    // merges a caller-supplied dictionary into the bag), so it cannot be left to fail at write
    // time or emit invalid JSON: the declared property wins and the shadowed key is dropped.
    //
    // Implemented by wrapping the container's getter rather than by a converter, because extension
    // data is written straight from whatever this getter returns. The wrapper hands back the SAME
    // dictionary reference whenever there is no collision, which is every ordinary payload — and
    // that matters beyond allocation: System.Text.Json also calls this getter on the DESERIALIZE
    // path, to find an existing dictionary to populate. Returning the original there is what keeps
    // binding untouched. The one uncovered corner is a model that pre-initializes its container
    // with a key equal to one of its own declared property names; deserializing into such an
    // instance would populate the filtered copy. That model is already self-contradictory, and the
    // alternative — emitting invalid JSON on every read of it — is worse.
    private static void SuppressKeysShadowingADeclaredProperty(
        JsonTypeInfo typeInfo, JsonPropertyInfo container, ILogger? logger)
    {
        Func<object, object?>? get = container.Get;
        if (get is null) return;

        // JSON names, so the configured naming policy is already applied — the same strings the
        // writer is about to emit. Ordinal: a duplicate JSON key is a byte-for-byte repeat, and
        // suppressing a merely case-differing key would be silent data loss, not a fix.
        var declaredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (!ReferenceEquals(property, container)) declaredNames.Add(property.Name);
        }
        if (declaredNames.Count == 0) return;

        Type ownerType = typeInfo.Type;
        container.Get = instance =>
        {
            object? bag = get(instance);
            return bag switch
            {
                IDictionary<string, object?> objectBag =>
                    DropShadowedKeys(objectBag, declaredNames, ownerType, logger),
                IDictionary<string, JsonElement> elementBag =>
                    DropShadowedKeys(elementBag, declaredNames, ownerType, logger),
                _ => bag,
            };
        };
    }

    private static IDictionary<string, TValue> DropShadowedKeys<TValue>(
        IDictionary<string, TValue> bag, HashSet<string> declaredNames, Type ownerType, ILogger? logger)
    {
        if (bag.Count == 0) return bag;

        List<string>? shadowed = null;
        foreach (string key in bag.Keys)
        {
            if (declaredNames.Contains(key)) (shadowed ??= new List<string>()).Add(key);
        }
        if (shadowed is null) return bag;

        IDictionary<string, TValue>? filtered = TryCreateEmptyLike(bag);
        if (filtered is null)
        {
            // Nothing safe to substitute. Better a duplicate key plus a loud error than a 500 on a
            // read: the request still returns the data, and the log says exactly what is wrong.
            logger?.LogError(
                "OhData: open complex type '{Type}' carries dynamic key(s) '{Keys}' that shadow its own " +
                "declared properties, and its container type '{ContainerType}' could not be cloned to " +
                "suppress them — the response will contain a duplicate JSON property name. Remove the " +
                "shadowing key(s), or give the container a type with a parameterless constructor.",
                ownerType.FullName, string.Join(", ", shadowed), bag.GetType().FullName);
            return bag;
        }

        foreach (KeyValuePair<string, TValue> entry in bag)
        {
            if (!declaredNames.Contains(entry.Key)) filtered.Add(entry.Key, entry.Value);
        }
        foreach (string key in shadowed)
        {
            logger?.LogWarning(
                "OhData: open complex type '{Type}' carries the dynamic key '{Key}', which is also " +
                "the name of one of its declared properties. The declared property wins and the " +
                "dynamic key is omitted from the response — emitting both would produce a duplicate " +
                "JSON property name.",
                ownerType.FullName, key);
        }
        return filtered;
    }

    // The replacement bag must be of the SAME runtime type. System.Text.Json resolves the
    // extension-data converter from the container's DECLARED property type and casts the value the
    // getter returns back to it, so handing a plain Dictionary back for a container declared as
    // `MyBag : Dictionary<string, object?>` throws InvalidCastException — measured, not assumed.
    // Cloning the bag's own runtime type covers that and the ordinary declared-as-interface case
    // alike. Returns null when the type cannot be instantiated (no parameterless constructor — a
    // ReadOnlyDictionary, say); a serialization path must never fault over a data condition, so the
    // caller degrades rather than throwing.
    private static IDictionary<string, TValue>? TryCreateEmptyLike<TValue>(IDictionary<string, TValue> bag)
    {
        Type runtimeType = bag.GetType();
        if (runtimeType == typeof(Dictionary<string, TValue>)) return new Dictionary<string, TValue>();
        try
        {
            return Activator.CreateInstance(runtimeType) as IDictionary<string, TValue>;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Surfaces a contract <c>System.Text.Json</c> rejects once from <c>MapOhData()</c> rather
    /// than as a 500 on the first request that touches the type. Covers two failure modes: a
    /// <see cref="JsonTypeInfo"/> that cannot be resolved at all, and a type that ends up with
    /// <b>more than one</b> extension-data member (the model already declares a different member
    /// as <c>[JsonExtensionData]</c>, and a type may have only one).
    /// </summary>
    /// <remarks>
    /// The second check is not redundant with the first: <c>GetTypeInfo</c> accepts a two-extension
    /// -member contract without complaint and the failure only appears from
    /// <c>JsonSerializer.Serialize</c> — measured, so the resolution probe alone would have missed
    /// the very case this method was written for. It is reachable through the ordinary EDM route
    /// whenever the competing member is one the model builder does not recognise as a container,
    /// e.g. <c>[JsonExtensionData] JsonObject</c> — that is <c>IDictionary&lt;string, JsonNode?&gt;</c>,
    /// so the builder walks past it and designates the real dictionary member instead.
    /// <para>
    /// <b>What this cannot cover.</b> Startup sees types, not values, so a fault that depends on
    /// the runtime <i>instance</i> in the container is out of reach — most notably a writable
    /// container property holding a read-only dictionary, which resolves a perfectly valid
    /// <see cref="JsonTypeInfo"/>, serializes fine, and throws
    /// <see cref="NotSupportedException"/> ("Collection is read-only") only when a write request
    /// tries to bind a dynamic key into it. That surfaces as a 500 through the group-level
    /// exception filter, exactly as any other handler-time fault does.
    /// </para>
    /// <para>
    /// Probes a throwaway copy of <paramref name="options"/>: resolving a
    /// <see cref="JsonTypeInfo"/> marks the options instance read-only, and the registration's own
    /// options must stay free to be copied and re-derived (nav suppression does exactly that, per
    /// request path). The copy shares the same resolver, so it exercises the same modifier chain.
    /// </para>
    /// </remarks>
    internal static void ValidateOrThrow(
        JsonSerializerOptions options,
        OpenComplexTypeContainers containers)
    {
        if (containers.IsEmpty) return;
        var probe = new JsonSerializerOptions(options);

        // Every open complex type, not just the container-DECLARING ones: a derived open type has
        // its own JsonTypeInfo contract, and its own chance to carry a competing extension-data
        // member, even though ByDeclaringType collapses it onto its base's entry.
        foreach (Type openClrType in containers.OpenClrTypes)
        {
            JsonTypeInfo typeInfo;
            try
            {
                typeInfo = probe.GetTypeInfo(openClrType);
            }
            // Any exception, wrapped: the point of this method is that MapOhData() explains what
            // went wrong once, rather than letting a bare System.Text.Json message escape with no
            // indication that an open type was involved.
            catch (Exception ex)
            {
                throw ContractRejected(openClrType, containers,
                    $"resolving its JSON contract threw {ex.GetType().Name}: {ex.Message}", ex);
            }

            int extensionDataMembers = typeInfo.Properties.Count(p => p.IsExtensionData);
            if (extensionDataMembers > 1)
            {
                throw ContractRejected(openClrType, containers,
                    $"the contract ended up with {extensionDataMembers} extension-data members. A type " +
                    "can carry only one, so remove any competing [JsonExtensionData] attribute on it.",
                    inner: null);
            }
        }
    }

    private static InvalidOperationException ContractRejected(
        Type openClrType, OpenComplexTypeContainers containers, string detail, Exception? inner)
    {
        string containerName =
            TryFindContainer(containers.ByDeclaringType, openClrType, out PropertyInfo? container)
                ? container.Name
                : "(unresolved)";
        return new InvalidOperationException(
            $"OhData: '{openClrType.FullName}' is an OData open complex type whose dynamic-property " +
            $"container is '{containerName}', but System.Text.Json rejected that contract: {detail}", inner);
    }

    // System.Text.Json's own requirements for an extension-data member (JsonPropertyInfo.
    // IsExtensionData): the member must be assignable to IDictionary<string, object> or
    // IDictionary<string, JsonElement>, and must be both readable and writable (it is populated
    // on read and enumerated on write).
    //
    // Both halves fail loudly rather than skipping the type, because the registration asked for
    // open types explicitly and every silent outcome is worse (see the remarks on
    // BuildOpenComplexTypeContainerMap). Their reachability differs:
    //   - The writability half is the idiomatic `public IDictionary<string, object?> Bag { get; }
    //     = new();`, which ODataConventionModelBuilder happily infers as a container. Measured: the
    //     CSDL says OpenType="true" with no Bag property, and the wire nests Bag anyway.
    //   - The type half is UNREACHABLE today, and is a defensive guard rather than a user-facing
    //     error. ODataConventionModelBuilder only ever infers a container from an
    //     IDictionary<string, object>-assignable member (measured: an IDictionary<string, string> or
    //     IDictionary<string, JsonElement> member is mapped as an ordinary Collection(KeyValuePair)
    //     property and its type is not marked open at all), and no consumer can write the annotation
    //     by hand -- EdmAnnotationExtensions exposes only a GETTER for it, and
    //     DynamicPropertyDictionaryAnnotation itself is internal to Microsoft.OData.ModelBuilder. So
    //     there is no test for this branch: it exists so a future widening of the builder's
    //     inference fails loudly at startup instead of throwing out of the modifier mid-request.
    //     (IDictionary<string, JsonElement> is accepted for the same reason -- it is half of what
    //     System.Text.Json actually allows, even though the builder never produces it.)
    private static void ThrowIfUnusableAsExtensionData(PropertyInfo container, IEdmComplexType complexType)
    {
        Type type = container.PropertyType;
        bool usableType = typeof(IDictionary<string, object>).IsAssignableFrom(type)
            || typeof(IDictionary<string, JsonElement>).IsAssignableFrom(type);

        string? problem = (container.CanRead, container.CanWrite, usableType) switch
        {
            (_, _, false) =>
                $"its type '{type.Name}' is not assignable to IDictionary<string, object> or " +
                "IDictionary<string, JsonElement>",
            (false, _, _) => "it has no accessible getter",
            (_, false, _) => "it has no accessible setter, so incoming dynamic keys could not be bound into it",
            _ => null,
        };
        if (problem is null) return;

        throw new InvalidOperationException(
            $"OhData: complex type '{complexType.FullName()}' is an OData open type whose " +
            $"dynamic-property container is '{container.DeclaringType!.Name}.{container.Name}', but " +
            $"that member cannot be used as System.Text.Json extension data because {problem}. " +
            "Give it a public getter and setter of type IDictionary<string, object?> " +
            "(for example `public IDictionary<string, object?>? Bag { get; set; }`), or drop the " +
            "member so the type is no longer open. Open types are opt-in " +
            "(AddOhData(o => o.WithOpenTypes())), so this is not skipped silently.");
    }

    // Walks the base-type chain so a DERIVED open complex type resolves the container its base
    // declares (the convention builder reports the base's PropertyInfo for the derived EDM type,
    // and System.Text.Json surfaces the same member — same declaring type, same metadata token —
    // on the derived contract). A derived type that SHADOWS the container with `new` gets its own
    // map entry — the builder records the derived member for the derived EDM type — and the exact
    // type match below finds it before the base's, so the shadowing member is what gets flattened.
    // Bounded by the CLR inheritance depth and runs once per type per options instance:
    // JsonTypeInfo is cached on the options after first resolution.
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

    // ── Write-side validation ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the first dynamic-property key in a write body that OData does not allow as a
    /// property name, or <c>null</c> when every key is acceptable. The caller turns a non-null
    /// result into a <c>400</c>.
    /// </summary>
    /// <remarks>
    /// Bag keys are otherwise persisted verbatim and echoed on every subsequent read, which turns
    /// a single POST into a <i>stored</i> fault against other consumers: <c>@odata.type</c> inside
    /// a complex value is what a conforming reader (Microsoft.OData.Client, for one) uses to
    /// resolve the type of that value, and <c>@odata.id</c> is an entity reference. Nesting these
    /// under a declared container — which is what happens without the feature — makes them inert
    /// payload; flattening them makes them control information. So the flattening is exactly what
    /// creates the need for this check.
    /// <para>
    /// <b>Walked as JSON against <see cref="JsonTypeInfo"/>, not as a CLR graph after binding.</b>
    /// The alternative — inspect the bound dictionaries — needs a reflection plan describing where
    /// bags can occur under each model type, plus a cycle guard for self-referential models.
    /// <see cref="JsonTypeInfo"/> already <i>is</i> that plan: it names the declared JSON
    /// properties, their types, and which member (if any) is extension data — resolved by the very
    /// options the binder is about to use, and cached on them. Recursion is bounded by the JSON
    /// document's depth, which the reader has already capped. Nothing is added to
    /// <c>OhDataEndpointFactory</c> beyond one call per write route.
    /// </para>
    /// <para>
    /// A key equal to a declared property's name needs no check here: <c>System.Text.Json</c> binds
    /// it to the declared property and it never reaches the bag (measured). The collision this
    /// cannot see is the one that arrives from server-side data, which
    /// <see cref="SuppressKeysShadowingADeclaredProperty"/> handles on the way out.
    /// </para>
    /// </remarks>
    internal static string? FindInvalidDynamicKey(
        JsonElement element, Type declaredType, JsonSerializerOptions options)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            // The element type comes from CLR reflection rather than JsonTypeInfo.ElementType,
            // which only exists from .NET 9 and this assembly also targets net8.0.
            Type? itemType = GetEnumerableElementType(declaredType);
            if (itemType is null) return null;
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? found = FindInvalidDynamicKey(item, itemType, options);
                if (found is not null) return found;
            }
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!options.TryGetTypeInfo(declaredType, out JsonTypeInfo? typeInfo)) return null;
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return null;

        // Resolved from the SAME contract the binder will use, so "declared" here means exactly
        // what it will mean during deserialization — including a [JsonPropertyName] rename, the
        // registration's naming policy, and any member an earlier modifier removed.
        bool isOpen = false;
        var declared = new Dictionary<string, JsonPropertyInfo>(
            options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.IsExtensionData) isOpen = true;
            else declared[property.Name] = property;
        }

        foreach (JsonProperty member in element.EnumerateObject())
        {
            if (declared.TryGetValue(member.Name, out JsonPropertyInfo? property))
            {
                if (member.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
                string? found = FindInvalidDynamicKey(member.Value, property.PropertyType, options);
                if (found is not null) return found;
            }
            // Unmatched members of a type that is NOT open are ignored on binding, exactly as they
            // are today — only a key that will actually land in a dynamic bag is policed.
            else if (isOpen && !IsValidDynamicPropertyName(member.Name))
            {
                return member.Name;
            }
        }
        return null;
    }

    // The item type of a collection-shaped CLR member, or null when the member is not a collection
    // of a single item type (a string is deliberately excluded — it is IEnumerable<char>, never a
    // JSON array here). `string`/primitive item types simply resolve to a JsonTypeInfo whose Kind
    // is not Object, so they terminate the walk one level down without a special case.
    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];
        foreach (Type candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>
    /// An OData dynamic property name must be a simple identifier (CSDL §4.1 <c>odataIdentifier</c>):
    /// a letter or <c>_</c> followed by up to 127 letters, digits or <c>_</c>.
    /// </summary>
    /// <remarks>
    /// The exclusions are what matter. <c>@</c> introduces control information (JSON Format §4.5)
    /// — <c>@odata.type</c>, <c>@odata.id</c> — and <c>Name@odata.count</c> is the inline
    /// annotation grammar, so any key carrying <c>@</c> is not a property name at all. <c>.</c> is
    /// the namespace separator. The empty string and embedded whitespace are not identifiers and
    /// are not addressable by any query option.
    /// </remarks>
    internal static bool IsValidDynamicPropertyName(string name)
    {
        if (name.Length is 0 or > 128) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_') return false;
        }
        return true;
    }
}
