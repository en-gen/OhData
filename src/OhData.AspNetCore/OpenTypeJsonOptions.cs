using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
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
/// <b>On by default; <c>OhDataBuilder.WithOpenTypes(false)</c> is the escape hatch.</b> A complex
/// type with a dictionary member <i>is</i> an open type, and the CSDL this same
/// <c>ODataConventionModelBuilder</c> emits has always said so — <c>OpenType="true"</c>, container
/// omitted from the declared properties — whether or not anything in this file ran. Leaving the
/// wire nested made <c>$metadata</c> and the payload disagree and made the conformant shape
/// something the developer had to know the spec to ask for. It also diverged from
/// <c>Microsoft.AspNetCore.OData</c>, whose <c>ODataResourceSerializer.AppendDynamicProperties</c>
/// reads the very same annotation and appends dynamic properties flat with <i>no</i> opt-in flag on
/// that path.
/// </para>
/// <para>
/// <b>The hazard the opt-in used to guard is real and did not go away.</b> Flattening a container
/// <i>re-binds</i> a body an existing adopter is already sending: once the member is extension data
/// it is no longer a <i>declared</i> property, so <c>{"Meta":{"Bag":{"a":1}}}</c> stops meaning "the
/// Bag property" and starts meaning "a dynamic key literally named Bag" holding the old dictionary.
/// Because the echo of that mis-bound value is byte-identical to the correct one, the corruption is
/// invisible from the wire and an adopter cannot find it by diffing staging responses.
/// <see cref="WarnWireShapeIsFlat"/> is the mitigation: one startup warning per affected complex
/// type, naming the type and the container. A model with <b>no</b> dictionary member is untouched in
/// either setting — <see cref="Build"/> returns the base options reference-equal, nothing is logged,
/// and <c>OhDataRegistration.OpenTypesActive</c> keeps every write path off.
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
/// <b>That ordering is an asserted invariant, not an emergent one</b> (<c>OpenTypeModifierOrderingTests</c>).
/// Running before the ignored-property modifier, or having nav suppression derive from the
/// pre-open-type options, would each break a property this file relies on — see the ordering note in
/// <c>OhDataEndpointFactory.MapAll</c>. The <c>ignoredJsonNamesByType</c> argument to
/// <see cref="Build"/> does <i>not</i> make the two modifiers meet: they still never touch the same
/// <see cref="JsonTypeInfo"/>. The withheld names cross as <b>data</b>, because <c>Ignore(...)</c>
/// works by <i>removing</i> a member and extension data captures exactly what a modifier removed —
/// so a bag key spelled like a withheld member would otherwise be a write-then-read oracle under the
/// withheld name. See <see cref="ThrowOnKeysThatCannotBeEmitted"/>.
/// </para>
/// <para>
/// <b>Scope: complex types only</b> — entity-root dynamic containers are not handled yet (see
/// <c>docs/open-types.md</c> and issue #398). What that scope is <i>not</i> justified by any more:
/// this file used to claim the blocker was that <c>Delta&lt;T&gt;</c> "has no mechanism" for
/// routing an undeclared key, because the PATCH loop resolves body members through
/// <c>FindClrPropertyByEdmName</c> and skips what it cannot resolve. <b>The premise was false.</b>
/// <c>Microsoft.AspNetCore.OData</c>'s <c>Delta&lt;T&gt;</c> has carried a
/// <c>dynamicDictionaryPropertyInfo</c> constructor parameter all along
/// (<c>Deltas/DeltaOfT.cs:112</c>), and Microsoft itself constructs it exactly that way from the EDM
/// in <c>ODataResourceDeserializer.cs:270-286</c>. Measured against the referenced package
/// (9.5.0, .NET 10.0.11) by direct invocation, not read off the source:
/// <c>new Delta&lt;T&gt;(typeof(T), updatableProperties, containerPropertyInfo)</c> exists, its
/// <c>TrySetPropertyValue("tier", "gold")</c> returns <c>true</c>, <c>Patch(target)</c> <b>merges</b>
/// into a pre-seeded container (<c>{pre:1}</c> + <c>tier</c> → <c>{pre:1, tier:gold}</c>), and an
/// <c>updatableProperties</c> allowlist makes it refuse a declared-but-withheld name outright rather
/// than routing it to the bag. <c>GetChangedPropertyNames()</c> does exclude dynamic names, which is
/// a real ergonomic gap, but it is not a blocker.
/// <para>
/// So what remains for entity roots is ordinary staged work — <c>Ignore()</c> containment, control
/// -information tolerance, <c>$select</c> pushdown, then the widening itself — not an absent
/// mechanism. Do not re-derive "entity roots are impossible" from this paragraph.
/// </para>
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
    /// member as extension data. Both alternative outcomes are worse than a startup failure that
    /// names the member and the fix: silently skipping leaves the CSDL saying
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
        foreach (IEdmComplexType complexType in
                 model.SchemaElements.OfType<IEdmComplexType>().Where(t => t.IsOpen))
        {
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
    // No ILogger parameter: the one thing this used to log — a bag key shadowing a declared property
    // — is now an InvalidOperationException, which the group-level exception filter logs (and renders
    // as 500 + the OData error envelope) like any other handler fault. A bag key that is not a valid
    // odataIdentifier throws from the same pass, for the same reason.
    internal static JsonSerializerOptions Build(
        JsonSerializerOptions baseOptions,
        OpenComplexTypeContainers containers,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredJsonNamesByType)
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
                ignoredJsonNamesByType.TryGetValue(typeInfo.Type, out IReadOnlySet<string>? ignoredNames);
                ThrowOnKeysThatCannotBeEmitted(typeInfo, property, ignoredNames);
                break;
            }
        });
        return derived;
    }

    // Wraps the container's getter with an inspection pass that rejects the three kinds of key
    // server-side data can put in a bag but the writer cannot legally emit: a key equal to a DECLARED
    // property's JSON name, a key spelled like a name the profile WITHHOLDS with Ignore(...), and a
    // key that is not a valid odataIdentifier at all. The rest of this comment is the reasoning for
    // the first; the third is argued at ThrowIfAnyKeyCannotBeEmitted and the second at the
    // withheld-name block below.
    //
    // A bag key equal to a DECLARED property's JSON name would otherwise emit that name twice in
    // one JSON object — measured: `{"Region":"declared","Region":"fromBag"}`, which every .NET
    // reader tested resolves in the BAG's favour, making the declared value unreachable, and which
    // Microsoft's ODataWriter runs an explicit duplicate-property-name check to prevent. This is a
    // hard error: the request fails with 500 + the OData error envelope.
    //
    // THE SPEC DOES NOT RULE ON IT -- checked, not assumed. OData CSDL 4.01 §6.3 (open entity type)
    // and §9.3 (open complex type) say only that an open type "allows clients to add properties
    // dynamically to instances of the type by specifying UNIQUELY NAMED property values in the
    // payload". That is directional but it is not a MUST NOT addressed to the server. OData JSON
    // Format 4.01 does not address it at all and defers to RFC 8259, where object member names
    // "SHOULD be unique". So dropping the key and failing the request are BOTH conformant; the only
    // thing actually ruled out is emitting the duplicate, which neither does.
    //
    // With no spec constraint, this follows Microsoft.AspNetCore.OData deliberately. It guards the
    // same condition in both directions and treats it as InvalidOperation (-> 500) on both:
    //   - Formatter/Serialization/ODataResourceSerializer.cs:825-829 -- builds
    //       new HashSet<string>(resource.Properties.Select(p => p.Name))
    //     and throws SRResources.DynamicPropertyNameAlreadyUsedAsDeclaredPropertyName ("The name of
    //     dynamic property '{0}' was already used as the declared property name of open type '{1}'.")
    //   - Formatter/Deserialization/DeserializationHelper.cs:266-269 -- throws
    //     SRResources.DuplicateDynamicPropertyNameFound. (Narrower than the serializer's check: that
    //     one fires when a dynamic key is ALREADY IN the bag, i.e. a duplicate DYNAMIC key, not a
    //     declared-name shadow. Cited here as evidence of the posture -- MS hard-errors on dynamic
    //     property name conflicts rather than degrading -- not as a second check of this exact
    //     condition.)
    //
    // The second reason is that the failure is SYSTEMATIC, not per-row. A client cannot create this
    // collision: System.Text.Json binds a body key matching a declared name to the DECLARED property,
    // so it never reaches the bag (measured). The only source is server-side code -- typically a
    // handler merging a caller-supplied dictionary into the container without excluding declared
    // names -- and if that can fire at all it fires for every row carrying the key. A warning in a
    // log stream is the wrong signal for a systematic defect.
    //
    // The cost is accepted deliberately and is stated in docs/open-types.md: a collection endpoint
    // faults on the bad data rather than serving the remaining rows.
    //
    // Implemented by wrapping the container's getter rather than by a converter, because extension
    // data is written straight from whatever this getter returns. The wrapper ALWAYS hands back the
    // same dictionary reference; it only inspects. That matters beyond allocation: System.Text.Json
    // also calls this getter on the DESERIALIZE path, to find an existing dictionary to populate, so
    // returning anything else there would corrupt binding. (The previous drop-and-clone
    // implementation did exactly that, and a container PRE-SEEDED with a declared name consequently
    // lost every dynamic key in the request while reporting 201 — #389 M2. Throwing removes that
    // corner entirely: such a model now fails loudly on the first request instead of silently
    // discarding writes.)
    private static void ThrowOnKeysThatCannotBeEmitted(
        JsonTypeInfo typeInfo, JsonPropertyInfo container, IReadOnlySet<string>? ignoredJsonNames)
    {
        Func<object, object?>? get = container.Get;
        if (get is null) return;

        // JSON names, so the configured naming policy is already applied — the same strings the
        // writer is about to emit. ORDINAL, deliberately: only a byte-for-byte repeat is a duplicate
        // JSON key, and faulting on a merely case-differing key would reject data that serializes
        // perfectly well. See docs/open-types.md for the consequence — OhData binds request bodies
        // case-insensitively, so a case-differing key can still round-trip into a corrupting write.
        //
        // THE WITHHELD NAMES ARE NOT UNIONED INTO THIS SET, and the split is the #398 review HIGH-1
        // fix. They are held to the BINDER's comparer instead (see below), which is a different
        // comparer from this one whenever PropertyNameCaseInsensitive is set — i.e. always, in
        // production. One HashSet cannot carry two comparers, so there are two.
        var declaredNames = new HashSet<string>(
            typeInfo.Properties.Where(p => !ReferenceEquals(p, container)).Select(p => p.Name),
            StringComparer.Ordinal);

        // THE Ignore()d NAMES HAVE TO BE UNIONED IN, and they cannot be read off typeInfo.Properties
        // — that is the whole reason they arrive as a separate argument. Ignore() (#226) works by
        // REMOVING the member in an earlier TypeInfoResolver modifier, so by the time this modifier
        // runs the member is gone from the contract and its JSON name is unrecoverable here.
        //
        // Extension data captures exactly what a modifier removed. Measured at raw System.Text.Json
        // level on .NET 10.0.11, one modifier removing "Removed" and a second marking "Bag" as
        // extension data:
        //
        //   Deserialize({"Id":1,"Removed":"leak","Hidden":"leak2","x":1})
        //     -> bag: Removed=leak, x=1        <- the REMOVED member landed in the bag
        //     -> Removed property itself: ""   <- still withheld, as intended
        //     -> reserialize -> {"Id":1,...,"Removed":"leak","x":1}
        //
        // Note "Hidden" — which carries [JsonIgnore] and is therefore STILL PRESENT in
        // typeInfo.Properties (see OhDataEndpointFactory.IsNavVisibleInBaseOptions) — did not leak.
        // Only a modifier-REMOVED member does. So the mechanism Ignore() is built on is precisely
        // the mechanism that would defeat it, and the consequence is not cosmetic: a bag key spelled
        // like a withheld property is a write-then-read oracle UNDER THE EXACT WITHHELD NAME, which
        // to any consumer or log looks like the withheld field being served.
        //
        // Checking them here makes such a key the same hard InvalidOperationException (-> 500 + the
        // OData error envelope) as any other declared-name collision, and it is the read-side half of
        // a pair: the write side drops the same name from the body in FindInvalidDynamicKey, so a
        // withheld name cannot enter the bag from a request NOR leave it towards a client.
        //
        // KEPT AS A SEPARATE SET RATHER THAN UNIONED INTO declaredNames — #398 review HIGH-1, and the
        // reason is a comparer, not tidiness. The withheld set arrives already built with
        // IgnoredPropertyJsonOptions.WithheldNameComparer, i.e. the comparer the BINDER matches body
        // keys with (OrdinalIgnoreCase in production). declaredNames above is deliberately ORDINAL for
        // a different question — whether two keys would emit as one duplicate JSON key — and the two
        // answers must not be merged: unioning an OrdinalIgnoreCase set into an Ordinal HashSet keeps
        // the HashSet's comparer, so every case-differing spelling of a withheld name silently passed.
        // Measured on the pre-fix tree: a server-side bag key `secret` serialized out where `Secret`
        // threw. Do NOT re-merge these, and do not make declaredNames case-insensitive to do it — that
        // would fault on data that serializes perfectly well (see the paragraph above, and #395).
        //
        // AT COMPLEX SCOPE THIS IS A NO-OP AND IS MEANT TO BE. Ignore(...) takes a root member of a
        // profile's ENTITY type, and a container lives on a COMPLEX type, which is never a profile's
        // model type — so ignoredJsonNames is null here for every type this modifier actually visits.
        // It ships now, with tests, so the mechanism is proven before the entity-root widening (#398)
        // makes it load-bearing; the alternative is landing the security-critical half and its first
        // real exercise in the same change.
        //
        // There is deliberately NO `declaredNames.Count == 0` bail here. There used to be one, when
        // shadowing was the only condition checked and a type with no other declared property had
        // nothing to collide with. The identifier check below does not depend on the declared set,
        // so a bag-only open complex type — `{ IDictionary<string, object?> Bag { get; set; } }` and
        // nothing else — has to be wrapped too.

        Type ownerType = typeInfo.Type;
        container.Get = instance =>
        {
            object? bag = get(instance);
            switch (bag)
            {
                // The two shapes System.Text.Json accepts as extension data. Only the KEYS are read;
                // the bag itself is returned untouched either way.
                case IDictionary<string, object?> objectBag:
                    ThrowIfAnyKeyCannotBeEmitted(objectBag.Keys, declaredNames, ignoredJsonNames, ownerType);
                    break;
                case IDictionary<string, JsonElement> elementBag:
                    ThrowIfAnyKeyCannotBeEmitted(elementBag.Keys, declaredNames, ignoredJsonNames, ownerType);
                    break;
            }
            return bag;
        };
    }

    // Throws on the FIRST offending key rather than collecting them all: a type carrying one bad key
    // almost always carries it on every instance, so an exhaustive list buys nothing. Values are never
    // included — this message can reach a log aggregator, and a dynamic value is arbitrary
    // caller-supplied data.
    //
    // The identifier message below does NOT quote the offending key, though the collision message
    // does. The asymmetry is deliberate: a colliding key is by definition equal to a DECLARED property
    // name, so it is bounded, developer-authored text. A key rejected by the grammar is by definition
    // NOT an identifier — it can hold newlines, control characters or arbitrary caller-derived data,
    // which is exactly the shape that corrupts a log line.
    //
    // THREE conditions, one pass over the bag. All have the same cause — server-side code put a key in
    // the container that the writer cannot legally emit — so they are checked together rather than in
    // three walks.
    //
    // The NAMELESS-KEY half is a DELIBERATE, RECORDED DIVERGENCE from Microsoft.AspNetCore.OData,
    // which SILENTLY SKIPS the empty case: ODataResourceSerializer.cs:820,
    // `if (string.IsNullOrEmpty(dynamicProperty.Key)) { continue; }`, immediately above the
    // declared-name collision check this file otherwise follows exactly. Three reasons to diverge
    // here and only here:
    //   - Matching the skip would mean REINTRODUCING the clone-and-substitute machinery deleted in
    //     e0edaac (TryCreateEmptyLike, DropShadowedKeys). This getter wrapper no longer produces a
    //     filtered copy — it inspects and hands back the SAME reference, which is precisely what
    //     removed the #389 M2 corner where a pre-seeded container silently lost every write.
    //     Resurrecting deleted code to produce a SILENT drop is the wrong trade.
    //   - Throwing is consistent with the house style just set for the declared-name collision
    //     directly below, and both conditions have the identical cause and the identical fix.
    //   - A nameless key is not an odataIdentifier (CSDL 4.01 §4.1), so emitting it produces a
    //     payload no conforming OData reader can address — an unaddressable property is not a lesser
    //     fault than a duplicated one.
    //
    // THE LINE IS THE FULL odataIdentifier GRAMMAR, and this is a widening of what used to be a mere
    // string.IsNullOrWhiteSpace test. The two conditions the old line separated —
    //   - a key that is null, empty or entirely whitespace, which is NOT A NAME AT ALL, and
    //   - "has space" or "@odata.type", which ARE names that merely happen to be illegal
    //     odataIdentifiers
    // — have the identical cause and the identical fix, and BOTH produce a property that no
    // conforming OData reader can address. The old line sat between them for a COST reason only:
    // IsNullOrWhiteSpace short-circuits on the first non-whitespace character, whereas naive grammar
    // validation is O(length) with a Unicode-category lookup per rune, which measured at ~3x the
    // declared-name hash lookup already in this loop.
    //
    // That cost is what IsValidDynamicPropertyNameCached removes: an ASCII SearchValues fast path
    // decides an ordinary key in one vectorized pass, measured at 4.6 ns/key against 16.9 for the
    // naive rune walk and 5.5 for the declared-name hash lookup this loop already performs. So full
    // grammar validation is now CHEAPER than the lookup it sits beside. The bounded validated-key
    // cache handles the remaining case, non-ASCII keys, which still pay the rune walk. See
    // IsValidDynamicPropertyNameCached for the full measurement table and for why the fast path is
    // deliberately NOT cached.
    //
    // A consequence worth stating, because it reverses a documented behaviour: a key made only of
    // FORMAT characters (U+200B ZERO WIDTH SPACE, category Cf) used to PASS here — it is not
    // whitespace by char.IsWhiteSpace — while still failing the ABNF, whose leading rune must be L or
    // Nl. It now fails, as do "has space" and "@odata.type". This check no longer merely rejects what
    // is not a name; it certifies that what remains is a valid one, so the read and write paths now
    // hold bag keys to exactly the same grammar.
    //
    // Still a DELIBERATE, RECORDED DIVERGENCE from Microsoft.AspNetCore.OData in the same direction
    // and for the same three reasons — it silently skips the empty key and polices nothing else.
    //
    // THREE conditions now, still one pass: the identifier grammar, a collision with a DECLARED name
    // (ordinal), and a collision with a WITHHELD name (the binder's comparer). The last two are
    // separate parameters rather than one merged set because they are asked with different comparers
    // — see ThrowOnKeysThatCannotBeEmitted for why, and why merging them was #398 review HIGH-1.
    private static void ThrowIfAnyKeyCannotBeEmitted(
        IEnumerable<string> keys,
        HashSet<string> declaredNames,
        IReadOnlySet<string>? withheldNames,
        Type ownerType)
    {
        foreach (string key in keys)
        {
            // The null test is separate and comes first: Dictionary<string, T> cannot hold a null
            // key, but the container is typed as IDictionary<string, T> and a custom implementation
            // can yield one — and every path below (the cache's hash, the span, the length) would
            // throw a bare NullReferenceException on it.
            if (key is null || !IsValidDynamicPropertyNameCached(key))
            {
                throw new InvalidOperationException(
                    "OhData: the dynamic-property container of open complex type " +
                    $"'{ownerType.FullName}' holds a dynamic property name that is not a valid OData " +
                    "identifier. A dynamic property name must be a simple identifier (CSDL 4.01 §4.1 " +
                    "odataIdentifier) — a leading letter or '_' followed by up to 127 letters, " +
                    "digits, marks, connectors or format characters — so emitting this one would " +
                    "produce a property that no conforming OData reader can address. Remove that key " +
                    "from the type's dynamic-property container — server-side code that merges a " +
                    "caller-supplied dictionary into the container must hold its keys to the same " +
                    "grammar. This did not come from the request body: such a key is rejected with " +
                    "400 before it can bind.");
            }

            // The WITHHELD half, checked BEFORE the declared-name collision below because a withheld
            // name is no longer a declared one (the modifier removed it), so the two can never both
            // match — and because its message has to say something different: this key is not
            // shadowing a property the client can see, it is spelled like one the profile
            // deliberately CONCEALS, so emitting it would serve the withheld name back to a caller
            // that was never meant to see it. Matched with the binder's comparer, so
            // 'secret'/'SECRET'/'sEcReT' are refused exactly as 'Secret' is (#398 review HIGH-1).
            if (withheldNames is not null && withheldNames.Contains(key))
            {
                throw new InvalidOperationException(
                    $"OhData: the dynamic property name '{key}' on open complex type " +
                    $"'{ownerType.FullName}' is spelled like a property the profile withholds with " +
                    "Ignore(...). Emitting it would serve that name back to the client, which to any " +
                    "consumer or log is indistinguishable from the withheld property itself. Remove " +
                    "that key from the type's dynamic-property container — server-side code that " +
                    "merges a caller-supplied dictionary into the container must exclude withheld " +
                    "names. A client cannot cause this: an incoming body key spelled like a withheld " +
                    "name, in any casing the binder would match, is dropped from the body before it " +
                    "can bind.");
            }

            if (!declaredNames.Contains(key)) continue;
            throw new InvalidOperationException(
                $"OhData: the dynamic property name '{key}' on open complex type " +
                $"'{ownerType.FullName}' is already used as a declared property name of that type. " +
                "Each dynamic property of an open type must be uniquely named, and emitting both " +
                "would produce a duplicate JSON property name. Remove that key from the type's " +
                "dynamic-property container — server-side code that merges a caller-supplied " +
                "dictionary into the container must exclude names that are declared properties of " +
                "the type. A client cannot cause this: an incoming body key matching a declared " +
                "name binds to the declared property and never reaches the container.");
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
    // THE FOUR CATCH CLAUSES BELOW ARE THE MEASURED SURFACE OF GetTypeInfo, not a guess and not a
    // catch-all. Each was reproduced against System.Text.Json on .NET 10 with the same modifier
    // chain Build() installs, and each has a test:
    //   - InvalidOperationException — every contract STJ itself rejects. Duplicate JSON property
    //     names (including two names that collide only under PropertyNameCaseInsensitive), a
    //     [JsonConverter] incompatible with the member it decorates, more than one
    //     [JsonConstructor], and an extension-data member whose type STJ will not accept.
    //   - NotSupportedException — the resolver has no metadata for the type. Reachable whenever the
    //     consumer supplies their own TypeInfoResolver (a source-generated JsonSerializerContext,
    //     typically) that does not know the open complex type.
    //   - TargetInvocationException — a [JsonConverter]'s own constructor threw; STJ instantiates it
    //     reflectively, so whatever it threw arrives wrapped.
    //   - ArgumentException — GetTypeInfo's own guard on a Type it cannot serialize at all (pointer,
    //     by-ref, open generic, void). Not reachable from the EDM, whose open complex types are
    //     closed classes; caught so a future caller that reaches it still gets the explanatory
    //     message rather than a bare argument error.
    //
    // What is deliberately NOT caught is an arbitrary exception out of consumer-supplied resolver or
    // modifier code, which is a fault in that code rather than a contract System.Text.Json rejected;
    // it still fails MapOhData() loudly, with its own type and stack intact and this frame on it.
    //
    // The old clause here was `catch (Exception)`, justified by a NotSupportedException read-only
    // case it was said to cover. Measured, it did not: a container PRE-INITIALISED with a read-only
    // dictionary resolves a perfectly valid JsonTypeInfo (see the remark above), so GetTypeInfo never
    // threw for it and nothing about that case was ever caught here.
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
            // Wrapped, not swallowed: the point of this method is that MapOhData() explains what went
            // wrong once, rather than letting a bare System.Text.Json message escape with no
            // indication that an open type was involved. See the note above the method for how this
            // list of four was established.
            catch (InvalidOperationException ex) { throw ContractResolutionFailed(openClrType, containers, ex); }
            catch (NotSupportedException ex) { throw ContractResolutionFailed(openClrType, containers, ex); }
            catch (TargetInvocationException ex) { throw ContractResolutionFailed(openClrType, containers, ex); }
            catch (ArgumentException ex) { throw ContractResolutionFailed(openClrType, containers, ex); }

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

    /// <summary>
    /// Logs one <c>Warning</c> per open complex type at <c>MapOhData()</c>, naming the CLR type and
    /// the container member whose dynamic keys now serialize flat. Logs <b>nothing at all</b> when
    /// the model has no open complex type.
    /// </summary>
    /// <remarks>
    /// <b>This is the mitigation for a breaking change that cannot be detected by testing.</b> Open
    /// types are on by default (a complex type with a dictionary member <i>is</i> an open type and
    /// the CSDL has always said so), but the hazard that originally justified opt-in does not go
    /// away with the default: once the container is <c>System.Text.Json</c> extension data it is no
    /// longer a <i>declared</i> property, so an existing adopter's body
    /// <c>{"Meta":{"Bag":{"a":1}}}</c> binds <c>Bag</c> as a dynamic key <i>holding</i> the old
    /// dictionary and the handler persists <c>Bag = { "Bag": {"a":1} }</c>.
    /// <para>
    /// What makes that worth a startup warning rather than a line in the release notes is that the
    /// response echo of the mis-bound value is <b>byte-identical</b> to the correct one. An ordinary
    /// breaking change surfaces in an adopter's tests or in a staging diff; this one surfaces in
    /// neither — the wire looks right while the stored shape is wrong. Naming the affected types at
    /// startup is the only signal available before the data is already corrupt, and it is free:
    /// <see cref="BuildOpenComplexTypeContainerMap"/> already runs here and already knows exactly
    /// which types are affected.
    /// </para>
    /// <para>
    /// Iterates <see cref="OpenComplexTypeContainers.OpenClrTypes"/> rather than the container map's
    /// keys, because that is the list of types whose <i>wire shape</i> changed: a derived open
    /// complex type serializes flat too, even though it shares — and is collapsed onto — its base's
    /// container entry. The container member is resolved through the same base-chain walk the
    /// modifier uses, so the name reported is the member actually being flattened.
    /// </para>
    /// <para>
    /// Emitted once per registration at startup, never per request. It is not suppressed for a
    /// registration that passed <c>WithOpenTypes(true)</c> explicitly: the flag records what the
    /// consumer asked for, while this records what the model turned out to contain, and the
    /// distinction a suppression would rest on is not one the plain <c>bool</c> carries.
    /// </para>
    /// </remarks>
    internal static void WarnWireShapeIsFlat(OpenComplexTypeContainers containers, ILogger? logger)
    {
        if (logger is null || containers.IsEmpty) return;

        foreach (Type openClrType in containers.OpenClrTypes)
        {
            string containerName =
                TryFindContainer(containers.ByDeclaringType, openClrType, out PropertyInfo? container)
                    ? container.Name
                    : "(unresolved)";
            // Each placeholder appears EXACTLY once. Microsoft.Extensions.Logging binds a message
            // template's placeholders to the argument array positionally, so a repeated {Container}
            // would consume a second argument that is not there and render literally.
            logger.LogWarning(
                "OhData: '{Type}' is an OData open complex type whose dynamic-property container is " +
                "the member '{Container}'. Its dynamic keys serialize FLAT — as siblings of the " +
                "declared properties — and an incoming body binds the same way. This is the conformant " +
                "shape, and the one $metadata has always advertised for this type (OpenType=\"true\", " +
                "with that member omitted from the declared properties). If existing clients send or " +
                "read that member NESTED under its own name, such a body now binds its whole dictionary " +
                "as ONE dynamic key of that same name, and the response echo of the mis-bound value is " +
                "byte-identical to the correct one — so the difference will not show up in a response " +
                "diff. Call AddOhData(o => o.WithOpenTypes(false)) to restore the previous nested shape.",
                openClrType.FullName, containerName);
        }
    }

    private static InvalidOperationException ContractResolutionFailed(
        Type openClrType, OpenComplexTypeContainers containers, Exception inner) =>
        ContractRejected(openClrType, containers,
            $"resolving its JSON contract threw {inner.GetType().Name}: {inner.Message}", inner);

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
    // Both halves fail loudly rather than skipping the type, because every silent outcome is worse
    // (see the remarks on BuildOpenComplexTypeContainerMap) and the throw names the member and the
    // fix. Their reachability differs:
    //   - The writability half is the idiomatic `public IDictionary<string, object?> Bag { get; }
    //     = new();`, which ODataConventionModelBuilder happily infers as a container. Measured: the
    //     CSDL says OpenType="true" with no Bag property, and the wire nests Bag anyway.
    //   - The type half is UNREACHABLE today, and is a defensive guard rather than a user-facing
    //     error. ODataConventionModelBuilder only ever infers a container from an
    //     IDictionary<string, object>-assignable member (measured: an IDictionary<string, string> or
    //     IDictionary<string, JsonElement> member is mapped as an ordinary Collection(KeyValuePair)
    //     property and its type is not marked open at all).
    //
    //     This comment used to add "and no consumer can write the annotation by hand --
    //     EdmAnnotationExtensions exposes only a GETTER for it, and DynamicPropertyDictionaryAnnotation
    //     itself is internal". BOTH halves of that were false (#389 L3, measured by reflection):
    //     DynamicPropertyDictionaryAnnotation is an EXPORTED PUBLIC type with a public
    //     ctor(PropertyInfo), and StructuralTypeConfiguration.AddDynamicPropertyDictionary(PropertyInfo),
    //     StructuralTypeConfiguration.ModelBuilder and ODataModelBuilder.AddComplexType(Type) are all
    //     public. The raw builder is reachable from what AdvancedConfigure is handed, too: the chain
    //     EntitySetConfiguration<T>.EntityType -> EntityTypeConfiguration<T>.BaseType (the NON-generic
    //     EntityTypeConfiguration, which IS a StructuralTypeConfiguration) -> .ModelBuilder COMPILES
    //     and returns the very instance OhData is building with -- measured, ReferenceEquals true,
    //     whenever the entity type has an EDM base type.
    //
    //     The branch is unreachable for a simpler and verifiable reason: the ONLY public API that
    //     records the annotation validates this exact condition itself. AddDynamicPropertyDictionary
    //     throws ArgumentException("The argument must be of type 'IDictionary<string, object>'") for
    //     any other member type -- measured on the reachable route above. So a wrong-typed container
    //     cannot enter the model through the builder at all, by hand or by convention, and there is
    //     no test for this branch: it exists so a future widening of the builder's inference (or of
    //     that argument check) fails loudly at startup instead of throwing out of the modifier
    //     mid-request. (IDictionary<string, JsonElement> is accepted for the same reason -- it is
    //     half of what System.Text.Json actually allows, even though the builder never produces it.)
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
            "member so the type is no longer open. This is not skipped silently, because skipping " +
            "would leave $metadata saying OpenType=\"true\" while the wire nested the member anyway; " +
            "AddOhData(o => o.WithOpenTypes(false)) turns open types off for the whole registration.");
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
    /// The outcome of one type-directed walk of a write body: the first key that cannot become a
    /// dynamic property (the caller turns it into a <c>400</c>), and whether the body carries
    /// control information that has to be stripped before it is bound.
    /// </summary>
    /// <param name="InvalidKey">
    /// The first key an open type would have bagged but that OData does not allow as a property name
    /// (the <c>odataIdentifier</c> grammar), or <c>null</c> when every key is acceptable.
    /// </param>
    /// <param name="CarriesUnbindableKeys">
    /// <c>true</c> when at least one key that <b>must not become a dynamic property</b> sits at an
    /// object level that would bag it — control information, or a name the profile withholds with
    /// <c>Ignore(...)</c>. The caller must then re-emit the body through
    /// <see cref="RewriteWithoutUnbindableKeys"/> before binding; see that method for why leaving
    /// them in place is not an option.
    /// </param>
    internal readonly record struct WriteBodyScan(string? InvalidKey, bool CarriesUnbindableKeys);

    /// <summary>
    /// Walks a write body against the contract the binder is about to use, and reports both of the
    /// things the caller has to act on: the first unacceptable dynamic-property key, and whether any
    /// control information needs stripping first.
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
    /// it to the declared property and it never reaches the bag (measured) — which is also why the
    /// collision is never a client's doing. The one that arrives from server-side data is caught on
    /// the way out by <see cref="ThrowOnKeysThatCannotBeEmitted"/>, which now applies <b>this same
    /// grammar</b> to every key on the way out, so a bag key is held to identical rules in both
    /// directions — a 400 on the way in, a 500 on the way out.
    /// </para>
    /// <para>
    /// A name the profile <b>withholds</b> with <c>Ignore(...)</c> is the exception to the paragraph
    /// above and is classified here explicitly — as unbindable, so it is <i>dropped</i> from the body
    /// rather than answered with a <c>400</c>. Such a member has been <i>removed</i> from the
    /// contract by <see cref="IgnoredPropertyJsonOptions"/>, so it is no longer declared as far as
    /// <c>System.Text.Json</c> is concerned and extension data would capture it — see
    /// <see cref="ThrowOnKeysThatCannotBeEmitted"/> for the measurement. That is why the ignored
    /// JSON names have to be threaded in rather than read off <see cref="JsonTypeInfo"/>, and why
    /// they arrive already built with <see cref="IgnoredPropertyJsonOptions.WithheldNameComparer"/>:
    /// the binder matches body keys case-insensitively, so an ordinal withheld set would contain the
    /// exact spelling and miss every other one (#398 review HIGH-1).
    /// </para>
    /// </remarks>
    internal static WriteBodyScan ScanWriteBody(
        JsonElement element,
        Type declaredType,
        JsonSerializerOptions options,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredJsonNamesByType)
    {
        bool carriesUnbindableKeys = false;
        string? invalid = FindInvalidDynamicKey(
            element, declaredType, options, ignoredJsonNamesByType, ref carriesUnbindableKeys);
        return new WriteBodyScan(invalid, carriesUnbindableKeys);
    }

    // `carriesUnbindableKeys` is threaded by ref rather than returned, because this method already
    // owns its return slot for the first invalid key and the two answers are independent: a body can
    // need a rewrite AND be rejected, or either alone. A ref bool keeps the walk allocation-free,
    // which a state object or a tuple return would not.
    private static string? FindInvalidDynamicKey(
        JsonElement element,
        Type declaredType,
        JsonSerializerOptions options,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredJsonNamesByType,
        ref bool carriesUnbindableKeys)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            // The element type comes from CLR reflection rather than JsonTypeInfo.ElementType,
            // which only exists from .NET 9 and this assembly also targets net8.0.
            Type? itemType = GetEnumerableElementType(declaredType);
            if (itemType is null) return null;
            // NOT the lazy Select/FirstOrDefault spelling the other collection walks use, and the
            // difference is forced rather than stylistic: a `ref bool` cannot be captured by a
            // lambda. Short-circuiting on the first invalid key is preserved explicitly by the
            // `return` below. The behavioural change is that sibling elements past a failure are no
            // longer skipped for the CONTROL-INFORMATION flag — they are not walked at all, since
            // the invalid key aborts the request anyway and no rewrite will happen.
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? found = FindInvalidDynamicKey(
                    item, itemType, options, ignoredJsonNamesByType, ref carriesUnbindableKeys);
                if (found is not null) return found;
            }
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!options.TryGetTypeInfo(declaredType, out JsonTypeInfo? typeInfo)) return null;

        // A DICTIONARY-valued declared member is a JSON object that is not a bag: its keys are map
        // keys of a declared property, but System.Text.Json binds straight through it into the value
        // type — and that value type can be an open complex type. Bailing here on "Kind is not
        // Object" (#389 round-3 L1) stopped the walk one member short of the bag: measured, the
        // byte-identical keys were rejected with a 400 through a plain `TOpenComplex` member and
        // accepted with a 201 through an `IDictionary<string, TOpenComplex>` one, then echoed on
        // every later read.
        //
        // Only the VALUES are walked. The dictionary's own keys are declared-member map keys, not
        // dynamic property names — they were always legal to send and are not held to the identifier
        // grammar.
        if (typeInfo.Kind == JsonTypeInfoKind.Dictionary)
        {
            // From CLR reflection rather than JsonTypeInfo.ElementType, for the same reason the array
            // branch above uses reflection: ElementType only exists from .NET 9 and this assembly
            // also targets net8.0.
            Type? valueType = GetDictionaryValueType(declaredType);
            if (valueType is null) return null;
            // Scalar map values terminate the walk, so they are skipped. Same short-circuit as the
            // array branch above, and spelled as a loop for the same forced reason: `ref bool`
            // cannot be captured by a lambda.
            foreach (JsonProperty entry in element.EnumerateObject())
            {
                if (entry.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
                string? found = FindInvalidDynamicKey(
                    entry.Value, valueType, options, ignoredJsonNamesByType, ref carriesUnbindableKeys);
                if (found is not null) return found;
            }
            return null;
        }

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

        // The withheld set is looked up ONCE per object level rather than per member, and is null
        // for all but a handful of types. See the classification block below for what it is for.
        //
        // IT ALREADY CARRIES THE RIGHT COMPARER and must not be re-wrapped here (that would be a
        // per-request allocation). IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap builds every
        // set with WithheldNameComparer — the same comparer `declared` above is built with, and for
        // the same reason: this branch is reached precisely BECAUSE the key missed `declared`, so a
        // withheld set that disagreed with it about casing would leave every case-differing spelling
        // of a withheld name classified as an ordinary dynamic key. That was #398 review HIGH-1.
        ignoredJsonNamesByType.TryGetValue(typeInfo.Type, out IReadOnlySet<string>? ignoredJsonNames);

        // Unlike the dictionary walk above, nothing here filters the sequence: every member of an
        // open type is visited, and which of the branches it takes — declared, so recurse with the
        // member's own declared type; withheld; control information; or dynamic, so validate the key
        // and then recurse type-free — is the work, not a guard in front of it.
        foreach (JsonProperty member in element.EnumerateObject())
        {
            if (declared.TryGetValue(member.Name, out JsonPropertyInfo? property))
            {
                if (member.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
                string? found = FindInvalidDynamicKey(
                    member.Value, property.PropertyType, options, ignoredJsonNamesByType,
                    ref carriesUnbindableKeys);
                if (found is not null) return found;
            }
            // Unmatched members of a type that is NOT open are ignored on binding, exactly as they
            // are today — only a key that will actually land in a dynamic bag is policed.
            else if (isOpen)
            {
                // ── UNBINDABLE KEYS: DROPPED, NOT POLICED ──────────────────────────────────────
                //
                // Two sources, one outcome. Neither may become a dynamic property, and neither is a
                // client error, so both are removed from the body before it is bound (the flag tells
                // the caller to re-emit it) and the request otherwise proceeds normally.
                //
                // (1) A NAME THE PROFILE WITHHOLDS WITH Ignore(...) -- #398 stage 1, and the
                //     security-critical half of it. It reaches this branch precisely BECAUSE
                //     Ignore() REMOVED the member from typeInfo.Properties, so `declared` above does
                //     not contain it; without this line System.Text.Json routes it into the bag and
                //     the next read echoes it back under the exact withheld name -- a write-then-read
                //     oracle. ThrowOnKeysThatCannotBeEmitted carries the raw-STJ measurement and the
                //     read-side pair of this check.
                //
                //     DROPPED RATHER THAN 400, AND THE SPEC DOES NOT MANDATE EITHER. The tempting
                //     citation is Protocol 4.01 §11.4.2 (Create an Entity), "The service MUST fail if
                //     unable to persist all property values specified in the request" -- but "property
                //     value" is a term of art that already excludes things physically present in the
                //     body (@odata.* control information is in the body and is plainly not one), and
                //     on a type that does not declare the name there is no property for it to be a
                //     value OF. Read that way the clause is about failing to persist LEGITIMATE
                //     values -- a read-only member, a constraint violation -- and the only clause
                //     addressing extra names is the SHOULD NOT aimed at the client. §11.4.1.3
                //     "Handling of Properties Not Advertised in Metadata" sounds governing and is
                //     not: it tells CLIENTS to be prepared to RECEIVE undeclared properties and says
                //     nothing about the server write path. And no clause contemplates a server that
                //     deliberately CONCEALS a declared property, which is exactly what Ignore() does.
                //
                //     Ambiguous, so this follows Microsoft.AspNetCore.OData, which drops:
                //     ODataInputFormatter.cs:203 deliberately clears
                //     ValidationKinds.ThrowOnUndeclaredPropertyForNonOpenType -- code written to STOP
                //     ODataLib throwing, and a split between two layers of the reference
                //     implementation (ODataLib defaults that validation ON) is close to a definition
                //     of ambiguous. It is also what OhData's own closed-type path already does for
                //     unknown members, and what a POST of a withheld name already did before this
                //     change, so nothing observable moves.
                //
                //     AT COMPLEX SCOPE THIS IS UNREACHABLE, deliberately: Ignore(...) names a root
                //     member of a profile's ENTITY type, containers live on COMPLEX types, and only an
                //     open type reaches this `else if`. The entity root that IS in this map is never
                //     open until the #398 widening, so `ignoredJsonNames` is null at every level that
                //     gets here. It ships now, with tests, so the mechanism is proven before it is
                //     load-bearing rather than landing with its first real exercise.
                //
                // (2) CONTROL INFORMATION -- #398 stage 2. See IsControlInformationName for the rule
                //     and for why "contains '@'" is the whole of it. Measured: '@odata.type',
                //     'Name@odata.type', 'a@b', '@' and 'x@' all land in extension data under raw
                //     System.Text.Json, so skipping the key here without stripping it from the body
                //     would store an annotation the read path then throws on forever.
                if ((ignoredJsonNames is not null && ignoredJsonNames.Contains(member.Name))
                    || IsControlInformationName(member.Name))
                {
                    carriesUnbindableKeys = true;
                    continue;
                }

                if (!IsValidDynamicPropertyName(member.Name)) return member.Name;
                // #389 H1: the VALUE of an accepted dynamic key has to be walked too. Everything
                // below a bag key is stored verbatim and echoed on every later read, so stopping at
                // the first level left the exact vector this check exists to close open one level
                // down: {"Meta":{"nested":{"@odata.type":"#Evil"}}} was accepted with a 201 and then
                // served back forever, control information and all.
                //
                // NOTE THE ASYMMETRY WITH THE BLOCK ABOVE, which is deliberate. Control information
                // is tolerated at an object level the CONTRACT describes, where an '@' key genuinely
                // is an annotation about a declared or dynamic property of a known type. Below an
                // accepted bag key there is no contract and no declared/annotation distinction — the
                // whole subtree is opaque data that will be stored and echoed verbatim — so an
                // unaddressable key there stays a 400.
                string? nested = FindInvalidKeyInDynamicValue(member.Value);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a JSON member name is OData <b>control information</b> rather than a property name.
    /// The rule is the whole of it: <b>any</b> name containing <c>U+0040 COMMERCIAL AT</c>, at any
    /// position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why "contains", not "starts with".</b> OData JSON Format 4.01 gives <c>@</c> two shapes and
    /// they differ only in position. §4.5 puts control information about the enclosing object under a
    /// leading <c>@</c> — <c>@odata.type</c>, <c>@odata.id</c>, <c>@odata.etag</c>. §18 puts an
    /// annotation about a <i>particular</i> property under <c>Property@annotation</c>, embedded —
    /// <c>Name@odata.type</c>, <c>Items@odata.count</c>. A leading-only rule would tolerate the first
    /// and reject the second, which is the more common spelling on a write body.
    /// </para>
    /// <para>
    /// <b>Why every other <c>@</c> position falls under the same rule rather than needing one of its
    /// own.</b> The residue — <c>a@b</c>, <c>@</c>, <c>x@</c> — is not valid OData control information
    /// either, but it is <i>structurally incapable</i> of being a dynamic property: <c>@</c> is
    /// <see cref="UnicodeCategory.OtherPunctuation"/>, which the <c>odataIdentifier</c> ABNF admits in
    /// neither leading nor following position, so every such name was already a hard reject. There is
    /// no name that "contains <c>@</c>" reclassifies away from being a legitimate dynamic property.
    /// Classifying the residue as control information rather than as a 400 costs nothing and keeps
    /// the rule statable in one line.
    /// </para>
    /// <para>
    /// <b>This follows the reference implementation structurally, not by imitation.</b> There is no
    /// <c>@</c>-handling code in <c>Microsoft.AspNetCore.OData</c> at all — no
    /// <c>StartsWith("@")</c>, no <c>Contains("@")</c> — because ODataLib's JSON reader consumes
    /// control information into <c>ODataResource.TypeName</c>/<c>.Id</c>/<c>.ETag</c> and custom
    /// annotations into <c>.InstanceAnnotations</c> before the deserializer ever runs, and only
    /// <c>.Properties</c> can become a dynamic property. An <c>@</c>-containing name is
    /// <i>structurally</i> incapable of being a dynamic property there. OhData binds with
    /// <c>System.Text.Json</c>, which has no such reader stage — measured, it routes all five
    /// spellings above straight into extension data — so the classification has to be written down
    /// explicitly. Same rule, different layer.
    /// </para>
    /// <para>
    /// <b>Not consulted for a name the contract declares.</b> The caller checks declared members
    /// first, so a member deliberately renamed into an <c>@</c>-containing JSON name with
    /// <c>[JsonPropertyName("weird@name")]</c> still binds — measured: <c>System.Text.Json</c> accepts
    /// such a name and reports it verbatim in <see cref="JsonTypeInfo.Properties"/>. Only an
    /// <i>unmatched</i> key on an <i>open</i> type reaches this method.
    /// </para>
    /// <para>
    /// <b>Ordering against <c>@odata.bind</c>.</b> <c>ContainsODataBindAnnotation</c> runs on the POST
    /// route before any of this and still answers <c>501</c>, so deep-insert-by-reference keeps its
    /// explicit non-support answer rather than being silently swallowed as an annotation.
    /// </para>
    /// </remarks>
    private static bool IsControlInformationName(string name) =>
        name.IndexOf('@') >= 0;

    /// <summary>
    /// Re-emits a write body with every <b>unbindable</b> member removed from the object levels that
    /// would otherwise bag it — control information, and names the profile withholds with
    /// <c>Ignore(...)</c>. Call only when <see cref="WriteBodyScan.CarriesUnbindableKeys"/> said so;
    /// the returned <see cref="JsonDocument"/> is the caller's to dispose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the body has to be rewritten rather than the keys merely skipped.</b> Classifying a key
    /// decides only that it is not a <i>dynamic property</i>. It does not stop
    /// <c>System.Text.Json</c> binding it: extension data captures every unmatched member (measured —
    /// see <see cref="IsControlInformationName"/> and <see cref="ThrowOnKeysThatCannotBeEmitted"/>).
    /// Skipping a key in the walk and leaving the body alone would therefore be strictly worse than
    /// the <c>400</c> it replaces. For control information the annotation would be stored in the bag
    /// and every later read would throw from <see cref="ThrowOnKeysThatCannotBeEmitted"/> — because
    /// <c>@</c> is not a valid <c>odataIdentifier</c> — turning one accepted write into a permanent
    /// <c>500</c> on the row. For a withheld name it would be worse still: no throw, just the
    /// withheld name echoed back to the client. <b>"Dropped" has to mean the binder never sees it.</b>
    /// </para>
    /// <para>
    /// <b>Type-directed, mirroring the walk exactly.</b> Only an object level whose
    /// <see cref="JsonTypeInfo"/> has an extension-data member strips anything, and only from members
    /// that level's contract does not declare. Everything else — a closed type's unmatched members, a
    /// declared member whose JSON name happens to contain <c>@</c>, and the entire subtree below an
    /// accepted dynamic key — is copied through by <see cref="JsonElement.WriteTo"/>, which preserves
    /// raw number text verbatim. A body whose scan reported no control information is never handed to
    /// this method at all, so an unaffected request pays one <c>bool</c> test.
    /// </para>
    /// <para>
    /// The result is re-parsed rather than mutated in place because <see cref="JsonElement"/> is
    /// immutable and is what every call site's binder consumes. Escaping may differ from the original
    /// bytes (<see cref="Utf8JsonWriter"/> applies its own encoder), which is lossless: the document
    /// is parsed straight back and only the decoded values ever reach the binder.
    /// </para>
    /// </remarks>
    internal static JsonDocument RewriteWithoutUnbindableKeys(
        JsonElement element,
        Type declaredType,
        JsonSerializerOptions options,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredJsonNamesByType)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteWithoutUnbindableKeys(writer, element, declaredType, options, ignoredJsonNamesByType);
        }
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static void WriteWithoutUnbindableKeys(
        Utf8JsonWriter writer,
        JsonElement element,
        Type declaredType,
        JsonSerializerOptions options,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredJsonNamesByType)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            Type? itemType = GetEnumerableElementType(declaredType);
            if (itemType is null) { element.WriteTo(writer); return; }
            writer.WriteStartArray();
            foreach (JsonElement item in element.EnumerateArray())
            {
                WriteWithoutUnbindableKeys(writer, item, itemType, options, ignoredJsonNamesByType);
            }
            writer.WriteEndArray();
            return;
        }

        // Anything that is not an object, or whose contract cannot be resolved, is copied verbatim —
        // there is no level here for a bag to exist at, so there is nothing to classify.
        if (element.ValueKind != JsonValueKind.Object
            || !options.TryGetTypeInfo(declaredType, out JsonTypeInfo? typeInfo))
        {
            element.WriteTo(writer);
            return;
        }

        // A dictionary-valued declared member: its own keys are map keys, never dynamic property
        // names, so none of them is ever stripped. Only the VALUES are descended into, because the
        // value type can itself be an open complex type. Same shape as the walk's dictionary branch.
        if (typeInfo.Kind == JsonTypeInfoKind.Dictionary)
        {
            Type? valueType = GetDictionaryValueType(declaredType);
            if (valueType is null) { element.WriteTo(writer); return; }
            writer.WriteStartObject();
            foreach (JsonProperty entry in element.EnumerateObject())
            {
                writer.WritePropertyName(entry.Name);
                WriteWithoutUnbindableKeys(writer, entry.Value, valueType, options, ignoredJsonNamesByType);
            }
            writer.WriteEndObject();
            return;
        }

        if (typeInfo.Kind != JsonTypeInfoKind.Object) { element.WriteTo(writer); return; }

        // Same contract read, same comparer, as the walk — so "declared" means here exactly what it
        // means there, and the two cannot disagree about which members are candidates for stripping.
        bool isOpen = false;
        var declared = new Dictionary<string, JsonPropertyInfo>(
            options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.IsExtensionData) isOpen = true;
            else declared[property.Name] = property;
        }

        // Same set, same comparer as the walk — see the corresponding lookup in FindInvalidDynamicKey.
        // The two must agree about which spellings are withheld or the scan would report a body as
        // needing a strip and the strip would then leave the key in place.
        ignoredJsonNamesByType.TryGetValue(typeInfo.Type, out IReadOnlySet<string>? ignoredJsonNames);

        writer.WriteStartObject();
        foreach (JsonProperty member in element.EnumerateObject())
        {
            if (declared.TryGetValue(member.Name, out JsonPropertyInfo? property))
            {
                writer.WritePropertyName(member.Name);
                WriteWithoutUnbindableKeys(
                    writer, member.Value, property.PropertyType, options, ignoredJsonNamesByType);
                continue;
            }
            // The one thing this whole method exists to do. Exactly the pair the walk classified as
            // unbindable, tested in the same order against the same sets, so the two cannot disagree
            // about which members disappear.
            if (isOpen
                && ((ignoredJsonNames is not null && ignoredJsonNames.Contains(member.Name))
                    || IsControlInformationName(member.Name)))
            {
                continue;
            }

            // An accepted dynamic key, or an unmatched member of a closed type (which the binder
            // discards anyway). Copied verbatim in both cases: a dynamic value is opaque, and its
            // keys were already validated by the walk.
            writer.WritePropertyName(member.Name);
            member.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Walks the value of an accepted dynamic key. Every object key at every depth below it must
    /// satisfy the same identifier rule, including through arrays.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>type-free</b>, unlike <see cref="FindInvalidDynamicKey"/>. A dynamic value has
    /// no declared type by construction — it binds to an <c>object</c>-typed slot and materialises as
    /// a <see cref="JsonElement"/> — so there is no <see cref="JsonTypeInfo"/> to consult and no
    /// notion of a "declared" member down here. Every key is a bag key, so every key is policed;
    /// scalars terminate the walk.
    /// <para>
    /// Linear in the size of the value and bounded by the JSON document's own depth. No second depth
    /// limit is imposed: <c>JsonDocument</c> has already enforced its 64-level cap at parse time, and
    /// this walk cannot outrun a tree the reader accepted — a body deep enough to overflow here was
    /// rejected with a <c>400</c> before <see cref="FindInvalidDynamicKey"/> was ever called.
    /// </para>
    /// </remarks>
    private static string? FindInvalidKeyInDynamicValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty member in value.EnumerateObject())
                {
                    if (!IsValidDynamicPropertyName(member.Name)) return member.Name;
                    string? found = FindInvalidKeyInDynamicValue(member.Value);
                    if (found is not null) return found;
                }
                return null;
            case JsonValueKind.Array:
                // Same spelling, same short-circuit, as the array branch of FindInvalidDynamicKey:
                // `Select` is lazy, so `FirstOrDefault` stops at the first failing element.
                return value.EnumerateArray()
                    .Select(FindInvalidKeyInDynamicValue)
                    .FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }

    // The value type of a dictionary-shaped CLR member, or null when none can be resolved (a
    // non-generic IDictionary, say) — in which case the walk simply stops, exactly as it did before.
    // The declared type itself is tested first so an `IDictionary<string, T>`-typed member resolves
    // without an interface scan.
    private static Type? GetDictionaryValueType(Type type)
    {
        if (IsDictionaryInterface(type)) return type.GetGenericArguments()[1];
        return type.GetInterfaces().FirstOrDefault(IsDictionaryInterface)?.GetGenericArguments()[1];
    }

    private static bool IsDictionaryInterface(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

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
        return type.GetInterfaces()
            .FirstOrDefault(candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>
    /// An OData dynamic property name must be a simple identifier (CSDL §4.1 <c>odataIdentifier</c>):
    /// one leading character from Unicode category <c>L</c> or <c>Nl</c> (or <c>_</c>), followed by
    /// up to 127 characters from <c>L</c>, <c>Nl</c>, <c>Nd</c>, <c>Mn</c>, <c>Mc</c>, <c>Pc</c> or
    /// <c>Cf</c>.
    /// </summary>
    /// <remarks>
    /// The exclusions are what matter. <c>@</c> introduces control information (JSON Format §4.5)
    /// — <c>@odata.type</c>, <c>@odata.id</c> — and <c>Name@odata.count</c> is the inline
    /// annotation grammar, so any key carrying <c>@</c> is not a property name at all. <c>.</c> is
    /// the namespace separator. The empty string and embedded whitespace are not identifiers and
    /// are not addressable by any query option.
    /// <para>
    /// <b>The category set is the normative grammar, not an ASCII-flavoured approximation of it</b>
    /// (#389 M3). The obvious spelling — <c>char.IsLetter</c> / <c>char.IsLetterOrDigit</c> — is
    /// narrower than the ABNF in a way that rejects legitimate clients: it excludes the combining
    /// marks (<c>Mn</c>/<c>Mc</c>) that Devanagari, Thai and every NFD-normalised Latin string are
    /// built from, and <c>Nl</c>. Measured rejections of valid identifiers included <c>नाम</c>,
    /// <c>ชื่อ</c>, <c>Ⅸ</c>, and NFD-decomposed <c>naïve</c>. That last one is the indefensible
    /// case: macOS hands back NFD where Windows hands back NFC, so the same key typed on two
    /// machines got two different HTTP status codes. Under the category rule the two spellings agree
    /// for any name <b>within the length limit</b> — decomposition adds code points, so a name
    /// already at the 128 cap in NFC can still exceed it in NFD (measured: 128 × U+00EF is accepted,
    /// its 256-code-point NFD form is not). That is the grammar's own boundary, which defines the
    /// limit in characters, not a normalisation inconsistency this could paper over.
    /// </para>
    /// <para>
    /// <b>Counted in code points, not UTF-16 code units.</b> The 128 cap is a character count in the
    /// grammar; charging an astral-plane identifier like <c>𝐀bc</c> double because the CLR stores it
    /// as a surrogate pair would be an artefact of the encoding. Rune enumeration also means no
    /// per-<c>char</c> inspection ever sees half a surrogate pair: an unpaired surrogate decodes to
    /// U+FFFD, whose category is in neither set, so a malformed name is rejected rather than
    /// silently accepted.
    /// </para>
    /// </remarks>
    internal static bool IsValidDynamicPropertyName(string name) =>
        name.Length != 0
        && (IsAllAsciiIdentifierCharacters(name)
            ? IsValidAsciiName(name)
            : IsValidDynamicPropertyNameByRuneWalk(name));

    // ── ASCII fast path ─────────────────────────────────────────────────────────────────────────
    //
    // ContainsAnyExcept(SearchValues<char>) is SIMD-accelerated (.NET 8+, and this assembly
    // multi-targets net8.0;net10.0 — verified to compile and run on both): it answers "is every
    // character in [A-Za-z0-9_]" in one vectorized pass over the whole string, with no per-char
    // branch, no rune decode and no Unicode-category table lookup. Real dynamic keys — "tier",
    // "region", "customerId" — take this path.
    //
    // [A-Za-z0-9_] is NOT a definition of the grammar. It is the intersection of the grammar with
    // ASCII, which is what makes it safe to decide here and to defer everything else to the rune
    // walk. The fast path is an ACCELERATOR, never a second opinion: OpenTypeJsonOptionsTests pins
    // the two against a shared corpus — plus every ASCII code point in both positions and every lone
    // surrogate — and asserts zero disagreement.
    private static readonly SearchValues<char> AsciiIdentifierCharacters = SearchValues.Create(
        "_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    private static bool IsAllAsciiIdentifierCharacters(string name) =>
        !name.AsSpan().ContainsAnyExcept(AsciiIdentifierCharacters);

    // Only sound for a name already known to be entirely [A-Za-z0-9_] and non-empty. Everything it
    // decides is decidable from that subset alone:
    //   - Length: an all-ASCII string has exactly one code point per char, so the grammar's
    //     128-CODE-POINT cap is the char count. This test CANNOT be hoisted above the
    //     IsAllAsciiIdentifierCharacters call — a non-ASCII name may be longer in chars than in code
    //     points (a surrogate pair is two chars, one code point), so `Length > 128` does not imply
    //     invalid in general. It only implies it here.
    //   - Leading character: within [A-Za-z0-9_], letters are L, '_' is admitted by name in the rune
    //     walk, and the digits are the ONLY members illegal in leading position (Nd is a
    //     following-only category). So one IsAsciiDigit test reproduces the whole leading rule.
    //   - Following characters: every member of the set is L, Nd or Pc, all of which are in the
    //     following set, so no further test is needed.
    private static bool IsValidAsciiName(string name) =>
        name.Length <= 128 && !char.IsAsciiDigit(name[0]);

    /// <summary>
    /// The normative implementation of <see cref="IsValidDynamicPropertyName"/>: rune enumeration
    /// plus a Unicode-category lookup per rune. Reached for any name containing a character outside
    /// <c>[A-Za-z0-9_]</c>, and used directly by the tests as the oracle the ASCII fast path is
    /// proven equivalent to.
    /// </summary>
    internal static bool IsValidDynamicPropertyNameByRuneWalk(string name)
    {
        if (name.Length == 0) return false;

        int codePoints = 0;
        bool leading = true;
        foreach (Rune rune in name.EnumerateRunes())
        {
            // 1 leading + 127 following. Checked inside the loop so a long name short-circuits
            // rather than being fully decoded first.
            if (++codePoints > 128) return false;

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (leading)
            {
                // '_' is Pc, which is a FOLLOWING category only, so it needs naming here.
                if (rune.Value != '_' && !IsIdentifierLeadingCategory(category)) return false;
                leading = false;
            }
            else if (!IsIdentifierFollowingCategory(category))
            {
                return false;
            }
        }
        return true;
    }

    // ── Validated-key cache: THE FALLBACK PATH ONLY ─────────────────────────────────────────────
    //
    // The serialize path calls this instead of IsValidDynamicPropertyName. The two differ in exactly
    // one place: what happens when the ASCII fast path is DECLINED.
    //
    // WHY THE FAST PATH IS DELIBERATELY NOT CACHED. Measured (warmed Stopwatch, best-of-15 rounds of
    // 4M invocations over a 20-key working set, all variants behind an identical Func<string,bool> so
    // the dispatch overhead cancels; ns per key):
    //
    //   ASCII keys ("tier", "region", "billingPlan", ...)
    //     declaredNames.Contains, the lookup already in this loop      5.5
    //     ASCII fast path, uncached                                    4.6
    //     ASCII fast path + cache lookup, EVERY key a HIT              6.1     <- caching is SLOWER
    //     ASCII fast path + cache lookup, every key a MISS            12.3
    //
    //   Non-ASCII keys ("naam", "chue", "kundennummer_groesse", ...) — fast path declined
    //     rune walk, uncached                                         16.9
    //     rune walk + cache lookup, EVERY key a HIT                    5.8     <- caching WINS 2.9x
    //
    // The reason is not subtle once measured: an ordinal cache lookup has to hash the whole string
    // and then compare it, which is strictly more work than the single vectorized ContainsAnyExcept
    // pass the fast path already does. Memoising the fast path pays ~1.5 ns per key to avoid ~4.6 ns
    // of work — a straight loss on every hit and a 7.7 ns loss on every miss. Memoising the rune walk
    // pays that same ~1.5 ns to avoid ~16.9 ns, which is a large win.
    //
    // Scoping the cache to the fallback also DELETES the worst case for the common shape: a page
    // whose dynamic keys are ASCII and all distinct never touches the cache at all, so it cannot
    // thrash, cannot saturate, and costs exactly the uncached 4.6 ns per key.
    //
    // Validity is a PURE function of the string, so one process-wide table is correct across every
    // CLR type, every open complex type and every OhData registration — there is nothing per-type to
    // key on. Ordinal, because the grammar is defined over code points and two keys differing by case
    // are two different property names.
    //
    // ONLY VALID KEYS ARE CACHED, and the absence of an entry means "not yet validated", never
    // "invalid". An invalid key THROWS, which aborts the whole response — so it is seen at most once
    // per response and a cached "false" could never be read back on a hot path. Storing it would only
    // spend capacity on strings that, by construction, are never revisited.
    //
    // BOUNDED AT 1024 DISTINCT KEYS, then frozen — insertion stops, lookups keep working, and
    // everything beyond simply revalidates at the uncached rune-walk cost. No eviction: an LRU or a
    // clear-and-refill needs bookkeeping on the READ path (the common case) to serve the overflow
    // case, and a thrashing cache is strictly worse than no cache. 1024 is chosen against the shape
    // of the data rather than a memory budget: a dynamic-property vocabulary is a schema-like thing
    // — the keys a handler puts in a bag come from a finite set of columns, tags or feature flags —
    // and this table only ever sees the NON-ASCII ones, so a process with more than 1024 distinct
    // non-ASCII dynamic property names is not using open types as a schema extension. At 1024 short
    // keys the table is tens of KB, noise next to one page of response JSON.
    //
    // WHY BOUNDING IS SAFE FROM AN ABUSE STANDPOINT, and not merely a heuristic: a client cannot
    // drive growth here at all. Every dynamic key arriving in a write body is validated by
    // FindInvalidDynamicKey and rejected with a 400 BEFORE it can bind, so an invalid key never
    // reaches this cache, and a valid one was already going to be stored by the application. The only
    // strings that can ever be inserted are ones server-side data put in a bag. The bound guards
    // against an unusual model — a handler synthesising per-row key names — not against a hostile
    // caller.
    private const int ValidatedKeyCacheCapacity = 1024;

    private static readonly ConcurrentDictionary<string, bool> ValidatedKeys =
        new(StringComparer.Ordinal);

    // Tracked separately from ValidatedKeys.Count, which is NOT a cheap read: ConcurrentDictionary
    // .Count acquires every lock in the table. This counter is read on each cache MISS, which is
    // every key once the cache is full, so it has to be a plain volatile int load. Interlocked keeps
    // it monotonic; a race can overshoot the capacity by at most the number of threads inserting
    // concurrently, which is a bound on the table size, not on correctness. No lock is taken on the
    // hot path in either direction.
    private static int validatedKeyCount;

    /// <summary>
    /// <see cref="IsValidDynamicPropertyName"/> with the <b>rune-walk fallback</b> memoised over a
    /// bounded process-wide table. This is what the serialize path calls, where the same key recurs
    /// once per row; the ASCII fast path is left uncached because a cache lookup costs more than it
    /// does (see above).
    /// </summary>
    internal static bool IsValidDynamicPropertyNameCached(string name) =>
        name.Length != 0
        && (IsAllAsciiIdentifierCharacters(name)
            ? IsValidAsciiName(name)
            : IsValidNonAsciiNameCached(name));

    private static bool IsValidNonAsciiNameCached(string name)
    {
        // Presence == valid, by the only-valid-keys-are-cached rule above.
        if (ValidatedKeys.ContainsKey(name)) return true;

        bool valid = IsValidDynamicPropertyNameByRuneWalk(name);
        if (valid
            && Volatile.Read(ref validatedKeyCount) < ValidatedKeyCacheCapacity
            && ValidatedKeys.TryAdd(name, true))
        {
            Interlocked.Increment(ref validatedKeyCount);
        }
        return valid;
    }

    // odataIdentifier's identifierLeadingCharacter: Unicode categories L (Lu/Ll/Lt/Lm/Lo) and Nl.
    private static bool IsIdentifierLeadingCategory(UnicodeCategory category) => category switch
    {
        UnicodeCategory.UppercaseLetter or
        UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or
        UnicodeCategory.OtherLetter or
        UnicodeCategory.LetterNumber => true,
        _ => false,
    };

    // identifierCharacter: the leading set plus Nd, Mn, Mc, Pc and Cf. Pc is what admits '_' (and
    // the other connector punctuation) without a special case here.
    private static bool IsIdentifierFollowingCategory(UnicodeCategory category) =>
        IsIdentifierLeadingCategory(category) || category switch
        {
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.Format => true,
            _ => false,
        };
}
